using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RelayDesk;

public static class CoreRepair
{
    public static string RebindZapret(string text,string previous,string executable)
    {
        var oldBin=Path.GetDirectoryName(previous);var newBin=Path.GetDirectoryName(executable);
        if(string.IsNullOrWhiteSpace(oldBin)||string.IsNullOrWhiteSpace(newBin))return text;
        string oldRoot=Path.GetFileName(oldBin).Equals("bin",StringComparison.OrdinalIgnoreCase)?Path.GetDirectoryName(oldBin)!:oldBin;
        string newRoot=Path.GetFileName(newBin).Equals("bin",StringComparison.OrdinalIgnoreCase)?Path.GetDirectoryName(newBin)!:newBin;
        return text.Replace(oldRoot+Path.DirectorySeparatorChar,newRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
    public record LockedCore(string Id,string Repository,string Version,string Url,long Size,string Sha256,string Executable);
    public static List<ComponentUpdate> Plan()
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("RelayDesk.Cores.lock.json")??throw new InvalidDataException("Список версий ядер отсутствует.");
        var cores=JsonSerializer.Deserialize<List<LockedCore>>(stream)??throw new InvalidDataException("Пустой список ядер.");
        if(cores.Count!=4||cores.Select(c=>c.Id).Distinct().Count()!=4)throw new InvalidDataException("Некорректный набор ядер.");
        return cores.Select(c=>
        {
            if(c.Repository!=ComponentUpdates.OfficialRepository(c.Id))throw new InvalidDataException("Неизвестный источник ядра.");
            var file=new UpdateFile(Path.GetFileName(new Uri(c.Url).AbsolutePath),c.Url,c.Size,c.Sha256);ComponentUpdates.ValidateUrl(file,c.Id);
            return new ComponentUpdate(c.Id,c.Id,c.Id=="zapret"?"flowseal:"+c.Version:c.Version,"","Закреплённая версия",true,"https://github.com/"+c.Repository,[file]);
        }).ToList();
    }
    public static List<string> ManagedInstalls(string folder)
    {
        var paths=new List<string>();
        foreach(string parent in new[]{folder,Path.Combine(folder,"components")})if(Directory.Exists(parent)&&(File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка ядер является ссылкой.");
        foreach(string id in new[]{"sing-box","Xray","mihomo","zapret"})
        {
            var root=Path.Combine(folder,"components",id);if(!Directory.Exists(root))continue;
            if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("Каталог ядер является ссылкой.");
            paths.AddRange(Directory.EnumerateDirectories(root).Where(p=>Guid.TryParseExact(Path.GetFileName(p),"N",out _)));
        }
        return paths;
    }
    public static void DeleteManagedInstall(string folder,string path)
    {
        string full=Path.GetFullPath(path);var root=Path.GetFullPath(Path.Combine(folder,"components"));
        var relative=Path.GetRelativePath(root,full).Split(Path.DirectorySeparatorChar);
        if(relative.Length!=2||relative[0] is not ("sing-box" or "Xray" or "mihomo" or "zapret")||!Guid.TryParseExact(relative[1],"N",out _)||!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Очистка разрешена только в папке ядер Tunnelito.");
        foreach(string ancestor in new[]{folder,root,Path.Combine(root,relative[0])})if((File.GetAttributes(ancestor)&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка ядер содержит ссылку.");
        if(!Directory.Exists(full))return;
        // Inspect the whole tree before deleting any file. Never follow junctions.
        var files=new List<string>();var directories=new List<string>();
        void Inspect(string directory)
        {
            if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Очистка папок-ссылок запрещена.");
            foreach(string item in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes=File.GetAttributes(item);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("В папке ядра найдена ссылка.");
                if((attributes&FileAttributes.Directory)!=0)Inspect(item);else files.Add(item);
            }
            directories.Add(directory);
        }
        Inspect(full);foreach(string file in files)File.Delete(file);foreach(string directory in directories)Directory.Delete(directory);
    }
}
