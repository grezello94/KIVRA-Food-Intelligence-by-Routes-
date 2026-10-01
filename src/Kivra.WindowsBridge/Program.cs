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
        Application.Run(new BridgeContext());
    }
}

sealed class BridgeContext : ApplicationContext
{
    const string Server = "https://kivralabels.redlanternrestaurant.in";
    const string Version = "1.5.0";
    readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIVRA", "PrintBridge");
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly CancellationTokenSource stopping = new();
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem statusMenu;
    readonly SynchronizationContext ui;
    StatusForm? statusForm;
    BridgeConfig? config;
    string status = "Starting…";
    bool offline;

    string ConfigPath => Path.Combine(dataDirectory, "windows-bridge.json");
    string LogPath => Path.Combine(dataDirectory, "bridge.log");

    public BridgeContext()
    {
        Directory.CreateDirectory(dataDirectory);
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        statusMenu = new ToolStripMenuItem(status) { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(statusMenu);
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
            MigrateLegacyConfig();
            config = LoadConfig();
            if (config is null)
            {
                var setup = ShowSetup();
                if (setup is null) { ExitBridge(); return; }
                SetStatus("Pairing with KIVRA…");
                var pair = await Post<PairResponse>($"{Server}/api/bridge/pair", new { code = setup.Value.Code, deviceName = $"{Environment.MachineName} USB Print Bridge", appVersion = Version, platform = "windows" });
                config = new(Server, pair.Token, setup.Value.Queue, pair.DeviceName);
                SaveConfig(config);
                Notify("Setup complete", "KIVRA Print Bridge will now run quietly in the notification area.", ToolTipIcon.Info);
            }
            InstallAutoStart();
            http.DefaultRequestHeaders.Add("X-Kivra-Bridge-Token", config.Token);
            await RunAsync(config, stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log($"Fatal error: {ex}");
            ui.Post(_ =>
            {
                MessageBox.Show($"KIVRA Print Bridge could not start.\n\n{ex.Message}\n\nDetails: {LogPath}", "KIVRA Print Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitBridge();
            }, null);
        }
    }

    async Task RunAsync(BridgeConfig current, CancellationToken token)
    {
        var lastHeartbeat = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var queues = WindowsPrinter.GetQueues();
                if (!queues.Contains(current.QueueName, StringComparer.OrdinalIgnoreCase))
                {
                    var replacement = DetectTsc(queues);
                    if (replacement is null)
                    {
                        SetStatus($"Waiting for printer: {current.QueueName}");
                        await Task.Delay(5000, token);
                        continue;
                    }
                    current = current with { QueueName = replacement };
                    config = current;
                    SaveConfig(current);
                    Log($"USB printer reconnected as: {replacement}");
                }
                if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(20))
                {
                    await Post<object>($"{current.Server}/api/bridge/heartbeat", new { appVersion = Version }, token);
                    lastHeartbeat = DateTimeOffset.UtcNow;
                }
                if (offline) { offline = false; Notify("Connection restored", "KIVRA Print Bridge is connected again.", ToolTipIcon.Info); }
                SetStatus($"Ready — {current.QueueName}");
                using var response = await http.PostAsync($"{current.Server}/api/bridge/jobs/claim", null, token);
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent) { await Task.Delay(500, token); continue; }
                response.EnsureSuccessStatusCode();
                var job = await response.Content.ReadFromJsonAsync<BridgeJob>(JsonOptions(), token) ?? throw new InvalidOperationException("The server returned an empty print job.");
                var error = WindowsPrinter.Send(current.QueueName, Encoding.ASCII.GetBytes(job.Payload));
                await Post<object>($"{current.Server}/api/bridge/jobs/{job.JobId}/complete", new { success = error is null, error }, token);
                Log(error is null ? $"Printed {job.ItemName} ({job.LabelCode})" : $"Print failed: {error}");
                if (error is not null) Notify("Print failed", error, ToolTipIcon.Error);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log($"Bridge error: {ex.Message}");
                SetStatus("Offline — retrying automatically");
                if (!offline) { offline = true; Notify("Connection interrupted", "KIVRA cannot reach the server. It will keep retrying in the background.", ToolTipIcon.Warning); }
                await Task.Delay(5000, token);
            }
        }
    }

    (string Code, string Queue)? ShowSetup()
    {
        using var form = new SetupForm(WindowsPrinter.GetQueues());
        return form.ShowDialog() == DialogResult.OK ? (form.PairingCode, form.QueueName) : null;
    }

    void ShowStatus()
    {
        if (statusForm is null || statusForm.IsDisposed) statusForm = new StatusForm();
        statusForm.UpdateStatus(status, config?.Server ?? Server, config?.QueueName ?? "Not configured", LogPath);
        statusForm.Show();
        statusForm.Activate();
    }

    void SetStatus(string value) => ui.Post(_ =>
    {
        status = value;
        statusMenu.Text = value;
        var tip = $"KIVRA Print Bridge — {value}";
        tray.Text = tip[..Math.Min(63, tip.Length)];
        statusForm?.UpdateStatus(status, config?.Server ?? Server, config?.QueueName ?? "Not configured", LogPath);
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
    void SaveConfig(BridgeConfig value) => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(value, JsonOptions()));
    static string? DetectTsc(IReadOnlyList<string> queues) => queues.FirstOrDefault(x => x.Contains("TA220", StringComparison.OrdinalIgnoreCase)) ?? queues.FirstOrDefault(x => x.Contains("TSC", StringComparison.OrdinalIgnoreCase));

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
    async Task<T> Post<T>(string url, object body, CancellationToken token = default)
    {
        using var response = await http.PostAsJsonAsync(url, body, JsonOptions(), token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Server returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(token)}");
        if (typeof(T) == typeof(object)) return (T)new object();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions(), token) ?? throw new InvalidOperationException("Server returned an empty response.");
    }
    static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

sealed class SetupForm : Form
{
    readonly TextBox code = new() { MaxLength = 6, Dock = DockStyle.Fill };
    readonly ComboBox printers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    public string PairingCode => code.Text.Trim();
    public string QueueName => printers.SelectedItem?.ToString() ?? "";
    public SetupForm(IReadOnlyList<string> queues)
    {
        Text = "Set up KIVRA Print Bridge"; ClientSize = new Size(460, 245); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        foreach (var queue in queues) printers.Items.Add(queue);
        var detected = queues.FirstOrDefault(x => x.Contains("TA220", StringComparison.OrdinalIgnoreCase)) ?? queues.FirstOrDefault(x => x.Contains("TSC", StringComparison.OrdinalIgnoreCase));
        if (detected is not null) printers.SelectedItem = detected; else if (printers.Items.Count > 0) printers.SelectedIndex = 0;
        var ok = new Button { Text = "Connect", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) =>
        {
            if (PairingCode.Length != 6 || !PairingCode.All(char.IsDigit)) { MessageBox.Show("Enter the six-digit code shown in KIVRA Settings > Print Bridge."); return; }
            if (QueueName.Length == 0) { MessageBox.Show("Connect and install the USB printer first."); return; }
            DialogResult = DialogResult.OK;
        };
        AcceptButton = ok; CancelButton = cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
        layout.Controls.Add(new Label { Text = "Choose the USB label printer and enter the pairing code from KIVRA.", AutoSize = true });
        layout.Controls.Add(new Label { Text = "Printer", AutoSize = true, Margin = new Padding(3, 14, 3, 3) }); layout.Controls.Add(printers);
        layout.Controls.Add(new Label { Text = "Six-digit pairing code", AutoSize = true, Margin = new Padding(3, 14, 3, 3) }); layout.Controls.Add(code); layout.Controls.Add(buttons); Controls.Add(layout);
    }
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
sealed record PairResponse(Guid DeviceId, string DeviceName, string Token);
sealed record BridgeJob(Guid JobId, string LabelCode, string ItemName, string PrinterName, string IpAddress, int TcpPort, string Payload, DateTimeOffset LeaseExpiresAt);

static class WindowsPrinter
{
    const int Local = 2, Connections = 4, InsufficientBuffer = 122;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct PrinterInfo4 { public IntPtr PrinterName; public IntPtr ServerName; public uint Attributes; }
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
        EnumPrinters(Local | Connections, null, 4, IntPtr.Zero, 0, out var needed, out _); if (needed == 0) return [];
        if (Marshal.GetLastWin32Error() != InsufficientBuffer) throw Error("enumerate printer queues");
        var buffer = Marshal.AllocHGlobal(needed);
        try { if (!EnumPrinters(Local | Connections, null, 4, buffer, needed, out _, out var returned)) throw Error("enumerate printer queues"); var size = Marshal.SizeOf<PrinterInfo4>(); var result = new List<string>(returned); for (var i = 0; i < returned; i++) { var info = Marshal.PtrToStructure<PrinterInfo4>(IntPtr.Add(buffer, i * size)); var name = Marshal.PtrToStringUni(info.PrinterName); if (!string.IsNullOrWhiteSpace(name)) result.Add(name); } return result.OrderBy(x => x).ToList(); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static string? Send(string queue, byte[] payload)
    {
        if (!OpenPrinter(queue, out var handle, IntPtr.Zero)) return Error("open printer").Message;
        try { var info = new DocInfo { DocumentName = "KIVRA Cloud Label", DataType = "RAW" }; if (StartDocPrinter(handle, 1, ref info) == 0) return Error("start document").Message; try { if (!StartPagePrinter(handle)) return Error("start page").Message; var memory = Marshal.AllocCoTaskMem(payload.Length); try { Marshal.Copy(payload, 0, memory, payload.Length); return WritePrinter(handle, memory, payload.Length, out var written) && written == payload.Length ? null : Error("write label").Message; } finally { Marshal.FreeCoTaskMem(memory); EndPagePrinter(handle); } } finally { EndDocPrinter(handle); } }
        finally { ClosePrinter(handle); }
    }
    static Exception Error(string action) => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not {action}");
}
