using Kivra.Application;
using Kivra.Domain;
using Microsoft.EntityFrameworkCore;
namespace Kivra.Infrastructure;
public sealed class LabelPrintService(KivraDbContext db, IExpiryCalculator expiry, ILabelCodeGenerator codes, ITsplGenerator tspl, IEnumerable<ILabelPrinter> printers, TimeZoneInfo restaurantTimeZone) {
 public async Task<PrintResult> PrintAsync(PrintLabelRequest request, CancellationToken ct) {
  if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("An Idempotency-Key is required.");
  var existing = await db.PrintJobs.Include(x => x.Label).SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
  if (existing is not null) {
   var existingLabel = existing.Label!;
   if (existing.IsReprint || existingLabel.LabelCode.StartsWith("OFF-", StringComparison.OrdinalIgnoreCase) || existing.RequestedByUserId != request.RequestedByUserId || existingLabel.ItemId != request.ItemId || existing.PrinterId != request.PrinterId ||
       request.StorageLocationId is { } storageId && existingLabel.StorageLocationId != storageId || existingLabel.Quantity != request.Quantity || existingLabel.Unit != request.Unit ||
       request.OperationalDateTime is { } requestedTime && existingLabel.OperationalDateTime != RestaurantOperationalDay.Resolve(requestedTime, restaurantTimeZone).ToUniversalTime())
    throw new IdempotencyConflictException("This Idempotency-Key was already used for a different print request.");
   return new(existing.LabelId, existingLabel.LabelCode, existing.Id, existing.Status, existing.FailureReason, true);
  }
  if (await RetiredPrintRequests.ContainsAsync(db, request.IdempotencyKey, ct))
   throw new IdempotencyConflictException("This print request belongs to deleted label history and cannot be printed again. Start a new print request if another label is needed.");
  // Item photos can be large; printing only needs the label fields and storage metadata.
  var item = await db.Items.Where(x => x.Id == request.ItemId && x.Active).Select(x => new {
   x.Id, x.Name, CategoryName = x.Category == null ? "Uncategorized" : x.Category.Name,
   x.Classification, x.DateTerminology, x.ShelfLifeValue, x.ShelfLifeUnit,
   x.DefaultStorageLocationId, DefaultStorageLocation = x.DefaultStorageLocation == null ? null : new { x.DefaultStorageLocation.Id, x.DefaultStorageLocation.Name, x.DefaultStorageLocation.Active }
  }).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Active item was not found.");
  var printer = await db.Printers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.PrinterId && x.Enabled, ct) ?? throw new KeyNotFoundException("Enabled printer was not found.");
  var locationId = request.StorageLocationId ?? item.DefaultStorageLocationId;
  var location = item.DefaultStorageLocation;
  if (locationId != item.DefaultStorageLocationId)
   location = await db.StorageLocations.Where(x => x.Id == locationId && x.Active).Select(x => new { x.Id, x.Name, x.Active }).SingleOrDefaultAsync(ct);
  if (location is not { Active: true }) throw new InvalidOperationException("Select an active storage location before printing.");
  var actual = request.OperationalDateTime ?? DateTimeOffset.UtcNow;
  var local = RestaurantOperationalDay.Resolve(actual, restaurantTimeZone);
  var operational = local.ToUniversalTime();
  var dayStart = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset).ToUniversalTime();
  var dayEnd = dayStart.AddDays(1);
  var sequence = await CountLabelsForDayAsync(dayStart, dayEnd, ct) + 1;
  var expiryTime = expiry.Calculate(operational, item.ShelfLifeValue, item.ShelfLifeUnit, restaurantTimeZone).ToUniversalTime();
  var label = new Label { LabelCode = codes.Next(local, sequence), ItemId = item.Id, ItemNameSnapshot = item.Name, CategorySnapshot = item.CategoryName, ClassificationSnapshot = item.Classification, DateTerminologySnapshot = item.DateTerminology, OperationalDateTime = operational, ExpiryDateTime = expiryTime, ShelfLifeRuleSnapshot = $"{item.ShelfLifeValue} {item.ShelfLifeUnit}", StorageLocationId = location.Id, StorageLocationSnapshot = location.Name, Quantity = request.Quantity, Unit = request.Unit, CreatedByUserId = request.RequestedByUserId, PrinterId = printer.Id };
  var bridge = UsesBridge(printer);
  // Persist the sending state before native printer I/O, in the same commit as the label.
  var job = new PrintJob { Label = label, PrinterId = printer.Id, RequestedByUserId = request.RequestedByUserId, IdempotencyKey = request.IdempotencyKey, Status = bridge ? PrintJobStatus.Queued : PrintJobStatus.Sending, StartedAt = bridge ? null : DateTimeOffset.UtcNow, Payload = tspl.Generate(label, printer, restaurantTimeZone) };
  db.Add(job); db.Add(new AuditLog { UserId = request.RequestedByUserId, Action = "LabelCreated", EntityName = nameof(Label), EntityId = label.Id, NewValues = label.LabelCode }); await db.SaveChangesAsync(ct);
  if (bridge) return new(label.Id, label.LabelCode, job.Id, job.Status, null, false);
  var implementation = Resolve(printer);
  var result = await implementation.PrintAsync(printer, job.Payload, ct); job.CompletedAt = DateTimeOffset.UtcNow;
  if (result.Success) { job.Status = PrintJobStatus.Printed; label.LastSuccessfulPrintAt = job.CompletedAt; label.SuccessfulPrintCount++; } else { job.Status = PrintJobStatus.Failed; job.FailureReason = result.Error; }
  db.Add(new AuditLog { UserId = request.RequestedByUserId, Action = result.Success ? "LabelPrinted" : "LabelPrintFailed", EntityName = nameof(PrintJob), EntityId = job.Id, NewValues = result.Error }); await db.SaveChangesAsync(ct);
  return new(label.Id, label.LabelCode, job.Id, job.Status, job.FailureReason, false);
 }
 public async Task<PrintResult> ReprintAsync(Guid labelId, Guid userId, string key, CancellationToken ct) {
  if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("An Idempotency-Key is required.");
  var prior = await db.PrintJobs.Include(x => x.Label).SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct); if (prior is not null) {
   if (!prior.IsReprint || prior.LabelId != labelId || prior.RequestedByUserId != userId)
    throw new IdempotencyConflictException("This Idempotency-Key was already used for a different print request.");
   return new(prior.LabelId, prior.Label!.LabelCode, prior.Id, prior.Status, prior.FailureReason, true);
  }
  if (await RetiredPrintRequests.ContainsAsync(db, key, ct))
   throw new IdempotencyConflictException("This Idempotency-Key belongs to deleted label history.");
  var label = await db.Labels.FindAsync([labelId], ct) ?? throw new KeyNotFoundException("Label not found."); var printer = await db.Printers.FindAsync([label.PrinterId], ct) ?? throw new KeyNotFoundException("Printer not found.");
  var bridge = UsesBridge(printer);
  var job = new PrintJob { LabelId = label.Id, PrinterId = printer.Id, RequestedByUserId = userId, IdempotencyKey = key, IsReprint = true, Status = bridge ? PrintJobStatus.Queued : PrintJobStatus.Sending, StartedAt = bridge ? null : DateTimeOffset.UtcNow }; db.Add(job); job.Payload = tspl.Generate(label, printer, restaurantTimeZone);
  if (bridge) db.Add(new AuditLog { UserId = userId, Action = "LabelReprintQueued", EntityName = nameof(Label), EntityId = label.Id });
  await db.SaveChangesAsync(ct);
  if (bridge) return new(label.Id, label.LabelCode, job.Id, job.Status, null, false);
  var implementation=Resolve(printer);var result = await implementation.PrintAsync(printer, job.Payload, ct); job.CompletedAt = DateTimeOffset.UtcNow; job.Status = result.Success ? PrintJobStatus.Printed : PrintJobStatus.Failed; job.FailureReason = result.Error; if (result.Success) { label.SuccessfulPrintCount++; label.LastSuccessfulPrintAt = job.CompletedAt; } db.Add(new AuditLog { UserId = userId, Action = "LabelReprinted", EntityName = nameof(Label), EntityId = label.Id }); await db.SaveChangesAsync(ct); return new(label.Id, label.LabelCode, job.Id, job.Status, job.FailureReason, false);
 }
 async Task<int> CountLabelsForDayAsync(DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken ct) {
  if (!db.Database.IsSqlite()) return await db.Labels.CountAsync(x => x.OperationalDateTime >= dayStart && x.OperationalDateTime < dayEnd, ct);
  // SQLite cannot compare DateTimeOffset through EF. Filter on the server first so
  // print latency does not grow with the entire label history. Keep an exact final
  // comparison because SQLite's Julian day conversion rounds fractional seconds.
  var candidates = await db.Database.SqlQuery<DateTimeOffset>($"""
   SELECT "OperationalDateTime" AS "Value" FROM "Labels"
   WHERE julianday("OperationalDateTime") >= julianday({dayStart.AddSeconds(-1)})
     AND julianday("OperationalDateTime") <= julianday({dayEnd.AddSeconds(1)})
   """).ToListAsync(ct);
  return candidates.Count(x => x >= dayStart && x < dayEnd);
 }
 ILabelPrinter Resolve(Printer printer)=>printers.FirstOrDefault(x=>printer.Driver switch{"TscTsplNetwork"=>x is TscTsplNetworkPrinter,"TscTsplUsb"=>x is TscTsplUsbPrinter,"EpsonEscPosUsb"=>x is EpsonEscPosUsbPrinter,_=>x is FakeLabelPrinter})??throw new InvalidOperationException($"Printer driver '{printer.Driver}' is not registered.");
 static bool UsesBridge(Printer printer)=>printer.Driver=="AndroidBridge"||(!OperatingSystem.IsWindows()&&printer.Driver is "TscTsplUsb" or "EpsonEscPosUsb");
}
