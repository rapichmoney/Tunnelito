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
        public Action? Cleanup;
    }
    readonly ProxyServer server=new(false,false,false){ForwardToUpstreamGateway=false,EnableConnectionPool=false,EnableTcpServerConnectionPrefetch=false};
    readonly HttpsAutoPolicy policy=new();
    readonly Preferences settings;
    readonly Profile profile;
    readonly ExternalProxy normal,vpn,direct;
    readonly Action<string> report;
    readonly System.Security.Cryptography.X509Certificates.X509Certificate2 authority;
    readonly Queue<string> events=new();
    readonly SemaphoreSlim transferSlots=new(8);
    readonly CancellationTokenSource stopping=new();
    readonly System.Net.Http.HttpClient recovery,normalTransfers,directTransfers;
    const int TransferLimit=4*1024*1024;
    static readonly TimeSpan TransferTimeout=TimeSpan.FromSeconds(8);
    public Uri Endpoint{get;}
    public string[] Events{get{lock(events)return events.ToArray();}}
    public HttpsAutoPolicy Policy=>policy;
    public ProxyConnectionOwners Owners{get;}
    static readonly System.Net.Http.HttpRequestOptionsKey<ProxyConnectionOwners.Owner> OwnerOption=new("Tunnelito.OriginalOwner");
    public HttpsInspectionProxy(string folder,Preferences preferences,Profile active,Uri normalEndpoint,int vpnPort,int directPort,int port,Action<string>? log=null,bool requireTrusted=true)
    {
        if(requireTrusted&&!HttpsCertificate.Trusted(folder))throw new InvalidOperationException("Сертификат HTTPS не доверен. Установи его в настройках Tunnelito.");
        settings=System.Text.Json.JsonSerializer.Deserialize<Preferences>(System.Text.Json.JsonSerializer.Serialize(preferences))!;profile=active;
        report=text=>{lock(events){events.Enqueue(DateTime.Now.ToString("HH:mm:ss")+" "+text);while(events.Count>100)events.Dequeue();}log?.Invoke(text);};
        normal=new(normalEndpoint.Host,normalEndpoint.Port){BypassLocalhost=false};vpn=new("127.0.0.1",vpnPort){BypassLocalhost=false};direct=new("127.0.0.1",directPort){BypassLocalhost=false};
        Owners=new(normalEndpoint.Port,vpnPort,directPort);
        recovery=TransferClient(new Uri("http://127.0.0.1:"+vpnPort));normalTransfers=TransferClient(normalEndpoint);directTransfers=TransferClient(new Uri("http://127.0.0.1:"+directPort));
        authority=HttpsCertificate.Load(folder,false);server.CertificateManager.RootCertificate=authority;server.CertificateManager.SaveFakeCertificates=false;
        // Keep server certificate validation enabled. Never save request/response bodies or cookies.
        server.BeforeRequest+=Request;server.BeforeResponse+=Response;
        server.OnServerConnectionCreate+=(_,socket)=>{Owners.CaptureReserved(socket);return Task.CompletedTask;};
        server.AfterResponse+=(_,e)=>{if(e.UserData is Attempt state)Interlocked.Exchange(ref state.Cleanup,null)?.Invoke();return Task.CompletedTask;};
        server.ExceptionFunc=error=>report(DescribeTransportError(error));
        var endpoint=new ExplicitProxyEndPoint(IPAddress.Loopback,port,true);
        endpoint.BeforeTunnelConnectRequest+=(_,e)=>
        {
            Owners.Watch(e);
            string host=e.HttpClient.Request.RequestUri.IdnHost;
            if(!HttpsAutoPolicy.PublicHost(host)||settings.HttpsExcludedDomains.Any(d=>RuleMatching.DomainIn(host,d)))
            {
                e.DecryptSsl=false;
                if(IPAddress.TryParse(normal.HostName,out var local)&&IPAddress.IsLoopback(local))e.HttpClient.UpStreamEndPoint=Owners.ReserveSource(e,local);
            }
            e.CustomUpStreamProxy=normal;return Task.CompletedTask;
        };
        endpoint.BeforeTunnelConnectResponse+=(_,e)=>{Owners.Capture(e);return Task.CompletedTask;};
        server.AddEndPoint(endpoint);
        try{server.Start();}catch{server.Dispose();recovery.Dispose();normalTransfers.Dispose();directTransfers.Dispose();authority.Dispose();throw;}
        Endpoint=new Uri("http://127.0.0.1:"+endpoint.Port);
    }
    IReadOnlyList<QuickRoute> Rules=>SharedRouting.Rules(profile,settings);
    public static string DescribeTransportError(Exception error)
    {
        // Exception messages may contain URLs, credentials or headers. Only emit known categories/codes.
        var chain=new List<Exception>();
        for(Exception? current=error;current!=null&&chain.Count<8;current=current.InnerException)chain.Add(current);
        var socket=chain.OfType<System.Net.Sockets.SocketException>().FirstOrDefault();
        if(socket!=null)return "Ошибка HTTPS-транспорта: сеть, "+socket.SocketErrorCode+" ("+socket.NativeErrorCode+").";
        if(chain.Any(e=>e is System.Security.Authentication.AuthenticationException))return "Ошибка HTTPS-транспорта: TLS, проверка сертификата или согласование соединения.";
        if(chain.Any(e=>e is OperationCanceledException))return "Ошибка HTTPS-транспорта: запрос отменён или истекло время ожидания.";
        if(chain.Any(e=>e is System.IO.IOException))return "Ошибка HTTPS-транспорта: поток соединения закрыт или недоступен.";
        return "Ошибка HTTPS-транспорта: код 0x"+chain[^1].HResult.ToString("X8")+".";
    }
    async Task Request(object sender,SessionEventArgs e)
    {
        Owners.Watch(e);
        if(e.UserData is Attempt existing&&existing.Retried){e.CustomUpStreamProxy=vpn;return;}
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
        bool inspect=settings.RoutingPreset=="rules"&&parentProxy&&request.ContentLength<=0&&!request.IsChunked&&policy.CanInspectTransfer(host,parent,request.Method,useVpn,Rules)&&request.Headers.GetFirstHeader("Range")==null&&request.Headers.GetFirstHeader("Upgrade")==null;
        var state=new Attempt{Host=host,Parent=parent,Proxy=useVpn,ParentProxied=parentProxy};e.UserData=state;
        e.CustomUpStreamProxy=useVpn?vpn:route=="direct"?direct:normal;
        if(inspect&&request.Method=="GET"&&transferSlots.Wait(0))await ProbeTransfer(e,state,route=="direct"?directTransfers:normalTransfers);
    }
    Task Response(object sender,SessionEventArgs e)
    {
        Owners.Capture(e);
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
    System.Net.Http.HttpClient TransferClient(Uri endpoint)=>new(new System.Net.Http.SocketsHttpHandler{
        Proxy=new WebProxy(endpoint),UseProxy=true,UseCookies=false,AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TransferTimeout,
        PooledConnectionLifetime=TimeSpan.Zero,PooledConnectionIdleTimeout=TimeSpan.Zero,
        ConnectCallback=async(context,token)=>
        {
            var socket=new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp){NoDelay=true};
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint,token);
                context.InitialRequestMessage.Options.TryGetValue(OwnerOption,out var owner);Owners.Track(socket,owner);
                return new System.Net.Sockets.NetworkStream(socket,true);
            }
            catch{socket.Dispose();throw;}
        }}){Timeout=Timeout.InfiniteTimeSpan};
    static readonly HashSet<string> HopHeaders=new(StringComparer.OrdinalIgnoreCase){"Host","Connection","Proxy-Connection","Proxy-Authorization","Proxy-Authenticate","Transfer-Encoding","Content-Length","Keep-Alive","TE","Trailer","Upgrade"};
    static System.Net.Http.HttpRequestMessage TransferRequest(SessionEventArgs e)
    {
        var request=new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,e.HttpClient.Request.RequestUri);
        if(ProxyConnectionOwners.Identify(e) is {} owner)request.Options.Set(OwnerOption,owner);
        request.Headers.ConnectionClose=true;
        var hop=new HashSet<string>(HopHeaders,StringComparer.OrdinalIgnoreCase);
        foreach(string named in (e.HttpClient.Request.Headers.GetFirstHeader("Connection")?.Value??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))hop.Add(named);
        foreach(var header in e.HttpClient.Request.Headers)if(!hop.Contains(header.Name))request.Headers.TryAddWithoutValidation(header.Name,header.Value);
        return request;
    }
    static Titanium.Web.Proxy.Http.Response TransferResponse(SessionEventArgs e,System.Net.Http.HttpResponseMessage result,long? size)
    {
        var response=new Titanium.Web.Proxy.Http.Response{StatusCode=(int)result.StatusCode,StatusDescription=result.ReasonPhrase??"",HttpVersion=e.HttpClient.Request.HttpVersion};
        var hop=new HashSet<string>(HopHeaders,StringComparer.OrdinalIgnoreCase);
        foreach(string named in result.Headers.Connection)hop.Add(named);
        foreach(var group in result.Headers.Concat(result.Content.Headers))if(!hop.Contains(group.Key))foreach(string value in group.Value)response.Headers.AddHeader(group.Key,value);
        response.ContentLength=size??-1;return response;
    }
    void SendBuffered(SessionEventArgs e,System.Net.Http.HttpResponseMessage result,byte[] body)
    {
        // Stream the raw representation, preserving Content-Encoding without recompressing it.
        e.RespondStreaming(TransferResponse(e,result,body.LongLength),(stream,token)=>stream.WriteAsync(body,token).AsTask(),closeServerConnection:true);
    }
    void HandoffStream(SessionEventArgs e,System.Net.Http.HttpResponseMessage result,System.IO.Stream source,byte[] prefix,byte[] pending)
    {
        var state=(Attempt)e.UserData!;
        e.RespondStreaming(TransferResponse(e,result,result.Content.Headers.ContentLength),async(stream,token)=>
        {
            try
            {
                using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stopping.Token);
                await stream.WriteAsync(prefix,linked.Token);await stream.WriteAsync(pending,linked.Token);await source.CopyToAsync(stream,linked.Token);
            }
            finally{Interlocked.Exchange(ref state.Cleanup,null)?.Invoke();}
        },closeServerConnection:true);
        state.Cleanup=()=>{source.Dispose();result.Dispose();transferSlots.Release();};
    }
    async Task ProbeTransfer(SessionEventArgs e,Attempt state,System.Net.Http.HttpClient client)
    {
        System.Net.Http.HttpResponseMessage? result=null;System.IO.Stream? source=null;bool handedOff=false;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);timeout.CancelAfter(TransferTimeout);
            using var request=TransferRequest(e);
            result=await client.SendAsync(request,System.Net.Http.HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if((int)result.StatusCode==403&&policy.Begin(state.Host,state.Parent,"GET",403,state.Proxy,Rules))
            {
                result.Dispose();result=null;await RecoverTransfer(e,state,"403");return;
            }
            source=await result.Content.ReadAsStreamAsync(timeout.Token);
            string type=result.Content.Headers.ContentType?.MediaType??"";
            if(!result.IsSuccessStatusCode||result.StatusCode==HttpStatusCode.PartialContent||result.Content.Headers.ContentLength>TransferLimit||type=="text/event-stream"||type.StartsWith("video/")||type.StartsWith("audio/"))
            {
                HandoffStream(e,result,source,[],[]);handedOff=true;return;
            }
            using var body=new System.IO.MemoryStream();byte[] buffer=new byte[32768];
            while(true)
            {
                int read=await source.ReadAsync(buffer,timeout.Token);if(read==0)break;
                if(body.Length+read>TransferLimit)
                {
                    HandoffStream(e,result,source,body.ToArray(),buffer.AsSpan(0,read).ToArray());handedOff=true;return;
                }
                body.Write(buffer,0,read);
            }
            if(result.Content.Headers.ContentLength is long expected&&body.Length!=expected)throw new System.IO.IOException("Incomplete response");
            SendBuffered(e,result,body.ToArray());
        }
        catch(Exception error)when(error is System.IO.IOException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            if(stopping.IsCancellationRequested)return;
            source?.Dispose();source=null;result?.Dispose();result=null;
            if(policy.BeginTransfer(state.Host,state.Parent,"GET",state.Proxy,Rules))await RecoverTransfer(e,state);
            else{report(state.Host+": загрузка не завершена; повтор временно недоступен. "+DescribeTransportError(error));e.GenericResponse("",HttpStatusCode.BadGateway,closeServerConnection:true);}
        }
        finally{if(!handedOff){source?.Dispose();result?.Dispose();transferSlots.Release();}}
    }
    async Task RecoverTransfer(SessionEventArgs e,Attempt state,string reason="загрузка не завершена")
    {
        state.Retried=true;
        report(state.Parent+" → "+state.Host+": "+reason+". Один повтор GET через VPN.");
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request=TransferRequest(e);
            using var result=await recovery.SendAsync(request,System.Net.Http.HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(result.Content.Headers.ContentLength>TransferLimit)throw new System.IO.IOException("Recovery body exceeds limit");
            await using var source=await result.Content.ReadAsStreamAsync(timeout.Token);
            using var body=new System.IO.MemoryStream();byte[] buffer=new byte[32768];
            while(true)
            {
                int read=await source.ReadAsync(buffer,timeout.Token);if(read==0)break;
                if(body.Length+read>TransferLimit)throw new System.IO.IOException("Recovery body exceeds limit");
                body.Write(buffer,0,read);
            }
            if(result.Content.Headers.ContentLength is long size&&body.Length!=size)throw new System.IO.IOException("Incomplete recovery body");
            int code=(int)result.StatusCode;
            bool learned=policy.Complete(state.Host,state.Parent,code);
            SendBuffered(e,result,body.ToArray());
            report(state.Parent+" → "+state.Host+": повтор завершён, HTTP "+code+(learned?". VPN на 30 минут для связанных запросов.":". Временное правило не сохранено."));
        }
        catch(Exception error)when(error is System.IO.IOException or OperationCanceledException or System.Net.Http.HttpRequestException||error.GetType()==typeof(Exception))
        {
            report(state.Host+": повтор через VPN не завершён. "+DescribeTransportError(error));
            if(!stopping.IsCancellationRequested)e.GenericResponse("",HttpStatusCode.BadGateway,closeServerConnection:true);
        }
    }
    public void Dispose(){stopping.Cancel();recovery.Dispose();normalTransfers.Dispose();directTransfers.Dispose();server.BeforeRequest-=Request;server.BeforeResponse-=Response;server.Stop();server.Dispose();Owners.Dispose();authority.Dispose();policy.Clear();}
}
