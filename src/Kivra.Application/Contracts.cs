using Kivra.Domain;

namespace Kivra.Application;
public record PrintLabelRequest(Guid ItemId, Guid PrinterId, Guid RequestedByUserId, Guid? StorageLocationId, DateTimeOffset? OperationalDateTime, decimal? Quantity, string? Unit, string IdempotencyKey);
public record OfflineLabelImportRequest(
 string IdempotencyKey, Guid LabelId, string LabelCode, Guid ItemId, Guid PrinterId, Guid StorageLocationId,
 DateTimeOffset OperationalDateTime, DateTimeOffset ExpiryDateTime,
 string ItemNameSnapshot, string CategorySnapshot, Classification ClassificationSnapshot,
 string DateTerminologySnapshot, int ShelfLifeValue, ShelfLifeUnit ShelfLifeUnit,
 string StorageLocationSnapshot, DateTimeOffset PrintedAt, string? Payload = null, bool IsReprint = false);
public record LabelHistoryDto(Guid Id, string LabelCode, string ItemName, DateTimeOffset OperationalDateTime, DateTimeOffset ExpiryDateTime, DateTimeOffset CreatedAt, string StorageLocation, LabelStatus Status, int SuccessfulPrintCount, PrintJobStatus? LastPrintStatus,
 Guid? ItemId = null, Guid? PrinterId = null, Guid? StorageLocationId = null, string? CategorySnapshot = null,
 Classification? ClassificationSnapshot = null, string? DateTerminologySnapshot = null, string? ShelfLifeRuleSnapshot = null);
public record PrintResult(Guid LabelId, string LabelCode, Guid PrintJobId, PrintJobStatus Status, string? FailureReason, bool Idempotent);
public record PrinterResult(bool Success, string? Error = null);
public interface ILabelPrinter { Task<PrinterResult> PrintAsync(Printer printer, string tspl, CancellationToken ct); Task<PrinterResult> TestConnectionAsync(Printer printer, CancellationToken ct); }
public interface ILabelCodeGenerator { string Next(DateTimeOffset localTime, int sequence); }
