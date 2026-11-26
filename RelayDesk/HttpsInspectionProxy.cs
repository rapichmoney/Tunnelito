using System.Diagnostics;
using System.Net;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace RelayDesk;
public sealed class HttpsInspectionProxy:IDisposable
{
    sealed class Attempt
    {
        public string Host="",Parent="";
        public bool Proxy,Retried,ParentProxied;
    }
    readonly ProxyServer server=new(false,false,false){ForwardToUpstreamGateway=false,EnableConnectionPool=false,EnableTcpServerConnectionPrefetch=false};
    readonly HttpsAutoPolicy policy=new();
    readonly Preferences settings;
    readonly Profile profile;
    readonly ExternalProxy normal,vpn,direct;
    readonly Action<string> report;
    readonly System.Security.Cryptography.X509Certificates.X509Certificate2 authority;
    readonly Queue<string> events=new();
    public Uri Endpoint{get;}
    public string[] Events{get{lock(events)return events.ToArray();}}
    public HttpsAutoPolicy Policy=>policy;
    public HttpsInspectionProxy(string folder,Preferences preferences,Profile active,Uri normalEndpoint,int vpnPort,int directPort,int port,Action<string>? log=null,bool requireTrusted=true)
    {
        if(requireTrusted&&!HttpsCertificate.Trusted(folder))throw new InvalidOperationException("Сертификат HTTPS не доверен. Установи его в настройках Tunnelito.");
        settings=System.Text.Json.JsonSerializer.Deserialize<Preferences>(System.Text.Json.JsonSerializer.Serialize(preferences))!;profile=active;
        report=text=>{lock(events){events.Enqueue(DateTime.Now.ToString("HH:mm:ss")+" "+text);while(events.Count>100)events.Dequeue();}log?.Invoke(text);};
        normal=new(normalEndpoint.Host,normalEndpoint.Port){BypassLocalhost=false};vpn=new("127.0.0.1",vpnPort){BypassLocalhost=false};direct=new("127.0.0.1",directPort){BypassLocalhost=false};
        authority=HttpsCertificate.Load(folder,false);server.CertificateManager.RootCertificate=authority;server.CertificateManager.SaveFakeCertificates=false;
        // Keep server certificate validation enabled. Never save request/response bodies or cookies.
        server.BeforeRequest+=Request;server.BeforeResponse+=Response;
        server.ExceptionFunc=_=>report("Ошибка HTTPS-транспорта. Содержимое запроса не сохранено.");
        var endpoint=new ExplicitProxyEndPoint(IPAddress.Loopback,port,true);
        endpoint.BeforeTunnelConnectRequest+=(_,e)=>
        {
            string host=e.HttpClient.Request.RequestUri.IdnHost;
            if(!HttpsAutoPolicy.PublicHost(host)||settings.HttpsExcludedDomains.Any(d=>RuleMatching.DomainIn(host,d)))e.DecryptSsl=false;
            e.CustomUpStreamProxy=normal;return Task.CompletedTask;
        };
        server.AddEndPoint(endpoint);
        try{server.Start();}catch{server.Dispose();authority.Dispose();throw;}
        Endpoint=new Uri("http://127.0.0.1:"+endpoint.Port);
    }
    IReadOnlyList<QuickRoute> Rules=>SharedRouting.Rules(profile,settings);
    Task Request(object sender,SessionEventArgs e)
    {
        if(e.UserData is Attempt existing&&existing.Retried){e.CustomUpStreamProxy=vpn;return Task.CompletedTask;}
        var request=e.HttpClient.Request;string host=request.RequestUri.IdnHost.ToLowerInvariant();
        string parent=HttpsAutoPolicy.Host(request.Headers.GetFirstHeader("Referer")?.Value);
        if(parent.Length==0)parent=HttpsAutoPolicy.Host(request.Headers.GetFirstHeader("Origin")?.Value);
        string route="";
        string path="",name="";
        try{using var process=Process.GetProcessById(e.HttpClient.ProcessId.Value);path=process.MainModule?.FileName??"";name=process.ProcessName+".exe";}catch(Exception ex) when(ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException){}
        // Preserve first-match semantics, including application rules, before trying temporary repairs.
        var match=Rules.FirstOrDefault(r=>RuleMatching.Matches(new RouteRule{Kind=r.Kind,Value=r.Value},new("",name,path,host,IPAddress.TryParse(host,out _)?host:"","",request.RequestUri.Port)));
        if(match!=null)route=match.Direct||match.Zapret?"direct":"proxy";
        if(settings.RoutingPreset=="proxy")route="proxy";else if(settings.RoutingPreset=="direct")route="direct";
        if(route.Length==0&&settings.FallbackRoute is "proxy" or "direct" or "zapret")route=settings.FallbackRoute=="proxy"?"proxy":"direct";
        bool temporary=policy.Learnt(host)&&parent.Length>0&&(HttpsAutoPolicy.DomainRoute(parent,Rules)=="proxy"||policy.Learnt(parent))&&HttpsAutoPolicy.DomainRoute(host,Rules)!="direct";
        bool useVpn=route=="proxy"||temporary;
        var parentRule=Rules.FirstOrDefault(r=>RuleMatching.Matches(new RouteRule{Kind=r.Kind,Value=r.Value},new("",name,path,parent,"","",443)));
        bool parentProxy=parentRule!=null&&!parentRule.Direct&&!parentRule.Zapret||policy.Learnt(parent);
        e.UserData=new Attempt{Host=host,Parent=parent,Proxy=useVpn,ParentProxied=parentProxy};
        e.CustomUpStreamProxy=useVpn?vpn:route=="direct"?direct:normal;
        return Task.CompletedTask;
    }
    Task Response(object sender,SessionEventArgs e)
    {
        if(e.UserData is not Attempt state)return Task.CompletedTask;
        int status=e.HttpClient.Response.StatusCode;
        if(state.Retried)
        {
            bool learned=policy.Complete(state.Host,state.Parent,status);
            report(state.Parent+" → "+state.Host+": 403 → "+status+(learned?". VPN на 30 минут для связанных запросов.":". Временное правило не сохранено."));
            return Task.CompletedTask;
        }
        if(settings.RoutingPreset=="rules"&&state.ParentProxied&&policy.Begin(state.Host,state.Parent,e.HttpClient.Request.Method,status,state.Proxy,Rules))
        {
            state.Retried=true;state.Proxy=true;e.CustomUpStreamProxy=vpn;e.ReRequest=true;
            report(state.Parent+" → "+state.Host+": 403. Один повтор GET/HEAD через VPN.");
        }
        return Task.CompletedTask;
    }
    public void Dispose(){server.BeforeRequest-=Request;server.BeforeResponse-=Response;server.Stop();server.Dispose();authority.Dispose();policy.Clear();}
}
