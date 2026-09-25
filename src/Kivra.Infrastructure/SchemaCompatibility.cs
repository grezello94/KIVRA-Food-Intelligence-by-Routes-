using System.Data;
using Microsoft.EntityFrameworkCore;
namespace Kivra.Infrastructure;
public static class SchemaCompatibility {
 public static async Task EnsureAsync(KivraDbContext db,CancellationToken ct=default) {
  if(!db.Database.IsSqlite())return;
  var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(ct);
  var columns=new HashSet<string>(StringComparer.OrdinalIgnoreCase);await using(var command=connection.CreateCommand()){command.CommandText="PRAGMA table_info('Printers')";await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))columns.Add(reader.GetString(1));}
  if(!columns.Contains("MediaSensingMode"))await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Printers\" ADD COLUMN \"MediaSensingMode\" TEXT NOT NULL DEFAULT 'Gap'",ct);
  if(!columns.Contains("GapMm"))await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Printers\" ADD COLUMN \"GapMm\" TEXT NOT NULL DEFAULT '2'",ct);
  if(!columns.Contains("PrintMethod"))await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Printers\" ADD COLUMN \"PrintMethod\" TEXT NOT NULL DEFAULT 'DirectThermal'",ct);
  if(!columns.Contains("PrintSpeedIps"))await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Printers\" ADD COLUMN \"PrintSpeedIps\" TEXT NOT NULL DEFAULT '3'",ct);
  if(!columns.Contains("PrintDensity"))await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Printers\" ADD COLUMN \"PrintDensity\" INTEGER NOT NULL DEFAULT 8",ct);
  if(!columns.Contains("UsbQueueName"))await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Printers\" ADD COLUMN \"UsbQueueName\" TEXT NULL",ct);
  var itemColumns=new HashSet<string>(StringComparer.OrdinalIgnoreCase);await using(var command=connection.CreateCommand()){command.CommandText="PRAGMA table_info('Items')";await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))itemColumns.Add(reader.GetString(1));}
 if(!itemColumns.Contains("ImageDataUrl"))await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Items\" ADD COLUMN \"ImageDataUrl\" TEXT NULL",ct);
  await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS \"DataProtectionKeys\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_DataProtectionKeys\" PRIMARY KEY AUTOINCREMENT, \"FriendlyName\" TEXT NULL, \"Xml\" TEXT NULL)",ct);
  var printJobColumns=new HashSet<string>(StringComparer.OrdinalIgnoreCase);await using(var command=connection.CreateCommand()){command.CommandText="PRAGMA table_info('PrintJobs')";await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))printJobColumns.Add(reader.GetString(1));}
  if(!printJobColumns.Contains("BridgeDeviceId"))await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"PrintJobs\" ADD COLUMN \"BridgeDeviceId\" TEXT NULL",ct);
  if(!printJobColumns.Contains("LeaseExpiresAt"))await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"PrintJobs\" ADD COLUMN \"LeaseExpiresAt\" TEXT NULL",ct);
  await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS \"BridgeDevices\" (\"Id\" TEXT NOT NULL CONSTRAINT \"PK_BridgeDevices\" PRIMARY KEY, \"Name\" TEXT NOT NULL, \"TokenHash\" TEXT NOT NULL, \"Active\" INTEGER NOT NULL, \"LastSeenAt\" TEXT NULL, \"AppVersion\" TEXT NULL, \"Platform\" TEXT NULL, \"RevokedAt\" TEXT NULL, \"CreatedAt\" TEXT NOT NULL, \"UpdatedAt\" TEXT NOT NULL)",ct);
  await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_BridgeDevices_TokenHash\" ON \"BridgeDevices\" (\"TokenHash\")",ct);
  await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS \"BridgePairingCodes\" (\"Id\" TEXT NOT NULL CONSTRAINT \"PK_BridgePairingCodes\" PRIMARY KEY, \"CodeHash\" TEXT NOT NULL, \"ExpiresAt\" TEXT NOT NULL, \"UsedAt\" TEXT NULL, \"CreatedByUserId\" TEXT NOT NULL, \"CreatedAt\" TEXT NOT NULL, \"UpdatedAt\" TEXT NOT NULL)",ct);
  await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_BridgePairingCodes_CodeHash\" ON \"BridgePairingCodes\" (\"CodeHash\")",ct);
 }
}
