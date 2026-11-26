using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Net.Security;
using System.Text;
using RelayDesk;

if(args.Contains("--namecheap")){await NamecheapLive.Run();return;}
int count=0;
void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);count++;}
Check(HttpsInspectionProxy.DescribeTransportError(new IOException("private-token",new SocketException((int)SocketError.ConnectionReset))).Contains("ConnectionReset"),"Nested transport failure exposes the socket code");
Check(!HttpsInspectionProxy.DescribeTransportError(new Exception("https://secret.example/private-token")).Contains("secret"),"Transport diagnostics never include exception messages or URLs");
Check(HttpsInspectionProxy.DescribeTransportError(new System.Security.Authentication.AuthenticationException("private-token")).Contains("TLS"),"TLS errors are distinguished from network failures");
int Port(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
string folder=Path.Combine(Path.GetTempPath(),"Tunnelito-HTTPS-QA-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
using var certificate=HttpsCertificate.Load(folder);using var reloaded=HttpsCertificate.Load(folder,false);
Check(certificate.Thumbprint==reloaded.Thumbprint&&certificate.HasPrivateKey,"User-specific certificate survives DPAPI-protected reload");
Check(!File.ReadAllBytes(Path.Combine(folder,"https-inspection","authority.dpapi")).AsSpan().StartsWith(certificate.Export(X509ContentType.Pfx)),"Stored authority is encrypted, not a plaintext PFX");
using var another=HttpsCertificate.Load(Path.Combine(folder,"second"));Check(another.Thumbprint!=certificate.Thumbprint,"Separate installations do not share certificate keys");
var rules=new[]{new QuickRoute("domain_suffix","root.example.test","@proxy"),new QuickRoute("domain","explicit.example.test","@direct",true)};
var policy=new HttpsAutoPolicy();
Check(policy.Begin("cdn.example.test","root.example.test","GET",403,false,rules),"403 GET with proxied parent qualifies for a single retry");
Check(!policy.Begin("cdn.example.test","root.example.test","GET",403,false,rules),"Repeated 403 retries are throttled");
Check(!policy.Begin("post.example.test","root.example.test","POST",403,false,rules),"POST is never automatically replayed");
Check(!policy.Begin("explicit.example.test","root.example.test","GET",403,false,rules),"Explicit direct domain rule is respected");
Check(!policy.Begin("other.example.test","","GET",403,false,rules),"No initiator means no guessed route");
Check(!policy.Complete("cdn.example.test","root.example.test",403)&&!policy.Learnt("cdn.example.test"),"Unchanged 403 does not create a temporary rule");
Check(policy.Complete("cdn.example.test","root.example.test",206)&&policy.Learnt("cdn.example.test"),"Successful partial media response learns temporary exact host");
using var normal=new Upstream(403);using var vpn=new Upstream(200);using var direct=new Upstream(403);
var settings=new Preferences{SharedQuickRoutes=rules.ToList()};
using(var proxy=new HttpsInspectionProxy(folder,settings,new Profile(),new Uri("http://127.0.0.1:"+normal.Port),vpn.Port,direct.Port,Port(),requireTrusted:false))
{
    using var handler=new HttpClientHandler{Proxy=new WebProxy(proxy.Endpoint),UseProxy=true};using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(12)};
    async Task<int> Send(string host,string? parent="root.example.test",string method="GET")
    {
        using var request=new HttpRequestMessage(new HttpMethod(method),"http://"+host+"/secret-path?private-token=never-log");if(parent!=null)request.Headers.Referrer=new Uri("https://"+parent+"/private-page");
        using var response=await client.SendAsync(request);await response.Content.ReadAsByteArrayAsync();return (int)response.StatusCode;
    }
    Check(await Send("cdn.example.test")==200&&normal.Requests==1&&vpn.Requests==1,"Real proxy retries 403 once over the VPN upstream and returns 200");
    Check(proxy.Policy.Learnt("cdn.example.test"),"Successful retry becomes temporary exact-host rule");
    Check(await Send("cdn.example.test")==200&&normal.Requests==1&&vpn.Requests==2,"Related subsequent request directly uses learned VPN upstream");
    Check(await Send("cdn.example.test",null)==403,"Unrelated request does not inherit the learned route");
    Check(await Send("explicit.example.test")==403&&direct.Requests==1,"Explicit direct target is neither retried nor learned");
    Check(await Send("post.example.test",method:"POST")==403,"Actual proxy does not replay POST after 403");
    vpn.Status=403;Check(await Send("failed.example.test")==403&&!proxy.Policy.Learnt("failed.example.test"),"Failed VPN retry rolls back temporary routing");
    Check(proxy.Events.All(e=>!e.Contains("secret-path")&&!e.Contains("private-token")&&!e.Contains("private-page")),"Diagnostic log contains hosts/statuses only");
}
string config=HttpsAutoPolicy.ConfigureCore("{\"inbounds\":[],\"outbounds\":[{\"type\":\"selector\",\"tag\":\"proxy\",\"outbounds\":[\"direct\"]},{\"type\":\"direct\",\"tag\":\"direct\"}],\"route\":{\"final\":\"proxy\",\"rules\":[]}}",Port(),Port());
var json=JsonTools.Object(config);Check(json["inbounds"]!.AsArray().Count==2&&json["route"]!["rules"]!.AsArray().Count==2,"Separate loopback VPN/direct inputs are installed before user routing");
var installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RelayDesk");var store=new Store(folder);BundledCores.Bind(store,installed);
var core=store.Settings.Engines.GetValueOrDefault("sing-box");if(core!=null)
{
    var file=Path.Combine(folder,"probe.json");File.WriteAllText(file,config);var start=new ProcessStartInfo(core){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in new[]{"check","-c",file})start.ArgumentList.Add(arg);using var process=Process.Start(start)!;string error=await process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();Check(process.ExitCode==0,"Real bundled sing-box accepts inspection routing: "+error);
}
Console.WriteLine($"COMPLETE: {count} HTTPS checks passed.");
using(var stuck=new Upstream(200){Payload="broken-first-response",Stall=true,Chunked=true})
using(var repaired=new Upstream(200){Payload="complete-vpn-script"})
using(var inspector=new HttpsInspectionProxy(folder,settings,new Profile(),new Uri("http://127.0.0.1:"+stuck.Port),repaired.Port,direct.Port,Port(),requireTrusted:false))
using(var client=new HttpClient(new HttpClientHandler{Proxy=new WebProxy(inspector.Endpoint),UseProxy=true}){Timeout=TimeSpan.FromSeconds(35)})
{
    using var request=new HttpRequestMessage(HttpMethod.Get,"http://stuck.example.test/file.js?private-token=never-log");request.Headers.Referrer=new Uri("https://root.example.test/private-page");request.Headers.Add("Cookie","private-cookie=do-not-log");
    using var result=await client.SendAsync(request);string body=await result.Content.ReadAsStringAsync();
    Check(result.StatusCode==HttpStatusCode.OK&&body=="complete-vpn-script"&&stuck.Requests==1&&repaired.Requests==1,"Stalled chunked HTTP 200 is replaced by exactly one complete VPN response, without leaking the partial prefix");
    Check(inspector.Policy.Learnt("stuck.example.test"),"Stalled transfer learns its host only after a complete successful VPN body");
    Check(repaired.LastHeaders.Contains("private-cookie=do-not-log"),"Recovery preserves request cookies in memory");
    Check(inspector.Events.All(e=>!e.Contains("private-token")&&!e.Contains("private-cookie")&&!e.Contains("private-page")),"Recovery diagnostics do not expose paths, tokens or cookies");
}
using(var broken=new Upstream(200){Payload="truncated",Truncate=true})
using(var failedVpn=new Upstream(200){Payload="also-truncated",Truncate=true})
using(var inspector=new HttpsInspectionProxy(folder,settings,new Profile(),new Uri("http://127.0.0.1:"+broken.Port),failedVpn.Port,direct.Port,Port(),requireTrusted:false))
using(var client=new HttpClient(new HttpClientHandler{Proxy=new WebProxy(inspector.Endpoint),UseProxy=true}){Timeout=TimeSpan.FromSeconds(35)})
{
    using var request=new HttpRequestMessage(HttpMethod.Get,"http://broken.example.test/file.js");request.Headers.Referrer=new Uri("https://root.example.test/");
    using var result=await client.SendAsync(request);
    Check(result.StatusCode==HttpStatusCode.BadGateway&&!inspector.Policy.Learnt("broken.example.test")&&failedVpn.Requests==1,"Incomplete VPN retry returns failure and never learns a rule or sends a partial file");
}
Check(!policy.CanInspectTransfer("explicit.example.test","root.example.test","GET",false,rules),"Transfer recovery respects explicit direct targets");
Check(!policy.CanInspectTransfer("new.example.test","","GET",false,rules)&&!policy.CanInspectTransfer("new.example.test","root.example.test","POST",false,rules),"Transfer recovery rejects missing parents and POST requests");
using(var complete=new Upstream(200){Payload="compressed-script",Chunked=true,Gzip=true})
using(var unusedVpn=new Upstream(200))
using(var inspector=new HttpsInspectionProxy(folder,settings,new Profile(),new Uri("http://127.0.0.1:"+complete.Port),unusedVpn.Port,direct.Port,Port(),requireTrusted:false))
using(var client=new HttpClient(new HttpClientHandler{Proxy=new WebProxy(inspector.Endpoint),UseProxy=true,AutomaticDecompression=DecompressionMethods.GZip}))
{
    using var request=new HttpRequestMessage(HttpMethod.Get,"http://compressed.example.test/file.js");request.Headers.Referrer=new Uri("https://root.example.test/");
    using var result=await client.SendAsync(request);
    Check(await result.Content.ReadAsStringAsync()=="compressed-script"&&unusedVpn.Requests==0&&!inspector.Policy.Rules.Any(),"Healthy compressed chunked response is forwarded byte-correctly without a VPN retry");
    complete.Gzip=false;complete.Payload=new string('X',4*1024*1024+512);
    using var largeRequest=new HttpRequestMessage(HttpMethod.Get,"http://large.example.test/file.js");largeRequest.Headers.Referrer=new Uri("https://root.example.test/");
    using var largeResult=await client.SendAsync(largeRequest);
    Check((await largeResult.Content.ReadAsByteArrayAsync()).Length==complete.Payload.Length&&unusedVpn.Requests==0,"Unknown-length body exceeding the buffer limit switches to streaming without losing bytes");
}
if(args.Contains("--tls")||args.Contains("--system-trust"))
{
    try
    {
        bool systemTrust=args.Contains("--system-trust");string tlsFolder=systemTrust?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RelayDesk"):folder;using var tlsAuthority=HttpsCertificate.Load(tlsFolder,false);
        using var tlsNormal=new Upstream(403,tlsAuthority);using var tlsVpn=new Upstream(200,tlsAuthority);
        using var inspector=new HttpsInspectionProxy(tlsFolder,settings,new Profile(),new Uri("http://127.0.0.1:"+tlsNormal.Port),tlsVpn.Port,direct.Port,Port(),requireTrusted:systemTrust);
        var engine=(Titanium.Web.Proxy.ProxyServer)typeof(HttpsInspectionProxy).GetField("server",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(inspector)!;
        if(!systemTrust)
        {
            // Isolated mock roots are trusted only by the test clients, never by production or Windows.
            foreach(string name in new[]{"normalTransfers","directTransfers","recovery"})
            {
                var transfer=(HttpClient)typeof(HttpsInspectionProxy).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(inspector)!;
                var transport=(SocketsHttpHandler)typeof(HttpMessageInvoker).GetField("_handler",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(transfer)!;
                transport.SslOptions.RemoteCertificateValidationCallback=(_,leaf,_,errors)=>
                {
                    using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(tlsAuthority);chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
                    using var cert=X509CertificateLoader.LoadCertificate(leaf!.GetRawCertData());return (errors&SslPolicyErrors.RemoteCertificateNameMismatch)==0&&chain.Build(cert);
                };
            }
        }
        engine.ExceptionFunc=error=>Console.WriteLine("Test proxy error: "+error.GetType().Name);
        if(!systemTrust)engine.ServerCertificateValidationCallback+=(_,e)=>{using var leaf=X509CertificateLoader.LoadCertificate(e.Certificate!.GetRawCertData());using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(tlsAuthority);chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;e.IsValid=(e.SslPolicyErrors&SslPolicyErrors.RemoteCertificateNameMismatch)==0&&chain.Build(leaf);return Task.CompletedTask;};
        using var handler=new HttpClientHandler{Proxy=new WebProxy(inspector.Endpoint),UseProxy=true};
        if(!systemTrust)handler.ServerCertificateCustomValidationCallback=(_,leaf,chain,errors)=>
        {
            using var custom=new X509Chain();custom.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;custom.ChainPolicy.CustomTrustStore.Add(certificate);custom.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
            bool built=leaf!=null&&custom.Build(leaf);return leaf!=null&&(errors&SslPolicyErrors.RemoteCertificateNameMismatch)==0&&built;
        };
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(15)};
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://tls-cdn.example.test/private?token=never-log");request.Headers.Referrer=new Uri("https://root.example.test/");
        using var response=await client.SendAsync(request);Check((int)response.StatusCode==200&&tlsNormal.Requests==1&&tlsVpn.Requests==1,"Real HTTPS with trusted leaf: 403 is retried over VPN and becomes 200");
        Check(!systemTrust||handler.ServerCertificateCustomValidationCallback==null,"System-trust check uses standard TLS validation without certificate overrides");
        Check(inspector.Policy.Learnt("tls-cdn.example.test"),"TLS success creates temporary contextual rule");
        if(systemTrust){ tlsNormal.Status=200;tlsNormal.Payload="truncated-tls-body";tlsNormal.Stall=true;tlsNormal.Chunked=true;tlsVpn.Payload="complete-tls-vpn-body";
        using var stuckRequest=new HttpRequestMessage(HttpMethod.Get,"https://tls-stuck.example.test/file.js");stuckRequest.Headers.Referrer=new Uri("https://root.example.test/");
        using var stuckResult=await client.SendAsync(stuckRequest);
        Check(stuckResult.StatusCode==HttpStatusCode.OK&&await stuckResult.Content.ReadAsStringAsync()=="complete-tls-vpn-body"&&inspector.Policy.Learnt("tls-stuck.example.test"),"Stalled real TLS body is replaced by a fully validated VPN TLS response"); }
    }
    finally{Check(!HttpsCertificate.Trusted(folder),"TLS test did not change Windows trusted roots");}
}
Console.WriteLine($"FINAL: {count} HTTPS checks passed.");

sealed class Upstream:IDisposable
{
    readonly TcpListener listener=new(IPAddress.Loopback,0);readonly CancellationTokenSource cancel=new();
    public int Port{get;}public int Status;public int Requests;
    public string Payload="",LastHeaders="";public bool Stall,Truncate,Chunked,Gzip;
    readonly X509Certificate2? authority;
    public Upstream(int status,X509Certificate2? root=null){authority=root;Status=status;listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;_=Loop();}
    async Task Loop(){try{while(!cancel.IsCancellationRequested){var socket=await listener.AcceptTcpClientAsync(cancel.Token);_=Handle(socket);}}catch(OperationCanceledException){}catch(SocketException){}}
    async Task Handle(TcpClient client)
    {
        using(client)
        {
            try
            {
                Stream stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);string? first=await reader.ReadLineAsync(cancel.Token);while(await reader.ReadLineAsync(cancel.Token) is string line&&line.Length>0){LastHeaders+=line+"\n";}
                if(first?.StartsWith("CONNECT ")==true&&authority!=null)
                {
                    string host=first.Split(' ')[1].Split(':')[0];await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"),cancel.Token);
                    using var key=RSA.Create(2048);var req=new CertificateRequest("CN="+host,key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);var san=new SubjectAlternativeNameBuilder();san.AddDnsName(host);req.CertificateExtensions.Add(san.Build());req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid("1.3.6.1.5.5.7.3.1")},false));
                    using var leaf=req.Create(authority,DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1),RandomNumberGenerator.GetBytes(16));using var ephemeral=leaf.CopyWithPrivateKey(key);using var cert=X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);using var tls=new SslStream(stream,false);await tls.AuthenticateAsServerAsync(cert);stream=tls;
                    using var secureReader=new StreamReader(tls,Encoding.ASCII,false,1024,true);while(await secureReader.ReadLineAsync(cancel.Token) is string secureLine&&secureLine.Length>0){}
                    Interlocked.Increment(ref Requests);await WritePayload(stream);return;
                }
                Interlocked.Increment(ref Requests);await WritePayload(stream);
            }
            catch(Exception ex)when(ex is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException){if(authority!=null)Console.WriteLine("Test origin: "+ex.Message);}
        }
    }
    async Task WritePayload(Stream stream)
    {
        var bytes=Encoding.ASCII.GetBytes(Payload);
        if(Gzip){using var memory=new MemoryStream();using(var gzip=new System.IO.Compression.GZipStream(memory,System.IO.Compression.CompressionMode.Compress,true))gzip.Write(bytes);bytes=memory.ToArray();}
        string message=$"HTTP/1.1 {Status} Result\r\n"+(Chunked?"Transfer-Encoding: chunked":$"Content-Length: {bytes.Length}")+"\r\n"+(Gzip?"Content-Encoding: gzip\r\n":"")+"Connection: close\r\n\r\n";await stream.WriteAsync(Encoding.ASCII.GetBytes(message),cancel.Token);
        if(Chunked)await stream.WriteAsync(Encoding.ASCII.GetBytes(bytes.Length.ToString("X")+"\r\n"),cancel.Token);
        await stream.WriteAsync(bytes.AsMemory(0,Stall||Truncate?Math.Min(3,bytes.Length):bytes.Length),cancel.Token);
        if(Chunked&&!Stall&&!Truncate)await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"),cancel.Token);
        if(Stall)await Task.Delay(TimeSpan.FromSeconds(30),cancel.Token);
    }
    public void Dispose(){cancel.Cancel();listener.Stop();cancel.Dispose();}
}
