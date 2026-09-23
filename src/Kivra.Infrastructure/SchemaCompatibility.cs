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
 }
}
