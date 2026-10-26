using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kivra.WindowsBridge;
using Xunit;

namespace Kivra.Tests;

public sealed class LocalPlugAndPlayTests
{
    const string Website = "https://kivra.example.test";

    [Fact]
    public void Missing_or_damaged_local_session_token_is_renewed_without_a_pairing_code()
    {
        using var fixture = new OfflinePrintEngineTests.PrintFixture();
        var engine = fixture.Engine((_, _) => null);
        var path = Path.Combine(fixture.Directory, "local-print-token");
        File.WriteAllText(path, "damaged");
        using var server = new LocalPrintServer(Website, fixture.Directory, () => "TSC TE210", "Kitchen PC", "1.8.0", engine, _ => { }, port: 0);
        var token = File.ReadAllText(path);
        Assert.Equal(64, token.Length);
        Assert.All(token, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public async Task Session_and_connection_test_report_installed_compatible_printers_without_cloud_pairing()
    {
        await using var fixture = new Fixture();
        var session = await fixture.SessionAsync();
        Assert.True(session.GetProperty("ready").GetBoolean());
        Assert.Equal("TSC TE210", session.GetProperty("queueName").GetString());
        Assert.False(session.GetProperty("needsSelection").GetBoolean());
        Assert.Contains("printer-selection", session.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("cloud-registration", session.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(2, session.GetProperty("printers").GetArrayLength());
        fixture.Snapshot = new("Old queue", false, [new("TSC TE210", true, true), new("TSC TA220", true, true)], "Choose a printer.");
        var response = await fixture.SendAsync(HttpMethod.Get, "/v1/connection-test", token: fixture.Token);
        var connection = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(connection.GetProperty("ready").GetBoolean());
        Assert.True(connection.GetProperty("needsSelection").GetBoolean());
        Assert.Equal("Choose a printer.", connection.GetProperty("detail").GetString());
        Assert.Equal(0, fixture.Sends);
    }

    [Fact]
    public async Task Printer_selection_accepts_only_installed_compatible_queues_and_requires_local_authorization()
    {
        await using var fixture = new Fixture();
        await fixture.SessionAsync();
        var selection = new { queueName = "TSC TE210" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/v1/printer", selection)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.SendAsync(HttpMethod.Post, "/v1/printer", selection, fixture.Token, "https://other.example.test")).StatusCode);
        foreach (var name in new[] { "Missing printer", "Microsoft Print to PDF", "TSC TE210\r\nPRINT 1,10" })
            Assert.Equal(HttpStatusCode.BadRequest, (await fixture.SendAsync(HttpMethod.Post, "/v1/printer", new { queueName = name }, fixture.Token)).StatusCode);
        Assert.Equal(0, fixture.Selections);
        fixture.Snapshot = fixture.Snapshot with { QueueName = "Old queue", PrinterAvailable = false };
        var selected = await fixture.SendAsync(HttpMethod.Post, "/v1/printer", new { queueName = "tsc te210" }, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var status = await selected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(status.GetProperty("ready").GetBoolean());
        Assert.Equal("TSC TE210", status.GetProperty("queueName").GetString());
        Assert.Equal(1, fixture.Selections);
        Assert.Equal(0, fixture.Sends);
    }

    [Fact]
    public async Task Unavailable_or_incompatible_selected_queue_cannot_receive_new_prints()
    {
        await using var fixture = new Fixture();
        await fixture.SessionAsync();
        fixture.Snapshot = new("Microsoft Print to PDF", true, [new("Microsoft Print to PDF", false, true)]);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.SendAsync(HttpMethod.Post, "/v1/print", OfflinePrintEngineTests.Request(), fixture.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.SendAsync(HttpMethod.Post, "/v1/test-print", TestRequest(), fixture.Token)).StatusCode);
        fixture.Snapshot = new("TSC TE210", false, []);
        Assert.Equal(HttpStatusCode.Conflict, (await fixture.SendAsync(HttpMethod.Post, "/v1/test-print", TestRequest(), fixture.Token)).StatusCode);
        Assert.Equal(0, fixture.Sends);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Print.Directory, "offline-jobs")));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Print.Directory, "test-print-jobs")));
    }

    [Fact]
    public async Task Test_print_requires_authorization_and_recovers_same_durable_receipt_without_food_history()
    {
        await using var fixture = new Fixture();
        await fixture.SessionAsync();
        var request = TestRequest();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/v1/test-print", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.SendAsync(HttpMethod.Post, "/v1/test-print", request, fixture.Token, "https://other.example.test")).StatusCode);
        Assert.Equal(0, fixture.Sends);
        var response = await fixture.SendAsync(HttpMethod.Post, "/v1/test-print", request, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = (await response.Content.ReadFromJsonAsync<LocalTestPrintResult>())!;
        Assert.Equal("Printed", first.Status);
        Assert.False(first.Idempotent);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Print.Directory, "offline-jobs")));
        var saved = JsonSerializer.Deserialize<LocalTestPrintReceipt>(File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(fixture.Print.Directory, "test-print-jobs")))), OfflinePrintEngine.JsonOptions)!;
        Assert.Contains("KIVRA TEST LABEL", saved.Payload);
        Assert.Contains("PRINT 1,1", saved.Payload);
        Assert.False(response.Headers.Contains("Receipt"));
        Assert.DoesNotContain("labelId", await response.Content.ReadAsStringAsync());
        // A disconnected printer must not prevent recovery of an accepted job.
        fixture.Snapshot = new("TSC TE210", false, []);
        var replayResponse = await fixture.SendAsync(HttpMethod.Post, "/v1/test-print", request, fixture.Token);
        var replay = (await replayResponse.Content.ReadFromJsonAsync<LocalTestPrintResult>())!;
        Assert.True(replay.Idempotent);
        Assert.Equal(first.PrintJobId, replay.PrintJobId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Get, "/v1/test-prints/" + request.IdempotencyKey)).StatusCode);
        var recoveredResponse = await fixture.SendAsync(HttpMethod.Get, "/v1/test-prints/" + request.IdempotencyKey, token: fixture.Token);
        Assert.Equal(HttpStatusCode.OK, recoveredResponse.StatusCode);
        Assert.Equal(first.PrintJobId, (await recoveredResponse.Content.ReadFromJsonAsync<LocalTestPrintResult>())!.PrintJobId);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.SendAsync(HttpMethod.Get, "/v1/test-prints/unknown", token: fixture.Token)).StatusCode);
        Assert.Equal(1, fixture.Sends);
    }

    [Fact]
    public async Task Cloud_registration_is_optional_authenticated_and_bound_to_the_trusted_website()
    {
        await using var fixture = new Fixture();
        await fixture.SessionAsync();
        var registration = new LocalCloudRegistration(Website, "new-cloud-token", "Kitchen PC");
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.SendAsync(HttpMethod.Post, "/v1/cloud", registration)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.SendAsync(HttpMethod.Post, "/v1/cloud", registration, fixture.Token, "https://other.example.test")).StatusCode);
        foreach (var server in new[] { "https://other.example.test", Website + "/other", Website + "/?query=1", Website + "/#fragment", "https://user@kivra.example.test" })
            Assert.Equal(HttpStatusCode.BadRequest, (await fixture.SendAsync(HttpMethod.Post, "/v1/cloud", registration with { Server = server }, fixture.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.SendAsync(HttpMethod.Post, "/v1/cloud", registration with { Token = "token\r\ninjection" }, fixture.Token)).StatusCode);
        Assert.Null(fixture.Registration);
        var response = await fixture.SendAsync(HttpMethod.Post, "/v1/cloud", registration, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(registration, fixture.Registration);
        Assert.DoesNotContain(registration.Token, await response.Content.ReadAsStringAsync());
        fixture.CloudError = new InvalidOperationException("Cloud unavailable");
        Assert.Equal(HttpStatusCode.InternalServerError, (await fixture.SendAsync(HttpMethod.Post, "/v1/cloud", registration, fixture.Token)).StatusCode);
        Assert.True((await fixture.SessionAsync()).GetProperty("ready").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await fixture.SendAsync(HttpMethod.Post, "/v1/test-print", TestRequest(), fixture.Token)).StatusCode);
        Assert.Equal(1, fixture.Sends);
    }

    [Fact]
    public async Task Test_receipt_precedes_spooling_and_restart_or_uncertain_outcomes_never_resend()
    {
        using var fixture = new OfflinePrintEngineTests.PrintFixture();
        var request = TestRequest();
        var sends = 0;
        var engine = fixture.Engine((_, _) =>
        {
            sends++;
            var saved = JsonSerializer.Deserialize<LocalTestPrintReceipt>(File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(fixture.Directory, "test-print-jobs")))), OfflinePrintEngine.JsonOptions)!;
            Assert.Equal("Sending", saved.Status);
            return null;
        });
        var first = await engine.TestPrintAsync(request, default);
        Assert.Equal("Printed", first.Status);
        var restarted = fixture.Engine((_, _) => { sends++; return null; });
        var replay = await restarted.TestPrintAsync(request, default);
        Assert.Equal(first.PrintJobId, replay.PrintJobId);
        Assert.True(replay.Idempotent);
        await Assert.ThrowsAsync<LocalPrintConflictException>(() => restarted.TestPrintAsync(request with { Printer = request.Printer with { PrintDensity = 9 } }, default));
        var path = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Directory, "test-print-jobs")));
        var receipt = JsonSerializer.Deserialize<LocalTestPrintReceipt>(File.ReadAllText(path), OfflinePrintEngine.JsonOptions)!;
        OfflinePrintEngine.WriteDurable(path, JsonSerializer.Serialize(receipt with { Status = "Sending", PrintedAt = null }, OfflinePrintEngine.JsonOptions));
        var interrupted = await restarted.GetTestAsync(request.IdempotencyKey, default);
        Assert.Equal("Uncertain", interrupted!.Status);
        Assert.Contains("physical label", interrupted.FailureReason);
        Assert.Equal("Uncertain", (await restarted.TestPrintAsync(request, default)).Status);
        Assert.Equal(1, sends);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Directory, "offline-jobs")));
    }

    [Fact]
    public async Task Test_print_failures_are_durable_and_validate_driver_before_any_spool()
    {
        using var fixture = new OfflinePrintEngineTests.PrintFixture();
        var sends = 0;
        var engine = fixture.Engine((_, _) => { sends++; return "USB disconnected"; });
        var request = TestRequest();
        Assert.Equal("Failed", (await engine.TestPrintAsync(request, default)).Status);
        Assert.Equal("USB disconnected", (await engine.TestPrintAsync(request, default)).FailureReason);
        var interrupted = fixture.Engine((_, _) => { sends++; throw new InvalidOperationException("Spooler interrupted"); });
        var uncertainRequest = TestRequest();
        Assert.Equal("Uncertain", (await interrupted.TestPrintAsync(uncertainRequest, default)).Status);
        Assert.Equal("Uncertain", (await interrupted.TestPrintAsync(uncertainRequest, default)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.TestPrintAsync(TestRequest() with { Printer = new() { Driver = "TscTsplNetwork" } }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.TestPrintAsync(TestRequest() with { IdempotencyKey = "" }, default));
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task Test_print_uses_shared_spool_gate()
    {
        using var fixture = new OfflinePrintEngineTests.PrintFixture();
        var sends = 0;
        var engine = fixture.Engine((_, _) => { sends++; return null; });
        await fixture.Gate.WaitAsync();
        var print = engine.TestPrintAsync(TestRequest(), default);
        Assert.False(print.IsCompleted);
        Assert.Equal(0, sends);
        fixture.Gate.Release();
        await print;
        Assert.Equal(1, sends);
    }

    static LocalTestPrintRequest TestRequest() => new(Guid.NewGuid().ToString("N"), new());

    sealed class Fixture : IAsyncDisposable
    {
        internal OfflinePrintEngineTests.PrintFixture Print { get; } = new();
        internal LocalPrinterSnapshot Snapshot = new("TSC TE210", true,
            [new("TSC TE210", true, true), new("Microsoft Print to PDF", false, false)]);
        internal int Sends, Selections;
        internal string Token = "";
        internal LocalCloudRegistration? Registration;
        internal Exception? CloudError;
        readonly HttpClient client;
        readonly LocalPrintServer server;
        readonly CancellationTokenSource stopping = new();
        readonly Task running;

        internal Fixture()
        {
            var engine = new OfflinePrintEngine(Print.Directory, () => Snapshot.QueueName, (_, _) => { Sends++; return null; }, Print.Gate);
            server = new(Website, Print.Directory, () => Snapshot.QueueName, "Kitchen PC", "1.8.0", engine, _ => { }, port: 0,
                getPrinterSnapshot: () => Snapshot,
                selectPrinter: (queue, _) => { Snapshot = Snapshot with { QueueName = queue, PrinterAvailable = true }; Selections++; return Task.CompletedTask; },
                registerCloud: (registration, _) => { if (CloudError is not null) throw CloudError; Registration = registration; return Task.CompletedTask; });
            running = server.RunAsync(stopping.Token);
            client = new(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{server.Port}"), Timeout = TimeSpan.FromSeconds(5) };
        }

        internal async Task<JsonElement> SessionAsync()
        {
            var response = await SendAsync(HttpMethod.Get, "/v1/session");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var session = await response.Content.ReadFromJsonAsync<JsonElement>();
            Token = session.GetProperty("token").GetString()!;
            return session;
        }

        internal Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? token = null, string origin = Website)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Origin", origin);
            if (token is not null) request.Headers.Add("X-Kivra-Local-Token", token);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, OfflinePrintEngine.JsonOptions), Encoding.UTF8, "application/json");
            return client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            stopping.Cancel();
            server.Dispose();
            await running;
            client.Dispose();
            stopping.Dispose();
            Print.Dispose();
        }
    }
}
