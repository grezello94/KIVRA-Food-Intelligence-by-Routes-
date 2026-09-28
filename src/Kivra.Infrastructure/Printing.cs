using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Globalization;
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
 public string Generate(Label l, Printer p, TimeZoneInfo tz) {
  var op=TimeZoneInfo.ConvertTime(l.OperationalDateTime,tz);var exp=TimeZoneInfo.ConvertTime(l.ExpiryDateTime,tz);
  var dotsPerMm=p.Dpi/25.4m;int Dot(decimal mm)=>Math.Max(0,(int)Math.Round(mm*dotsPerMm,MidpointRounding.AwayFromZero));
  var widthDots=Dot(p.LabelWidthMm);var margin=Dot(2);var valueX=Dot(p.LabelWidthMm<45?20:28);
  var titleFont=p.Dpi>=300?"4":"3";var bodyFont=p.Dpi>=300?"3":"2";
  var titleCharDots=p.Dpi>=300?24:16;var bodyCharDots=p.Dpi>=300?18:12;
  var name=Truncate(Clean(l.ItemNameSnapshot).ToUpperInvariant(),Math.Max(8,(widthDots-margin*2)/titleCharDots));
  var classification=l.ClassificationSnapshot switch{Classification.NonVeg=>"NON-VEG",Classification.Veg=>"VEG",Classification.Egg=>"EGG",_=>"OTHER"};
  var titleX=Math.Max(margin,(widthDots-name.Length*titleCharDots)/2);
  var markTop=Dot(5.6m);var markSize=Dot(3.2m);var markInset=Math.Max(1,Dot(.55m));var markStroke=Math.Max(1,Dot(.25m));
  var hasFoodMark=l.ClassificationSnapshot is Classification.Veg or Classification.NonVeg;
  var classificationWidth=classification.Length*bodyCharDots+(hasFoodMark?markSize+Dot(1):0);
  var classificationX=Math.Max(margin,(widthDots-classificationWidth)/2);
  var classificationMark=l.ClassificationSnapshot switch
  {
   Classification.Veg =>
    $"BOX {classificationX},{markTop},{classificationX+markSize},{markTop+markSize},{markStroke}\r\n"+
    $"CIRCLE {classificationX+markInset},{markTop+markInset},{Math.Max(2,markSize-markInset*2)},{Math.Max(1,Dot(.65m))}\r\n",
   Classification.NonVeg =>
    $"BOX {classificationX},{markTop},{classificationX+markSize},{markTop+markSize},{markStroke}\r\n"+
    $"DIAGONAL {classificationX+markSize/2},{markTop+markInset},{classificationX+markInset},{markTop+markSize-markInset},{Math.Max(1,Dot(.55m))}\r\n"+
    $"DIAGONAL {classificationX+markInset},{markTop+markSize-markInset},{classificationX+markSize-markInset},{markTop+markSize-markInset},{Math.Max(1,Dot(.55m))}\r\n"+
    $"DIAGONAL {classificationX+markSize-markInset},{markTop+markSize-markInset},{classificationX+markSize/2},{markTop+markInset},{Math.Max(1,Dot(.55m))}\r\n",
   _ => string.Empty
  };
  var media=p.MediaSensingMode switch{"BlackMark"=>$"BLINE {Num(p.GapMm)} mm,0 mm","Continuous"=>"GAP 0 mm,0 mm",_=>$"GAP {Num(p.GapMm)} mm,0 mm"};
  return $"SIZE {Num(p.LabelWidthMm)} mm,{Num(p.LabelHeightMm)} mm\r\n{media}\r\nSPEED {Num(p.PrintSpeedIps)}\r\nDENSITY {p.PrintDensity}\r\nSET RIBBON {(p.PrintMethod=="ThermalTransfer"?"ON":"OFF")}\r\nDIRECTION 1\r\nREFERENCE 0,0\r\nCLS\r\n"+
   $"TEXT {titleX},{Dot(2.8m)},\"{titleFont}\",0,1,1,\"{name}\"\r\n"+
   classificationMark+
   $"TEXT {(hasFoodMark?classificationX+markSize+Dot(1):classificationX)},{Dot(6.2m)},\"{bodyFont}\",0,1,1,\"{classification}\"\r\n"+
   $"BAR {margin},{Dot(9.3m)},{Math.Max(1,widthDots-margin*2)},1\r\n"+
   $"TEXT {margin},{Dot(10.3m)},\"{bodyFont}\",0,1,1,\"{Truncate(Clean(l.DateTerminologySnapshot).ToUpperInvariant(),9)}\"\r\n"+
   $"TEXT {valueX},{Dot(10.3m)},\"{bodyFont}\",0,1,1,\"{op:dd MMM yyyy}\"\r\n"+
   $"TEXT {valueX},{Dot(13m)},\"{bodyFont}\",0,1,1,\"{op:hh:mm tt}\"\r\n"+
   $"TEXT {margin},{Dot(16.4m)},\"{bodyFont}\",0,1,1,\"USE BY\"\r\n"+
   $"TEXT {valueX},{Dot(16.4m)},\"{bodyFont}\",0,1,1,\"{exp:dd MMM yyyy}\"\r\n"+
   $"TEXT {valueX},{Dot(19.1m)},\"{bodyFont}\",0,1,1,\"{exp:hh:mm tt}\"\r\n"+
   $"BAR {margin},{Dot(Math.Max(22,p.LabelHeightMm-2))},{Math.Max(1,widthDots-margin*2)},1\r\n"+
   "PRINT 1,1\r\n";
 }
 static string Num(decimal value)=>value.ToString("0.##",CultureInfo.InvariantCulture);
 static string Truncate(string value,int max)=>value.Length<=max?value:value[..Math.Max(1,max-1)]+"~";
 static string Clean(string input) => input.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
}
