using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The KIVRA Windows Print Bridge requires Windows.");
using var singleInstance = new Mutex(true, @"Local\KIVRA.WindowsPrintBridge", out var isFirstInstance);
if (!isFirstInstance) return;
Console.Title = "KIVRA Windows Print Bridge";
Console.WriteLine("KIVRA Windows Print Bridge - Easy USB Setup\n");

const string defaultServer = "https://kivra-labels.vercel.app";
const string appVersion = "1.3.0";
var configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIVRA", "PrintBridge");
Directory.CreateDirectory(configDirectory);
var configPath = Path.Combine(configDirectory, "windows-bridge.json");
var legacyConfigPath = Path.Combine(AppContext.BaseDirectory, "windows-bridge.json");
if (!File.Exists(configPath) && File.Exists(legacyConfigPath))
{
    File.Copy(legacyConfigPath, configPath);
}
var config = File.Exists(configPath)
    ? JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(configPath), JsonOptions())
    : null;
var requiresSetup = config is null;
if (!requiresSetup || args.Contains("--background", StringComparer.OrdinalIgnoreCase)) HideConsole();

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
if (config is null)
{
    var queues = WindowsPrinter.GetQueues();
    var queue = SelectPrinter(queues);
    Console.WriteLine($"Printer found: {queue}");
    Console.Write("Six-digit pairing code: ");
    var code = Console.ReadLine()?.Trim() ?? string.Empty;
    if (code.Length != 6 || !code.All(char.IsDigit)) throw new InvalidOperationException("Enter the six-digit code shown in KIVRA Settings > Print Bridge.");
    var name = $"{Environment.MachineName} USB Print Bridge";
    var pair = await Post<PairResponse>(http, $"{defaultServer}/api/bridge/pair", new { code, deviceName = name, appVersion, platform = "windows" });
    config = new(defaultServer, pair.Token, queue, name);
    SaveConfig(configPath, config);
    Console.WriteLine("\nSetup complete. KIVRA will start this bridge automatically when you sign in to Windows.");
    await Task.Delay(1200);
    HideConsole();
}

EnableAutoStart();
config = await WaitForPrinter(config, configPath);

Console.WriteLine($"Server:  {config.Server}");
Console.WriteLine($"Printer: {config.QueueName}");
Console.WriteLine("Status:  Ready. You may minimize this window.\n");
http.DefaultRequestHeaders.Add("X-Kivra-Bridge-Token", config.Token);
var lastHeartbeat = DateTimeOffset.MinValue;

while (true)
{
    try
    {
        var availableQueues = WindowsPrinter.GetQueues();
        if (!availableQueues.Contains(config.QueueName, StringComparer.OrdinalIgnoreCase))
        {
            config = await WaitForPrinter(config, configPath);
        }
        if (DateTimeOffset.UtcNow-lastHeartbeat>=TimeSpan.FromSeconds(20))
        {
            await Post<object>(http, $"{config.Server}/api/bridge/heartbeat", new { appVersion });
            lastHeartbeat=DateTimeOffset.UtcNow;
        }
        using var response = await http.PostAsync($"{config.Server}/api/bridge/jobs/claim", null);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) { await Task.Delay(500); continue; }
        response.EnsureSuccessStatusCode();
        var job = await response.Content.ReadFromJsonAsync<BridgeJob>(JsonOptions()) ?? throw new InvalidOperationException("The server returned an empty print job.");
        var error = WindowsPrinter.Send(config.QueueName, Encoding.ASCII.GetBytes(job.Payload));
        await Post<object>(http, $"{config.Server}/api/bridge/jobs/{job.JobId}/complete", new { success = error is null, error });
        Console.WriteLine(error is null ? $"{DateTime.Now:T} Printed {job.ItemName} ({job.LabelCode})" : $"{DateTime.Now:T} Print failed: {error}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{DateTime.Now:T} Bridge error: {ex.Message}");
        await Task.Delay(5000);
    }
}

static string SelectPrinter(IReadOnlyList<string> queues)
{
    if (queues.Count == 0) throw new InvalidOperationException("No Windows printer is installed. Connect the USB printer and install its Windows driver first.");
    var detected = DetectTscPrinter(queues);
    if (detected is not null) return detected;
    if (queues.Count == 1) return queues[0];
    Console.WriteLine("More than one printer was found:");
    for (var i = 0; i < queues.Count; i++) Console.WriteLine($"  {i + 1}. {queues[i]}");
    Console.Write("Select the USB label printer once: ");
    if (!int.TryParse(Console.ReadLine(), out var choice) || choice < 1 || choice > queues.Count) throw new InvalidOperationException("Invalid printer selection.");
    return queues[choice - 1];
}

static string? DetectTscPrinter(IReadOnlyList<string> queues)
    => queues.FirstOrDefault(x => x.Contains("TA220", StringComparison.OrdinalIgnoreCase))
       ?? queues.FirstOrDefault(x => x.Contains("TSC", StringComparison.OrdinalIgnoreCase));

static async Task<BridgeConfig> WaitForPrinter(BridgeConfig config, string configPath)
{
    var notified = false;
    while (true)
    {
        var queues = WindowsPrinter.GetQueues();
        if (queues.Contains(config.QueueName, StringComparer.OrdinalIgnoreCase)) return config;
        var replacement = DetectTscPrinter(queues);
        if (replacement is not null)
        {
            config = config with { QueueName = replacement };
            SaveConfig(configPath, config);
            Console.WriteLine($"USB printer reconnected as: {replacement}");
            return config;
        }
        if (!notified)
        {
            Console.WriteLine($"Waiting for USB printer '{config.QueueName}'. Connect it and switch it on; pairing is still saved.");
            notified = true;
        }
        await Task.Delay(5000);
    }
}

static void SaveConfig(string path, BridgeConfig config)
    => File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions()));

static void EnableAutoStart()
{
    var executable = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(executable)) return;
    var escapedExecutable = executable.Replace("'", "''");
    var command = $"powershell.exe -NoProfile -WindowStyle Hidden -Command \"Start-Process -WindowStyle Hidden -FilePath '{escapedExecutable}' -ArgumentList '--background'\"";
    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
    key?.SetValue("KIVRA Windows Print Bridge", command);
}

static void HideConsole()
{
    var window = NativeConsole.GetConsoleWindow();
    if (window != IntPtr.Zero) NativeConsole.ShowWindow(window, 0);
}

static async Task<T> Post<T>(HttpClient http, string url, object body)
{
    using var response = await http.PostAsJsonAsync(url, body, JsonOptions());
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Server returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    if (typeof(T) == typeof(object)) return (T)new object();
    return await response.Content.ReadFromJsonAsync<T>(JsonOptions()) ?? throw new InvalidOperationException("Server returned an empty response.");
}
static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = true };
sealed record BridgeConfig(string Server, string Token, string QueueName, string DeviceName);
sealed record PairResponse(Guid DeviceId, string DeviceName, string Token);
sealed record BridgeJob(Guid JobId, string LabelCode, string ItemName, string PrinterName, string IpAddress, int TcpPort, string Payload, DateTimeOffset LeaseExpiresAt);

static class NativeConsole
{
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
}

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
        EnumPrinters(Local | Connections, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return [];
        if (Marshal.GetLastWin32Error() != InsufficientBuffer) throw Error("enumerate printer queues");
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(Local | Connections, null, 4, buffer, needed, out _, out var returned)) throw Error("enumerate printer queues");
            var size = Marshal.SizeOf<PrinterInfo4>(); var result = new List<string>(returned);
            for (var i = 0; i < returned; i++) { var info = Marshal.PtrToStructure<PrinterInfo4>(IntPtr.Add(buffer, i * size)); var name = Marshal.PtrToStringUni(info.PrinterName); if (!string.IsNullOrWhiteSpace(name)) result.Add(name); }
            return result.OrderBy(x => x).ToList();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static string? Send(string queue, byte[] payload)
    {
        if (!OpenPrinter(queue, out var handle, IntPtr.Zero)) return Error("open printer").Message;
        try
        {
            var info = new DocInfo { DocumentName = "KIVRA Cloud Label", DataType = "RAW" };
            if (StartDocPrinter(handle, 1, ref info) == 0) return Error("start document").Message;
            try
            {
                if (!StartPagePrinter(handle)) return Error("start page").Message;
                var memory = Marshal.AllocCoTaskMem(payload.Length);
                try { Marshal.Copy(payload, 0, memory, payload.Length); return WritePrinter(handle, memory, payload.Length, out var written) && written == payload.Length ? null : Error("write label").Message; }
                finally { Marshal.FreeCoTaskMem(memory); EndPagePrinter(handle); }
            }
            finally { EndDocPrinter(handle); }
        }
        finally { ClosePrinter(handle); }
    }
    static Exception Error(string action) => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not {action}");
}
