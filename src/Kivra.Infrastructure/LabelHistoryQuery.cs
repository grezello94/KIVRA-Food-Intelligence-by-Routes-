using Kivra.Application;
using Microsoft.EntityFrameworkCore;

namespace Kivra.Infrastructure;

public sealed class LabelHistoryQuery(KivraDbContext db)
{
    public const int DefaultLimit = 500;
    public const int MaximumLimit = 500;

    public Task<List<LabelHistoryDto>> ReadAsync(int take = DefaultLimit, int skip = 0, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaximumLimit);
        skip = Math.Max(0, skip);

        // CreatedAt and RequestedAt are application-managed UTC timestamps. Native SQL
        // preserves their indexed ordering on both providers; SQLite's EF provider
        // cannot translate OrderBy over DateTimeOffset. Project only the latest job's
        // status so print payloads and the rest of the job history never enter memory.
        return db.Database.SqlQuery<LabelHistoryDto>($"""
            SELECT l."Id", l."LabelCode", l."ItemNameSnapshot" AS "ItemName",
                   l."OperationalDateTime", l."ExpiryDateTime", l."CreatedAt",
                   l."StorageLocationSnapshot" AS "StorageLocation", l."CurrentStatus" AS "Status",
                   l."SuccessfulPrintCount",
                   (SELECT j."Status" FROM "PrintJobs" AS j
                    WHERE j."LabelId" = l."Id"
                    ORDER BY j."RequestedAt" DESC, j."Id" DESC LIMIT 1) AS "LastPrintStatus",
                   l."ItemId", l."PrinterId", l."StorageLocationId", l."CategorySnapshot",
                   l."ClassificationSnapshot", l."DateTerminologySnapshot", l."ShelfLifeRuleSnapshot"
            FROM "Labels" AS l
            ORDER BY l."CreatedAt" DESC, l."Id" DESC
            LIMIT {take} OFFSET {skip}
            """).ToListAsync(ct);
    }
}
