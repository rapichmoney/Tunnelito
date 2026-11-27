using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Titanium.Web.Proxy.EventArguments;

namespace RelayDesk;

public sealed class ProxyConnectionOwners:IDisposable
{
    public sealed record Owner(int Pid,string Path,string Name);
    sealed record Tracked(WeakReference<Socket> Socket,Owner? Owner);
    readonly ConcurrentDictionary<Socket,Tracked> sockets=new();
    readonly ConcurrentDictionary<Guid,WeakReference<SessionEventArgsBase>> sessions=new();
    readonly ConcurrentDictionary<int,(Owner? Owner,DateTime Expires)> pending=new();
    readonly HashSet<int> upstreamPorts;
    readonly string self=Environment.ProcessPath??"";
    // Titanium 3.2.3 exposes no upstream socket API; read only its pinned internal properties.
    static readonly PropertyInfo? connection=typeof(Titanium.Web.Proxy.Http.HttpWebClient).GetProperty("Connection",BindingFlags.NonPublic|BindingFlags.Instance);
    public ProxyConnectionOwners(params int[] ports){upstreamPorts=ports.ToHashSet();}
    public static Owner? Identify(SessionEventArgsBase session)
    {
        try
        {
            int pid=session.HttpClient.ProcessId.Value;using var process=Process.GetProcessById(pid);
            string path=process.MainModule?.FileName??"";return new(pid,path,process.ProcessName+".exe");
        }
        catch(Exception error)when(error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){return null;}
    }
    public void Watch(SessionEventArgsBase session){if(sessions.Count<4096)sessions[session.ClientConnectionId]=new(session);Capture(session);}
    public IPEndPoint? ReserveSource(SessionEventArgsBase session,IPAddress address)
    {
        foreach(var entry in pending.Where(p=>p.Value.Expires<DateTime.UtcNow))pending.TryRemove(entry.Key,out _);
        if(pending.Count>=4096)return null;
        try
        {
            // Only the ephemeral loopback source port changes; the target and VPN route stay intact.
            using var probe=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);probe.Bind(new IPEndPoint(address,0));
            var endpoint=(IPEndPoint)probe.LocalEndPoint!;
            return pending.TryAdd(endpoint.Port,(Identify(session),DateTime.UtcNow.AddSeconds(30)))?endpoint:null;
        }
        catch(SocketException){return null;}
    }
    public void CaptureReserved(Socket socket)
    {
        try{if(socket.LocalEndPoint is IPEndPoint local&&pending.TryRemove(local.Port,out var item)&&item.Expires>=DateTime.UtcNow)Track(socket,item.Owner);}
        catch(Exception error)when(error is ObjectDisposedException or SocketException){}
    }
    public void Capture(SessionEventArgsBase session)
    {
        try
        {
            object? upstream=connection?.GetValue(session.HttpClient);
            if(upstream?.GetType().GetProperty("TcpSocket",BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(upstream) is Socket socket)Track(socket,Identify(session));
        }
        catch(Exception error)when(error is TargetInvocationException or InvalidOperationException or ObjectDisposedException or SocketException){}
    }
    public void Track(Socket socket,Owner? owner)
    {
        try
        {
            if(socket.RemoteEndPoint is not IPEndPoint remote||!IPAddress.IsLoopback(remote.Address)||!upstreamPorts.Contains(remote.Port))return;
            if(sockets.Count<8192)sockets[socket]=new(new(socket),owner);
        }
        catch(Exception error)when(error is ObjectDisposedException or SocketException){}
    }
    public List<ConnectionRow> Project(IEnumerable<ConnectionRow> rows)
    {
        foreach(var entry in sessions)
        {
            if(entry.Value.TryGetTarget(out var session))Capture(session);else sessions.TryRemove(entry.Key,out _);
        }
        var lookup=new Dictionary<(string,int),List<Owner?>>();
        foreach(var pair in sockets)
        {
            try
            {
                var socket=pair.Key;
                if(socket.SafeHandle.IsClosed||socket.LocalEndPoint is not IPEndPoint local){sockets.TryRemove(socket,out _);continue;}
                var key=(local.Address.MapToIPv6().ToString(),local.Port);
                if(!lookup.TryGetValue(key,out var owners))lookup[key]=owners=[];
                owners.Add(pair.Value.Owner);
            }
            catch(Exception error)when(error is ObjectDisposedException or SocketException){sockets.TryRemove(pair.Key,out _);}
        }
        return rows.Select(row=>
        {
            if(row.ProcessPath.Length>0&&!row.ProcessPath.Equals(self,StringComparison.OrdinalIgnoreCase)||row.Network!="TCP"||!IPAddress.TryParse(row.SourceAddress,out var source)||!IPAddress.IsLoopback(source))return row;
            if(!lookup.TryGetValue((source.MapToIPv6().ToString(),row.SourcePort),out var owners))return row;
            var unique=owners.Distinct().ToArray();
            if(unique.Length!=1||unique[0] is not Owner owner||owner.Pid<=0)return row with{Application="Владелец HTTPS не определён",ProcessPath="",Rule=row.Rule+" · HTTPS-перехват: владелец не подтверждён"};
            return row with{Application=owner.Name,ProcessPath=owner.Path,Rule=row.Rule+" · Через HTTPS-перехват Tunnelito",OriginalOwner=true};
        }).ToList();
    }
    public void Dispose(){sockets.Clear();sessions.Clear();pending.Clear();}
}
