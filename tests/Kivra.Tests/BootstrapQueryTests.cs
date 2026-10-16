using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Kivra.Domain;
using Kivra.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Kivra.Tests;

public sealed class BootstrapQueryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task History_reads_only_a_bounded_projection_without_print_payloads()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        const int labelCount = 750;
        const int jobsPerLabel = 3;
        var payload = new string('P', 8192);
        for (var i = 0; i < labelCount; i++)
        {
            var label = NewLabel(i, now.AddMinutes(i));
            for (var job = 0; job < jobsPerLabel; job++)
                label.PrintJobs.Add(new PrintJob
                {
                    LabelId = label.Id, RequestedAt = now.AddMinutes(job),
                    Status = job == jobsPerLabel - 1 ? PrintJobStatus.Printed : PrintJobStatus.Failed,
                    Payload = payload
                });
            fixture.Db.Add(label);
        }
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.Commands.Reset();

        var timer = Stopwatch.StartNew();
        var oldLabels = await fixture.Db.Labels.Include(x => x.PrintJobs).ToListAsync();
        var oldRows = fixture.Commands.ReadCounts.Sum();
        timer.Stop();
        var oldTime = timer.Elapsed;
        Assert.Equal(labelCount * jobsPerLabel, oldLabels.Sum(x => x.PrintJobs.Count));
        fixture.Db.ChangeTracker.Clear();
        fixture.Commands.Reset();

        timer.Restart();
        var query = new LabelHistoryQuery(fixture.Db);
        var labels = await query.ReadAsync(take: int.MaxValue);
        timer.Stop();

        Assert.Equal(LabelHistoryQuery.MaximumLimit, labels.Count);
        Assert.Equal("L-749", labels[0].LabelCode);
        Assert.Equal("L-250", labels[^1].LabelCode);
        Assert.All(labels, x => Assert.Equal(PrintJobStatus.Printed, x.LastPrintStatus));
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
        var command = Assert.Single(fixture.Commands.Sql);
        Assert.Contains("LIMIT", command);
        Assert.Contains("OFFSET", command);
        Assert.DoesNotContain("Payload", command);
        Assert.DoesNotContain("JOIN", command);
        Assert.InRange(Assert.Single(fixture.Commands.ReadCounts), labels.Count, labels.Count + 1);
        output.WriteLine($"750 labels / 2,250 jobs / 17.6 MiB job payloads: old query {oldTime.TotalMilliseconds:F1} ms, {oldRows} reader operations; bounded projection {timer.Elapsed.TotalMilliseconds:F1} ms, {fixture.Commands.ReadCounts.Sum()} reader operations. Timing is diagnostic, not asserted.");

        var page = await query.ReadAsync(take: 2, skip: 1);
        Assert.Equal(new[] { "L-748", "L-747" }, page.Select(x => x.LabelCode));
        Assert.Single(await query.ReadAsync(take: 0, skip: -1));
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 6)]
    public async Task Bootstrap_preserves_role_filtered_shape_and_avoids_duplicate_queries(bool administrator, int expectedQueries)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var category = new Category { Name = "Prepared" };
        var location = new StorageLocation { Name = "Chiller" };
        var inactiveLocation = new StorageLocation { Name = "Inactive", Active = false };
        var printer = new Printer { Name = "Enabled", Model = "Fake", Driver = "Fake" };
        var disabledPrinter = new Printer { Name = "Disabled", Model = "Fake", Driver = "Fake", Enabled = false };
        var item = new Item
        {
            Name = "Rice", Category = category, LabelType = "Food", DateTerminology = "PREPARED",
            ShelfLifeValue = 1, ImageDataUrl = "data:image/webp;base64,preserved",
            DefaultStorageLocation = location
        };
        var label = NewLabel(1, DateTimeOffset.UtcNow);
        fixture.Db.AddRange(category, location, inactiveLocation, printer, disabledPrinter, item, label,
            new User { DisplayName = "Staff", PinHash = "must-not-be-returned", Role = UserRole.KitchenStaff });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.Commands.Reset();

        var result = await new BootstrapQuery(fixture.Db, new LabelHistoryQuery(fixture.Db)).ReadAsync(administrator);
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(expectedQueries, fixture.Commands.Sql.Count);
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
        Assert.Equal(1, json.GetProperty("printers").GetArrayLength());
        Assert.Equal(1, json.GetProperty("locations").GetArrayLength());
        Assert.Equal(administrator ? 2 : 0, json.GetProperty("allPrinters").GetArrayLength());
        Assert.Equal(administrator ? 2 : 0, json.GetProperty("allLocations").GetArrayLength());
        Assert.Equal(administrator ? 1 : 0, json.GetProperty("users").GetArrayLength());
        Assert.DoesNotContain("must-not-be-returned", json.ToString());
        Assert.Equal(item.ImageDataUrl, json.GetProperty("items")[0].GetProperty("imageDataUrl").GetString());
        Assert.Equal(category.Name, json.GetProperty("items")[0].GetProperty("category").GetString());
        Assert.Equal(location.Name, json.GetProperty("items")[0].GetProperty("storage").GetString());
        Assert.Equal(label.ItemNameSnapshot, json.GetProperty("expiry")[0].GetProperty("itemNameSnapshot").GetString());
        Assert.Equal(label.StorageLocationSnapshot, json.GetProperty("expiry")[0].GetProperty("storageLocationSnapshot").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("labels")[0].GetProperty("lastPrintStatus").ValueKind);
    }

    private static Label NewLabel(int number, DateTimeOffset createdAt) => new()
    {
        LabelCode = $"L-{number}", ItemNameSnapshot = "Rice", CategorySnapshot = "Prepared",
        DateTerminologySnapshot = "PREPARED", ShelfLifeRuleSnapshot = "1 Days",
        StorageLocationSnapshot = "Chiller", CreatedAt = createdAt,
        OperationalDateTime = createdAt, ExpiryDateTime = createdAt.AddDays(1)
    };

    private sealed class DatabaseFixture(SqliteConnection connection, KivraDbContext db, CommandRecorder commands) : IAsyncDisposable
    {
        public KivraDbContext Db { get; } = db;
        public CommandRecorder Commands { get; } = commands;

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var commands = new CommandRecorder();
            var options = new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).AddInterceptors(commands).Options;
            var db = new KivraDbContext(options);
            await db.Database.EnsureCreatedAsync();
            commands.Reset();
            return new DatabaseFixture(connection, db, commands);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public List<int> ReadCounts { get; } = [];

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            ReadCounts.Add(eventData.ReadCount);
            return result;
        }

        public void Reset()
        {
            Sql.Clear();
            ReadCounts.Clear();
        }
    }
}
