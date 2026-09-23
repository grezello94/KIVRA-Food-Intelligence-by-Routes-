using Kivra.Application;
using Kivra.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Kivra.Infrastructure;
public sealed class PrinterOperations(KivraDbContext db, IEnumerable<ILabelPrinter> printers, ITsplGenerator tspl, TimeZoneInfo tz) {
 ILabelPrinter Resolve(Printer p)=>printers.FirstOrDefault(x=>p.Driver switch{"TscTsplNetwork"=>x is TscTsplNetworkPrinter,"TscTsplUsb"=>x is TscTsplUsbPrinter,_=>x is FakeLabelPrinter})??throw new InvalidOperationException($"Printer driver '{p.Driver}' is not registered.");
 public Task<PrinterResult> TestAsync(Guid printerId,CancellationToken ct) => TestCore(printerId,ct);
 async Task<PrinterResult> TestCore(Guid id,CancellationToken ct) { var p=await db.Printers.FindAsync([id],ct)??throw new KeyNotFoundException("Printer not found."); return await Resolve(p).TestConnectionAsync(p,ct); }
 public async Task<PrinterResult> TestPrintAsync(Guid printerId,CancellationToken ct) { var p=await db.Printers.FindAsync([printerId],ct)??throw new KeyNotFoundException("Printer not found."); var sample=new Label { LabelCode="TEST-0001",ItemNameSnapshot="KIVRA TEST LABEL",CategorySnapshot="System",ClassificationSnapshot=Classification.NotApplicable,DateTerminologySnapshot="TESTED",OperationalDateTime=DateTimeOffset.UtcNow,ExpiryDateTime=DateTimeOffset.UtcNow,ShelfLifeRuleSnapshot="Test",StorageLocationSnapshot="PRINTER TEST" }; return await Resolve(p).PrintAsync(p,tspl.Generate(sample,p,tz),ct); }
}
public sealed class ExpiryNotificationWorker(IServiceScopeFactory scopes, ILogger<ExpiryNotificationWorker> log) : BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken stoppingToken) { while(!stoppingToken.IsCancellationRequested) { try { await Process(stoppingToken); } catch(Exception ex) { log.LogError(ex,"Expiry processing failed"); } await Task.Delay(TimeSpan.FromMinutes(5),stoppingToken); } }
 async Task Process(CancellationToken ct) { using var scope=scopes.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<KivraDbContext>(); var now=DateTimeOffset.UtcNow; var active=await db.Labels.Where(x=>x.CurrentStatus==LabelStatus.Active).ToListAsync(ct); var expired=active.Where(x=>x.ExpiryDateTime<=now).ToList(); foreach(var label in expired) { label.CurrentStatus=LabelStatus.Expired; db.Notifications.Add(new Notification { Type="Expired",Title="Item expired",Message=$"{label.ItemNameSnapshot} ({label.LabelCode}) is expired.",LabelId=label.Id }); db.AuditLogs.Add(new AuditLog { Action="LabelExpired",EntityName="Label",EntityId=label.Id }); } if(expired.Count>0)await db.SaveChangesAsync(ct); }
}
