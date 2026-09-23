using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
namespace Kivra.Infrastructure;
public sealed record PrinterCandidate(string IpAddress,int Port,string Network,long ResponseTimeMs);
public sealed record PrinterDiscoveryResult(IReadOnlyList<string> Networks,IReadOnlyList<PrinterCandidate> Candidates);
public sealed class PrinterDiscoveryService {
 public async Task<PrinterDiscoveryResult> DiscoverAsync(int port,CancellationToken ct) {
  if(port is <1 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
  var addresses=NetworkInterface.GetAllNetworkInterfaces().Where(x=>x.OperationalStatus==OperationalStatus.Up&&x.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel).SelectMany(x=>x.GetIPProperties().UnicastAddresses).Where(x=>x.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(x.Address)&&!x.Address.ToString().StartsWith("169.254.")).Select(x=>x.Address).Distinct().ToList();
  var networks=addresses.Select(Network24).Distinct().ToList();var candidates=new List<PrinterCandidate>();
  foreach(var network in networks) {
   var prefix=network[..network.LastIndexOf('.')];
   await Parallel.ForEachAsync(Enumerable.Range(1,254),new ParallelOptions{MaxDegreeOfParallelism=32,CancellationToken=ct},async(host,token)=>{var ip=$"{prefix}.{host}";var sw=Stopwatch.StartNew();try{using var client=new TcpClient();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMilliseconds(350));await client.ConnectAsync(ip,port,timeout.Token);sw.Stop();lock(candidates)candidates.Add(new PrinterCandidate(ip,port,network,sw.ElapsedMilliseconds));}catch(Exception ex) when(ex is SocketException or OperationCanceledException){}});
  }
  return new PrinterDiscoveryResult(networks,candidates.OrderBy(x=>IPAddress.Parse(x.IpAddress).GetAddressBytes(),ByteArrayComparer.Instance).ToList());
 }
 static string Network24(IPAddress address){var bytes=address.GetAddressBytes();return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";}
 sealed class ByteArrayComparer:IComparer<byte[]>{public static readonly ByteArrayComparer Instance=new();public int Compare(byte[]? x,byte[]? y){if(x is null||y is null)return 0;for(var i=0;i<Math.Min(x.Length,y.Length);i++){var c=x[i].CompareTo(y[i]);if(c!=0)return c;}return x.Length.CompareTo(y.Length);}}
}
