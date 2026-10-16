using Kivra.Application;
using Kivra.Domain;
using Kivra.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kivra.Tests;

public sealed class AndroidBridgeServiceTests
{
    [Fact]
    public async Task Paired_bridge_claims_and_completes_a_queued_label()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).Options;
        await using var db = new KivraDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var category = new Category { Name = "Prepared Food" };
        var location = new StorageLocation { Name = "Chiller 1" };
        var item = new Item
        {
            Name = "Bridge Test Item",
            Category = category,
            CategoryId = category.Id,
            Classification = Classification.Veg,
            LabelType = "Food",
            DateTerminology = "PREPARED",
            ShelfLifeValue = 1,
            ShelfLifeUnit = ShelfLifeUnit.Days,
            DefaultStorageLocation = location,
            DefaultStorageLocationId = location.Id
        };
        var printer = new Printer
        {
            Name = "Kitchen Bridge Printer",
            Model = "TSC TE210",
            Driver = "AndroidBridge",
            IpAddress = "192.168.1.50",
            TcpPort = 9100
        };
        db.AddRange(category, location, item, printer);
        await db.SaveChangesAsync();

        var printService = new LabelPrintService(
            db,
            new ExpiryCalculator(),
            new LabelCodeGenerator(),
            new TsplGenerator(),
            [new FakeLabelPrinter()],
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var userId = Guid.NewGuid();
        var result = await printService.PrintAsync(
            new PrintLabelRequest(item.Id, printer.Id, userId, location.Id, null, null, null, Guid.NewGuid().ToString("N")),
            CancellationToken.None);

        Assert.Equal(PrintJobStatus.Queued, result.Status);

        var bridge = new AndroidBridgeService(db);
        var pairing = await bridge.CreatePairingCodeAsync(userId, CancellationToken.None);
        var paired = await bridge.PairAsync(pairing.Code, "Kitchen phone", "1.0.0", "Android", CancellationToken.None);
        Assert.NotNull(paired);

        var device = await bridge.AuthenticateAsync(paired!.Token, CancellationToken.None);
        Assert.NotNull(device);
        var claimed = await bridge.ClaimAsync(device!, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(result.PrintJobId, claimed!.JobId);
        Assert.Equal("192.168.1.50", claimed.IpAddress);
        Assert.Contains("PRINT 1,1", claimed.Payload);

        var leasedJob = await db.PrintJobs.FindAsync(claimed.JobId);
        leasedJob!.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        var secondCode = await bridge.CreatePairingCodeAsync(userId, CancellationToken.None);
        var secondPair = await bridge.PairAsync(secondCode.Code, "Backup phone", "1.0.0", "Android", CancellationToken.None);
        var secondDevice = await bridge.AuthenticateAsync(secondPair!.Token, CancellationToken.None);
        Assert.Null(await bridge.ClaimAsync(secondDevice!, CancellationToken.None));

        var reclaimed = await bridge.ClaimAsync(device, CancellationToken.None);
        Assert.Equal(claimed.JobId, reclaimed!.JobId);
        Assert.True(await bridge.CompleteAsync(device, reclaimed.JobId, true, null, CancellationToken.None));
        var completed = await db.PrintJobs.FindAsync(claimed.JobId);
        var label = await db.Labels.FindAsync(result.LabelId);
        Assert.Equal(PrintJobStatus.Printed, completed!.Status);
        Assert.Equal(1, label!.SuccessfulPrintCount);
        Assert.NotNull(label.LastSuccessfulPrintAt);
        Assert.True(await bridge.CompleteAsync(device, reclaimed.JobId, true, null, CancellationToken.None));
        Assert.Equal(1, label.SuccessfulPrintCount);
    }

    [Fact]
    public async Task Windows_bridge_claims_oldest_supported_job_without_loading_other_payloads()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).Options;
        await using var db = new KivraDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var windowsPrinter = new Printer { Name = "USB", Model = "TSC", Driver = "TscTsplUsb" };
        var androidPrinter = new Printer { Name = "Android", Model = "TSC", Driver = "AndroidBridge" };
        var disabledPrinter = new Printer { Name = "Disabled", Model = "TSC", Driver = "TscTsplUsb", Enabled = false };
        db.AddRange(windowsPrinter, androidPrinter, disabledPrinter);
        var jobs = Enumerable.Range(0, 80).Select(i => NewJob(windowsPrinter, DateTimeOffset.UtcNow.AddMinutes(-i))).ToArray();
        db.AddRange(jobs);
        db.AddRange(NewJob(androidPrinter, DateTimeOffset.UtcNow.AddHours(-2)), NewJob(disabledPrinter, DateTimeOffset.UtcNow.AddHours(-2)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var bridge = new AndroidBridgeService(db);
        var device = new BridgeDevice { Name = "Kitchen PC", TokenHash = "test", Platform = "windows" };
        var claimed = await bridge.ClaimAsync(device, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(jobs[^1].Id, claimed.JobId);
        Assert.Equal(jobs[^1].Payload, claimed.Payload);
        Assert.Single(db.ChangeTracker.Entries<PrintJob>());
        Assert.Single(db.ChangeTracker.Entries<Label>());
        Assert.Equal(1, await db.PrintJobs.CountAsync(x => x.Status == PrintJobStatus.Sending));
    }

    [Fact]
    public async Task Exhausted_leases_fail_without_reclaiming_another_bridges_job()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).Options;
        await using var db = new KivraDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var printer = new Printer { Name = "USB", Model = "TSC", Driver = "TscTsplUsb" };
        var device = new BridgeDevice { Name = "Kitchen PC", TokenHash = "test", Platform = "windows" };
        db.Add(printer);
        var exhausted = NewJob(printer, DateTimeOffset.UtcNow.AddHours(-1));
        exhausted.Status = PrintJobStatus.Sending;
        exhausted.BridgeDeviceId = device.Id;
        exhausted.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        exhausted.RetryCount = 3;
        var otherBridge = NewJob(printer, DateTimeOffset.UtcNow.AddMinutes(-50));
        otherBridge.Status = PrintJobStatus.Sending;
        otherBridge.BridgeDeviceId = Guid.NewGuid();
        otherBridge.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var queued = NewJob(printer, DateTimeOffset.UtcNow);
        db.AddRange(exhausted, otherBridge, queued);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var claimed = await new AndroidBridgeService(db).ClaimAsync(device, CancellationToken.None);

        Assert.Equal(queued.Id, claimed!.JobId);
        Assert.Equal(PrintJobStatus.Failed, (await db.PrintJobs.FindAsync(exhausted.Id))!.Status);
        var untouched = await db.PrintJobs.FindAsync(otherBridge.Id);
        Assert.Equal(PrintJobStatus.Sending, untouched!.Status);
        Assert.Equal(otherBridge.BridgeDeviceId, untouched.BridgeDeviceId);
    }

    static PrintJob NewJob(Printer printer, DateTimeOffset requestedAt) => new()
    {
        PrinterId = printer.Id,
        RequestedAt = requestedAt,
        Payload = new string('X', 16_384),
        Label = new Label
        {
            LabelCode = Guid.NewGuid().ToString("N"),
            ItemNameSnapshot = "Test label",
            CategorySnapshot = "Prepared food",
            DateTerminologySnapshot = "PREPARED",
            ShelfLifeRuleSnapshot = "1 day",
            StorageLocationSnapshot = "Chiller"
        }
    };

    [Fact]
    public async Task Pairing_code_is_single_use()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).Options;
        await using var db = new KivraDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var bridge = new AndroidBridgeService(db);
        var pairing = await bridge.CreatePairingCodeAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.NotNull(await bridge.PairAsync(pairing.Code, "First phone", "1.0", "Android", CancellationToken.None));
        Assert.Null(await bridge.PairAsync(pairing.Code, "Second phone", "1.0", "Android", CancellationToken.None));
    }
}
