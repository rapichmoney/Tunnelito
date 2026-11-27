using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using RelayDesk;

static class HappLinkChecks
{
    public static async Task Run(string[] args)
    {
        int count=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);count++;}
        using var resource=typeof(HappEncryptedLink).Assembly.GetManifestResourceStream("RelayDesk.HappCrypt4.compatibility.der")!;using var memory=new MemoryStream();resource.CopyTo(memory);
        using var rsa=RSA.Create();rsa.ImportRSAPrivateKey(memory.ToArray(),out _);
        string Wrap(string text)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(text);using var cipher=new MemoryStream();int block=rsa.KeySize/8-11;
            for(int i=0;i<bytes.Length;i+=block)cipher.Write(rsa.Encrypt(bytes.AsSpan(i,Math.Min(block,bytes.Length-i)).ToArray(),RSAEncryptionPadding.Pkcs1));
            return "happ://crypt4/"+Convert.ToBase64String(cipher.ToArray());
        }
        string url="https://example.invalid/subscription?token=synthetic-private-token",encrypted=Wrap(url);
        Check(Subscriptions.Address(encrypted).AbsoluteUri==url,"crypt4 decodes locally to an HTTPS subscription");
        Check(Subscriptions.Address("\""+encrypted.Replace("happ://",@"happ\://")+"\"").AbsoluteUri==url,"Quoted and markdown-escaped Happ input is normalized");
        Check(Subscriptions.IsInput(encrypted)&&Subscriptions.IsInput("happ://crypt5/example"),"Clipboard and QR recognize Happ inputs before profile parsing");
        Check(Subscriptions.StoredAddress(encrypted)==encrypted,"Saved subscription retains ciphertext rather than exposing its URL");
        Check(Subscriptions.Address(Wrap(url+new string('a',1200))).AbsoluteUri==url+new string('a',1200),"Multi-block RSA URLs decode without truncation");
        Check(Subscriptions.Address(encrypted.TrimEnd('=').Replace('+','-').Replace('/','_').Replace("happ:__crypt4_","happ://crypt4/")).AbsoluteUri==url,"URL-safe Base64 and omitted padding are accepted");
        Check(Subscriptions.Address("happ://add/"+url).AbsoluteUri==url,"Existing unencrypted Happ subscription imports still work");
        foreach(string input in new[]{"happ://crypt4/not-valid","happ://crypt5/unsupported",Wrap("http://example.invalid/private"),Wrap("https://user:pass@example.invalid/private"),Wrap("https://example.invalid/sub#fragment"),Wrap("file:///private"),encrypted[..^8],"happ://crypt4/"+new string('A',17000)})
        {
            bool rejected=false;try{_=Subscriptions.Address(input);}catch(FormatException e){rejected=!e.Message.Contains("synthetic-private-token")&&!e.Message.Contains("user:pass");}
            Check(rejected,"Unsupported, corrupted or unsafe link fails with a sanitized message");
        }
        Check(!ProgramJournal.Clean("Paste "+encrypted).Contains("happ://crypt4/"),"Logs redact entire encrypted Happ links");
        Check(!ProgramJournal.Clean(encrypted.Replace("happ://",@"happ\://")).Contains("crypt4"),"Logs also redact markdown-escaped encrypted links");
        using var handler=new MockHandler();var download=await Subscriptions.Download(new SubscriptionSource{Url=encrypted,SendDeviceId=false},Guid.NewGuid().ToString("N"),CancellationToken.None,handler);
        Check(download.Batch.Profiles.Count==1&&handler.CorrectUri,"Encrypted URL integrates with subscription download using an isolated mock provider");
        bool denied=false;try{_=Subscriptions.Parse("vless://11111111-1111-4111-8111-111111111111@0.0.0.0:443#App%20not%20supported");}catch(InvalidOperationException){denied=true;}
        Check(denied,"Provider denial placeholder is not silently imported or bypassed");
        int privateIndex=Array.IndexOf(args,"--private-input");
        if(privateIndex>=0)
        {
            string input=File.ReadAllText(args[privateIndex+1]);var decoded=Subscriptions.Address(input);
            Check(decoded.Scheme=="https"&&decoded.Host.Length>0,"User-provided crypt4 link decodes successfully; no URL or subscription data printed");
        }
        Console.WriteLine("COMPLETE: "+count+" Happ link checks passed.");
    }
    sealed class MockHandler:HttpMessageHandler
    {
        public bool CorrectUri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            CorrectUri=request.RequestUri?.Host=="example.invalid"&&request.RequestUri.Query.Contains("synthetic-private-token");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("vless://11111111-1111-4111-8111-111111111111@node.example.invalid:443?security=tls&type=tcp#QA")});
        }
    }
}
