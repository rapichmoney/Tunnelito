using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelayDesk;

public record DomainEntry(string Kind,string Value,string Route);
public record DomainBatch(List<DomainEntry> Entries,List<string> Errors,int Duplicates);
public static class DomainRuleTransfer
{
    public const int Maximum=20000;
    public static bool IsDomain(string kind)=>kind is "domain" or "domain_suffix" or "domain_keyword";
    public static DomainBatch Parse(string text,string kind="domain_suffix",string route="Через VPN")
    {
        if(text.Length>4_000_000)throw new InvalidDataException("Список превышает 4 МБ.");
        var entries=new List<DomainEntry>();var errors=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int duplicates=0,total=0;
        void Add(string k,string v,string r)
        {
            if(++total>Maximum)throw new InvalidDataException("За один раз можно добавить до 20 000 доменов.");
            try
            {
                if(!IsDomain(k)||!RouteRules.Routes.Contains(r))throw new InvalidDataException("Неизвестный тип или маршрут.");
                v=RouteRules.Normalize(k,v);if(!seen.Add(k+"\n"+v)){duplicates++;return;}entries.Add(new(k,v,r));
            }
            catch(Exception ex) when(ex is InvalidDataException or FormatException){errors.Add(v+": "+ex.Message);}
        }
        text=text.Trim();
        if(text.StartsWith('{')||text.StartsWith('[')&&!Regex.IsMatch(text,@"^\[(vpn|direct|zapret)\](\r?\n|$)"))
        {
            using var doc=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=20});var root=doc.RootElement;
            var rows=root.ValueKind==JsonValueKind.Array?root:root.GetProperty("Rules");
            foreach(var row in rows.EnumerateArray())
            {
                if(row.ValueKind==JsonValueKind.String)Add(kind,row.GetString()??"",route);
                else Add(row.GetProperty("Kind").GetString()??"",row.GetProperty("Value").GetString()??"",row.TryGetProperty("Route",out var r)?r.GetString()??route:route);
            }
        }
        else foreach(string line in text.Split('\n'))
        {
            string value=line.Trim();if(value.Length==0||value.StartsWith('#')||value.StartsWith('!')||value.StartsWith("//"))continue;
            if(value.StartsWith('[')&&value.EndsWith(']'))
            {route=value switch{"[vpn]"=>"Через VPN","[direct]"=>"Напрямую","[zapret]"=>"Через zapret",_=>throw new InvalidDataException("Неизвестный раздел списка.")};continue;}
            var columns=value.Split('\t');
            if(columns.Length==3){Add(columns[0],columns[1],columns[2]);continue;}
            foreach(string token in Regex.Split(value,@"[\s,;]+"))if(token.Length>0)Add(kind,token,route);
        }
        return new(entries,errors,duplicates);
    }
    public static string Export(IEnumerable<DomainEntry> entries,bool json)
    {
        var list=entries.ToArray();
        return json?JsonSerializer.Serialize(new{Format="Tunnelito.DomainRules",Version=1,Rules=list},JsonTools.Options):
            "# Tunnelito: тип, домен, маршрут (разделитель — табуляция)\n"+string.Join('\n',list.Select(r=>r.Kind+"\t"+r.Value+"\t"+r.Route))+"\n";
    }
}
