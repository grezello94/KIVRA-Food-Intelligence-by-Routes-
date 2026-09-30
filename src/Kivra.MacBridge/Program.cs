using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The KIVRA Mac Print Bridge requires macOS.");
const string server = "https://kivralabels.redlanternrestaurant.in";
const string appVersion = "1.0.0";
var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var dataDirectory = Path.Combine(home, "Library", "Application Support", "KIVRA", "PrintBridge");
Directory.CreateDirectory(dataDirectory);
var configPath = Path.Combine(dataDirectory, "mac-bridge.json");
var config = File.Exists(configPath) ? JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(configPath), JsonOptions()) : null;
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

if (config is null)
{
    var queues = await MacPrinter.GetQueues();
    if (queues.Count == 0) throw new InvalidOperationException("No macOS printer queue was found. Add the label printer in System Settings > Printers & Scanners first.");
    var queue = queues.FirstOrDefault(x => x.Contains("TSC", StringComparison.OrdinalIgnoreCase)) ?? queues[0];
    if (queues.Count > 1 && !args.Contains("--background"))
    {
        Console.WriteLine("Installed printer queues:");
        for (var i = 0; i < queues.Count; i++) Console.WriteLine($"  {i + 1}. {queues[i]}");
        Console.Write($"Select printer [1-{queues.Count}, default {queues.IndexOf(queue) + 1}]: ");
        if (int.TryParse(Console.ReadLine(), out var selected) && selected >= 1 && selected <= queues.Count) queue = queues[selected - 1];
    }
    Console.Write("Six-digit pairing code: ");
    var code = Console.ReadLine()?.Trim() ?? string.Empty;
    if (code.Length != 6 || !code.All(char.IsDigit)) throw new InvalidOperationException("Enter the six-digit code shown in KIVRA Settings > Print Bridge.");
    var pair = await Post<PairResponse>(http, $"{server}/api/bridge/pair", new { code, deviceName = $"{Environment.MachineName} Mac Print Bridge", appVersion, platform = "mac" });
    config = new(server, pair.Token, queue);
    File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOptions()));
}

InstallAndEnableAutoStart();
http.DefaultRequestHeaders.Add("X-Kivra-Bridge-Token", config.Token);
var lastHeartbeat = DateTimeOffset.MinValue;
while (true)
{
    try
    {
        if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(20))
        {
            await Post<object>(http, $"{config.Server}/api/bridge/heartbeat", new { appVersion });
            lastHeartbeat = DateTimeOffset.UtcNow;
        }
        using var response = await http.PostAsync($"{config.Server}/api/bridge/jobs/claim", null);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) { await Task.Delay(500); continue; }
        response.EnsureSuccessStatusCode();
        var job = await response.Content.ReadFromJsonAsync<BridgeJob>(JsonOptions()) ?? throw new InvalidOperationException("The server returned an empty print job.");
        var error = await MacPrinter.Send(config.QueueName, job.Payload);
        await Post<object>(http, $"{config.Server}/api/bridge/jobs/{job.JobId}/complete", new { success = error is null, error });
    }
    catch (Exception ex) { Console.Error.WriteLine($"{DateTime.Now:T} Bridge error: {ex.Message}"); await Task.Delay(5000); }
}

void InstallAndEnableAutoStart()
{
    var source = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(source)) return;
    var installed = Path.Combine(dataDirectory, "KIVRA Mac Print Bridge");
    if (!Path.GetFullPath(source).Equals(Path.GetFullPath(installed), StringComparison.Ordinal))
    {
        File.Copy(source, installed, true);
        File.SetUnixFileMode(installed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    var agents = Path.Combine(home, "Library", "LaunchAgents");
    Directory.CreateDirectory(agents);
    var plist = Path.Combine(agents, "in.redlanternrestaurant.kivra-print-bridge.plist");
    var escaped = System.Security.SecurityElement.Escape(installed);
    File.WriteAllText(plist, $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>Label</key><string>in.redlanternrestaurant.kivra-print-bridge</string><key>ProgramArguments</key><array><string>{escaped}</string><string>--background</string></array><key>RunAtLoad</key><true/><key>KeepAlive</key><true/></dict></plist>
""");
}

static async Task<T> Post<T>(HttpClient http, string url, object body)
{
    using var response = await http.PostAsJsonAsync(url, body, JsonOptions());
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Server returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    if (typeof(T) == typeof(object)) return (T)new object();
    return await response.Content.ReadFromJsonAsync<T>(JsonOptions()) ?? throw new InvalidOperationException("Server returned an empty response.");
}
static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = true };
sealed record BridgeConfig(string Server, string Token, string QueueName);
sealed record PairResponse(Guid DeviceId, string DeviceName, string Token);
sealed record BridgeJob(Guid JobId, string LabelCode, string ItemName, string PrinterName, string IpAddress, int TcpPort, string Payload, DateTimeOffset LeaseExpiresAt);

static class MacPrinter
{
    public static async Task<List<string>> GetQueues()
    {
        var output = await Run("lpstat", "-p");
        return output.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.StartsWith("printer ")).Select(x => x[8..].Split(' ', 2)[0]).OrderBy(x => x).ToList();
    }
    public static async Task<string?> Send(string queue, string payload)
    {
        var file = Path.Combine(Path.GetTempPath(), $"kivra-{Guid.NewGuid():N}.prn");
        try
        {
            await File.WriteAllBytesAsync(file, Encoding.ASCII.GetBytes(payload));
            var result = await Run("lp", "-d", queue, "-o", "raw", file);
            return result.ExitCode == 0 ? null : result.StandardError.Trim();
        }
        finally { File.Delete(file); }
    }
    static async Task<(int ExitCode, string StandardOutput, string StandardError)> Run(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}
