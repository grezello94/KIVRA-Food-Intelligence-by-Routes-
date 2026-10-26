using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kivra.WindowsBridge;

internal sealed record LocalPrinterQueue(string QueueName, bool Compatible, bool Ready, string? Detail = null, string? DriverName = null);
internal sealed record LocalPrinterSnapshot(string QueueName, bool PrinterAvailable, IReadOnlyList<LocalPrinterQueue> InstalledQueues, string? Detail = null);
internal sealed record LocalCloudRegistration(string Server, string Token, string DeviceName);

// TcpListener needs no machine-wide HTTP URL reservation or administrator setup.
// It listens only on loopback and accepts only the configured website's exact Origin.
internal sealed class LocalPrintServer : IDisposable
{
    const int HeaderLimit = 16 * 1024, BodyLimit = 64 * 1024;
    readonly TcpListener listener;
    readonly string origin;
    readonly string localToken;
    readonly string deviceName;
    readonly string version;
    readonly OfflinePrintEngine engine;
    readonly Action<string> log;
    readonly Func<LocalPrinterSnapshot> getPrinterSnapshot;
    readonly Func<string, CancellationToken, Task>? selectPrinter;
    readonly Func<LocalCloudRegistration, CancellationToken, Task>? registerCloud;
    readonly SemaphoreSlim connections = new(16, 16);

    internal LocalPrintServer(string server, string dataDirectory, Func<string> queueName,
        string deviceName, string version, OfflinePrintEngine engine, Action<string> log, int port = 17847,
        Func<LocalPrinterSnapshot>? getPrinterSnapshot = null,
        Func<string, CancellationToken, Task>? selectPrinter = null,
        Func<LocalCloudRegistration, CancellationToken, Task>? registerCloud = null)
    {
        var serverUri = new Uri(server, UriKind.Absolute);
        if (serverUri.Scheme is not ("http" or "https")) throw new ArgumentException("The bridge website must use HTTP or HTTPS.");
        origin = serverUri.GetLeftPart(UriPartial.Authority);
        Directory.CreateDirectory(dataDirectory);
        var tokenPath = Path.Combine(dataDirectory, "local-print-token");
        var savedToken = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : "";
        if (savedToken.Length != 64 || !savedToken.All(Uri.IsHexDigit))
        {
            savedToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            OfflinePrintEngine.WriteDurable(tokenPath, savedToken);
        }
        localToken = savedToken;
        this.deviceName = deviceName;
        this.version = version;
        this.engine = engine;
        this.log = log;
        this.getPrinterSnapshot = getPrinterSnapshot ?? (() =>
        {
            var selected = queueName();
            return new(selected, !string.IsNullOrWhiteSpace(selected),
                string.IsNullOrWhiteSpace(selected) ? [] : [new(selected, true, true)]);
        });
        this.selectPrinter = selectPrinter;
        this.registerCloud = registerCloud;
        listener = new TcpListener(IPAddress.Loopback, port);
    }

    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    internal async Task RunAsync(CancellationToken token)
    {
        listener.Start(16);
        log($"Local offline printing is ready at http://127.0.0.1:{Port}");
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                if (!await connections.WaitAsync(0, token)) { client.Dispose(); continue; }
                _ = ServeAsync(client, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        finally { listener.Stop(); }
    }

    async Task ServeAsync(TcpClient client, CancellationToken stopping)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            var allowOrigin = false;
            try
            {
                client.NoDelay = true;
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                reading.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await ReadRequestAsync(stream, reading.Token);
                var cors = request.Headers.TryGetValue("Origin", out var suppliedOrigin) && suppliedOrigin == origin;
                if (!cors) { await ReplyAsync(stream, 403, new { detail = "This website is not allowed to access the local print bridge." }, false, stopping); return; }
                allowOrigin = true;
                if (!request.Headers.TryGetValue("Host", out var host) || host != $"127.0.0.1:{Port}")
                { await ReplyAsync(stream, 403, new { detail = "Use the loopback bridge address." }, true, stopping); return; }
                if (request.Method == "OPTIONS")
                {
                    var method = request.Headers.GetValueOrDefault("Access-Control-Request-Method");
                    var headers = (request.Headers.GetValueOrDefault("Access-Control-Request-Headers") ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (method is not ("GET" or "POST") || headers.Any(header => !header.Equals("content-type", StringComparison.OrdinalIgnoreCase) && !header.Equals("x-kivra-local-token", StringComparison.OrdinalIgnoreCase)))
                    { await ReplyAsync(stream, 403, new { detail = "Unsupported local print request." }, true, stopping); return; }
                    await ReplyAsync(stream, 204, null, true, stopping, preflight: true);
                    return;
                }
                if (request.Method == "GET" && request.Path == "/v1/session")
                {
                    var snapshot = getPrinterSnapshot();
                    await ReplyAsync(stream, 200, new
                    {
                        token = localToken, version, queueName = snapshot.QueueName, deviceName,
                        ready = snapshot.PrinterAvailable, printers = PrinterChoices(snapshot),
                        needsSelection = NeedsSelection(snapshot), detail = snapshot.Detail,
                        capabilities = new[] { "connection-test", "test-print" }
                            .Concat(selectPrinter is null ? [] : new[] { "printer-selection" })
                            .Concat(registerCloud is null ? [] : new[] { "cloud-registration" }).ToArray()
                    }, true, stopping);
                    return;
                }
                if (!HasToken(request.Headers.GetValueOrDefault("X-Kivra-Local-Token")))
                { await ReplyAsync(stream, 401, new { detail = "Connect to the local print bridge again." }, true, stopping); return; }
                if (request.Method == "GET" && request.Path is "/v1/printer" or "/v1/connection-test")
                {
                    await ReplyAsync(stream, 200, PrinterStatus(), true, stopping);
                    return;
                }
                if (request.Method == "POST" && request.Path is "/v1/print" or "/v1/test-print" or "/v1/printer" or "/v1/cloud")
                {
                    if (!request.Headers.GetValueOrDefault("Content-Type", "").Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
                    { await ReplyAsync(stream, 415, new { detail = "Send a JSON print request." }, true, stopping); return; }
                }
                if (request.Method == "POST" && request.Path == "/v1/printer")
                {
                    if (selectPrinter is null) throw new ArgumentException("Update the local print bridge to select a Windows printer from the app.");
                    var selection = JsonSerializer.Deserialize<PrinterSelection>(request.Body, OfflinePrintEngine.JsonOptions)
                        ?? throw new ArgumentException("Select an installed TSC printer.");
                    if (string.IsNullOrWhiteSpace(selection.QueueName) || selection.QueueName.Length > 256 || selection.QueueName.Any(char.IsControl))
                        throw new ArgumentException("Select an installed TSC printer.");
                    var installed = getPrinterSnapshot().InstalledQueues.FirstOrDefault(queue =>
                        queue.QueueName.Equals(selection.QueueName, StringComparison.OrdinalIgnoreCase));
                    if (installed is null || !installed.Compatible) throw new ArgumentException("Select an installed compatible TSC printer from this PC.");
                    // The callback rechecks the installed queue under the shared
                    // spool gate and persists it before acknowledging selection.
                    await selectPrinter(installed.QueueName, stopping);
                    await ReplyAsync(stream, 200, PrinterStatus(), true, stopping);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/cloud")
                {
                    if (registerCloud is null) throw new ArgumentException("Update the local print bridge to enable printing from other devices in the app.");
                    var registration = JsonSerializer.Deserialize<LocalCloudRegistration>(request.Body, OfflinePrintEngine.JsonOptions)
                        ?? throw new ArgumentException("A printing connection is required.");
                    if (!Uri.TryCreate(registration.Server, UriKind.Absolute, out var registeredServer) ||
                        registeredServer.Scheme is not ("http" or "https") || registeredServer.GetLeftPart(UriPartial.Authority) != origin ||
                        registeredServer.UserInfo.Length != 0 || registeredServer.AbsolutePath != "/" || registeredServer.Query.Length != 0 || registeredServer.Fragment.Length != 0)
                        throw new ArgumentException("Enable printing only for this bridge's configured website.");
                    if (string.IsNullOrWhiteSpace(registration.Token) || registration.Token.Length > 8192 || registration.Token.Any(char.IsWhiteSpace) || registration.Token.Any(char.IsControl) ||
                        string.IsNullOrWhiteSpace(registration.DeviceName) || registration.DeviceName.Length > 100 || registration.DeviceName.Any(char.IsControl))
                        throw new ArgumentException("A valid printing connection and device name are required.");
                    await registerCloud(registration, stopping);
                    await ReplyAsync(stream, 200, new { success = true }, true, stopping);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/print")
                {
                    var print = JsonSerializer.Deserialize<OfflinePrintRequest>(request.Body, OfflinePrintEngine.JsonOptions)
                        ?? throw new ArgumentException("A print request is required.");
                    var expectedQueue = ExpectedQueueName(request.Body);
                    if (await engine.GetAsync(print.IdempotencyKey, stopping) is null) RequireReadyPrinter();
                    // A browser timeout/disconnect does not cancel an accepted
                    // spool operation. The durable key can recover its receipt.
                    var result = await engine.PrintAsync(print, stopping, expectedQueue);
                    await ReplyAsync(stream, 200, result, true, stopping);
                    return;
                }
                if (request.Method == "POST" && request.Path == "/v1/test-print")
                {
                    var print = JsonSerializer.Deserialize<LocalTestPrintRequest>(request.Body, OfflinePrintEngine.JsonOptions)
                        ?? throw new ArgumentException("A test print request is required.");
                    var expectedQueue = ExpectedQueueName(request.Body);
                    if (await engine.GetTestAsync(print.IdempotencyKey, stopping) is null) RequireReadyPrinter();
                    var result = await engine.TestPrintAsync(print, stopping, expectedQueue);
                    await ReplyAsync(stream, 200, result, true, stopping);
                    return;
                }
                if (request.Method == "GET" && request.Path.StartsWith("/v1/test-prints/", StringComparison.Ordinal))
                {
                    var key = Uri.UnescapeDataString(request.Path[16..]);
                    var result = await engine.GetTestAsync(key, stopping);
                    await ReplyAsync(stream, result is null ? 404 : 200, result is null ? (object)new { detail = "No saved local test print has this key." } : result, true, stopping);
                    return;
                }
                if (request.Method == "GET" && request.Path.StartsWith("/v1/jobs/", StringComparison.Ordinal))
                {
                    var key = Uri.UnescapeDataString(request.Path[9..]);
                    var result = await engine.GetAsync(key, stopping);
                    await ReplyAsync(stream, result is null ? 404 : 200, result is null ? (object)new { detail = "No saved local print has this key." } : result, true, stopping);
                    return;
                }
                await ReplyAsync(stream, 404, new { detail = "Local print route not found." }, true, stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not SocketException && ex is not IOException)
            {
                log($"Local print request: {ex.Message}");
                try
                {
                    var status = ex is LocalPrintConflictException ? 409 : ex is ArgumentException or JsonException or TimeZoneNotFoundException or InvalidTimeZoneException or FormatException ? 400 : 500;
                    await ReplyAsync(stream, status, new { detail = status < 500 ? ex.Message : "The local print bridge could not save this job. Check its log before retrying." }, allowOrigin, stopping);
                }
                catch (Exception replyError) when (replyError is IOException or SocketException or ObjectDisposedException) { }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
            { if (!stopping.IsCancellationRequested) log($"Local print connection: {ex.Message}"); }
            finally { connections.Release(); }
        }
    }

    bool HasToken(string? token) => token is not null && CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(localToken)));

    object PrinterStatus()
    {
        var snapshot = getPrinterSnapshot();
        return new { queueName = snapshot.QueueName, ready = snapshot.PrinterAvailable,
            printers = PrinterChoices(snapshot), needsSelection = NeedsSelection(snapshot), detail = snapshot.Detail };
    }

    static object[] PrinterChoices(LocalPrinterSnapshot snapshot) => snapshot.InstalledQueues.Select(queue =>
        (object)new { name = queue.QueueName, compatible = queue.Compatible, ready = queue.Ready, detail = queue.Detail, driverName = queue.DriverName }).ToArray();
    static bool NeedsSelection(LocalPrinterSnapshot snapshot) => !snapshot.PrinterAvailable && snapshot.InstalledQueues.Count(queue => queue.Compatible) > 1;

    string ExpectedQueueName(byte[] body)
    {
        // Queue binding is transport metadata, deliberately excluded from the
        // durable request model/hash so receipts from previous versions recover.
        using var document = JsonDocument.Parse(body);
        var printer = document.RootElement.EnumerateObject().LastOrDefault(property => property.Name.Equals("printer", StringComparison.OrdinalIgnoreCase)).Value;
        if (printer.ValueKind == JsonValueKind.Object)
        {
            var queue = printer.EnumerateObject().LastOrDefault(property => property.Name.Equals("usbQueueName", StringComparison.OrdinalIgnoreCase)).Value;
            if (queue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                if (queue.ValueKind != JsonValueKind.String) throw new ArgumentException("The selected USB printer name must be a string.");
                var name = queue.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (name.Length > 256 || name.Any(char.IsControl)) throw new ArgumentException("Select a valid USB printer in KIVRA printer settings.");
                    return name;
                }
            }
        }
        return getPrinterSnapshot().QueueName;
    }

    void RequireReadyPrinter()
    {
        var snapshot = getPrinterSnapshot();
        if (!snapshot.PrinterAvailable || !snapshot.InstalledQueues.Any(queue => queue.Compatible &&
            queue.QueueName.Equals(snapshot.QueueName, StringComparison.OrdinalIgnoreCase)))
            throw new LocalPrintConflictException(snapshot.Detail ?? "Connect or select an installed compatible TSC printer in the app before printing.");
    }

    async Task ReplyAsync(Stream stream, int status, object? body, bool allowOrigin, CancellationToken token, bool preflight = false)
    {
        var payload = body is null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(body, OfflinePrintEngine.JsonOptions);
        var reason = status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 415 => "Unsupported Media Type", _ => "Internal Server Error" };
        var headers = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\nVary: Origin\r\n";
        if (allowOrigin) headers += $"Access-Control-Allow-Origin: {origin}\r\n";
        if (preflight) headers += "Access-Control-Allow-Methods: GET, POST\r\nAccess-Control-Allow-Headers: content-type, x-kivra-local-token\r\nAccess-Control-Allow-Private-Network: true\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + "\r\n"), token);
        if (payload.Length > 0) await stream.WriteAsync(payload, token);
    }

    static async Task<LocalRequest> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        using var header = new MemoryStream();
        var one = new byte[1];
        var boundary = 0;
        while (header.Length < HeaderLimit)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("The local print request ended early.");
            header.WriteByte(one[0]);
            boundary = one[0] == (boundary is 0 or 2 ? '\r' : '\n') ? boundary + 1 : one[0] == '\r' ? 1 : 0;
            if (boundary == 4) break;
        }
        if (boundary != 4) throw new ArgumentException("Local print headers are too large.");
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] != "HTTP/1.1" || !first[1].StartsWith('/') || first[1].Contains('#')) throw new ArgumentException("Invalid local print request.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new ArgumentException("Invalid or repeated local print header.");
        }
        if (headers.ContainsKey("Transfer-Encoding")) throw new ArgumentException("Chunked local print requests are not supported.");
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var size) && (!int.TryParse(size, out length) || length < 0 || length > BodyLimit)) throw new ArgumentException("The local print request is too large.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return new(first[0], first[1], headers, body);
    }

    public void Dispose() => listener.Stop();
    sealed record PrinterSelection(string QueueName);
    sealed record LocalRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);
}
