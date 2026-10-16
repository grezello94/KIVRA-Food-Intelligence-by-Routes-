using Kivra.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kivra.Infrastructure;

public sealed class BootstrapQuery(KivraDbContext db, LabelHistoryQuery history)
{
    public async Task<object> ReadAsync(bool administrator, CancellationToken ct = default)
    {
        var items = await db.Items.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name)
            .Select(x => new
            {
                x.Id, x.Name, x.ShortName, x.ImageDataUrl, x.CategoryId, x.Classification,
                x.LabelType, x.DateTerminology, x.ShelfLifeValue, x.ShelfLifeUnit,
                x.DefaultStorageLocationId, x.DefaultLabelTemplate, x.Active,
                Category = x.Category!.Name,
                Storage = x.DefaultStorageLocation != null ? x.DefaultStorageLocation.Name : null
            }).ToListAsync(ct);
        var printers = await db.Printers.AsNoTracking().Where(x => administrator || x.Enabled)
            .OrderBy(x => x.Name).ToListAsync(ct);
        var locations = await db.StorageLocations.AsNoTracking().Where(x => administrator || x.Active)
            .OrderBy(x => x.Name).ToListAsync(ct);
        var labels = await history.ReadAsync(ct: ct);
        var categories = await db.Categories.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
        var users = administrator
            ? await db.Users.AsNoTracking().OrderBy(x => x.DisplayName)
                .Select(x => new { x.Id, x.DisplayName, x.Role, x.Active, x.CreatedAt }).ToListAsync(ct)
            : [];

        return new
        {
            items,
            printers = printers.Where(x => x.Enabled).ToList(),
            locations = locations.Where(x => x.Active).ToList(),
            labels,
            expiry = labels.Where(x => x.Status is LabelStatus.Active or LabelStatus.Expired)
                .OrderBy(x => x.ExpiryDateTime).Select(x => new
                {
                    x.Id, ItemNameSnapshot = x.ItemName, x.LabelCode, x.OperationalDateTime,
                    x.ExpiryDateTime, StorageLocationSnapshot = x.StorageLocation, CurrentStatus = x.Status
                }).ToList(),
            categories,
            allPrinters = administrator ? printers : [],
            allLocations = administrator ? locations : [],
            users
        };
    }
}
