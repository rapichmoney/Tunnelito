namespace RelayDesk;

public static class ServerLatencyOrder
{
    public static IEnumerable<Profile> Sort(IEnumerable<Profile> profiles,IReadOnlyDictionary<string,long?> delays)=>
        profiles.OrderBy(p=>delays.TryGetValue(p.Id,out var value)&&value>=0?value.Value:long.MaxValue);
}
