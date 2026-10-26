using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Kivra.WindowsBridge;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var instance = new Mutex(true, @"Local\KIVRA.WindowsPrintBridge", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new BridgeContext());
    }
}

sealed class BridgeContext : ApplicationContext
{
    const string Server = "https://kivralabels.redlanternrestaurant.in";
    const string Version = "1.8.0";
    readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIVRA", "PrintBridge");
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly CancellationTokenSource stopping = new();
    readonly SemaphoreSlim printGate = new(1, 1);
    readonly object configurationSync = new();
    LocalPrintServer? localServer;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem statusMenu;
    readonly SynchronizationContext ui;
    StatusForm? statusForm;
    BridgeConfig config = new(Server, "", "", $"{Environment.MachineName} USB Print Bridge");
    IReadOnlyList<string> installedQueues = [];
    IReadOnlyDictionary<string, string> installedDrivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? rejectedCloudToken;
    bool localServerReady;
    string? localServerError;
    string status = "Starting local USB printing…";
    bool cloudOffline;

    string ConfigPath => Path.Combine(dataDirectory, "windows-bridge.json");
    string LogPath => Path.Combine(dataDirectory, "bridge.log");

    public BridgeContext()
    {
        Directory.CreateDirectory(dataDirectory);
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        statusMenu = new ToolStripMenuItem(status) { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(statusMenu);
        menu.Items.Add("Open KIVRA printer settings", null, (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Server + "/#printer-setup") { UseShellExecute = true }));
        menu.Items.Add("Open status", null, (_, _) => ShowStatus());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitBridge());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "KIVRA Print Bridge — Starting", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowStatus();
        _ = StartAsync();
    }

    async Task StartAsync()
    {
        try
        {
            try
            {
                MigrateLegacyConfig();
                var saved = LoadConfig();
                if (saved is not null) config = new(Server, saved.Token ?? "", saved.QueueName ?? "", string.IsNullOrWhiteSpace(saved.DeviceName) ? config.DeviceName : saved.DeviceName);
            }
            catch (Exception ex) { Log($"Saved settings could not be read; local USB printing will still start: {ex.Message}"); }

            // The loopback listener and printer monitor never require a cloud
            // token, a pairing code, or a successful internet request.
            _ = RunLocalServerAsync(stopping.Token);
            var running = RunAsync(stopping.Token);
            try { await Task.Run(InstallAutoStart, stopping.Token); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception ex) { Log($"Automatic startup could not be installed: {ex.Message}"); }
            await running;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log($"Bridge startup error: {ex}");
            SetStatus("Open KIVRA printer settings to check USB printing");
            Notify("Check KIVRA Print Bridge", $"Open the bridge log for details: {ex.Message}", ToolTipIcon.Error);
        }
    }

    async Task RunAsync(CancellationToken token)
    {
        var lastHeartbeat = DateTimeOffset.MinValue;
        var lastCloudToken = "";
        var queues = new PrinterQueueMonitor(WindowsPrinter.GetQueues);
        queues.RefreshIfDue();
        while (!token.IsCancellationRequested)
        {
            var attemptedCloudToken = "";
            try
            {
                if (await queues.ApplyRefreshAsync())
                {
                    lock (configurationSync)
                    {
                        installedQueues = queues.KnownQueues!.ToArray();
                        installedDrivers = WindowsPrinter.GetCachedDriverNames();
                    }
                    await SelectDefaultQueueAsync(token);
                }
                var current = ReadConfig();
                bool queueInstalled;
                bool cloudRejected;
                lock (configurationSync)
                {
                    queueInstalled = installedQueues.Contains(current.QueueName, StringComparer.OrdinalIgnoreCase);
                    cloudRejected = rejectedCloudToken == current.Token;
                }
                if (!queueInstalled)
                {
                    SetConnectionStatus();
                    queues.RefreshIfDue(retrySoon: true);
                    await Task.Delay(250, token);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(current.Token) || cloudRejected)
                {
                    SetConnectionStatus(cloudRejected ? "shared printing needs reconnection in KIVRA" : null);
                    queues.RefreshIfDue();
                    await Task.Delay(250, token);
                    continue;
                }
                if (lastCloudToken != current.Token)
                {
                    lastCloudToken = current.Token;
                    lastHeartbeat = DateTimeOffset.MinValue;
                }
                attemptedCloudToken = current.Token;
                using var response = await SendCloudAsync(current, "/api/bridge/jobs/claim", null, token);
                response.EnsureSuccessStatusCode();
                if (cloudOffline)
                {
                    cloudOffline = false;
                    Notify("Shared printing reconnected", "Local USB printing and shared printing are available.", ToolTipIcon.Info);
                }
                SetConnectionStatus();
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    queues.RefreshIfDue();
                    await HeartbeatIfDue(current);
                    await Task.Delay(250, token);
                    continue;
                }
                var job = await response.Content.ReadFromJsonAsync<BridgeJob>(JsonOptions(), token) ?? throw new InvalidOperationException("The server returned an empty print job.");
                string? error;
                await printGate.WaitAsync(token);
                try
                {
                    var selectedQueue = ReadConfig().QueueName;
                    error = await Task.Run(() => WindowsPrinter.Send(selectedQueue, Encoding.ASCII.GetBytes(job.Payload)), token);
                }
                finally { printGate.Release(); }
                await PostCloudAsync(current, $"/api/bridge/jobs/{job.JobId}/complete", new { success = error is null, error }, token);
                Log(error is null ? $"Printed {job.ItemName} ({job.LabelCode})" : $"Print failed: {error}");
                if (error is not null)
                {
                    queues.RefreshIfDue(force: true);
                    Notify("Print failed", error, ToolTipIcon.Error);
                }
                await HeartbeatIfDue(current);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                lock (configurationSync)
                {
                    // A late response for an old token must not disable a new
                    // registration received through the local web-app endpoint.
                    if (config.Token == attemptedCloudToken) rejectedCloudToken = attemptedCloudToken;
                }
                Log("Shared-print authentication was rejected (401). Local USB printing remains available; reconnect shared printing in KIVRA.");
                SetConnectionStatus("shared printing needs reconnection in KIVRA");
                cloudOffline = false;
            }
            catch (Exception ex)
            {
                Log($"Shared printing or printer discovery: {ex.Message}");
                SetConnectionStatus("shared printing is offline");
                cloudOffline = true;
                queues.RefreshIfDue(retrySoon: true);
                await Task.Delay(5000, token);
            }
        }

        async Task HeartbeatIfDue(BridgeConfig current)
        {
            if (DateTimeOffset.UtcNow - lastHeartbeat < TimeSpan.FromSeconds(20)) return;
            await PostCloudAsync(current, "/api/bridge/heartbeat", new { appVersion = Version }, token);
            lastHeartbeat = DateTimeOffset.UtcNow;
        }
    }

    BridgeConfig ReadConfig() { lock (configurationSync) return config; }

    void SetConnectionStatus(string? sharedStatus = null)
    {
        var snapshot = GetPrinterSnapshot();
        string message;
        lock (configurationSync)
        {
            message = !localServerReady ? localServerError ?? "Starting local USB printing…" :
                !snapshot.PrinterAvailable ? snapshot.Detail ?? "Choose a TSC USB printer in KIVRA" :
                sharedStatus is null ? $"Local USB ready — {snapshot.QueueName}" : $"Local USB ready; {sharedStatus}";
        }
        SetStatus(message);
    }

    LocalPrinterSnapshot GetPrinterSnapshot()
    {
        lock (configurationSync)
        {
                var installed = installedQueues.Select(name =>
                {
                    var compatible = PrinterQueueSelection.IsCompatible(name, installedDrivers.GetValueOrDefault(name));
                    return new LocalPrinterQueue(name, compatible, compatible, compatible ? null : "Choose a supported TSC label printer.", installedDrivers.GetValueOrDefault(name));
                }).ToArray();
            var ready = installed.Any(queue => queue.Compatible && queue.QueueName.Equals(config.QueueName, StringComparison.OrdinalIgnoreCase));
            var detail = ready ? "Windows USB printer queue is installed. Use Test Print to confirm physical output." : installed.Any(queue => queue.Compatible) ? "Choose the USB label printer in KIVRA printer settings." : "Connect the USB label printer and install its Windows printer driver.";
            return new(config.QueueName, ready, installed, detail);
        }
    }

    async Task SelectDefaultQueueAsync(CancellationToken token)
    {
        await printGate.WaitAsync(token);
        try
        {
            lock (configurationSync)
            {
                var selected = PrinterQueueSelection.SelectDefault(config.QueueName, installedQueues, installedDrivers);
                if (selected == config.QueueName) return;
                var replacement = config with { QueueName = selected };
                SaveConfig(replacement);
                config = replacement;
                Log($"USB printer selected automatically: {selected}");
            }
        }
        finally { printGate.Release(); }
    }

    async Task SelectPrinterAsync(string queueName, CancellationToken token)
    {
        await printGate.WaitAsync(token);
        try
        {
            lock (configurationSync)
            {
                var installed = installedQueues.FirstOrDefault(name => name.Equals(queueName, StringComparison.OrdinalIgnoreCase));
                if (installed is null || !PrinterQueueSelection.IsCompatible(installed, installedDrivers.GetValueOrDefault(installed))) throw new ArgumentException("Select an installed TSC USB label printer.");
                var replacement = config with { QueueName = installed };
                SaveConfig(replacement);
                config = replacement;
            }
        }
        finally { printGate.Release(); }
    }

    async Task RegisterCloudAsync(LocalCloudRegistration registration, CancellationToken token)
    {
        if (!Uri.TryCreate(registration.Server, UriKind.Absolute, out var website) ||
            website.GetLeftPart(UriPartial.Authority) != Server || website.AbsolutePath != "/" ||
            website.UserInfo.Length != 0 || website.Query.Length != 0 || website.Fragment.Length != 0)
            throw new ArgumentException("Shared printing must use the trusted KIVRA website.");
        if (string.IsNullOrWhiteSpace(registration.Token) || registration.Token.Length > 8192 ||
            !registration.Token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
            string.IsNullOrWhiteSpace(registration.DeviceName) || registration.DeviceName.Length > 100)
            throw new ArgumentException("The shared-print registration is incomplete.");
        await printGate.WaitAsync(token);
        try
        {
            lock (configurationSync)
            {
                var replacement = config with { Server = Server, Token = registration.Token, DeviceName = registration.DeviceName.Trim() };
                SaveConfig(replacement);
                config = replacement;
                rejectedCloudToken = null;
            }
            Log("Shared printing connected through KIVRA. Local USB settings were retained.");
        }
        finally { printGate.Release(); }
    }

    async Task RunLocalServerAsync(CancellationToken token)
    {
        try
        {
            var engine = new OfflinePrintEngine(dataDirectory, () => ReadConfig().QueueName, WindowsPrinter.Send, printGate);
            localServer = new LocalPrintServer(Server, dataDirectory, () => ReadConfig().QueueName, ReadConfig().DeviceName, Version, engine, Log,
                getPrinterSnapshot: GetPrinterSnapshot, selectPrinter: SelectPrinterAsync, registerCloud: RegisterCloudAsync);
            var running = localServer.RunAsync(token);
            if (running.IsFaulted) await running;
            lock (configurationSync) { localServerReady = true; localServerError = null; }
            await running;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log($"Local USB printing unavailable: {ex}");
            lock (configurationSync) { localServerReady = false; localServerError = "Local USB printing needs attention — open the bridge log"; }
            SetConnectionStatus();
            Notify("Check local USB printing", "KIVRA could not start the local connection. Open the bridge log for details.", ToolTipIcon.Warning);
        }
        finally { lock (configurationSync) localServerReady = false; }
    }

    void ShowStatus()
    {
        var current = ReadConfig();
        if (statusForm is null || statusForm.IsDisposed) statusForm = new StatusForm();
        statusForm.UpdateStatus(status, Server, string.IsNullOrWhiteSpace(current.QueueName) ? "Not detected" : current.QueueName, LogPath);
        statusForm.Show();
        statusForm.Activate();
    }

    void SetStatus(string value) => ui.Post(_ =>
    {
        status = value;
        statusMenu.Text = value;
        var tip = $"KIVRA Print Bridge — {value}";
        tray.Text = tip[..Math.Min(63, tip.Length)];
        statusForm?.UpdateStatus(status, Server, ReadConfig().QueueName, LogPath);
    }, null);

    void Notify(string title, string message, ToolTipIcon icon) => ui.Post(_ => tray.ShowBalloonTip(5000, title, message, icon), null);
    void OpenLog()
    {
        if (!File.Exists(LogPath)) File.WriteAllText(LogPath, "KIVRA Print Bridge log\r\n");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogPath) { UseShellExecute = true });
    }
    void ExitBridge()
    {
        stopping.Cancel();
        localServer?.Dispose();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }
    void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}"); } catch { }
    }
    void MigrateLegacyConfig()
    {
        var legacy = Path.Combine(AppContext.BaseDirectory, "windows-bridge.json");
        if (!File.Exists(ConfigPath) && File.Exists(legacy)) File.Copy(legacy, ConfigPath);
    }
    BridgeConfig? LoadConfig() => File.Exists(ConfigPath) ? JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(ConfigPath), JsonOptions()) : null;
    void SaveConfig(BridgeConfig value)
    {
        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions()));
        File.Move(temporary, ConfigPath, overwrite: true);
    }

    static void InstallAutoStart()
    {
        var running = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(running)) return;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIVRA", "PrintBridge", "app");
        Directory.CreateDirectory(directory);
        var installed = Path.Combine(directory, "KIVRA Windows Print Bridge.exe");
        if (!Path.GetFullPath(running).Equals(Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase)) File.Copy(running, installed, true);
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key?.SetValue("KIVRA Windows Print Bridge", $"\"{installed}\" --background");
    }
    async Task<HttpResponseMessage> SendCloudAsync(BridgeConfig current, string path, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Server + path);
        request.Headers.Add("X-Kivra-Bridge-Token", current.Token);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions());
        return await http.SendAsync(request, token);
    }
    async Task PostCloudAsync(BridgeConfig current, string path, object body, CancellationToken token)
    {
        using var response = await SendCloudAsync(current, path, body, token);
        response.EnsureSuccessStatusCode();
    }
    static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
sealed class StatusForm : Form
{
    readonly Label state = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    readonly Label details = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    public StatusForm()
    {
        Text = "KIVRA Print Bridge"; ClientSize = new Size(560, 190); StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        var hide = new Button { Text = "Hide", AutoSize = true }; hide.Click += (_, _) => Hide();
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(20), WrapContents = false };
        layout.Controls.Add(new Label { Text = "KIVRA Windows Print Bridge", AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 14, FontStyle.Bold) }); layout.Controls.Add(state); layout.Controls.Add(details); layout.Controls.Add(hide); Controls.Add(layout);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }
    public void UpdateStatus(string value, string server, string printer, string log) { state.Text = value; details.Text = $"Printer: {printer}\nServer: {server}\n\nThe bridge keeps running when this window is hidden.\nLog: {log}"; }
}

sealed record BridgeConfig(string Server, string Token, string QueueName, string DeviceName);
sealed record BridgeJob(Guid JobId, string LabelCode, string ItemName, string PrinterName, string IpAddress, int TcpPort, string Payload, DateTimeOffset LeaseExpiresAt);

static class WindowsPrinter
{
    const int Local = 2, Connections = 4, InsufficientBuffer = 122;
    static readonly object driverSync = new();
    static IReadOnlyDictionary<string, string> cachedDrivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PrinterInfo2
    {
        public IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location;
        public IntPtr DeviceMode, SeparatorFile, PrintProcessor, DataType, Parameters, SecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePagesPerMinute;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DocInfo { [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName; [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile; [MarshalAs(UnmanagedType.LPWStr)] public string DataType; }
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool EnumPrinters(int flags, string? name, int level, IntPtr buffer, int size, out int needed, out int returned);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] static extern int StartDocPrinter(IntPtr handle, int level, ref DocInfo info);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool EndDocPrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool StartPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool EndPagePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool WritePrinter(IntPtr handle, IntPtr bytes, int count, out int written);
    public static IReadOnlyList<string> GetQueues()
    {
        // Level 2 includes the installed driver name, so a TSC printer renamed
        // "Kitchen Printer" remains discoverable. This runs on the monitor's
        // worker thread; HTTP requests consume only the cached metadata.
        var initial = EnumPrinters(Local | Connections, null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (!initial && Marshal.GetLastWin32Error() != InsufficientBuffer) throw Error("enumerate printer queues");
        if (needed == 0)
        {
            lock (driverSync) cachedDrivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return [];
        }
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(Local | Connections, null, 2, buffer, needed, out _, out var returned)) throw Error("enumerate printer queues");
            var size = Marshal.SizeOf<PrinterInfo2>();
            var drivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<PrinterInfo2>(IntPtr.Add(buffer, i * size));
                var name = Marshal.PtrToStringUni(info.PrinterName);
                if (!string.IsNullOrWhiteSpace(name)) drivers[name] = Marshal.PtrToStringUni(info.DriverName) ?? "";
            }
            lock (driverSync) cachedDrivers = drivers;
            return drivers.Keys.OrderBy(name => name).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static IReadOnlyDictionary<string, string> GetCachedDriverNames() { lock (driverSync) return cachedDrivers; }
    public static string? Send(string queue, byte[] payload)
    {
        if (!OpenPrinter(queue, out var handle, IntPtr.Zero)) return Error("open printer").Message;
        try { var info = new DocInfo { DocumentName = "KIVRA Cloud Label", DataType = "RAW" }; if (StartDocPrinter(handle, 1, ref info) == 0) return Error("start document").Message; try { if (!StartPagePrinter(handle)) return Error("start page").Message; var memory = Marshal.AllocCoTaskMem(payload.Length); try { Marshal.Copy(payload, 0, memory, payload.Length); return WritePrinter(handle, memory, payload.Length, out var written) && written == payload.Length ? null : Error("write label").Message; } finally { Marshal.FreeCoTaskMem(memory); EndPagePrinter(handle); } } finally { EndDocPrinter(handle); } }
        finally { ClosePrinter(handle); }
    }
    static Exception Error(string action) => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not {action}");
}
