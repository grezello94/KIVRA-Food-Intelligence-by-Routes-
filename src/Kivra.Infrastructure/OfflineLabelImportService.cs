using Kivra.Application;
using Kivra.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kivra.Infrastructure;

/// <summary>Records a locally printed label without sending it to a printer again.</summary>
public sealed class OfflineLabelImportService(KivraDbContext db, IExpiryCalculator expiry, TimeZoneInfo restaurantTimeZone)
{
    public async Task<PrintResult> ImportAsync(OfflineLabelImportRequest request, Guid userId, CancellationToken ct)
    {
        Validate(request);
        var prior = await db.PrintJobs.Include(x => x.Label)
            .SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
        if (prior is not null)
        {
            if (prior.RequestedByUserId != userId || prior.IsReprint != request.IsReprint ||
                prior.Label is null || !MatchesSnapshot(prior.Label, request) || prior.Status != PrintJobStatus.Printed ||
                prior.CompletedAt != request.PrintedAt || prior.Payload != request.Payload)
                throw new IdempotencyConflictException("This Idempotency-Key was already used for a different print operation.");
            return new(prior.LabelId, prior.Label.LabelCode, prior.Id, prior.Status, prior.FailureReason, true);
        }
        // Current names/rules/active flags may have changed during an outage.
        // Validate identity while retaining the actual printed snapshot.
        if (!await db.Items.AnyAsync(x => x.Id == request.ItemId, ct) ||
            !await db.Printers.AnyAsync(x => x.Id == request.PrinterId, ct) ||
            !await db.StorageLocations.AnyAsync(x => x.Id == request.StorageLocationId, ct) ||
            !await db.Users.AnyAsync(x => x.Id == userId, ct))
            throw new ArgumentException("The printed label references an unknown item, printer, storage location, or user.");

        var printedAt = request.PrintedAt.ToUniversalTime();
        Label label;
        if (request.IsReprint)
        {
            // Different receipt keys can reprint the same label concurrently.
            // Lock its row before reading the count inside the receipt transaction.
            var original = db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null
                ? db.Labels.FromSqlInterpolated($"SELECT * FROM \"Labels\" WHERE \"Id\" = {request.LabelId} FOR UPDATE")
                : db.Labels.Where(x => x.Id == request.LabelId);
            label = await original.SingleOrDefaultAsync(ct)
                ?? throw new ArgumentException("The original label must be synchronized before its offline reprint.");
            if (!MatchesSnapshot(label, request))
                throw new IdempotencyConflictException("The reprint snapshot does not match the original label.");
            label.SuccessfulPrintCount++;
            if (label.LastSuccessfulPrintAt is null || label.LastSuccessfulPrintAt < printedAt)
                label.LastSuccessfulPrintAt = printedAt;
            label.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            if (await db.Labels.AnyAsync(x => x.Id == request.LabelId || x.LabelCode == request.LabelCode, ct))
                throw new IdempotencyConflictException("This offline label identifier has already been used.");
            label = new Label
            {
                Id = request.LabelId, LabelCode = request.LabelCode, ItemId = request.ItemId,
                ItemNameSnapshot = request.ItemNameSnapshot, CategorySnapshot = request.CategorySnapshot,
                ClassificationSnapshot = request.ClassificationSnapshot, DateTerminologySnapshot = request.DateTerminologySnapshot,
                OperationalDateTime = request.OperationalDateTime.ToUniversalTime(), ExpiryDateTime = request.ExpiryDateTime.ToUniversalTime(),
                ShelfLifeRuleSnapshot = $"{request.ShelfLifeValue} {request.ShelfLifeUnit}",
                StorageLocationId = request.StorageLocationId, StorageLocationSnapshot = request.StorageLocationSnapshot,
                CreatedByUserId = userId, PrinterId = request.PrinterId, CreatedAt = printedAt,
                LastSuccessfulPrintAt = printedAt, SuccessfulPrintCount = 1
            };
        }
        var job = new PrintJob
        {
            Label = label, PrinterId = request.PrinterId, RequestedByUserId = userId,
            IdempotencyKey = request.IdempotencyKey, Status = PrintJobStatus.Printed, IsReprint = request.IsReprint,
            RequestedAt = printedAt, StartedAt = printedAt, CompletedAt = printedAt,
            Payload = request.Payload, CreatedAt = printedAt
        };
        db.PrintJobs.Add(job);
        db.Add(new AuditLog { UserId = userId, Action = request.IsReprint ? "OfflineLabelReprinted" : "OfflineLabelPrinted", EntityName = nameof(Label), EntityId = label.Id, NewValues = label.LabelCode });
        await db.SaveChangesAsync(ct);
        return new(label.Id, label.LabelCode, job.Id, job.Status, null, false);
    }

    void Validate(OfflineLabelImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 || request.IdempotencyKey != request.IdempotencyKey.Trim())
            throw new ArgumentException("A valid Idempotency-Key is required.");
        if (request.LabelId == Guid.Empty || request.ItemId == Guid.Empty || request.PrinterId == Guid.Empty || request.StorageLocationId == Guid.Empty ||
            !Text(request.LabelCode, 64) || !request.IsReprint && !string.Equals(request.LabelCode, $"OFF-{request.LabelId:N}"[..16], StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Offline labels require a unique label identifier and its matching OFF- label code.");
        if (!Text(request.ItemNameSnapshot, 200) || !Text(request.CategorySnapshot, 200) ||
            !Text(request.StorageLocationSnapshot, 200) || !Text(request.DateTerminologySnapshot, 64) ||
            !Enum.IsDefined(request.ClassificationSnapshot) || !Enum.IsDefined(request.ShelfLifeUnit) ||
            request.ShelfLifeValue is < 1 or > 100000 || request.Payload?.Length > 256 * 1024)
            throw new ArgumentException("The offline label snapshot contains an invalid field.");
        var now = DateTimeOffset.UtcNow;
        if (request.PrintedAt == default || request.OperationalDateTime == default || request.ExpiryDateTime == default ||
            request.PrintedAt > now.AddMinutes(5) || request.OperationalDateTime > request.PrintedAt.AddMinutes(5))
            throw new ArgumentException("The offline label contains an invalid print or preparation time.");
        DateTimeOffset expected;
        try { expected = expiry.Calculate(request.OperationalDateTime, request.ShelfLifeValue, request.ShelfLifeUnit, restaurantTimeZone); }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException("The offline label shelf life is out of range."); }
        if (expected != request.ExpiryDateTime)
            throw new ArgumentException("The offline expiry time does not match its printed shelf life rule.");
    }

    static bool Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);

    static bool MatchesSnapshot(Label label, OfflineLabelImportRequest request) =>
        label.Id == request.LabelId && label.LabelCode == request.LabelCode && label.ItemId == request.ItemId &&
        label.PrinterId == request.PrinterId && label.StorageLocationId == request.StorageLocationId &&
        label.ItemNameSnapshot == request.ItemNameSnapshot && label.CategorySnapshot == request.CategorySnapshot &&
        label.ClassificationSnapshot == request.ClassificationSnapshot && label.DateTerminologySnapshot == request.DateTerminologySnapshot &&
        label.StorageLocationSnapshot == request.StorageLocationSnapshot && label.ShelfLifeRuleSnapshot == $"{request.ShelfLifeValue} {request.ShelfLifeUnit}" &&
        label.OperationalDateTime == request.OperationalDateTime && label.ExpiryDateTime == request.ExpiryDateTime;
}
