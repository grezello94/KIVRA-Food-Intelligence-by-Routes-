using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The KIVRA Windows Print Bridge requires Windows.");
Console.Title = "KIVRA Windows Print Bridge";
Console.WriteLine("KIVRA Windows Print Bridge\n");

var configPath = Path.Combine(AppContext.BaseDirectory, "windows-bridge.json");
var config = File.Exists(configPath)
    ? JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(configPath), JsonOptions())
    : null;

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
if (config is null)
{
    var queues = WindowsPrinter.GetQueues();
    if (queues.Count == 0) throw new InvalidOperationException("Windows has no installed printer queues.");
    Console.WriteLine("Installed printer queues:");
    for (var i = 0; i < queues.Count; i++) Console.WriteLine($"  {i + 1}. {queues[i]}");
    Console.Write("\nSelect queue number: ");
    if (!int.TryParse(Console.ReadLine(), out var choice) || choice < 1 || choice > queues.Count) throw new InvalidOperationException("Invalid printer selection.");
    Console.Write("KIVRA server [https://kivra-labels.vercel.app]: ");
    var server = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(server)) server = "https://kivra-labels.vercel.app";
    Console.Write("Six-digit pairing code: ");
    var code = Console.ReadLine()?.Trim() ?? string.Empty;
    Console.Write("Bridge name [Kitchen Windows Bridge]: ");
    var name = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(name)) name = "Kitchen Windows Bridge";
    var pair = await Post<PairResponse>(http, $"{server.TrimEnd('/')}/api/bridge/pair", new { code, deviceName = name, appVersion = "1.0.0", platform = "windows" });
    config = new(server.TrimEnd('/'), pair.Token, queues[choice - 1], name);
    File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions()));
    Console.WriteLine("\nPaired successfully. Configuration saved beside this program.");
}

Console.WriteLine($"Server:  {config.Server}");
Console.WriteLine($"Printer: {config.QueueName}");
Console.WriteLine("Status:  Waiting for labels. Keep this window open.\n");
http.DefaultRequestHeaders.Add("X-Kivra-Bridge-Token", config.Token);

while (true)
{
    try
    {
        await Post<object>(http, $"{config.Server}/api/bridge/heartbeat", new { appVersion = "1.0.0" });
        using var response = await http.PostAsync($"{config.Server}/api/bridge/jobs/claim", null);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) { await Task.Delay(3000); continue; }
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
