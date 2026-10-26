using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kivra.WindowsBridge;
using Xunit;

namespace Kivra.Tests;

public sealed class LocalQueueBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_binding_rejects_without_a_new_receipt_or_spool(bool testPrint)
    {
        await using var fixture = new Fixture();
        await Assert.ThrowsAsync<LocalPrintConflictException>(() => Print(fixture, testPrint, "TSC Previous"));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.json", SearchOption.AllDirectories));
        Assert.Equal(0, fixture.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queue_changed_while_waiting_for_the_spool_gate_is_rechecked(bool testPrint)
    {
        await using var fixture = new Fixture();
        await fixture.Gate.WaitAsync();
        var printing = Print(fixture, testPrint, fixture.Queue);
        Assert.False(printing.IsCompleted);
        fixture.Queue = "TSC Prep";
        fixture.Gate.Release();
        await Assert.ThrowsAsync<LocalPrintConflictException>(() => printing);
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.json", SearchOption.AllDirectories));
        Assert.Equal(0, fixture.Sends);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Existing_receipts_recover_after_queue_disappears_without_rehashing_or_spooling(bool testPrint, bool interrupted)
    {
        await using var fixture = new Fixture();
        // A receipt created without queue binding represents the previous schema.
        var first = await Print(fixture, testPrint, null);
        if (interrupted)
        {
            var file = Assert.Single(Directory.GetFiles(fixture.Directory, "*.json", SearchOption.AllDirectories));
            var receipt = JsonNode.Parse(File.ReadAllText(file))!;
            receipt["status"] = "Sending";
            receipt["printedAt"] = null;
            File.WriteAllText(file, receipt.ToJsonString(OfflinePrintEngine.JsonOptions));
        }
        fixture.Queue = "";
        var recovered = await Print(fixture, testPrint, "TSC Kitchen");
        Assert.Equal(first.JobId, recovered.JobId);
        Assert.True(recovered.Idempotent);
        Assert.Equal(interrupted ? "Uncertain" : "Printed", recovered.Status);
        Assert.Equal(1, fixture.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_binding_rejects_a_stale_browser_and_recovers_an_existing_receipt(bool testPrint)
    {
        await using var fixture = new Fixture(startServer: true);
        var body = Body(fixture, testPrint);
        body["printer"]!["usbQueueName"] = "TSC Previous";
        using var stale = await fixture.Post(testPrint, body);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.json", SearchOption.AllDirectories));
        Assert.Equal(0, fixture.Sends);

        body["printer"]!["usbQueueName"] = "tsc kitchen";
        using var printed = await fixture.Post(testPrint, body);
        Assert.Equal(HttpStatusCode.OK, printed.StatusCode);
        var first = await printed.Content.ReadFromJsonAsync<JsonElement>();
        fixture.Queue = "";
        using var recovered = await fixture.Post(testPrint, body);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var receipt = await recovered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(receipt.GetProperty("idempotent").GetBoolean());
        Assert.Equal(first.GetProperty("printJobId").GetGuid(), receipt.GetProperty("printJobId").GetGuid());
        Assert.Equal(1, fixture.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_without_binding_captures_selection_before_waiting_for_the_gate(bool testPrint)
    {
        await using var fixture = new Fixture(startServer: true);
        await fixture.Gate.WaitAsync();
        fixture.SnapshotRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = fixture.Post(testPrint, Body(fixture, testPrint));
        await fixture.SnapshotRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Queue = "TSC Prep";
        fixture.Gate.Release();
        using var rejected = await response;
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.json", SearchOption.AllDirectories));
        Assert.Equal(0, fixture.Sends);
    }

    static JsonNode Body(Fixture fixture, bool testPrint) => testPrint
        ? JsonSerializer.SerializeToNode(fixture.TestRequest, OfflinePrintEngine.JsonOptions)!
        : JsonSerializer.SerializeToNode(fixture.FoodRequest, OfflinePrintEngine.JsonOptions)!;

    static async Task<(Guid JobId, bool Idempotent, string Status)> Print(Fixture fixture, bool testPrint, string? expected)
    {
        if (testPrint)
        {
            var result = await fixture.Engine.TestPrintAsync(fixture.TestRequest, default, expected);
            return (result.PrintJobId, result.Idempotent, result.Status);
        }
        var food = await fixture.Engine.PrintAsync(fixture.FoodRequest, default, expected);
        return (food.PrintJobId, food.Idempotent, food.Status);
    }

    sealed class Fixture : IAsyncDisposable
    {
        const string Website = "https://kivra.example.test";
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "kivra-queue-binding-" + Guid.NewGuid().ToString("N"));
        internal string Queue = "TSC Kitchen";
        internal int Sends;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal OfflinePrintRequest FoodRequest { get; } = OfflinePrintEngineTests.Request();
        internal LocalTestPrintRequest TestRequest { get; } = new(Guid.NewGuid().ToString("N"), new());
        internal OfflinePrintEngine Engine { get; }
        internal TaskCompletionSource? SnapshotRead;
        readonly CancellationTokenSource stopping = new();
        readonly LocalPrintServer? server;
        readonly Task? running;
        readonly HttpClient? client;
        readonly string? localToken;

        internal Fixture(bool startServer = false)
        {
            Engine = new(Directory, () => Queue, (_, _) => { Interlocked.Increment(ref Sends); return null; }, Gate);
            if (!startServer) return;
            server = new(Website, Directory, () => Queue, "Kitchen PC", "1.8.0", Engine, _ => { }, port: 0,
                getPrinterSnapshot: () =>
                {
                    var selected = Queue;
                    var snapshot = new LocalPrinterSnapshot(selected, selected.Length != 0,
                        selected.Length == 0 ? [] : [new(selected, true, true)]);
                    SnapshotRead?.TrySetResult();
                    return snapshot;
                });
            running = server.RunAsync(stopping.Token);
            localToken = File.ReadAllText(Path.Combine(Directory, "local-print-token"));
            client = new(new HttpClientHandler { UseProxy = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"), Timeout = TimeSpan.FromSeconds(10) };
        }

        internal async Task<HttpResponseMessage> Post(bool testPrint, JsonNode body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, testPrint ? "/v1/test-print" : "/v1/print");
            request.Headers.Add("Origin", Website);
            request.Headers.Add("X-Kivra-Local-Token", localToken);
            request.Content = new StringContent(body.ToJsonString(OfflinePrintEngine.JsonOptions), Encoding.UTF8, "application/json");
            return await client!.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            stopping.Cancel();
            server?.Dispose();
            if (running is not null) await running;
            client?.Dispose();
            stopping.Dispose();
            Gate.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
