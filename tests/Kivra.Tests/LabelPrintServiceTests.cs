using Kivra.Application;
using Kivra.Domain;
using Kivra.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kivra.Tests;

public sealed class LabelPrintServiceTests
{
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
}
