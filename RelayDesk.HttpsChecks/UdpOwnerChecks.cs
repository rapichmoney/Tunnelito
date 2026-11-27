using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using RelayDesk;

static class UdpOwnerChecks
{
    public static async Task Run()
    {
        int count=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
        var owners=new[]{new UdpProcessRoutes.Owner(21298,10,@"C:\apps\uTorrent.exe"),new UdpProcessRoutes.Owner(21298,10,@"C:\apps\uTorrent.exe"),new UdpProcessRoutes.Owner(25000,11,@"C:\apps\other.exe"),new UdpProcessRoutes.Owner(25000,10,@"C:\apps\uTorrent.exe"),new UdpProcessRoutes.Owner(26000,12,""),new UdpProcessRoutes.Owner(6771,10,@"C:\apps\uTorrent.exe")};
        Check(UdpProcessRoutes.SelectPorts(owners,[@"c:\APPS\utorrent.EXE"],[]).SequenceEqual([21298]),"Only exclusive known-owner ports qualify; dual-stack duplicates are allowed");
        Check(UdpProcessRoutes.SelectPorts(owners,[],["uTorrent.exe"]).SequenceEqual([21298]),"Process-name rules resolve without case-sensitive path assumptions");
        Check(UdpProcessRoutes.SelectPorts(owners,[@"C:\missing.exe"],[]).Length==0,"Unrelated apps never inherit fallback ports");
        string folder=Path.Combine(Path.GetTempPath(),"Tunnelito-UDP-QA-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string path=Environment.ProcessPath!;
        var config=new JsonObject{
            ["log"]=new JsonObject{["level"]="debug"},
            ["inbounds"]=new JsonArray(new JsonObject{["type"]="tun",["tag"]="qa-tun",["address"]=new JsonArray("172.29.250.1/30"),["auto_route"]=true}),
            ["outbounds"]=new JsonArray(new JsonObject{["type"]="direct",["tag"]="direct"}),
            ["route"]=new JsonObject{["rules"]=new JsonArray(new JsonObject{["process_path"]=new JsonArray(path),["action"]="route",["outbound"]="direct"}),["final"]="direct"}};
        using var controller=new UdpProcessRoutes(config.ToJsonString(),folder);
        var configured=JsonTools.Object(controller.Config);
        Check(controller.RuleCount==1,"One local fallback is created next to the original process rule");
        var fallback=configured["route"]!["rules"]![1]!;
        Check(JsonTools.Text(fallback["rules"]![0]!["inbound"]![0])=="qa-tun"&&JsonTools.Text(fallback["rules"]![0]!["network"])=="udp"&&JsonTools.Text(fallback["rules"]![1]!["invert"])=="true","Fallback is restricted to TUN UDP with an unknown process path");
        string rulesFile=JsonTools.Text(configured["route"]!["rule_set"]![0]!["path"]);
        var store=new Store(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RelayDesk"));
        string core=store.Settings.Engines["sing-box"];
        string runtime=Path.Combine(folder,"runtime.json");File.WriteAllText(runtime,controller.Config);
        var checkStart=new ProcessStartInfo(core){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};foreach(var arg in new[]{"check","-c",runtime})checkStart.ArgumentList.Add(arg);
        using(var check=Process.Start(checkStart)!){string error=await check.StandardError.ReadToEndAsync();await check.WaitForExitAsync();Check(check.ExitCode==0,"Bundled sing-box validates generated UDP owner rule-set: "+error);}
        // Exercise the file watcher without creating TUN routes or modifying the active user's network.
        using var portProbe=new TcpListener(IPAddress.Loopback,0);portProbe.Start();int socksPort=((IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();
        configured["inbounds"]=new JsonArray(new JsonObject{["type"]="socks",["tag"]="qa-socks",["listen"]="127.0.0.1",["listen_port"]=socksPort});
        var testRule=(JsonObject)fallback.DeepClone();testRule["rules"]![0]!["inbound"]=new JsonArray("qa-socks");
        // The harness tests file reload independently of native process recognition, checked above.
        testRule["rules"]![1]!["process_path_regex"]=new JsonArray("__qa_never_matches__");
        configured["route"]!["rules"]=new JsonArray(testRule,new JsonObject{["action"]="reject"});
        File.WriteAllText(runtime,configured.ToJsonString());
        var start=new ProcessStartInfo(core){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};foreach(var arg in new[]{"run","-c",runtime})start.ArgumentList.Add(arg);
        using var engine=Process.Start(start)!;var logs=new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task Read(StreamReader reader){while(await reader.ReadLineAsync() is string line)logs.Enqueue(line);}
        var stderr=Read(engine.StandardError);var stdout=Read(engine.StandardOutput);
        try
        {
            await Task.Delay(700);Check(!engine.HasExited,"Auxiliary core starts without modifying Windows proxy or routes");
            using(var socket=new UdpClient(new IPEndPoint(IPAddress.Any,0)))
            {
                int port=((IPEndPoint)socket.Client.LocalEndPoint!).Port;
                var live=UdpProcessRoutes.ReadOwners();Check(live.Any(o=>o.Port==port&&o.Pid==Environment.ProcessId&&o.Path.Equals(path,StringComparison.OrdinalIgnoreCase)),"Native Windows IPv4 UDP table resolves the real socket owner");
                await Task.Delay(2700);Check(File.ReadAllText(rulesFile).Contains(port.ToString()),"New socket appears automatically in the local rule-set");
                using var echo=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));int echoPort=((IPEndPoint)echo.Client.LocalEndPoint!).Port;
                using var control=new TcpClient();await control.ConnectAsync(IPAddress.Loopback,socksPort);var stream=control.GetStream();
                await stream.WriteAsync(new byte[]{5,1,0});byte[] greeting=new byte[2];await stream.ReadExactlyAsync(greeting);
                await stream.WriteAsync(new byte[]{5,3,0,1,0,0,0,0,0,0});byte[] reply=new byte[10];await stream.ReadExactlyAsync(reply);
                Check(reply[1]==0,"Auxiliary SOCKS UDP association is accepted");
                int relayPort=reply[8]*256+reply[9];byte[] packet={0,0,0,1,127,0,0,1,(byte)(echoPort>>8),(byte)echoPort,65,66,67};
                await socket.SendAsync(packet,new IPEndPoint(IPAddress.Loopback,relayPort));
                var received=await echo.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await echo.SendAsync(received.Buffer,received.RemoteEndPoint);
                var returned=await socket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(returned.Buffer.TakeLast(3).SequenceEqual(new byte[]{65,66,67}),"Running sing-box hot-reloads a newly owned UDP source port and forwards its real payload");
            }
            await Task.Delay(2700);Check(JsonTools.Object(File.ReadAllText(rulesFile))["rules"]!.AsArray().Count==0,"Closing the socket removes its fallback and never creates an empty match-all rule");
            controller.Dispose();Check(!engine.HasExited,"Stopping owner tracking does not terminate unrelated cores");
        }
        finally{if(!engine.HasExited){engine.Kill();await engine.WaitForExitAsync();}await Task.WhenAll(stderr,stdout);}
        Console.WriteLine("COMPLETE: "+count+" UDP owner checks passed.");
    }
}
