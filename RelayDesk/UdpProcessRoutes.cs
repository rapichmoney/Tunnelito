using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace RelayDesk;

// Windows UDP process lookup can fail for wildcard-bound sockets. Local rule sets
// supply a fallback only for TUN packets whose process path is still unknown.
public sealed class UdpProcessRoutes:IDisposable
{
    public sealed record Owner(int Port,int Pid,string Path);
    sealed record Binding(string File,string[] Paths,string[] Names);
    readonly List<Binding> bindings=[];
    readonly object gate=new();
    readonly Timer? timer;
    readonly string directory;
    bool disposed;
    public string Config{get;}
    public int RuleCount=>bindings.Count;
    public UdpProcessRoutes(string config,string folder)
    {
        directory=System.IO.Path.Combine(folder,"udp-process-routes",Guid.NewGuid().ToString("N"));
        var root=JsonTools.Object(config);
        var inbound=(root["inbounds"] as JsonArray??[]).Where(n=>JsonTools.Text(n?["type"])=="tun").Select(n=>JsonTools.Text(n?["tag"])).Where(t=>t.Length>0).ToArray();
        if(!OperatingSystem.IsWindows()||inbound.Length==0||root["route"] is not JsonObject route||route["rules"] is not JsonArray rules){Config=config;return;}
        var sets=route["rule_set"] as JsonArray??[];route["rule_set"]??=sets;
        for(int i=0;i<rules.Count;i++)
        {
            if(rules[i] is not JsonObject rule||JsonTools.Text(rule["action"])!="route")continue;
            // Preserve priority and refuse complex/custom matchers instead of broadening their scope.
            if(rule.Any(p=>p.Key is not ("process_path" or "process_name" or "action" or "outbound")))continue;
            string[] paths=Strings(rule["process_path"]),names=Strings(rule["process_name"]);
            if(paths.Length+names.Length==0)continue;
            string tag="tunnelito-udp-owner-"+bindings.Count;
            if(sets.Any(s=>JsonTools.Text(s?["tag"])==tag))throw new InvalidDataException("UDP owner rule-set tag collision.");
            string file=System.IO.Path.Combine(directory,bindings.Count+".json");bindings.Add(new(file,paths,names));
            sets.Add(new JsonObject{["type"]="local",["tag"]=tag,["format"]="source",["path"]=file});
            rules.Insert(++i,new JsonObject{
                ["type"]="logical",["mode"]="and",["action"]="route",["outbound"]=JsonTools.Text(rule["outbound"]),
                ["rules"]=new JsonArray(
                    new JsonObject{["inbound"]=new JsonArray(inbound.Select(t=>(JsonNode?)JsonValue.Create(t)).ToArray()),["network"]="udp",["rule_set"]=tag},
                    new JsonObject{["process_path_regex"]=new JsonArray(".+"),["invert"]=true})});
        }
        Config=root.ToJsonString(JsonTools.Options);
        if(bindings.Count==0)return;
        Directory.CreateDirectory(directory);
        Refresh();timer=new Timer(_=>Refresh(),null,TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(2));
    }
    static string[] Strings(JsonNode? node)=>node is JsonArray a?a.Select(JsonTools.Text).Where(s=>s.Length>0).ToArray():JsonTools.Text(node) is string s&&s.Length>0?[s]:[];
    public static int[] SelectPorts(IEnumerable<Owner> owners,IEnumerable<string> paths,IEnumerable<string> names)
    {
        var wantedPaths=paths.ToHashSet(StringComparer.OrdinalIgnoreCase);var wantedNames=names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return owners.Where(o=>o.Port>=1024&&o.Port<=65535&&o.Port is not (1900 or 5353 or 5355 or 6771)).GroupBy(o=>o.Port)
            .Where(g=>g.Select(o=>o.Pid).Distinct().Count()==1&&g.All(o=>o.Path.Length>0&&(wantedPaths.Contains(o.Path)||wantedNames.Contains(System.IO.Path.GetFileName(o.Path)))))
            .Select(g=>g.Key).Order().ToArray();
    }
    void Refresh()
    {
        lock(gate)
        {
            if(disposed)return;
            var owners=ReadOwners();
            foreach(var binding in bindings)
            {
                int[] ports=SelectPorts(owners,binding.Paths,binding.Names);
                // An empty rule-set is explicitly nonmatching, never an empty default rule.
                var rules=ports.Length==0?new JsonArray():new JsonArray(new JsonObject{["source_port"]=new JsonArray(ports.Select(p=>(JsonNode?)JsonValue.Create(p)).ToArray())});
                string text=new JsonObject{["version"]=3,["rules"]=rules}.ToJsonString();
                try
                {
                    if(File.Exists(binding.File)&&File.ReadAllText(binding.File)==text)continue;
                    File.WriteAllText(binding.File,text);
                }
                catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }
    public static IReadOnlyList<Owner> ReadOwners()
    {
        var rows=new List<(int Port,int Pid)>();
        if(!OperatingSystem.IsWindows())return [];
        foreach(int family in new[]{2,23})
        {
            int size=0;if(GetExtendedUdpTable(IntPtr.Zero,ref size,false,family,1,0)!=122||size<=0)continue;
            var memory=Marshal.AllocHGlobal(size);
            try
            {
                if(GetExtendedUdpTable(memory,ref size,false,family,1,0)!=0)continue;
                int count=Marshal.ReadInt32(memory),stride=family==2?12:28,portOffset=family==2?4:20,pidOffset=family==2?8:24;
                for(int i=0;i<count&&4+(i+1)*stride<=size;i++)
                {
                    var row=IntPtr.Add(memory,4+i*stride);int port=(Marshal.ReadByte(row,portOffset)<<8)|Marshal.ReadByte(row,portOffset+1);
                    rows.Add((port,Marshal.ReadInt32(row,pidOffset)));
                }
            }
            finally{Marshal.FreeHGlobal(memory);}
        }
        var paths=new Dictionary<int,string>();
        foreach(int pid in rows.Select(r=>r.Pid).Distinct())
        {
            string path="";IntPtr handle=OpenProcess(0x1000,false,pid);
            if(handle!=IntPtr.Zero){try{var text=new StringBuilder(32768);int size=text.Capacity;if(QueryFullProcessImageName(handle,0,text,ref size))path=text.ToString();}finally{CloseHandle(handle);}}
            paths[pid]=path;
        }
        return rows.Select(r=>new Owner(r.Port,r.Pid,paths[r.Pid])).ToArray();
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;timer?.Dispose();}}
    [DllImport("iphlpapi.dll")]static extern uint GetExtendedUdpTable(IntPtr table,ref int size,bool order,int family,int tableClass,uint reserved);
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(IntPtr handle,int flags,StringBuilder name,ref int size);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
}
