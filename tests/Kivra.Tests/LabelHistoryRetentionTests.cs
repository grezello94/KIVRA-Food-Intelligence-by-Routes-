using Kivra.Application;
using Kivra.Domain;
using Kivra.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kivra.Tests;

public sealed class LabelHistoryRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Retention_is_disabled_by_default()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Add(fixture.Label(Now.AddDays(-30)));
        await fixture.Db.SaveChangesAsync();

        var retention = new LabelHistoryRetention(fixture.Db, new HistoryRetentionOptions());

        Assert.False(retention.Enabled);
        Assert.Equal(0, await retention.DeleteExpiredAsync(Now));
        Assert.Equal(1, await fixture.Db.Labels.CountAsync());
    }

    [Fact]
    public async Task Deletes_only_expiry_strictly_older_than_five_days_with_exact_ticks_and_offsets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var cutoff = Now.AddDays(-5);
        var old = fixture.Label(cutoff.AddTicks(-1));
        var oldOffset = fixture.Label(cutoff.AddHours(-1).ToOffset(TimeSpan.FromHours(5.5)));
        var boundary = fixture.Label(cutoff.ToOffset(TimeSpan.FromHours(-4)));
        var recent = fixture.Label(cutoff.AddTicks(1));
        var active = fixture.Label(Now.AddDays(1), LabelStatus.Active);
        fixture.Db.AddRange(old, oldOffset, boundary, recent, active);
        await fixture.Db.SaveChangesAsync();

        var deleted = await fixture.Retention.DeleteExpiredAsync(Now);
        var remaining = await fixture.Db.Labels.AsNoTracking().Select(x => x.Id).ToListAsync();

        Assert.Equal(2, deleted);
        Assert.DoesNotContain(old.Id, remaining);
        Assert.DoesNotContain(oldOffset.Id, remaining);
        Assert.Contains(boundary.Id, remaining);
        Assert.Contains(recent.Id, remaining);
        Assert.Contains(active.Id, remaining);
        Assert.Equal(0, await fixture.Retention.DeleteExpiredAsync(Now));
    }

    [Fact]
    public async Task Preserves_queued_and_sending_labels_but_removes_finished_jobs_notifications_and_old_labels()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queued = fixture.Label(Now.AddDays(-10));
        var sending = fixture.Label(Now.AddDays(-10));
        var mixed = fixture.Label(Now.AddDays(-10));
        var printed = fixture.Label(Now.AddDays(-10));
        var failed = fixture.Label(Now.AddDays(-10));
        var cancelled = fixture.Label(Now.AddDays(-10));
        fixture.Db.AddRange(queued, sending, mixed, printed, failed, cancelled);
        fixture.Db.AddRange(
            fixture.Job(queued, PrintJobStatus.Queued), fixture.Job(sending, PrintJobStatus.Sending),
            fixture.Job(mixed, PrintJobStatus.Printed), fixture.Job(mixed, PrintJobStatus.Queued),
            fixture.Job(printed, PrintJobStatus.Printed), fixture.Job(failed, PrintJobStatus.Failed),
            fixture.Job(cancelled, PrintJobStatus.Cancelled));
        foreach (var label in new[] { queued, sending, mixed, printed, failed, cancelled })
            fixture.Db.Add(new Notification { LabelId = label.Id, Type = "Expired", Title = "Expired", Message = "Test" });
        await fixture.Db.SaveChangesAsync();

        Assert.Equal(3, await fixture.Retention.DeleteExpiredAsync(Now));

        var ids = await fixture.Db.Labels.AsNoTracking().Select(x => x.Id).ToListAsync();
        Assert.Equal(3, ids.Count);
        Assert.Contains(queued.Id, ids);
        Assert.Contains(sending.Id, ids);
        Assert.Contains(mixed.Id, ids);
        Assert.Equal(4, await fixture.Db.PrintJobs.CountAsync());
        Assert.Equal(3, await fixture.Db.Notifications.CountAsync());
        Assert.Equal(3, await fixture.Db.OfflineMutationReceipts.CountAsync());
        Assert.Equal(1, await fixture.Db.Items.CountAsync());
        Assert.Equal(1, await fixture.Db.Printers.CountAsync());
        Assert.Equal(1, await fixture.Db.Users.CountAsync());
        Assert.Equal(1, await fixture.Db.Categories.CountAsync());
        Assert.Equal(1, await fixture.Db.StorageLocations.CountAsync());
        Assert.Equal(1, await fixture.Db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Failed_delete_rolls_back_jobs_notifications_and_retry_guards_together()
    {
        await using var fixture = await Fixture.CreateAsync();
        var old = fixture.Label(Now.AddDays(-10));
        fixture.Db.AddRange(old, fixture.Job(old, PrintJobStatus.Printed),
            new Notification { LabelId = old.Id, Type = "Expired", Title = "Expired", Message = "Test" });
        await fixture.Db.SaveChangesAsync();
        await fixture.Db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER prevent_label_delete BEFORE DELETE ON "Labels"
            BEGIN SELECT RAISE(ABORT, 'test deletion failure'); END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => fixture.Retention.DeleteExpiredAsync(Now));

        Assert.Equal(1, await fixture.Db.Labels.CountAsync());
        Assert.Equal(1, await fixture.Db.PrintJobs.CountAsync());
        Assert.Equal(1, await fixture.Db.Notifications.CountAsync());
        Assert.Equal(0, await fixture.Db.OfflineMutationReceipts.CountAsync());
    }

    [Fact]
    public async Task Replaying_a_print_after_its_history_is_deleted_cannot_send_a_second_copy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var printer = new FakeLabelPrinter();
        var service = new LabelPrintService(fixture.Db, new ExpiryCalculator(), new LabelCodeGenerator("R261024"),
            new TsplGenerator(), [printer], TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var request = new PrintLabelRequest(fixture.Item.Id, fixture.Printer.Id, fixture.User.Id, null,
            Now.AddDays(-10), null, null, Guid.NewGuid().ToString("N"));
        await service.PrintAsync(request, CancellationToken.None);

        Assert.Equal(1, await fixture.Retention.DeleteExpiredAsync(Now));
        fixture.Db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<IdempotencyConflictException>(() => service.PrintAsync(request, CancellationToken.None));
        // A tombstone is global, matching the old unique PrintJobs idempotency index.
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => service.PrintAsync(request with { RequestedByUserId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Single(printer.Commands);
        Assert.Equal(0, await fixture.Db.Labels.CountAsync());
        Assert.Equal(0, await fixture.Db.PrintJobs.CountAsync());
        Assert.Equal(1, await fixture.Db.OfflineMutationReceipts.CountAsync());

        // An intentional new request is still printable with the recovered catalog.
        var current = await service.PrintAsync(request with { IdempotencyKey = Guid.NewGuid().ToString("N"), OperationalDateTime = Now }, CancellationToken.None);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => service.ReprintAsync(current.LabelId, fixture.User.Id, request.IdempotencyKey, CancellationToken.None));

        var offlineId = Guid.NewGuid();
        var offline = new OfflineLabelImportRequest(request.IdempotencyKey, offlineId, $"OFF-{offlineId:N}"[..16],
            fixture.Item.Id, fixture.Printer.Id, fixture.Item.DefaultStorageLocationId!.Value,
            Now.AddDays(-10), Now.AddDays(-9), "Test item", "Test category", Classification.Veg,
            "PREPARED", 1, ShelfLifeUnit.Days, "Test storage", Now.AddDays(-10));
        var imports = new OfflineLabelImportService(fixture.Db, new ExpiryCalculator(), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => imports.ImportAsync(offline, fixture.User.Id, CancellationToken.None));
        Assert.Equal(2, printer.Commands.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(36501)]
    public void Invalid_retention_period_is_rejected(int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryRetentionOptions(days));

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required KivraDbContext Db { get; init; }
        public required Item Item { get; init; }
        public required Printer Printer { get; init; }
        public required User User { get; init; }
        public LabelHistoryRetention Retention => new(Db, new HistoryRetentionOptions(5));

        public Label Label(DateTimeOffset expiry, LabelStatus status = LabelStatus.Expired) => new()
        {
            LabelCode = Guid.NewGuid().ToString("N"), ItemId = Item.Id, PrinterId = Printer.Id,
            ItemNameSnapshot = "Test item", CategorySnapshot = "Test category", DateTerminologySnapshot = "PREPARED",
            ShelfLifeRuleSnapshot = "1 Days", StorageLocationSnapshot = "Test storage",
            OperationalDateTime = expiry.AddDays(-1), ExpiryDateTime = expiry, CurrentStatus = status
        };

        public PrintJob Job(Label label, PrintJobStatus status) => new()
        {
            Label = label, LabelId = label.Id, PrinterId = Printer.Id, RequestedByUserId = User.Id,
            Status = status, IdempotencyKey = Guid.NewGuid().ToString("N"), Payload = "test TSPL"
        };

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new KivraDbContext(new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var category = new Category { Name = "Test category" };
            var storage = new StorageLocation { Name = "Test storage" };
            var item = new Item { Name = "Test item", Category = category, DefaultStorageLocation = storage,
                LabelType = "Food", DateTerminology = "PREPARED", ShelfLifeValue = 1, ShelfLifeUnit = ShelfLifeUnit.Days };
            var printer = new Printer { Name = "Test printer", Model = "Test", Driver = "Fake", UsbQueueName = "Keep this queue" };
            var user = new User { DisplayName = "Test user", PinHash = "test-hash", Role = UserRole.Administrator };
            db.AddRange(category, storage, item, printer, user, new AuditLog { Action = "Test", EntityName = "Test" });
            await db.SaveChangesAsync();
            return new Fixture { Connection = connection, Db = db, Item = item, Printer = printer, User = user };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
