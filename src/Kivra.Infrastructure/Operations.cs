using Kivra.Application;
using Kivra.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Kivra.Infrastructure;
public sealed class PrinterOperations(KivraDbContext db, IEnumerable<ILabelPrinter> printers, ITsplGenerator tspl, TimeZoneInfo tz) {
 ILabelPrinter Resolve(Printer p)=>printers.FirstOrDefault(x=>p.Driver switch{"TscTsplNetwork"=>x is TscTsplNetworkPrinter,"TscTsplUsb"=>x is TscTsplUsbPrinter,"EpsonEscPosUsb"=>x is EpsonEscPosUsbPrinter,_=>x is FakeLabelPrinter})??throw new InvalidOperationException($"Printer driver '{p.Driver}' is not registered.");
 public Task<PrinterResult> TestAsync(Guid printerId,CancellationToken ct) => TestCore(printerId,ct);
 async Task<PrinterResult> TestCore(Guid id,CancellationToken ct) { var p=await db.Printers.FindAsync([id],ct)??throw new KeyNotFoundException("Printer not found."); if(RequiresBridge(p))return await BridgeReadinessAsync(p.Driver=="AndroidBridge"?"android":"windows",ct);return await Resolve(p).TestConnectionAsync(p,ct); }
 public async Task<PrinterResult> TestPrintAsync(Guid printerId,Guid userId,CancellationToken ct) { var p=await db.Printers.FindAsync([printerId],ct)??throw new KeyNotFoundException("Printer not found.");if(RequiresBridge(p)){var readiness=await BridgeReadinessAsync(p.Driver=="AndroidBridge"?"android":"windows",ct);if(!readiness.Success)return readiness;var now=DateTimeOffset.UtcNow;var sample=new Label { LabelCode=$"TEST-{now:yyMMddHHmmss}-{Guid.NewGuid():N}"[..25],ItemNameSnapshot="KIVRA TEST LABEL",CategorySnapshot="System",ClassificationSnapshot=Classification.NotApplicable,DateTerminologySnapshot="TESTED",OperationalDateTime=now,ExpiryDateTime=now,ShelfLifeRuleSnapshot="Test",StorageLocationSnapshot="PRINTER TEST",CreatedByUserId=userId,PrinterId=p.Id,CurrentStatus=LabelStatus.Cancelled };var job=new PrintJob { Label=sample,PrinterId=p.Id,RequestedByUserId=userId,IdempotencyKey=$"test-{Guid.NewGuid():N}",Status=PrintJobStatus.Queued,Payload=tspl.Generate(sample,p,tz) };db.Add(job);db.Add(new AuditLog { UserId=userId,Action="PrinterTestQueued",EntityName=nameof(Printer),EntityId=p.Id,NewValues=p.Name });await db.SaveChangesAsync(ct);return new(true); }var directSample=new Label { LabelCode="TEST-0001",ItemNameSnapshot="KIVRA TEST LABEL",CategorySnapshot="System",ClassificationSnapshot=Classification.NotApplicable,DateTerminologySnapshot="TESTED",OperationalDateTime=DateTimeOffset.UtcNow,ExpiryDateTime=DateTimeOffset.UtcNow,ShelfLifeRuleSnapshot="Test",StorageLocationSnapshot="PRINTER TEST" };return await Resolve(p).PrintAsync(p,tspl.Generate(directSample,p,tz),ct); }
 bool RequiresBridge(Printer p)=>p.Driver=="AndroidBridge"||(!OperatingSystem.IsWindows()&&p.Driver is "TscTsplUsb" or "EpsonEscPosUsb");
 async Task<PrinterResult> BridgeReadinessAsync(string platform,CancellationToken ct) {
  // Project only matching pairings' heartbeat timestamps. Comparing them after
  // the query also supports SQLite, which cannot translate DateTimeOffset order.
  var heartbeats=await db.BridgeDevices.AsNoTracking()
   .Where(x=>x.Active&&x.RevokedAt==null&&x.Platform!=null&&x.Platform.ToLower()==platform)
   .Select(x=>x.LastSeenAt).ToListAsync(ct);
  var name=PlatformName(platform);
  if(heartbeats.Count==0)return new(false,$"No {name} Print Bridge is paired. Open Settings > Print Bridge, generate a pairing code, then pair the bridge app on the printer device.");
  if(!heartbeats.Any(x=>x>DateTimeOffset.UtcNow.AddMinutes(-1)))return new(false,$"No {name} Print Bridge is online. Open the bridge app and check its printer and internet connection. If its pairing has expired, pair it again in Settings > Print Bridge.");
  return new(true);
 }
 static string PlatformName(string platform)=>platform=="windows"?"Windows":"Android";
}
public sealed class ExpiryNotificationWorker(IServiceScopeFactory scopes, ILogger<ExpiryNotificationWorker> log) : BackgroundService {
 DateTimeOffset? lastRetentionRun;
 protected override async Task ExecuteAsync(CancellationToken stoppingToken) { while(!stoppingToken.IsCancellationRequested) { try { await Process(stoppingToken); } catch(Exception ex) { log.LogError(ex,"Expiry processing failed"); } await Task.Delay(TimeSpan.FromMinutes(5),stoppingToken); } }
 async Task Process(CancellationToken ct) { using var scope=scopes.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<KivraDbContext>(); var now=DateTimeOffset.UtcNow; var active=await db.Labels.Where(x=>x.CurrentStatus==LabelStatus.Active).ToListAsync(ct); var expired=active.Where(x=>x.ExpiryDateTime<=now).ToList(); foreach(var label in expired) { label.CurrentStatus=LabelStatus.Expired; db.Notifications.Add(new Notification { Type="Expired",Title="Item expired",Message=$"{label.ItemNameSnapshot} ({label.LabelCode}) is expired.",LabelId=label.Id }); db.AuditLogs.Add(new AuditLog { Action="LabelExpired",EntityName="Label",EntityId=label.Id }); } if(expired.Count>0)await db.SaveChangesAsync(ct);
  var retention=scope.ServiceProvider.GetRequiredService<LabelHistoryRetention>();
  if(retention.Enabled && (lastRetentionRun is null || now-lastRetentionRun.Value>=TimeSpan.FromHours(1))) {
   var deleted=await retention.DeleteExpiredAsync(now,ct);
   lastRetentionRun=now;
   if(deleted>0)log.LogInformation("Deleted {Count} labels beyond the configured expiry retention period",deleted);
  }
 }
}
