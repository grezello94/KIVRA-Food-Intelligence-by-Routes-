using System.Data;
using System.Security.Cryptography;
using System.Text;
using Kivra.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kivra.Infrastructure;

public sealed class HistoryRetentionOptions
{
    public int ExpiredLabelDays { get; }

    public HistoryRetentionOptions(int expiredLabelDays = 0)
    {
        if (expiredLabelDays is < 0 or > 36500)
            throw new ArgumentOutOfRangeException(nameof(expiredLabelDays), "HistoryRetention:ExpiredLabelDays must be between 0 (disabled) and 36500.");
        ExpiredLabelDays = expiredLabelDays;
    }
}

public sealed class LabelHistoryRetention(KivraDbContext db, HistoryRetentionOptions options)
{
    public bool Enabled => options.ExpiredLabelDays > 0;

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (!Enabled) return 0;
        var cutoff = now.ToUniversalTime().AddDays(-options.ExpiredLabelDays);
        var queued = (int)PrintJobStatus.Queued;
        var sending = (int)PrintJobStatus.Sending;
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            List<RetentionCandidate> candidates;
            if (db.Database.IsSqlite())
            {
                // SQLite rounds fractional seconds in julianday. Read a narrow
                // superset and apply the exact UTC cutoff before deleting anything.
                candidates = await db.Database.SqlQuery<RetentionCandidate>($"""
                    SELECT l."Id", l."ExpiryDateTime" FROM "Labels" AS l
                    WHERE julianday(l."ExpiryDateTime") < julianday({cutoff.AddSeconds(1)})
                      AND NOT EXISTS (SELECT 1 FROM "PrintJobs" AS j
                          WHERE j."LabelId" = l."Id" AND j."Status" IN ({queued}, {sending}))
                    """).ToListAsync(ct);
            }
            else
            {
                // Lock parent labels before deleting their jobs. A competing
                // reprint cannot commit its FK-backed sending job and start I/O
                // while retention removes the label. Busy labels are skipped.
                candidates = await db.Database.SqlQuery<RetentionCandidate>($"""
                    SELECT l."Id", l."ExpiryDateTime" FROM "Labels" AS l
                    WHERE l."ExpiryDateTime" < {cutoff}
                      AND NOT EXISTS (SELECT 1 FROM "PrintJobs" AS j
                          WHERE j."LabelId" = l."Id" AND j."Status" IN ({queued}, {sending}))
                    FOR UPDATE OF l SKIP LOCKED
                    """).ToListAsync(ct);
            }

            var ids = candidates.Where(x => x.ExpiryDateTime < cutoff).Select(x => x.Id).ToArray();
            if (ids.Length == 0)
            {
                await transaction.CommitAsync(ct);
                return 0;
            }

            var keys = await db.PrintJobs.Where(x => ids.Contains(x.LabelId) && x.IdempotencyKey != null)
                .Select(x => x.IdempotencyKey!).ToListAsync(ct);
            await RetiredPrintRequests.RecordAsync(db, keys, now.ToUniversalTime(), ct);
            await db.PrintJobs.Where(x => ids.Contains(x.LabelId)).ExecuteDeleteAsync(ct);
            await db.Notifications.Where(x => x.LabelId.HasValue && ids.Contains(x.LabelId.Value)).ExecuteDeleteAsync(ct);
            var deleted = await db.Labels.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
            return deleted;
        });
    }

    private sealed class RetentionCandidate
    {
        public Guid Id { get; set; }
        public DateTimeOffset ExpiryDateTime { get; set; }
    }
}

// Keep compact, indexed retry guards after the label, TSPL and print-job history
// are removed. Guid.Empty is reserved here so these are separate from users'
// normal offline mutation receipts. No PIN, label payload or catalog data is kept.
internal static class RetiredPrintRequests
{
    private const string Marker = "retired-label-print";
    private static string Key(string key) => "retired-print-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static Task<bool> ContainsAsync(KivraDbContext db, string key, CancellationToken ct)
    {
        var storedKey = Key(key);
        return db.OfflineMutationReceipts.AnyAsync(x => x.UserId == Guid.Empty && x.IdempotencyKey == storedKey && x.RequestHash == Marker, ct);
    }

    public static async Task RecordAsync(KivraDbContext db, IEnumerable<string> keys, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            var storedKey = Key(key);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "OfflineMutationReceipts"
                    ("UserId", "IdempotencyKey", "RequestHash", "StatusCode", "ContentType", "Location", "ResponseBody", "CreatedAt")
                VALUES ({Guid.Empty}, {storedKey}, {Marker}, {409}, NULL, NULL, {Array.Empty<byte>()}, {now})
                ON CONFLICT ("UserId", "IdempotencyKey") DO NOTHING
                """, ct);
        }
    }
}
