using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
namespace Kivra.Infrastructure;
public sealed record PrinterCandidate(string IpAddress,int Port,string Network,long ResponseTimeMs);
public sealed record PrinterDiscoveryResult(IReadOnlyList<string> Networks,IReadOnlyList<PrinterCandidate> Candidates);
public sealed class PrinterDiscoveryService(IConfiguration configuration) {
 public async Task<PrinterDiscoveryResult> DiscoverAsync(int port,IPAddress? clientAddress,CancellationToken ct) {
  if(port is <1 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
  var addresses=NetworkInterface.GetAllNetworkInterfaces().Where(x=>x.OperationalStatus==OperationalStatus.Up&&x.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel).SelectMany(x=>x.GetIPProperties().UnicastAddresses).Where(x=>x.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(x.Address)&&!x.Address.ToString().StartsWith("169.254.")).Select(x=>x.Address).Distinct().ToList();
  var configured=(configuration["PrinterDiscovery:Networks"]??string.Empty).Split([',',';'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(Normalize24).Where(x=>x is not null).Cast<string>();
  var clientNetwork=clientAddress is not null&&IsPrivateV4(clientAddress)?Network24(clientAddress.MapToIPv4()):null;
  var networks=addresses.Select(Network24).Concat(configured).Concat(clientNetwork is null?[]:[clientNetwork]).Distinct().ToList();var candidates=new List<PrinterCandidate>();
  foreach(var network in networks) {
   var prefix=network[..network.LastIndexOf('.')];
   await Parallel.ForEachAsync(Enumerable.Range(1,254),new ParallelOptions{MaxDegreeOfParallelism=32,CancellationToken=ct},async(host,token)=>{var ip=$"{prefix}.{host}";var sw=Stopwatch.StartNew();try{using var client=new TcpClient();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMilliseconds(350));await client.ConnectAsync(ip,port,timeout.Token);sw.Stop();lock(candidates)candidates.Add(new PrinterCandidate(ip,port,network,sw.ElapsedMilliseconds));}catch(Exception ex) when(ex is SocketException or OperationCanceledException){}});
  }
  return new PrinterDiscoveryResult(networks,candidates.OrderBy(x=>IPAddress.Parse(x.IpAddress).GetAddressBytes(),ByteArrayComparer.Instance).ToList());
 }
 static string? Normalize24(string value){var addressPart=value.Split('/')[0];return IPAddress.TryParse(addressPart,out var address)&&address.AddressFamily==AddressFamily.InterNetwork?Network24(address):null;}
 static bool IsPrivateV4(IPAddress address){if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();if(address.AddressFamily!=AddressFamily.InterNetwork)return false;var b=address.GetAddressBytes();return b[0]==10||(b[0]==172&&b[1] is >=16 and <=31)||(b[0]==192&&b[1]==168);}
 static string Network24(IPAddress address){var bytes=address.GetAddressBytes();return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";}
 sealed class ByteArrayComparer:IComparer<byte[]>{public static readonly ByteArrayComparer Instance=new();public int Compare(byte[]? x,byte[]? y){if(x is null||y is null)return 0;for(var i=0;i<Math.Min(x.Length,y.Length);i++){var c=x[i].CompareTo(y[i]);if(c!=0)return c;}return x.Length.CompareTo(y.Length);}}
}
