using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RelayDesk;

public static class HappEncryptedLink
{
    const string Prefix="happ://crypt4/";
    public static string Normalize(string text)
    {
        text=text.Trim();
        if(text.Length>=2&&text[0]=='"'&&text[^1]=='"')text=text[1..^1].Trim();
        if(text.StartsWith(@"happ\://",StringComparison.OrdinalIgnoreCase))text="happ://"+text[8..];
        return text;
    }
    public static bool IsEncrypted(string text)=>Normalize(text).StartsWith(Prefix,StringComparison.OrdinalIgnoreCase);
    public static string Decode(string text)
    {
        text=Normalize(text);
        if(!text.StartsWith(Prefix,StringComparison.OrdinalIgnoreCase))
        {
            if(text.StartsWith("happ://crypt",StringComparison.OrdinalIgnoreCase))throw new FormatException("Эта версия зашифрованной ссылки Happ пока не поддерживается. Используй crypt4 или обычную HTTPS-подписку.");
            return text;
        }
        if(text.Length>16384)throw Invalid();
        try
        {
            string encoded=Uri.UnescapeDataString(text[Prefix.Length..]).Replace('-','+').Replace('_','/');
            if(encoded.Any(char.IsWhiteSpace))throw Invalid();
            encoded=encoded.PadRight((encoded.Length+3)/4*4,'=');
            byte[] cipher=Convert.FromBase64String(encoded);
            using var stream=typeof(HappEncryptedLink).Assembly.GetManifestResourceStream("RelayDesk.HappCrypt4.compatibility.der")??throw new InvalidOperationException("Отсутствуют данные совместимости Happ crypt4.");
            using var data=new MemoryStream();stream.CopyTo(data);
            using var rsa=RSA.Create();rsa.ImportRSAPrivateKey(data.ToArray(),out _);int block=rsa.KeySize/8;
            if(cipher.Length==0||cipher.Length%block!=0)throw Invalid();
            using var plain=new MemoryStream();
            for(int offset=0;offset<cipher.Length;offset+=block)
            {
                byte[] bytes=rsa.Decrypt(cipher.AsSpan(offset,block).ToArray(),RSAEncryptionPadding.Pkcs1);
                try{plain.Write(bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}
            }
            byte[] result=plain.ToArray();
            try
            {
                string value=new UTF8Encoding(false,true).GetString(result);
                if(value.Any(char.IsControl)||!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||uri.Fragment.Length>0)throw Invalid();
                return uri.AbsoluteUri;
            }
            finally{CryptographicOperations.ZeroMemory(result);if(plain.TryGetBuffer(out var buffer))CryptographicOperations.ZeroMemory(buffer.AsSpan());}
        }
        catch(Exception error)when(error is CryptographicException or ArgumentException or FormatException){throw Invalid();}
    }
    static FormatException Invalid()=>new("Не удалось прочитать ссылку Happ crypt4. Скопируй её целиком без изменений или запроси HTTPS-подписку у провайдера.");
}
