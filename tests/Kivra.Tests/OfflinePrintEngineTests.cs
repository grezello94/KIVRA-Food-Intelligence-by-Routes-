using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kivra.Domain;
using Kivra.WindowsBridge;
using Xunit;

namespace Kivra.Tests;

public sealed class OfflinePrintEngineTests
{
    [Fact]
    public async Task Receipt_is_durable_before_spool_and_retries_after_restart_do_not_print_twice()
    {
        using var fixture = new PrintFixture();
        var request = Request();
        var sends = 0;
        var engine = fixture.Engine((queue, payload) =>
        {
            sends++;
            Assert.Equal("Installed TSC queue", queue);
            var file = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Directory, "offline-jobs"), "*.json"));
            var saved = JsonSerializer.Deserialize<OfflinePrintReceipt>(File.ReadAllText(file), OfflinePrintEngine.JsonOptions)!;
            Assert.Equal("Sending", saved.Status);
            Assert.Equal(request.LabelId, saved.Request.LabelId);
            Assert.Contains("PRINT 1,1", System.Text.Encoding.ASCII.GetString(payload));
            return null;
        });
        var result = await engine.PrintAsync(request, default);
        Assert.Equal("Printed", result.Status);
        Assert.False(result.Idempotent);
        Assert.NotNull(result.Receipt.PrintedAt);
        var restarted = fixture.Engine((_, _) => { sends++; return null; });
        var replay = await restarted.PrintAsync(request, default);
        Assert.True(replay.Idempotent);
        Assert.Equal(result.PrintJobId, replay.PrintJobId);
        Assert.Equal(result.Receipt.Payload, replay.Receipt.Payload);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Reusing_a_key_for_different_content_is_a_conflict()
    {
        using var fixture = new PrintFixture();
        var sends = 0;
        var engine = fixture.Engine((_, _) => { sends++; return null; });
        var request = Request();
        await engine.PrintAsync(request, default);
        await Assert.ThrowsAsync<LocalPrintConflictException>(() => engine.PrintAsync(request with { ItemNameSnapshot = "Different food" }, default));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task An_interrupted_Sending_receipt_becomes_uncertain_and_is_never_resent()
    {
        using var fixture = new PrintFixture();
        var request = Request();
        var first = await fixture.Engine((_, _) => null).PrintAsync(request, default);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Directory, "offline-jobs"), "*.json"));
        OfflinePrintEngine.WriteDurable(path, JsonSerializer.Serialize(first.Receipt with { Status = "Sending", PrintedAt = null }, OfflinePrintEngine.JsonOptions));
        var restarted = fixture.Engine((_, _) => throw new InvalidOperationException("Must not spool again"));
        var recovered = await restarted.PrintAsync(request, default);
        Assert.Equal("Uncertain", recovered.Status);
        Assert.Contains("physical label", recovered.FailureReason);
        Assert.Equal("Uncertain", (await restarted.GetAsync(request.IdempotencyKey, default))!.Status);
    }

    [Fact]
    public async Task Printer_errors_and_interrupted_native_calls_are_retained_without_retries()
    {
        using var fixture = new PrintFixture();
        var sends = 0;
        var engine = fixture.Engine((_, _) => { sends++; return "USB printer disconnected"; });
        var request = Request();
        var failed = await engine.PrintAsync(request, default);
        Assert.Equal("Failed", failed.Status);
        Assert.Null(failed.Receipt.PrintedAt);
        Assert.Equal("USB printer disconnected", failed.FailureReason);
        Assert.Equal("Failed", (await engine.PrintAsync(request, default)).Status);
        Assert.Equal(1, sends);
        var interrupted = fixture.Engine((_, _) => throw new InvalidOperationException("Native call interrupted"));
        Assert.Equal("Uncertain", (await interrupted.PrintAsync(Request(), default)).Status);
    }

    [Fact]
    public async Task Operational_rollover_and_month_expiry_match_shared_restaurant_rules()
    {
        using var fixture = new PrintFixture();
        var request = Request() with
        {
            OperationalDateTime = DateTimeOffset.Parse("2026-02-01T01:30:00+05:30"),
            ShelfLifeValue = 1, ShelfLifeUnit = ShelfLifeUnit.Months
        };
        var result = await fixture.Engine((_, _) => null).PrintAsync(request, default);
        Assert.Equal(DateTimeOffset.Parse("2026-01-30T20:00:00Z"), result.Receipt.OperationalDateTime);
        Assert.Equal(DateTimeOffset.Parse("2026-02-27T20:00:00Z"), result.Receipt.ExpiryDateTime);
        Assert.Contains("31 Jan 2026", result.Receipt.Payload);
        Assert.Contains("28 Feb 2026", result.Receipt.Payload);
        Assert.Equal(request.OperationalDateTime, result.Receipt.Request.OperationalDateTime);
    }

    [Fact]
    public async Task A_reprint_preserves_the_original_operational_day_and_expiry_without_resolving_again()
    {
        using var fixture = new PrintFixture();
        var sends = 0;
        var engine = fixture.Engine((_, _) => { sends++; return null; });
        var original = DateTimeOffset.Parse("2026-10-17T01:30:00+05:30");
        var request = Request() with
        {
            IsReprint = true, LabelCode = "RL-261017-0001", OperationalDateTime = original,
            ExpiryDateTime = original.AddDays(2)
        };
        var result = await engine.PrintAsync(request, default);
        Assert.Equal("Printed", result.Status);
        Assert.Equal(original, result.Receipt.OperationalDateTime);
        Assert.Equal(original.AddDays(2), result.Receipt.ExpiryDateTime);
        Assert.Contains("17 Oct 2026", result.Receipt.Payload);
        Assert.Equal("RL-261017-0001", result.LabelCode);
        Assert.True(result.Receipt.Request.IsReprint);
        Assert.True((await engine.PrintAsync(request, default)).Idempotent);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task A_reprint_cannot_change_the_original_expiry_or_inject_a_label_code()
    {
        using var fixture = new PrintFixture();
        var engine = fixture.Engine((_, _) => throw new InvalidOperationException("Must not spool"));
        var request = Request() with { IsReprint = true, LabelCode = "RL-261018-0001" };
        await Assert.ThrowsAsync<ArgumentException>(() => engine.PrintAsync(request, default));
        request = request with { ExpiryDateTime = request.OperationalDateTime.AddDays(3) };
        await Assert.ThrowsAsync<ArgumentException>(() => engine.PrintAsync(request, default));
        request = request with { ExpiryDateTime = request.OperationalDateTime.AddDays(2), LabelCode = "RL-261018\r\nPRINT 1,10" };
        await Assert.ThrowsAsync<ArgumentException>(() => engine.PrintAsync(request, default));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Directory, "offline-jobs"), "*.json"));
    }

    [Fact]
    public async Task Local_prints_use_the_same_gate_as_cloud_spool_work()
    {
        using var fixture = new PrintFixture();
        var sends = 0;
        var engine = fixture.Engine((_, _) => { sends++; return null; });
        await fixture.Gate.WaitAsync();
        var print = engine.PrintAsync(Request(), default);
        Assert.False(print.IsCompleted);
        Assert.Equal(0, sends);
        fixture.Gate.Release();
        await print;
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Invalid_label_codes_and_non_USB_drivers_cannot_reach_the_spooler()
    {
        using var fixture = new PrintFixture();
        var engine = fixture.Engine((_, _) => throw new InvalidOperationException("Must not spool"));
        var request = Request();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.PrintAsync(request with { LabelCode = "forged" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.PrintAsync(request with { Printer = request.Printer with { Driver = "TscTsplNetwork" } }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.PrintAsync(request with { Printer = request.Printer with { PrintSpeedIps = 99 } }, default));
        Assert.Null(await engine.GetAsync("unknown-key", default));
    }

    internal static OfflinePrintRequest Request()
    {
        var id = Guid.NewGuid();
        return new(Guid.NewGuid().ToString(), id, "OFF-" + id.ToString("N")[..12], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.Parse("2026-10-18T12:00:00+05:30"), "Boiled Rice", "Prepared Food", Classification.Veg,
            "PREPARED", 2, ShelfLifeUnit.Days, "Kitchen Chiller", "Asia/Kolkata", new LocalPrinterSettings());
    }

    internal sealed class PrintFixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "kivra-local-print-" + Guid.NewGuid().ToString("N"));
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal OfflinePrintEngine Engine(Func<string, byte[], string?> send) => new(Directory, () => "Installed TSC queue", send, Gate);
        public void Dispose() { Gate.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}

public sealed class LocalPrintServerTests
{
    const string Website = "https://kivra.example.test";

    [Fact]
    public async Task Only_the_paired_website_can_read_the_persisted_local_session_token()
    {
        await using var fixture = new ServerFixture();
        var allowed = await fixture.SendAsync(HttpMethod.Get, "/v1/session");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(Website, Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.True(allowed.Headers.CacheControl!.NoStore);
        var session = (await allowed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        Assert.Equal(64, session!.Length);
        Assert.Equal(session, File.ReadAllText(Path.Combine(fixture.Print.Directory, "local-print-token")));
        var unknown = await fixture.SendAsync(HttpMethod.Get, "/v1/session", origin: "https://unrelated.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.False(unknown.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.DoesNotContain(session, await unknown.Content.ReadAsStringAsync());
        var absent = await fixture.SendAsync(HttpMethod.Get, "/v1/session", origin: null);
        Assert.Equal(HttpStatusCode.Forbidden, absent.StatusCode);
        var rebound = await fixture.SendAsync(HttpMethod.Get, "/v1/session", host: "unrelated.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, rebound.StatusCode);
    }

    [Fact]
    public async Task Preflight_allows_only_the_print_methods_headers_and_private_network_access()
    {
        await using var fixture = new ServerFixture();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/print");
        request.Headers.Add("Origin", Website);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "Content-Type, X-Kivra-Local-Token");
        request.Headers.Add("Access-Control-Request-Private-Network", "true");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Private-Network")));
        Assert.Equal("GET, POST", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Methods")));
        using var bad = new HttpRequestMessage(HttpMethod.Options, "/v1/print");
        bad.Headers.Add("Origin", Website);
        bad.Headers.Add("Access-Control-Request-Method", "DELETE");
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.SendAsync(bad)).StatusCode);
    }

    [Fact]
    public async Task Printing_requires_the_token_returns_a_recoverable_receipt_and_exposes_conflicts()
    {
        await using var fixture = new ServerFixture();
        var request = OfflinePrintEngineTests.Request();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/v1/print", request)).StatusCode);
        var sessionResponse = await fixture.SendAsync(HttpMethod.Get, "/v1/session");
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var token = session.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/v1/print", request, token: "wrong")).StatusCode);
        var printed = await fixture.SendAsync(HttpMethod.Post, "/v1/print", request, token: token);
        Assert.Equal(HttpStatusCode.OK, printed.StatusCode);
        var result = await printed.Content.ReadFromJsonAsync<LocalPrintResult>(OfflinePrintEngine.JsonOptions);
        Assert.Equal("Printed", result!.Status);
        Assert.Equal(request.IdempotencyKey, result.IdempotencyKey);
        var recovered = await fixture.SendAsync(HttpMethod.Get, "/v1/jobs/" + Uri.EscapeDataString(request.IdempotencyKey), token: token);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(1, fixture.Sends);
        var conflict = await fixture.SendAsync(HttpMethod.Post, "/v1/print", request with { ItemNameSnapshot = "Other Food" }, token: token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(Website, Assert.Single(conflict.Headers.GetValues("Access-Control-Allow-Origin")));
        var invalid = await fixture.SendAsync(HttpMethod.Post, "/v1/print", request with { LabelCode = "invalid" }, token: token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("offline label code", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Sends);
    }

    sealed class ServerFixture : IAsyncDisposable
    {
        internal OfflinePrintEngineTests.PrintFixture Print { get; } = new();
        internal HttpClient Client { get; }
        internal int Sends;
        readonly LocalPrintServer server;
        readonly CancellationTokenSource stopping = new();
        readonly Task running;

        internal ServerFixture()
        {
            var engine = Print.Engine((_, _) => { Sends++; return null; });
            server = new(Website, Print.Directory, () => "Installed TSC queue", "Kitchen PC", "1.7.0", engine, _ => { }, port: 0);
            running = server.RunAsync(stopping.Token);
            Client = new(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"), Timeout = TimeSpan.FromSeconds(5) };
        }

        internal Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? token = null, string? origin = Website, string? host = null)
        {
            var request = new HttpRequestMessage(method, path);
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (token is not null) request.Headers.Add("X-Kivra-Local-Token", token);
            if (host is not null) request.Headers.Host = host;
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, OfflinePrintEngine.JsonOptions), System.Text.Encoding.UTF8, "application/json");
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            stopping.Cancel();
            server.Dispose();
            await running;
            Client.Dispose();
            stopping.Dispose();
            Print.Dispose();
        }
    }
}
