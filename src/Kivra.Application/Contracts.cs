using Kivra.Domain;

namespace Kivra.Application;
public record PrintLabelRequest(Guid ItemId, Guid PrinterId, Guid RequestedByUserId, Guid? StorageLocationId, DateTimeOffset? OperationalDateTime, decimal? Quantity, string? Unit, string IdempotencyKey);
public record LabelHistoryDto(Guid Id, string LabelCode, string ItemName, DateTimeOffset OperationalDateTime, DateTimeOffset ExpiryDateTime, string StorageLocation, LabelStatus Status, int SuccessfulPrintCount, PrintJobStatus? LastPrintStatus);
public record PrintResult(Guid LabelId, string LabelCode, Guid PrintJobId, PrintJobStatus Status, string? FailureReason, bool Idempotent);
public record PrinterResult(bool Success, string? Error = null);
public interface ILabelPrinter { Task<PrinterResult> PrintAsync(Printer printer, string tspl, CancellationToken ct); Task<PrinterResult> TestConnectionAsync(Printer printer, CancellationToken ct); }
public interface ILabelCodeGenerator { string Next(DateTimeOffset localTime, int sequence); }
