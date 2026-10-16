using Kivra.Application;
using Kivra.Domain;
using Kivra.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Kivra.Tests;

public sealed class LabelPrintServiceTests
{
    [Fact]
    public async Task Prints_without_photo_or_storage_roundtrip_and_commits_sending_before_io()
    {
        await using var fixture = await PrintFixture.CreateAsync();
        var request = fixture.Request();

        var result = await fixture.Service.PrintAsync(request, CancellationToken.None);
        var repeated = await fixture.Service.PrintAsync(request, CancellationToken.None);

        Assert.Equal(PrintJobStatus.Printed, result.Status);
        Assert.True(repeated.Idempotent);
        Assert.Equal(result.PrintJobId, repeated.PrintJobId);
        Assert.Single(fixture.Printer.Commands);
        Assert.Equal(new[] { (PrintJobStatus.Sending, 0), (PrintJobStatus.Printed, 1) }, fixture.Saves.JobStates);
        Assert.DoesNotContain(fixture.Commands, x => x.Contains("\"ImageDataUrl\""));
        Assert.DoesNotContain(fixture.Commands, x => x.Contains("FROM \"StorageLocations\""));
        Assert.Single(await fixture.Db.Labels.ToListAsync());
        Assert.Equal(2, await fixture.Db.AuditLogs.CountAsync());
        Assert.Equal("Chiller 1", (await fixture.Db.Labels.SingleAsync()).StorageLocationSnapshot);
    }

    [Fact]
    public async Task Bridge_reprint_commits_job_and_audit_together_without_printing_or_duplicate_jobs()
    {
        await using var fixture = await PrintFixture.CreateAsync("AndroidBridge");
        var created = await fixture.Service.PrintAsync(fixture.Request(), CancellationToken.None);
        fixture.Saves.JobStates.Clear();
        var key = Guid.NewGuid().ToString("N");

        var result = await fixture.Service.ReprintAsync(created.LabelId, Guid.NewGuid(), key, CancellationToken.None);
        var repeated = await fixture.Service.ReprintAsync(created.LabelId, Guid.NewGuid(), key, CancellationToken.None);

        Assert.Equal(PrintJobStatus.Queued, result.Status);
        Assert.True(repeated.Idempotent);
        Assert.Equal(result.PrintJobId, repeated.PrintJobId);
        Assert.Empty(fixture.Printer.Commands);
        Assert.Equal(new[] { (PrintJobStatus.Queued, 0) }, fixture.Saves.JobStates);
        Assert.Equal(2, await fixture.Db.PrintJobs.CountAsync());
        Assert.Single(await fixture.Db.AuditLogs.Where(x => x.Action == "LabelReprintQueued").ToListAsync());
    }

    [Fact]
    public async Task Native_reprint_persists_sending_before_io_and_reuses_idempotent_result()
    {
        await using var fixture = await PrintFixture.CreateAsync();
        var created = await fixture.Service.PrintAsync(fixture.Request(), CancellationToken.None);
        fixture.Saves.JobStates.Clear();
        var key = Guid.NewGuid().ToString("N");

        var result = await fixture.Service.ReprintAsync(created.LabelId, Guid.NewGuid(), key, CancellationToken.None);
        var repeated = await fixture.Service.ReprintAsync(created.LabelId, Guid.NewGuid(), key, CancellationToken.None);

        Assert.Equal(PrintJobStatus.Printed, result.Status);
        Assert.True(repeated.Idempotent);
        Assert.Equal(new[] { (PrintJobStatus.Sending, 1), (PrintJobStatus.Printed, 2) }, fixture.Saves.JobStates);
        Assert.Equal(2, fixture.Printer.Commands.Count);
        Assert.Equal(2, (await fixture.Db.Labels.SingleAsync()).SuccessfulPrintCount);
        Assert.Single(await fixture.Db.AuditLogs.Where(x => x.Action == "LabelReprinted").ToListAsync());
    }

    [Fact]
    public async Task Sqlite_day_sequence_preserves_offsets_and_fractional_boundary_timestamps()
    {
        await using var fixture = await PrintFixture.CreateAsync();
        var local = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.FromHours(5.5));
        var start = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();
        var times = new[] { start.AddDays(-20), start.AddTicks(-1), start, start.AddHours(1).ToOffset(TimeSpan.FromHours(-4)), start.AddDays(1).AddTicks(-1), start.AddDays(1), start.AddDays(20) };
        for (var index = 0; index < times.Length; index++)
            fixture.Db.Add(new Label { LabelCode = $"history-{index}", ItemId = fixture.ItemId, PrinterId = fixture.PrinterId, ItemNameSnapshot = "Cut Carrot", CategorySnapshot = "Cut Vegetables", DateTerminologySnapshot = "CUT ON", ShelfLifeRuleSnapshot = "2 Days", StorageLocationSnapshot = "Chiller 1", OperationalDateTime = times[index], ExpiryDateTime = times[index].AddDays(2) });
        await fixture.Db.SaveChangesAsync();
        fixture.Commands.Clear();

        var result = await fixture.Service.PrintAsync(fixture.Request() with { OperationalDateTime = local }, CancellationToken.None);

        Assert.Equal("RL-261017-0004", result.LabelCode);
        Assert.Contains(fixture.Commands, x => x.Contains("julianday(\"OperationalDateTime\")"));
    }

    [Fact]
    public async Task Sqlite_compatibility_creates_print_indexes_and_daily_query_uses_expression_index()
    {
        await using var fixture = await PrintFixture.CreateAsync();
        await SchemaCompatibility.EnsureAsync(fixture.Db);
        await SchemaCompatibility.EnsureAsync(fixture.Db);
        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT \"OperationalDateTime\" FROM \"Labels\" WHERE julianday(\"OperationalDateTime\") >= julianday('2026-10-16 18:30:00+00:00') AND julianday(\"OperationalDateTime\") <= julianday('2026-10-17 18:30:00+00:00')";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Contains("IX_Labels_OperationalJulianDay", reader.GetString(3));
        await reader.DisposeAsync();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name IN ('IX_Labels_OperationalDateTime', 'IX_PrintJobs_Status_RequestedAt')";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Stores_label_timestamps_as_utc_for_postgres_compatibility()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).Options;
        await using var db = new KivraDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var category = new Category { Name = "Cut Vegetables" };
        var location = new StorageLocation { Name = "Chiller 1" };
        var item = new Item { Name = "Cut Carrot", Category = category, CategoryId = category.Id, Classification = Classification.Veg, LabelType = "Food", DateTerminology = "CUT ON", ShelfLifeValue = 2, ShelfLifeUnit = ShelfLifeUnit.Days, DefaultStorageLocation = location, DefaultStorageLocationId = location.Id };
        var printer = new Printer { Name = "Fake", Model = "Fake", Driver = "Fake" };
        db.AddRange(category, location, item, printer);
        await db.SaveChangesAsync();
        var service = new LabelPrintService(db, new ExpiryCalculator(), new LabelCodeGenerator(), new TsplGenerator(), [new FakeLabelPrinter()], TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var localTime = new DateTimeOffset(2026, 9, 28, 23, 27, 0, TimeSpan.FromHours(5.5));

        await service.PrintAsync(new(item.Id, printer.Id, Guid.NewGuid(), location.Id, localTime, null, null, Guid.NewGuid().ToString("N")), CancellationToken.None);

        var label = await db.Labels.SingleAsync();
        Assert.Equal(TimeSpan.Zero, label.OperationalDateTime.Offset);
        Assert.Equal(TimeSpan.Zero, label.ExpiryDateTime.Offset);
        Assert.Equal(localTime.UtcDateTime, label.OperationalDateTime.UtcDateTime);
    }

    [Fact]
    public async Task Rejects_an_inactive_default_storage_location()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).Options;
        await using var db = new KivraDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var category = new Category { Name = "Prepared Food" };
        var location = new StorageLocation { Name = "Chiller 1", Active = false };
        var item = new Item
        {
            Name = "Test Item",
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
        var printer = new Printer { Name = "Fake", Model = "Fake", Driver = "Fake" };
        db.AddRange(category, location, item, printer);
        await db.SaveChangesAsync();

        var service = new LabelPrintService(
            db,
            new ExpiryCalculator(),
            new LabelCodeGenerator(),
            new TsplGenerator(),
            [new FakeLabelPrinter()],
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        var request = new PrintLabelRequest(item.Id, printer.Id, Guid.NewGuid(), null, null, null, null, Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrintAsync(request, CancellationToken.None));

        Assert.Equal("Select an active storage location before printing.", error.Message);
    }

    sealed class PrintSaveObserver(FakeLabelPrinter printer) : SaveChangesInterceptor
    {
        public List<(PrintJobStatus Status, int Sent)> JobStates { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<PrintJob>().Where(x => x.State is EntityState.Added or EntityState.Modified))
                JobStates.Add((entry.Entity.Status, printer.Commands.Count));
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    sealed class PrintFixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required KivraDbContext Db { get; init; }
        public required LabelPrintService Service { get; init; }
        public required FakeLabelPrinter Printer { get; init; }
        public required PrintSaveObserver Saves { get; init; }
        public required List<string> Commands { get; init; }
        public required Guid ItemId { get; init; }
        public required Guid PrinterId { get; init; }
        public PrintLabelRequest Request() => new(ItemId, PrinterId, Guid.NewGuid(), null, null, null, null, Guid.NewGuid().ToString("N"));

        public static async Task<PrintFixture> CreateAsync(string driver = "Fake")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var printer = new FakeLabelPrinter();
            var observer = new PrintSaveObserver(printer);
            var commands = new List<string>();
            var options = new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).AddInterceptors(observer)
                .LogTo(commands.Add, [RelationalEventId.CommandExecuted]).Options;
            var db = new KivraDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var category = new Category { Name = "Cut Vegetables" };
            var location = new StorageLocation { Name = "Chiller 1" };
            var item = new Item { Name = "Cut Carrot", ImageDataUrl = "data:image/webp;base64," + new string('a', 1024 * 1024), Category = category, CategoryId = category.Id, Classification = Classification.Veg, LabelType = "Food", DateTerminology = "CUT ON", ShelfLifeValue = 2, ShelfLifeUnit = ShelfLifeUnit.Days, DefaultStorageLocation = location, DefaultStorageLocationId = location.Id };
            var configuration = new Printer { Name = "Test", Model = "Test", Driver = driver };
            db.AddRange(category, location, item, configuration);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            commands.Clear();
            return new PrintFixture { Connection = connection, Db = db, Printer = printer, Saves = observer, Commands = commands, ItemId = item.Id, PrinterId = configuration.Id, Service = new LabelPrintService(db, new ExpiryCalculator(), new LabelCodeGenerator(), new TsplGenerator(), [printer], TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")) };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
