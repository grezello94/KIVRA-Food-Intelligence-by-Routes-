using Kivra.Application;
using Kivra.Domain;
using Microsoft.EntityFrameworkCore;
namespace Kivra.Infrastructure;
public sealed class LabelPrintService(KivraDbContext db, IExpiryCalculator expiry, ILabelCodeGenerator codes, ITsplGenerator tspl, IEnumerable<ILabelPrinter> printers, TimeZoneInfo restaurantTimeZone) {
 public async Task<PrintResult> PrintAsync(PrintLabelRequest request, CancellationToken ct) {
  if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("An Idempotency-Key is required.");
  var existing = await db.PrintJobs.Include(x => x.Label).SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
  if (existing is not null) return new(existing.LabelId, existing.Label!.LabelCode, existing.Id, existing.Status, existing.FailureReason, true);
  var item = await db.Items.Include(x => x.Category).Include(x => x.DefaultStorageLocation).SingleOrDefaultAsync(x => x.Id == request.ItemId && x.Active, ct) ?? throw new KeyNotFoundException("Active item was not found.");
  var printer = await db.Printers.SingleOrDefaultAsync(x => x.Id == request.PrinterId && x.Enabled, ct) ?? throw new KeyNotFoundException("Enabled printer was not found.");
  var location = request.StorageLocationId is null ? item.DefaultStorageLocation : await db.StorageLocations.SingleOrDefaultAsync(x => x.Id == request.StorageLocationId && x.Active, ct);
  if (location is null) throw new InvalidOperationException("A storage location is required for this label.");
  var operational = request.OperationalDateTime ?? DateTimeOffset.UtcNow;
  var local = TimeZoneInfo.ConvertTime(operational, restaurantTimeZone); var todayStart = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset); var createdDates=await db.Labels.Select(x=>x.CreatedAt).ToListAsync(ct); var sequence=createdDates.Count(x=>x>=todayStart.ToUniversalTime())+1;
  var label = new Label { LabelCode = codes.Next(local, sequence), ItemId = item.Id, ItemNameSnapshot = item.Name, CategorySnapshot = item.Category?.Name ?? "Uncategorized", ClassificationSnapshot = item.Classification, DateTerminologySnapshot = item.DateTerminology, OperationalDateTime = operational, ExpiryDateTime = expiry.Calculate(operational, item.ShelfLifeValue, item.ShelfLifeUnit, restaurantTimeZone), ShelfLifeRuleSnapshot = $"{item.ShelfLifeValue} {item.ShelfLifeUnit}", StorageLocationId = location.Id, StorageLocationSnapshot = location.Name, Quantity = request.Quantity, Unit = request.Unit, CreatedByUserId = request.RequestedByUserId, PrinterId = printer.Id };
  var job = new PrintJob { Label = label, PrinterId = printer.Id, RequestedByUserId = request.RequestedByUserId, IdempotencyKey = request.IdempotencyKey, Status = PrintJobStatus.Queued };
  db.Add(job); db.Add(new AuditLog { UserId = request.RequestedByUserId, Action = "LabelCreated", EntityName = nameof(Label), EntityId = label.Id, NewValues = label.LabelCode }); await db.SaveChangesAsync(ct);
  job.Status = PrintJobStatus.Sending; job.StartedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct);
  var implementation = Resolve(printer);
  job.Payload = tspl.Generate(label, printer, restaurantTimeZone); var result = await implementation.PrintAsync(printer, job.Payload, ct); job.CompletedAt = DateTimeOffset.UtcNow;
  if (result.Success) { job.Status = PrintJobStatus.Printed; label.LastSuccessfulPrintAt = job.CompletedAt; label.SuccessfulPrintCount++; } else { job.Status = PrintJobStatus.Failed; job.FailureReason = result.Error; }
  db.Add(new AuditLog { UserId = request.RequestedByUserId, Action = result.Success ? "LabelPrinted" : "LabelPrintFailed", EntityName = nameof(PrintJob), EntityId = job.Id, NewValues = result.Error }); await db.SaveChangesAsync(ct);
  return new(label.Id, label.LabelCode, job.Id, job.Status, job.FailureReason, false);
 }
 public async Task<PrintResult> ReprintAsync(Guid labelId, Guid userId, string key, CancellationToken ct) {
  if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("An Idempotency-Key is required.");
  var prior = await db.PrintJobs.Include(x => x.Label).SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct); if (prior is not null) return new(prior.LabelId, prior.Label!.LabelCode, prior.Id, prior.Status, prior.FailureReason, true);
  var label = await db.Labels.FindAsync([labelId], ct) ?? throw new KeyNotFoundException("Label not found."); var printer = await db.Printers.FindAsync([label.PrinterId], ct) ?? throw new KeyNotFoundException("Printer not found.");
  var job = new PrintJob { LabelId = label.Id, PrinterId = printer.Id, RequestedByUserId = userId, IdempotencyKey = key, IsReprint = true, Status = PrintJobStatus.Sending, StartedAt = DateTimeOffset.UtcNow }; db.Add(job); await db.SaveChangesAsync(ct);
  var implementation=Resolve(printer);job.Payload = tspl.Generate(label, printer, restaurantTimeZone); var result = await implementation.PrintAsync(printer, job.Payload, ct); job.CompletedAt = DateTimeOffset.UtcNow; job.Status = result.Success ? PrintJobStatus.Printed : PrintJobStatus.Failed; job.FailureReason = result.Error; if (result.Success) { label.SuccessfulPrintCount++; label.LastSuccessfulPrintAt = job.CompletedAt; } db.Add(new AuditLog { UserId = userId, Action = "LabelReprinted", EntityName = nameof(Label), EntityId = label.Id }); await db.SaveChangesAsync(ct); return new(label.Id, label.LabelCode, job.Id, job.Status, job.FailureReason, false);
 }
 ILabelPrinter Resolve(Printer printer)=>printers.FirstOrDefault(x=>printer.Driver switch{"TscTsplNetwork"=>x is TscTsplNetworkPrinter,"TscTsplUsb"=>x is TscTsplUsbPrinter,_=>x is FakeLabelPrinter})??throw new InvalidOperationException($"Printer driver '{printer.Driver}' is not registered.");
}
