using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace RelayDesk;
public sealed class HttpsAutoPolicy
{
    public record Learned(string Host,string Parent,DateTime Expires);
    readonly ConcurrentDictionary<string,Learned> learned=new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string,DateTime> attempted=new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<Learned> Rules=>learned.Values.Where(r=>r.Expires>DateTime.UtcNow);
    public bool Learnt(string host)=>learned.TryGetValue(host,out var rule)&&rule.Expires>DateTime.UtcNow;
    public void Clear(){learned.Clear();attempted.Clear();}
    public static string Host(string? value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&uri.UserInfo.Length==0?uri.IdnHost.TrimEnd('.').ToLowerInvariant():"";
    public static bool PublicHost(string host)=>host.Contains('.')&&host!="localhost"&&!host.EndsWith(".local",StringComparison.OrdinalIgnoreCase)&&!IPAddress.TryParse(host,out _);
    public static string? DomainRoute(string host,IEnumerable<QuickRoute> rules)
    {
        var rule=rules.FirstOrDefault(r=>r.Kind switch{"domain"=>host.Equals(r.Value,StringComparison.OrdinalIgnoreCase),"domain_suffix"=>RuleMatching.DomainIn(host,r.Value),"domain_keyword"=>host.Contains(r.Value,StringComparison.OrdinalIgnoreCase),_=>false});
        return rule==null?null:rule.Direct||rule.Zapret?"direct":"proxy";
    }
    public bool Begin(string host,string parent,string method,int status,bool alreadyProxy,IEnumerable<QuickRoute> rules)
    {
        return status==403&&BeginTransfer(host,parent,method,alreadyProxy,rules);
    }
    public bool CanInspectTransfer(string host,string parent,string method,bool alreadyProxy,IEnumerable<QuickRoute> rules)=>
        !alreadyProxy&&method is "GET" or "HEAD"&&PublicHost(host)&&PublicHost(parent)&&host!=parent&&(DomainRoute(parent,rules)=="proxy"||Learnt(parent))&&DomainRoute(host,rules)!="direct";
    public bool BeginTransfer(string host,string parent,string method,bool alreadyProxy,IEnumerable<QuickRoute> rules)
    {
        if(!CanInspectTransfer(host,parent,method,alreadyProxy,rules))return false;
        foreach(var expired in learned.Where(p=>p.Value.Expires<=DateTime.UtcNow).Select(p=>p.Key).ToArray())learned.TryRemove(expired,out _);
        if(learned.Count>=200)return false;
        var now=DateTime.UtcNow;
        foreach(var expired in attempted.Where(p=>p.Value<=now).Select(p=>p.Key).ToArray())attempted.TryRemove(expired,out _);
        if(attempted.Count>=1000)return false;
        if(attempted.TryGetValue(host,out var until)&&until>now)return false;
        if(until!=default)attempted.TryRemove(new KeyValuePair<string,DateTime>(host,until));
        return attempted.TryAdd(host,now.AddMinutes(2));
    }
    public bool Complete(string host,string parent,int status)
    {
        if(status is >=200 and <300){learned[host]=new(host,parent,DateTime.UtcNow.AddMinutes(30));return true;}return false;
    }
    public static string ConfigureCore(string config,int vpnPort,int directPort)
    {
        var root=JsonTools.Object(config);var outs=(root["outbounds"] as JsonArray??[]).OfType<JsonObject>().ToArray();
        string final=JsonTools.Text(root["route"]?["final"]);
        var candidates=outs.Where(o=>JsonTools.Text(o["type"]) is not ("direct" or "block" or "dns")).Select(o=>JsonTools.Text(o["tag"])).Where(t=>t.Length>0).ToArray();
        string proxy=candidates.FirstOrDefault(t=>t==final)??candidates.FirstOrDefault()??throw new InvalidOperationException("Для HTTPS-проверки нужен выход VPN.");
        const string vpnTag="tunnelito-https-vpn",directTag="tunnelito-https-direct",outTag="tunnelito-https-egress";
        var inputs=root["inbounds"] as JsonArray??[];root["inbounds"]??=inputs;
        if(inputs.Any(i=>JsonTools.Text(i?["tag"]) is vpnTag or directTag)||outs.Any(o=>JsonTools.Text(o["tag"])==outTag))throw new InvalidOperationException("Служебный тег HTTPS уже занят в конфигурации.");
        inputs.Add(new JsonObject{["type"]="http",["tag"]=vpnTag,["listen"]="127.0.0.1",["listen_port"]=vpnPort});
        inputs.Add(new JsonObject{["type"]="http",["tag"]=directTag,["listen"]="127.0.0.1",["listen_port"]=directPort});
        root["outbounds"]!.AsArray().Add(new JsonObject{["type"]="direct",["tag"]=outTag});
        root["route"]??=new JsonObject();var rules=root["route"]!["rules"] as JsonArray??[];root["route"]!["rules"]??=rules;
        rules.Insert(0,new JsonObject{["inbound"]=new JsonArray(vpnTag),["action"]="route",["outbound"]=proxy});
        rules.Insert(1,new JsonObject{["inbound"]=new JsonArray(directTag),["action"]="route",["outbound"]=outTag});
        return root.ToJsonString(JsonTools.Options);
    }
}
