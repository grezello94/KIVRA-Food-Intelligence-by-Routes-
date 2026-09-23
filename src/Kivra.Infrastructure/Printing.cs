using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text;
using Kivra.Application;
using Kivra.Domain;
namespace Kivra.Infrastructure;
public sealed class FakeLabelPrinter : ILabelPrinter {
 public ConcurrentQueue<string> Commands { get; } = new();
 public Task<PrinterResult> PrintAsync(Printer printer, string tspl, CancellationToken ct) { if (!printer.Enabled) return Task.FromResult(new PrinterResult(false, "Printer is disabled.")); Commands.Enqueue(tspl); return Task.FromResult(new PrinterResult(true)); }
 public Task<PrinterResult> TestConnectionAsync(Printer printer, CancellationToken ct) => Task.FromResult(printer.Enabled ? new PrinterResult(true) : new PrinterResult(false, "Printer is disabled."));
}
public sealed class TscTsplNetworkPrinter : ILabelPrinter {
 public async Task<PrinterResult> PrintAsync(Printer printer, string tspl, CancellationToken ct) { try { using var client = new TcpClient(); await client.ConnectAsync(printer.IpAddress, printer.TcpPort, ct); await using var stream = client.GetStream(); await stream.WriteAsync(Encoding.ASCII.GetBytes(tspl), ct); await stream.FlushAsync(ct); return new(true); } catch (OperationCanceledException) { return new(false, "Connection to printer timed out."); } catch (SocketException ex) { return new(false, $"Could not reach {printer.Name} at {printer.IpAddress}:{printer.TcpPort}: {ex.Message}"); } catch (Exception ex) { return new(false, $"Printer error: {ex.Message}"); } }
 public async Task<PrinterResult> TestConnectionAsync(Printer p, CancellationToken ct) { try { using var client = new TcpClient(); await client.ConnectAsync(p.IpAddress, p.TcpPort, ct); return new(true); } catch (Exception ex) { return new(false, $"Connection failed: {ex.Message}"); } }
}
public interface ITsplGenerator { string Generate(Label label, Printer printer, TimeZoneInfo timeZone); }
public sealed class TsplGenerator : ITsplGenerator {
 public string Generate(Label l, Printer p, TimeZoneInfo tz) { var op=TimeZoneInfo.ConvertTime(l.OperationalDateTime,tz);var exp=TimeZoneInfo.ConvertTime(l.ExpiryDateTime,tz);var name=Clean(l.ItemNameSnapshot).ToUpperInvariant();var media=p.MediaSensingMode switch{"BlackMark"=>$"BLINE {p.GapMm:0.##} mm,0 mm","Continuous"=>"GAP 0 mm,0 mm",_=>$"GAP {p.GapMm:0.##} mm,0 mm"};return $"SIZE {p.LabelWidthMm:0.##} mm,{p.LabelHeightMm:0.##} mm\r\n{media}\r\nSPEED {p.PrintSpeedIps:0.#}\r\nDENSITY {p.PrintDensity}\r\nDIRECTION 1\r\nCLS\r\nTEXT 16,10,\"3\",0,1,1,\"{name}\"\r\nTEXT 16,39,\"1\",0,1,1,\"{l.ClassificationSnapshot.ToString().ToUpperInvariant()}\"\r\nBAR 16,59,368,1\r\nTEXT 16,69,\"2\",0,1,1,\"{Clean(l.DateTerminologySnapshot)}\"\r\nTEXT 155,69,\"2\",0,1,1,\"{op:dd MMM yyyy}\"\r\nTEXT 155,91,\"2\",0,1,1,\"{op:hh:mm tt}\"\r\nTEXT 16,124,\"3\",0,1,1,\"USE BY\"\r\nTEXT 155,121,\"3\",0,1,1,\"{exp:dd MMM yyyy}\"\r\nTEXT 155,149,\"3\",0,1,1,\"{exp:hh:mm tt}\"\r\nBAR 16,183,368,1\r\nTEXT 16,195,\"2\",0,1,1,\"{Clean(l.StorageLocationSnapshot).ToUpperInvariant()}\"\r\nTEXT 250,195,\"2\",0,1,1,\"{l.LabelCode}\"\r\nPRINT 1,1\r\n"; }
 static string Clean(string input) => input.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
}
