using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Diagnostics;
using RelayDesk;

static class ConnectionOwnerChecks
{
    public static async Task Run()
    {
        int count=0;void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);count++;}
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var map=new ProxyConnectionOwners(port);using var first=new TcpClient();await first.ConnectAsync(IPAddress.Loopback,port);using var acceptedFirst=await listener.AcceptTcpClientAsync();
        using var second=new TcpClient();await second.ConnectAsync(IPAddress.Loopback,port);using var acceptedSecond=await listener.AcceptTcpClientAsync();
        map.Track(first.Client,new(10,@"C:\QA\browser.exe","browser.exe"));map.Track(second.Client,new(11,@"C:\QA\steamwebhelper.exe","steamwebhelper.exe"));
        ConnectionRow Row(Socket socket,string id)=>new(id,"Tunnelito.exe","store.example.test:443","TCP","Через VPN / прокси","proxy","final","","↑ 1 / ↓ 2 Б"){
            ProcessPath=Environment.ProcessPath!,Host="store.example.test",SourceAddress="127.0.0.1",SourcePort=((IPEndPoint)socket.LocalEndPoint!).Port,TotalBytes=3};
        var a=Row(first.Client,"a");var b=Row(second.Client,"b");var projected=map.Project([a,b]);
        Check(projected[0].Application=="browser.exe"&&projected[1].Application=="steamwebhelper.exe","Two applications requesting the same hostname keep separate owners by exact socket");
        Check(projected[0].ProcessPath==@"C:\QA\browser.exe"&&projected[0].CanEditApplication&&projected[0].OriginalOwner,"Application actions target the real owner's executable");
        Check(projected[0].Id==a.Id&&projected[0].Route==a.Route&&projected[0].Outbound==a.Outbound&&projected[0].TotalBytes==a.TotalBytes,"Attribution preserves IDs, actual routes and traffic counters");
        Check(map.Project([a with{SourcePort=1}])[0].Application=="Tunnelito.exe","Unmatched sockets are never attributed by hostname guesses");
        Check(map.Project([a with{ProcessPath=@"C:\QA\other.exe"}])[0].ProcessPath==@"C:\QA\other.exe","Kernel-confirmed unrelated applications are preserved");
        Check(map.Project([a with{SourceAddress="192.0.2.1"}])[0].Application=="Tunnelito.exe","Non-loopback traffic is not mistaken for a proxy's upstream socket");
        Check(map.Project([a with{ProcessPath=""}])[0].Application=="browser.exe","Exact socket attribution also works when the kernel omitted the process path");
        map.Track(first.Client,null);var unknown=map.Project([a])[0];
        Check(unknown.Application=="Владелец HTTPS не определён"&&!unknown.CanEditApplication,"Unresolved original owner is explicit and cannot create a rule for Tunnelito");
        first.Dispose();Check(map.Project([a])[0].Application=="Tunnelito.exe","Closed sockets are removed before their port can be reused");
        var profiles=new[]{new Profile{Id="slow",Name="same"},new Profile{Id="fast",Name="same"},new Profile{Id="unknown"},new Profile{Id="failed"}};
        var ordered=ServerLatencyOrder.Sort(profiles,new Dictionary<string,long?>{{"slow",1076},{"fast",50},{"failed",null}}).Select(p=>p.Id).ToArray();
        Check(ordered.SequenceEqual(new[]{"fast","slow","unknown","failed"}),"Numeric latency sorts 50 before 1076, with unknown/failed last and stable ties");
        using var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();int probePort=((IPEndPoint)probe.LocalEndpoint).Port;
        var duplicateNames=new[]{new Profile{Id="one",Name="same",Core="sing-box",Managed=true,Host="127.0.0.1",Port=probePort},new Profile{Id="two",Name="same",Core="sing-box",Managed=true,Host="127.0.0.1",Port=probePort}};
        var results=new System.Collections.Concurrent.ConcurrentBag<ServerLatency>();
        await ServerLatencyProbe.Run(duplicateNames,r=>results.Add(r),CancellationToken.None);
        Check(results.Select(r=>r.ProfileId).Distinct().Count()==2&&results.All(r=>r.Milliseconds.HasValue),"Ping results identify profiles by ID instead of duplicate display names");
        var parsed=SubscriptionWindow.ParseProfile("vless://11111111-1111-4111-8111-111111111111@node.example.invalid:8446?type=ws&path=%2F&security=none#QA");
        Check(parsed.Profile.Protocol=="VLESS"&&parsed.Profile.Port==8446,"Subscription dialog recognizes a standalone VLESS WebSocket profile");
        const string sample="vless://11111111-1111-4111-8111-111111111111@node.example.invalid:8446?security=none&fp=chrome&type=ws&path=%2Fvless&packetEncoding=xudp&encryption=none#QA";
        var result=SubscriptionWindow.ParseProfile(sample);var outbound=JsonTools.Object(result.Profile.Config)["outbounds"]![0]!;
        Check(outbound["tls"]==null&&outbound["packet_encoding"]!.ToString()=="xudp"&&outbound["transport"]!["type"]!.ToString()=="ws"&&outbound["transport"]!["path"]!.ToString()=="/vless","No-TLS VLESS with a redundant fingerprint preserves WebSocket path and XUDP without enabling TLS");
        Check(result.Summary.Contains("fp не применяется")&&Subscriptions.Parse(sample).Profiles.Count==1,"Preview explains ignored fingerprint and subscription parsing accepts the same combination");
        bool rejected=false;try{_=ImportParser.Parse(sample.Replace("#QA","&sni=secure.example.invalid#QA"));}catch(FormatException){rejected=true;}
        Check(rejected,"Other conflicting TLS parameters remain rejected with security=none");
        bool clear=false;try{_=SubscriptionWindow.ParseProfile(sample.Replace("#QA","&sni=secure.example.invalid#QA"));}catch(FormatException error){clear=error.Message.Contains("TLS")&&!error.Message.Contains("В подписке нет");}
        Check(clear,"Standalone link errors retain their actual cause instead of a generic subscription failure");
        string config=JsonTools.Build(result.Profile);string folder=Path.Combine(Path.GetTempPath(),"Tunnelito-VLESS-QA-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string file=Path.Combine(folder,"runtime.json");File.WriteAllText(file,config);
        var store=new Store(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RelayDesk"));
        var start=new ProcessStartInfo(store.Settings.Engines["sing-box"]){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};foreach(string arg in new[]{"check","-c",file})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;string stderr=await process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
        Check(process.ExitCode==0,"Installed sing-box accepts the generated no-TLS WebSocket/XUDP configuration: "+stderr);
        Console.WriteLine("COMPLETE: "+count+" connection ownership, latency and import checks passed.");
    }
}
