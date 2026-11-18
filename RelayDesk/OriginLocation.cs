using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayDesk.Orbit;

namespace RelayDesk;
public sealed record OriginLocation(string Code,string Country,string City,double Latitude,double Longitude,string NetworkKey,DateTimeOffset DetectedAt)
{
    [System.Text.Json.Serialization.JsonIgnore] public Place Place=>new(Code,City.Length>0?City+", "+Country:Country,Latitude,Longitude,[]);
}
public static class OriginLocator
{
    public static string NetworkKey(NetworkInterface network)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(network.Id+"|"+string.Join('|',network.GetIPProperties().GatewayAddresses.Select(g=>g.Address.ToString()))+"|"+string.Join('|',network.GetIPProperties().UnicastAddresses.Where(a=>a.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork).Select(a=>a.Address.ToString())))));
    public static async Task<OriginLocation> Locate(NetworkInterface network,CancellationToken cancellation)
    {
        using var client=new HttpClient(DirectNetwork.Handler(network)){Timeout=TimeSpan.FromSeconds(10)};
        return await Locate(client,NetworkKey(network),cancellation);
    }
    public static async Task<OriginLocation> Locate(HttpClient client,string networkKey,CancellationToken cancellation)
    {
        using var response=await client.GetAsync("https://ipwho.is/?fields=success,message,country,country_code,city,latitude,longitude",HttpCompletionOption.ResponseHeadersRead,cancellation);response.EnsureSuccessStatusCode();
        await using var input=await response.Content.ReadAsStreamAsync(cancellation);using var buffer=new MemoryStream();var bytes=new byte[4096];int read;
        while((read=await input.ReadAsync(bytes,cancellation))>0){if(buffer.Length+read>16384)throw new InvalidDataException("Ответ геолокации слишком большой.");buffer.Write(bytes,0,read);}
        return Parse(Encoding.UTF8.GetString(buffer.ToArray()),networkKey);
    }
    public static OriginLocation Parse(string json,string networkKey)
    {
        using var data=JsonDocument.Parse(json);var root=data.RootElement;
        if(!root.TryGetProperty("success",out var success)||success.ValueKind!=JsonValueKind.True)throw new InvalidDataException("Сервис не определил исходное положение.");
        string Text(string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?new string((value.GetString()??"").Where(c=>!char.IsControl(c)).Take(80).ToArray()).Trim():"";
        string code=Text("country_code").ToUpperInvariant();
        if(!Regex.IsMatch(code,"^[A-Z]{2}$")||!root.TryGetProperty("latitude",out var lat)||lat.ValueKind!=JsonValueKind.Number||!lat.TryGetDouble(out double latitude)||!root.TryGetProperty("longitude",out var lon)||lon.ValueKind!=JsonValueKind.Number||!lon.TryGetDouble(out double longitude)||!double.IsFinite(latitude)||!double.IsFinite(longitude)||latitude is < -90 or > 90||longitude is < -180 or > 180)throw new InvalidDataException("Некорректные координаты исходного подключения.");
        return new(code,CountryBorders.Name(code)??Text("country"),Text("city"),latitude,longitude,networkKey,DateTimeOffset.UtcNow);
    }
}
