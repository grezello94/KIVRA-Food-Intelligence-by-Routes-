using Kivra.Domain;
using Kivra.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kivra.Tests;

public sealed class PrinterOperationsTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("revoked")]
    [InlineData("inactive")]
    [InlineData("wrong-platform")]
    public async Task Unpaired_bridge_provides_pairing_recovery_without_queuing_a_test_label(string deviceState)
    {
        await using var fixture = await PrinterFixture.CreateAsync();
        if (deviceState != "missing")
        {
            var device = fixture.Device(DateTimeOffset.UtcNow);
            if (deviceState == "revoked") device.RevokedAt = DateTimeOffset.UtcNow;
            if (deviceState == "inactive") device.Active = false;
            if (deviceState == "wrong-platform") device.Platform = fixture.Platform == "windows" ? "android" : "windows";
            fixture.Db.Add(device);
            await fixture.Db.SaveChangesAsync();
        }

        var connection = await fixture.Operations.TestAsync(fixture.PrinterId, CancellationToken.None);
        var print = await fixture.Operations.TestPrintAsync(fixture.PrinterId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(connection, print);
        Assert.False(connection.Success);
        Assert.Contains($"No {fixture.PlatformName} Print Bridge is paired", connection.Error);
        Assert.Contains("Settings > Print Bridge", connection.Error);
        Assert.Contains("pairing code", connection.Error);
        Assert.Empty(await fixture.Db.PrintJobs.ToListAsync());
        Assert.Empty(await fixture.Db.Labels.ToListAsync());
        Assert.Empty(fixture.Printer.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paired_bridge_without_a_recent_heartbeat_provides_connection_and_pairing_recovery(bool previouslySeen)
    {
        await using var fixture = await PrinterFixture.CreateAsync();
        fixture.Db.Add(fixture.Device(previouslySeen ? DateTimeOffset.UtcNow.AddMinutes(-2) : null));
        await fixture.Db.SaveChangesAsync();

        var connection = await fixture.Operations.TestAsync(fixture.PrinterId, CancellationToken.None);
        var print = await fixture.Operations.TestPrintAsync(fixture.PrinterId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(connection, print);
        Assert.False(connection.Success);
        Assert.Contains($"No {fixture.PlatformName} Print Bridge is online", connection.Error);
        Assert.Contains("printer and internet connection", connection.Error);
        Assert.Contains("pair it again", connection.Error);
        Assert.Empty(await fixture.Db.PrintJobs.ToListAsync());
        Assert.Empty(await fixture.Db.Labels.ToListAsync());
        Assert.Empty(fixture.Printer.Commands);
    }

    [Fact]
    public async Task Recent_active_bridge_accepts_one_queued_test_label_without_direct_printing()
    {
        await using var fixture = await PrinterFixture.CreateAsync();
        fixture.Db.Add(fixture.Device(DateTimeOffset.UtcNow.AddMinutes(-2)));
        var online = fixture.Device(DateTimeOffset.UtcNow);
        online.Platform = fixture.Platform.ToUpperInvariant();
        fixture.Db.Add(online);
        await fixture.Db.SaveChangesAsync();
        var userId = Guid.NewGuid();

        var connection = await fixture.Operations.TestAsync(fixture.PrinterId, CancellationToken.None);
        Assert.True(connection.Success);
        Assert.Empty(await fixture.Db.PrintJobs.ToListAsync());

        var print = await fixture.Operations.TestPrintAsync(fixture.PrinterId, userId, CancellationToken.None);

        Assert.True(print.Success);
        var job = await fixture.Db.PrintJobs.Include(x => x.Label).SingleAsync();
        Assert.Equal(PrintJobStatus.Queued, job.Status);
        Assert.Equal(userId, job.RequestedByUserId);
        Assert.Equal(fixture.PrinterId, job.PrinterId);
        Assert.Contains("KIVRA TEST LABEL", job.Payload);
        Assert.Equal(LabelStatus.Cancelled, job.Label!.CurrentStatus);
        Assert.Single(await fixture.Db.AuditLogs.Where(x => x.Action == "PrinterTestQueued").ToListAsync());
        Assert.Empty(fixture.Printer.Commands);
    }

    private sealed class PrinterFixture(SqliteConnection connection, KivraDbContext db, Guid printerId) : IAsyncDisposable
    {
        public KivraDbContext Db { get; } = db;
        public Guid PrinterId { get; } = printerId;
        public string Platform { get; } = OperatingSystem.IsWindows() ? "android" : "windows";
        public string PlatformName => Platform == "windows" ? "Windows" : "Android";
        public FakeLabelPrinter Printer { get; } = new();
        public PrinterOperations Operations => new(Db, [Printer], new TsplGenerator(), TimeZoneInfo.Utc);

        public BridgeDevice Device(DateTimeOffset? lastSeenAt) => new()
        {
            Name = "Kitchen bridge", TokenHash = Guid.NewGuid().ToString("N"),
            Platform = Platform, LastSeenAt = lastSeenAt
        };

        public static async Task<PrinterFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new KivraDbContext(new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            // Windows hosts use their local USB queues directly; a cloud Linux
            // host routes USB printers through the Windows bridge instead.
            var printer = new Printer
            {
                Name = "Kitchen Printer", Model = "TSC TE210",
                Driver = OperatingSystem.IsWindows() ? "AndroidBridge" : "TscTsplUsb"
            };
            db.Add(printer);
            await db.SaveChangesAsync();
            return new PrinterFixture(connection, db, printer.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
