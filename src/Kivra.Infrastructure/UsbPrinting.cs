using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kivra.Application;
using Kivra.Domain;
namespace Kivra.Infrastructure;
public sealed class UsbPrinterCatalog {
 public async Task<IReadOnlyList<string>> GetQueuesAsync(CancellationToken ct) {
  if(OperatingSystem.IsWindows())return WindowsPrinterQueues.GetQueues();
  var start=new ProcessStartInfo{RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
  start.FileName="lpstat";start.ArgumentList.Add("-p");
  try{using var process=Process.Start(start);if(process is null)return[];var output=await process.StandardOutput.ReadToEndAsync(ct);await process.WaitForExitAsync(ct);if(process.ExitCode!=0)return[];return output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(ParseUnixQueue).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToList();}catch(Exception ex)when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){return[];}
 }
 static string ParseUnixQueue(string line){const string prefix="printer ";if(!line.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))return string.Empty;var rest=line[prefix.Length..];var end=rest.IndexOf(' ');return end<0?rest:rest[..end];}

 static class WindowsPrinterQueues {
  const int PrinterEnumLocal=2,PrinterEnumConnections=4,ErrorInsufficientBuffer=122;
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct PrinterInfo4{public IntPtr PrinterName;public IntPtr ServerName;public uint Attributes;}
  [DllImport("winspool.drv",SetLastError=true,CharSet=CharSet.Unicode)]static extern bool EnumPrinters(int flags,string? name,int level,IntPtr buffer,int size,out int needed,out int returned);
  public static IReadOnlyList<string> GetQueues(){
   const int flags=PrinterEnumLocal|PrinterEnumConnections;
   EnumPrinters(flags,null,4,IntPtr.Zero,0,out var needed,out _);
   var error=Marshal.GetLastWin32Error();
   if(needed==0){if(error==0)return[];throw new System.ComponentModel.Win32Exception(error,"Windows could not enumerate printer queues.");}
   if(error!=ErrorInsufficientBuffer)throw new System.ComponentModel.Win32Exception(error,"Windows could not size the printer queue list.");
   var buffer=Marshal.AllocHGlobal(needed);
   try{
    if(!EnumPrinters(flags,null,4,buffer,needed,out _,out var returned))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Windows could not enumerate printer queues.");
    var size=Marshal.SizeOf<PrinterInfo4>();
    var queues=new List<string>(returned);
    for(var i=0;i<returned;i++){var info=Marshal.PtrToStructure<PrinterInfo4>(IntPtr.Add(buffer,i*size));var queue=Marshal.PtrToStringUni(info.PrinterName);if(!string.IsNullOrWhiteSpace(queue))queues.Add(queue);}
    return queues.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToList();
   }finally{Marshal.FreeHGlobal(buffer);}
  }
 }
}
public sealed class TscTsplUsbPrinter(UsbPrinterCatalog catalog):ILabelPrinter {
 public async Task<PrinterResult> TestConnectionAsync(Printer printer,CancellationToken ct){if(string.IsNullOrWhiteSpace(printer.UsbQueueName))return new(false,"USB printer queue is not configured.");var queues=await catalog.GetQueuesAsync(ct);return queues.Contains(printer.UsbQueueName,StringComparer.OrdinalIgnoreCase)?new(true):new(false,$"USB printer queue '{printer.UsbQueueName}' was not found on the server.");}
 public async Task<PrinterResult> PrintAsync(Printer printer,string tspl,CancellationToken ct){var connection=await TestConnectionAsync(printer,ct);if(!connection.Success)return connection;try{if(OperatingSystem.IsWindows())return WindowsRawPrinter.Send(printer.UsbQueueName!,Encoding.ASCII.GetBytes(tspl));var start=new ProcessStartInfo{FileName="lp",RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("-d");start.ArgumentList.Add(printer.UsbQueueName!);start.ArgumentList.Add("-o");start.ArgumentList.Add("raw");using var process=Process.Start(start);if(process is null)return new(false,"Could not start the USB print process.");await process.StandardInput.WriteAsync(tspl.AsMemory(),ct);process.StandardInput.Close();var error=await process.StandardError.ReadToEndAsync(ct);await process.WaitForExitAsync(ct);return process.ExitCode==0?new(true):new(false,$"USB print queue rejected the job: {error.Trim()}");}catch(OperationCanceledException){return new(false,"USB print job timed out.");}catch(Exception ex){return new(false,$"USB print failed: {ex.Message}");}}
  internal static class WindowsRawPrinter {
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct DocInfo{[MarshalAs(UnmanagedType.LPWStr)]public string DocumentName;[MarshalAs(UnmanagedType.LPWStr)]public string? OutputFile;[MarshalAs(UnmanagedType.LPWStr)]public string DataType;}
  [DllImport("winspool.drv",SetLastError=true,CharSet=CharSet.Unicode)]static extern bool OpenPrinter(string name,out IntPtr handle,IntPtr defaults);
  [DllImport("winspool.drv",SetLastError=true)]static extern bool ClosePrinter(IntPtr handle);
  [DllImport("winspool.drv",SetLastError=true,CharSet=CharSet.Unicode)]static extern int StartDocPrinter(IntPtr handle,int level,ref DocInfo info);
  [DllImport("winspool.drv",SetLastError=true)]static extern bool EndDocPrinter(IntPtr handle);
  [DllImport("winspool.drv",SetLastError=true)]static extern bool StartPagePrinter(IntPtr handle);
  [DllImport("winspool.drv",SetLastError=true)]static extern bool EndPagePrinter(IntPtr handle);
  [DllImport("winspool.drv",SetLastError=true)]static extern bool WritePrinter(IntPtr handle,IntPtr bytes,int count,out int written);
  public static PrinterResult Send(string queue,byte[] payload){if(!OpenPrinter(queue,out var handle,IntPtr.Zero))return Error("open printer");try{var info=new DocInfo{DocumentName="KIVRA Kitchen Label",DataType="RAW",OutputFile=null};if(StartDocPrinter(handle,1,ref info)==0)return Error("start document");try{if(!StartPagePrinter(handle))return Error("start page");var memory=Marshal.AllocCoTaskMem(payload.Length);try{Marshal.Copy(payload,0,memory,payload.Length);if(!WritePrinter(handle,memory,payload.Length,out var written)||written!=payload.Length)return Error("write data");return new(true);}finally{Marshal.FreeCoTaskMem(memory);EndPagePrinter(handle);}}finally{EndDocPrinter(handle);}}finally{ClosePrinter(handle);}}
  static PrinterResult Error(string operation)=>new(false,$"Windows USB queue could not {operation} (error {Marshal.GetLastWin32Error()}).");
 }
}
public sealed class EpsonEscPosUsbPrinter(UsbPrinterCatalog catalog):ILabelPrinter {
 public async Task<PrinterResult> TestConnectionAsync(Printer printer,CancellationToken ct){if(string.IsNullOrWhiteSpace(printer.UsbQueueName))return new(false,"USB printer queue is not configured.");var queues=await catalog.GetQueuesAsync(ct);return queues.Contains(printer.UsbQueueName,StringComparer.OrdinalIgnoreCase)?new(true):new(false,$"USB printer queue '{printer.UsbQueueName}' was not found on the server.");}
 public async Task<PrinterResult> PrintAsync(Printer printer,string tspl,CancellationToken ct){var connection=await TestConnectionAsync(printer,ct);if(!connection.Success)return connection;try{if(!OperatingSystem.IsWindows())return new(false,"Epson receipt printing is available only on Windows.");return TscTsplUsbPrinter.WindowsRawPrinter.Send(printer.UsbQueueName!,Encoding.ASCII.GetBytes(Render(tspl)));}catch(Exception ex){return new(false,$"Epson receipt print failed: {ex.Message}");}}
 internal static string Render(string tspl){var values=System.Text.RegularExpressions.Regex.Matches(tspl,"TEXT \\d+,\\d+,\\\"[^\\\"]+\\\",0,1,1,\\\"(?<value>[^\\\"]*)\\\"").Select(x=>x.Groups["value"].Value).ToList();string value(int index,string fallback="")=>values.Count>index?values[index]:fallback;const string esc="\x1b";return $"{esc}@{esc}a\x01{esc}! \x20{value(0,"KIVRA LABEL")}\n{esc}!\x00-------------------------------\n{esc}a\x00{value(2,"PREPARED"),-14}{value(3)}\n{' ',14}{value(4)}\n\n{value(5,"USE BY"),-14}{value(6)}\n{' ',14}{value(7)}\n-------------------------------\n{value(8)}\n{esc}a\x02{value(9)}\n\n\n";}
}
