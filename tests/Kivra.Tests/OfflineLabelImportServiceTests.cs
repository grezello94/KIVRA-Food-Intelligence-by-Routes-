using Kivra.Application;
using Kivra.Domain;
using Kivra.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kivra.Tests;

public sealed class OfflineLabelImportServiceTests
{
    [Fact]
    public async Task Imports_exact_printed_snapshot_once_without_creating_another_print_dispatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request();
        // Configuration changed while this device was offline.
        var item = await fixture.Db.Items.SingleAsync();
        item.Name = "New item name";
        item.ShelfLifeValue = 99;
        item.Active = false;
        (await fixture.Db.StorageLocations.SingleAsync()).Active = false;
        (await fixture.Db.Printers.SingleAsync()).Enabled = false;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.ImportAsync(request, fixture.UserId, CancellationToken.None);
        var repeated = await fixture.Service.ImportAsync(request, fixture.UserId, CancellationToken.None);

        Assert.Equal(PrintJobStatus.Printed, result.Status);
        Assert.True(repeated.Idempotent);
        Assert.Equal(result.PrintJobId, repeated.PrintJobId);
        var label = await fixture.Db.Labels.SingleAsync();
        Assert.Equal(request.LabelId, label.Id);
        Assert.Equal("Original food name", label.ItemNameSnapshot);
        Assert.Equal(request.OperationalDateTime.ToUniversalTime(), label.OperationalDateTime);
        Assert.Equal(request.ExpiryDateTime.ToUniversalTime(), label.ExpiryDateTime);
        Assert.Equal("2 Days", label.ShelfLifeRuleSnapshot);
        Assert.Equal(request.PrintedAt.ToUniversalTime(), label.LastSuccessfulPrintAt);
        Assert.Equal(1, label.SuccessfulPrintCount);
        var job = await fixture.Db.PrintJobs.SingleAsync();
        Assert.Equal(PrintJobStatus.Printed, job.Status);
        Assert.Null(job.BridgeDeviceId);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Equal(request.Payload, job.Payload);
        Assert.Single(await fixture.Db.AuditLogs.Where(x => x.Action == "OfflineLabelPrinted").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reprints_existing_native_or_offline_label_without_new_label_or_pre_2am_rollover(bool native)
    {
        await using var fixture = await Fixture.CreateAsync();
        var operational = new DateTimeOffset(2026, 10, 16, 1, 30, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();
        var original = fixture.Request() with { OperationalDateTime = operational, ExpiryDateTime = operational.AddDays(2) };
        await fixture.Service.ImportAsync(original, fixture.UserId, CancellationToken.None);
        var originalLabel = await fixture.Db.Labels.SingleAsync();
        if (native)
        {
            originalLabel.LabelCode = "RL-261016-0001";
            await fixture.Db.SaveChangesAsync();
        }
        var createdAt = originalLabel.CreatedAt;
        var request = original with
        {
            IdempotencyKey = Guid.NewGuid().ToString("N"), LabelCode = originalLabel.LabelCode,
            IsReprint = true, PrintedAt = DateTimeOffset.UtcNow
        };

        var result = await fixture.Service.ImportAsync(request, fixture.UserId, CancellationToken.None);
        var repeated = await fixture.Service.ImportAsync(request, fixture.UserId, CancellationToken.None);

        Assert.Equal(PrintJobStatus.Printed, result.Status);
        Assert.True(repeated.Idempotent);
        Assert.Equal(result.PrintJobId, repeated.PrintJobId);
        var label = await fixture.Db.Labels.SingleAsync();
        Assert.Equal(original.OperationalDateTime, label.OperationalDateTime);
        Assert.Equal(original.ExpiryDateTime, label.ExpiryDateTime);
        Assert.Equal(createdAt, label.CreatedAt);
        Assert.Equal(2, label.SuccessfulPrintCount);
        Assert.Equal(request.PrintedAt.ToUniversalTime(), label.LastSuccessfulPrintAt);
        Assert.Equal(2, await fixture.Db.PrintJobs.CountAsync());
        Assert.Single(await fixture.Db.PrintJobs.Where(x => x.IsReprint && x.Status == PrintJobStatus.Printed).ToListAsync());
        Assert.Single(await fixture.Db.AuditLogs.Where(x => x.Action == "OfflineLabelReprinted").ToListAsync());
    }

    [Theory]
    [InlineData("name")]
    [InlineData("category")]
    [InlineData("classification")]
    [InlineData("terminology")]
    [InlineData("storageName")]
    [InlineData("rule")]
    [InlineData("operational")]
    [InlineData("printer")]
    public async Task Rejects_reprints_whose_snapshot_changes_the_original_label(string changed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Request();
        await fixture.Service.ImportAsync(original, fixture.UserId, CancellationToken.None);
        var printer = new Printer { Name = "Other printer", Model = "TSC", Driver = "TscTsplUsb", UsbQueueName = "Other printer" };
        fixture.Db.Printers.Add(printer);
        await fixture.Db.SaveChangesAsync();
        var request = original with { IdempotencyKey = Guid.NewGuid().ToString("N"), IsReprint = true, PrintedAt = DateTimeOffset.UtcNow };
        request = changed switch
        {
            "name" => request with { ItemNameSnapshot = "Changed food" },
            "category" => request with { CategorySnapshot = "Changed category" },
            "classification" => request with { ClassificationSnapshot = Classification.NonVeg },
            "terminology" => request with { DateTerminologySnapshot = "CUT ON" },
            "storageName" => request with { StorageLocationSnapshot = "Changed chiller" },
            "rule" => request with { ShelfLifeValue = 48, ShelfLifeUnit = ShelfLifeUnit.Hours },
            "operational" => request with { OperationalDateTime = request.OperationalDateTime.AddHours(-1), ExpiryDateTime = request.ExpiryDateTime.AddHours(-1) },
            _ => request with { PrinterId = printer.Id }
        };

        await Assert.ThrowsAsync<IdempotencyConflictException>(() => fixture.Service.ImportAsync(request, fixture.UserId, CancellationToken.None));

        Assert.Single(await fixture.Db.Labels.ToListAsync());
        Assert.Single(await fixture.Db.PrintJobs.ToListAsync());
        Assert.Equal(1, (await fixture.Db.Labels.SingleAsync()).SuccessfulPrintCount);
    }

    [Fact]
    public async Task Reprint_requires_original_label_and_preserves_latest_success_time_for_late_sync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Request();
        var reprint = original with { IdempotencyKey = Guid.NewGuid().ToString("N"), IsReprint = true, PrintedAt = DateTimeOffset.UtcNow.AddMinutes(-2) };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ImportAsync(reprint, fixture.UserId, CancellationToken.None));
        await fixture.Service.ImportAsync(original, fixture.UserId, CancellationToken.None);
        await fixture.Service.ImportAsync(reprint, fixture.UserId, CancellationToken.None);
        var label = await fixture.Db.Labels.SingleAsync();
        Assert.Equal(original.PrintedAt, label.LastSuccessfulPrintAt);
        Assert.Equal(2, label.SuccessfulPrintCount);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("code")]
    [InlineData("future")]
    [InlineData("shelfLife")]
    [InlineData("control")]
    [InlineData("unknownItem")]
    public async Task Rejects_invalid_or_unknown_print_snapshots_without_storing_jobs(string invalid)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request();
        request = invalid switch
        {
            "expiry" => request with { ExpiryDateTime = request.ExpiryDateTime.AddHours(1) },
            "code" => request with { LabelCode = "OFF-000000000000" },
            "future" => request with { PrintedAt = DateTimeOffset.UtcNow.AddHours(1) },
            "shelfLife" => request with { ShelfLifeValue = 0 },
            "control" => request with { ItemNameSnapshot = "Food\nINJECTED COMMAND" },
            _ => request with { ItemId = Guid.NewGuid() }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ImportAsync(request, fixture.UserId, CancellationToken.None));

        Assert.Empty(await fixture.Db.Labels.ToListAsync());
        Assert.Empty(await fixture.Db.PrintJobs.ToListAsync());
    }

    [Fact]
    public async Task Rejects_cross_user_or_cross_label_reuse_of_print_key()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request();
        await fixture.Service.ImportAsync(request, fixture.UserId, CancellationToken.None);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => fixture.Service.ImportAsync(request, Guid.NewGuid(), CancellationToken.None));
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => fixture.Service.ImportAsync(request with { LabelId = id, LabelCode = $"OFF-{id:N}"[..16] }, fixture.UserId, CancellationToken.None));
        Assert.Single(await fixture.Db.Labels.ToListAsync());
        Assert.Single(await fixture.Db.PrintJobs.ToListAsync());
    }

    sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required KivraDbContext Db { get; init; }
        public required OfflineLabelImportService Service { get; init; }
        public required Guid UserId { get; init; }
        public required Guid ItemId { get; init; }
        public required Guid PrinterId { get; init; }
        public required Guid StorageLocationId { get; init; }

        public OfflineLabelImportRequest Request()
        {
            var id = Guid.NewGuid();
            var operational = DateTimeOffset.UtcNow.AddHours(-2);
            return new(Guid.NewGuid().ToString("N"), id, $"OFF-{id:N}"[..16], ItemId, PrinterId, StorageLocationId,
                operational, operational.AddDays(2), "Original food name", "Prepared Food", Classification.Veg,
                "PREPARED", 2, ShelfLifeUnit.Days, "Chiller 1", DateTimeOffset.UtcNow.AddMinutes(-1), "SIZE 50 mm,30 mm\r\nPRINT 1,1\r\n");
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new KivraDbContext(new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var category = new Category { Name = "Prepared Food" };
            var location = new StorageLocation { Name = "Chiller 1" };
            var user = new User { DisplayName = "Kitchen", PinHash = "not-used", Role = UserRole.KitchenStaff };
            var item = new Item { Name = "Original food name", CategoryId = category.Id, DateTerminology = "PREPARED", LabelType = "Food", ShelfLifeValue = 2, ShelfLifeUnit = ShelfLifeUnit.Days, DefaultStorageLocationId = location.Id };
            var printer = new Printer { Name = "Kitchen USB", Model = "TSC TA220", Driver = "TscTsplUsb", UsbQueueName = "TSC TA220" };
            db.AddRange(category, location, user, item, printer);
            await db.SaveChangesAsync();
            return new Fixture { Connection = connection, Db = db, UserId = user.Id, ItemId = item.Id, PrinterId = printer.Id,
                StorageLocationId = location.Id, Service = new OfflineLabelImportService(db, new ExpiryCalculator(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")) };
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
}
