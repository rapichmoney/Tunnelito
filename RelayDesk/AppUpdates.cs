using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RelayDesk;

public sealed record AppRelease(string Repository, string Version, string Title, string Notes, string Page, UpdateFile File, bool Available);

public sealed class AppUpdates
{
    readonly HttpClient http;
    public AppUpdates(HttpClient? client = null)
    {
        http = client ?? SharedClient;
    }
    static readonly HttpClient SharedClient = CreateClient();
    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tunnelito-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    public static string CurrentVersion => typeof(AppUpdates).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public const string DefaultRepository = BrandIdentity.Repository;
    static readonly string[] Sources=[DefaultRepository,"rapichmoney/RelayDesk-VPN","bernicetownsend1955/RelayDesk-VPN"];
    public static bool TrustedRepository(string repository)=>Sources.Contains(repository,StringComparer.OrdinalIgnoreCase);
    public static string InstallerName(string version)=>$"Tunnelito-Setup-{version}-x64.exe";
    public static bool TrustedPackage(string repository,string version,UpdateFile file)
    {
        if(!TrustedRepository(repository)||!Regex.IsMatch(version,@"^\d+\.\d+\.\d+$"))return false;
        if(file.Name!=InstallerName(version)&&file.Name!=$"RelayDesk-Setup-{version}-x64.exe")return false;
        return Sources.Any(source=>new[]{"v"+version,version}.Any(tag=>file.Url.Equals($"https://github.com/{source}/releases/download/{tag}/{file.Name}",StringComparison.OrdinalIgnoreCase)));
    }
    public static string Repository(string value)
    {
        value = value.Trim();
        if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) value = value[19..].TrimEnd('/');
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$") || value.Split('/')[1] is "." or "..")
            throw new FormatException("Укажи публичный GitHub-репозиторий: владелец/репозиторий.");
        return value;
    }
    static string VersionText(string tag)
    {
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$") || !Version.TryParse(tag.TrimStart('v'), out _)) throw new InvalidDataException("Некорректная версия Tunnelito в релизе.");
        return tag.TrimStart('v');
    }
    public static AppRelease Parse(JsonObject release, string repository, string current)
    {
        repository = Repository(repository);
        if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true) throw new InvalidDataException("Релиз ещё не опубликован или является предварительным.");
        var tag = JsonTools.Text(release["tag_name"]); var version = VersionText(tag);
        var name = InstallerName(version);
        var assets = release["assets"] as JsonArray ?? throw new InvalidDataException("Нет установщика Tunnelito.");
        if(!assets.Any(a=>JsonTools.Text(a?["name"])==name))name=$"RelayDesk-Setup-{version}-x64.exe";
        var matches = assets.Where(a => JsonTools.Text(a?["name"]) == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("В релизе должен быть один Windows x64-установщик Tunnelito.");
        var asset = matches[0]!;
        var url = JsonTools.Text(asset["browser_download_url"]);
        if (!TrustedPackage(repository,version,new(name,url,0,""))) throw new InvalidDataException("Установщик находится вне выбранного GitHub-релиза.");
        var digest = JsonTools.Text(asset["digest"]);
        if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("GitHub не опубликовал SHA-256 установщика.");
        var size = asset["size"]?.GetValue<long>() ?? 0;
        if (size is < 1024 or > 250_000_000) throw new InvalidDataException("Недопустимый размер установщика.");
        var notes = PlainText(JsonTools.Text(release["body"]), 12000);
        var title = PlainText(JsonTools.Text(release["name"]), 160);
        return new(repository, version, title.Length > 0 ? title : "Tunnelito " + version, notes, $"https://github.com/{repository}/releases/tag/{tag}", new(name, url, size, digest[7..]), System.Version.Parse(version) > System.Version.Parse(current));
    }
    public static string PlainText(string text, int limit)
    {
        text = new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()).Trim();
        return text.Length > limit ? text[..limit] + "…" : text;
    }
    public async Task<AppRelease> Check(string repository, string current, CancellationToken cancellation)
    {
        repository = Repository(repository);
        if (!TrustedRepository(repository)) throw new InvalidDataException("Источник Tunnelito закреплён в приложении.");
        repository=DefaultRepository;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream(); var buffer = new byte[65536]; int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0) { if (memory.Length + read > 1_000_000) throw new InvalidDataException("Описание релиза превышает допустимый размер."); memory.Write(buffer, 0, read); }
        return Parse(JsonTools.Object(System.Text.Encoding.UTF8.GetString(memory.ToArray())), repository, current);
    }
    public async Task<string> Download(AppRelease release, string dataFolder, Action<string> progress, CancellationToken cancellation)
    {
        // Revalidate public record fields before treating them as paths or executable metadata.
        var version = VersionText(release.Version); var repository = Repository(release.Repository);
        if (!TrustedRepository(repository)) throw new InvalidDataException("Источник Tunnelito закреплён в приложении.");
        var name = release.File.Name;
        var uri = new Uri(release.File.Url);
        if (!release.Available || !TrustedPackage(repository,version,release.File) || uri.Scheme != "https" || uri.Host != "github.com" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0 || uri.Port != 443 || !Regex.IsMatch(release.File.Hash, "^[a-fA-F0-9]{64}$") || release.File.Size is < 1024 or > 250_000_000) throw new InvalidDataException("Некорректный пакет Tunnelito.");
        var folder = Path.Combine(dataFolder, "application-updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var partial = Path.Combine(folder, name + ".partial"); var target = Path.Combine(folder, name);
        try
        {
            using var response = await http.GetAsync(release.File.Url, HttpCompletionOption.ResponseHeadersRead, cancellation); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long publishedSize && publishedSize != release.File.Size) throw new InvalidDataException("Размер установщика изменился.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = File.Create(partial))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536]; long total = 0; int read; int lastPercent = -1;
                while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    total += read; if (total > release.File.Size) throw new InvalidDataException("Размер установщика превышает опубликованный.");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    var percent = (int)(total * 100 / release.File.Size);
                    if (percent != lastPercent) { lastPercent = percent; progress($"Загрузка Tunnelito {release.Version}: {percent}%"); }
                }
                if (total != release.File.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.File.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Проверка SHA-256 не пройдена. Установщик не будет запущен.");
            }
            cancellation.ThrowIfCancellationRequested(); File.Move(partial, target); return target;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
