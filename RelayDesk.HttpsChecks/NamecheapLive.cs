using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayDesk;

static class NamecheapLive
{
    static int Port(){var s=new TcpListener(IPAddress.Loopback,0);s.Start();int port=((IPEndPoint)s.LocalEndpoint).Port;s.Stop();return port;}
    public static async Task Run()
    {
        string data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RelayDesk");
        var store=new Store(data);var profile=store.Profiles.Single(p=>p.Id==store.Settings.LastConnectedProfileId);
        string runtime=Path.Combine(data,profile.Id+".runtime.json");var original=JsonTools.Object(File.ReadAllText(runtime));
        string folder=Path.Combine(data,"https-live-check",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        int normal=Port(),vpn=Port(),direct=Port(),intercept=Port();
        var root=(JsonObject)original.DeepClone();root.Remove("experimental");
        var clonedOutbounds=root["outbounds"]!.AsArray();
        foreach(var item in clonedOutbounds.Where(n=>n?["tag"]?.GetValue<string>()=="tunnelito-https-egress").ToArray())clonedOutbounds.Remove(item);
        root["inbounds"]=new JsonArray(new JsonObject{["type"]="http",["tag"]="test-normal",["listen"]="127.0.0.1",["listen_port"]=normal});
        var route=root["route"]!.AsObject();route["rules"]=new JsonArray();route["final"]="test-normal-direct";route.Remove("auto_detect_interface");route["default_interface"]=DirectNetwork.Interface().Name;
        root["outbounds"]!.AsArray().Add(new JsonObject{["type"]="direct",["tag"]="test-normal-direct"});
        string config=HttpsAutoPolicy.ConfigureCore(root.ToJsonString(),vpn,direct);var file=Path.Combine(folder,"test.runtime.json");File.WriteAllText(file,config);
        var start=new ProcessStartInfo(store.Settings.Engines["sing-box"]){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string arg in new[]{"run","-c",file})start.ArgumentList.Add(arg);
        using var core=Process.Start(start)!;_ =core.StandardOutput.ReadToEndAsync();var error=core.StandardError.ReadToEndAsync();
        try
        {
            await Task.Delay(1500);if(core.HasExited)throw new Exception("Auxiliary core failed: "+ProgramJournal.Clean(await error));
            using var cert=HttpsCertificate.Load(data);
            var settings=new Preferences{SharedQuickRoutes=[new("domain","www.namecheap.com","@proxy"),new("domain","namecheap.com","@proxy")]};
            using var inspector=new HttpsInspectionProxy(data,settings,profile,new Uri("http://127.0.0.1:"+normal),vpn,direct,intercept,requireTrusted:false);
            var endpoints=new{Folder=folder,Pid=Environment.ProcessId,CorePid=core.Id,Direct="http://127.0.0.1:"+direct,VPN="http://127.0.0.1:"+vpn,Automatic=inspector.Endpoint.AbsoluteUri,CertificateTrusted=HttpsCertificate.Trusted(data),RemovedInTest=new[]{"www.namecheap.com","static.nc-img.com"}};
            File.WriteAllText(Path.Combine(data,"https-live-check","current.json"),JsonSerializer.Serialize(endpoints));
            Console.WriteLine("Namecheap test ready. Ordinary user settings and running VPN were not modified.");
            DateTime end=DateTime.UtcNow.AddMinutes(10);
            while(DateTime.UtcNow<end&&!File.Exists(Path.Combine(folder,"stop")))
            {
                if(core.HasExited)throw new Exception("Auxiliary core exited.");
                File.WriteAllText(Path.Combine(folder,"summary.json"),JsonSerializer.Serialize(new{Events=inspector.Events,Temporary=inspector.Policy.Rules}));await Task.Delay(500);
            }
        }
        finally{if(!core.HasExited){core.Kill(true);await core.WaitForExitAsync();}}
    }
}
