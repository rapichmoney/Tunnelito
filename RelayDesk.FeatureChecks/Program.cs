using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RelayDesk;

static class Program
{
    static int count;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
    static object? Call(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    static void Pump(int ms=100){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    static void Wait(Task task){while(!task.IsCompleted)Pump();task.GetAwaiter().GetResult();}
    static void AcceptRules(App app,MainWindow window,Action open)
    {
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};bool seen=false;
        timer.Tick+=(_,_)=>{var dialog=app.Windows.OfType<DomainBatchWindow>().FirstOrDefault();if(dialog==null)return;timer.Stop();seen=dialog.Owner==window;var add=Tree<Button>(dialog).Single(b=>b.Content?.ToString()=="Добавить выбранные");if(add.IsEnabled)add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));else dialog.Close();};
        timer.Start();try{open();}finally{timer.Stop();}Pump();Check(seen,"Export uses the domain preview rather than VPN profile import");
    }
    static IEnumerable<T> Tree<T>(DependencyObject root) where T:DependencyObject
    {if(root is T match)yield return match;for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Tree<T>(System.Windows.Media.VisualTreeHelper.GetChild(root,i)))yield return child;}
    [STAThread] static int Main(string[] args)
    {
        var app=new App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/RelayDesk;component/Theme.xaml")});SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window=null;
        try
        {
            foreach(string mode in new[]{"orbit","classic"})
            {
                string folder=Path.Combine(Path.GetTempPath(),"Tunnelito-feature-QA-"+Guid.NewGuid().ToString("N"));Environment.SetEnvironmentVariable("RELAYDESK_DATA_DIR",folder);Environment.SetEnvironmentVariable("RELAYDESK_INLINE_OVERLAY","1");
                var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
                var store=new Store(folder);var profile=new Profile{Name="Test only",LocalPort=port,ProxyUri="http://127.0.0.1:"+port,Config=$"{{\"inbounds\":[{{\"type\":\"mixed\",\"listen\":\"127.0.0.1\",\"listen_port\":{port}}}],\"outbounds\":[{{\"type\":\"selector\",\"tag\":\"proxy\",\"outbounds\":[\"direct\"]}},{{\"type\":\"direct\",\"tag\":\"direct\"}}]}}"};store.Profiles.Add(profile);store.Settings.LastConnectedProfileId=profile.Id;store.Settings.Connection=new(false,false,port);store.Settings.SharedQuickRoutes=[];store.Settings.UiMode=mode;store.Settings.TrayEnabled=false;store.Settings.QuietMode=false;store.Settings.CheckComponentUpdates=false;store.Settings.InterfaceMotion=false;store.Settings.AsciiBackground=false;store.Save();
                window=new MainWindow();window.Show();Pump();var backend=(Workspace)typeof(MainWindow).GetField("backend",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
                var slow=new Profile{Name="Slow QA",Core="sing-box",Config=profile.Config};var fast=new Profile{Name="Fast QA",Core="sing-box",Config=profile.Config};
                backend.ModernStore.Profiles.Add(slow);backend.ModernStore.Profiles.Add(fast);
                Call(window,"RecordLatency",new ServerLatency(slow.Name,slow.Core,"qa","1076 мс",1076){ProfileId=slow.Id});
                Call(window,"RecordLatency",new ServerLatency(fast.Name,fast.Core,"qa","50 мс",50){ProfileId=fast.Id});
                Call(window,"RefreshProfiles");var rows=(StackPanel)window.FindName("ProfileRows");
                Check(Tree<TextBlock>(rows).Where(t=>t.Text is "Fast QA" or "Slow QA").Select(t=>t.Text).SequenceEqual(new[]{"Fast QA","Slow QA"}),mode+" profile list sorts by measured delay");
                if(mode=="orbit")
                {
                    Call(window,"RefreshOrbitTree");var tree=(StackPanel)window.FindName("OrbitServerTree");
                    Check(Tree<Button>(tree).Where(b=>b.Tag?.ToString()==fast.Id||b.Tag?.ToString()==slow.Id).Select(b=>b.Tag?.ToString()).SequenceEqual(new[]{fast.Id,slow.Id}),"Orbit sidebar uses the same numeric delay order");
                }
                Check(backend.ModernProfile?.Id==profile.Id,mode+" latency sorting preserves the selected profile");
                backend.ModernStore.Profiles.Remove(slow);backend.ModernStore.Profiles.Remove(fast);Call(window,"RefreshProfiles");
                using(var importDialog=new DisposableSubscriptionDialog(window))
                {
                    Check(importDialog.Accepted&&importDialog.Window.ProfilePreview?.Profile.Protocol=="VLESS"&&importDialog.Window.Source==null,mode+" subscription dialog routes a VLESS link into profile preview");
                }
                Call(window,"SelectPage",2);Pump();Check(Tree<Button>(window).Any(b=>b.Content?.ToString()=="Домены из буфера…"),mode+" offers bulk domain insertion");
                bool seen=false;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
                timer.Tick+=(_,_)=>{var dialog=app.Windows.OfType<DomainBatchWindow>().FirstOrDefault();if(dialog==null)return;timer.Stop();seen=dialog.Owner==window;Tree<Button>(dialog).Single(b=>b.Content?.ToString()=="Добавить выбранные").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
                timer.Start();try{Call(window,"OpenDomainBatch",string.Join('\n',Enumerable.Range(0,1000).Select(i=>"bulk"+i+".example.org")));}finally{timer.Stop();}Pump();
                Check(seen&&backend.ModernStore.Settings.SharedQuickRoutes!.Count==1000,mode+" bulk preview saves one thousand domains: "+((TextBlock)window.FindName("Notice")).Text);
                Check(((StackPanel)window.FindName("AppRoutes")).Children.Count==100,mode+" large library renders one page");
                Check(new Store(folder).Settings.SharedQuickRoutes!.Count==1000,mode+" domain rules survive reload");
                Check(Tree<Button>(window).Any(b=>b.Content?.ToString()=="Экспорт доменов…"),mode+" offers export");
                foreach(bool asJson in new[]{true,false})
                {
                    string domain=asJson?"json-import.example.org":"text-import.example.org";
                    string exported=DomainRuleTransfer.Export([new("domain_suffix",domain,"Через VPN")],asJson);
                    string file=Path.Combine(folder,asJson?"export.json":"export.txt");File.WriteAllText(file,exported);
                    AcceptRules(app,window,()=>backend.ModernImportText(File.ReadAllText(file)));
                    Check(backend.ModernStore.Settings.SharedQuickRoutes!.Any(r=>r.Value==domain)&&backend.ModernStore.Profiles.Count==1,mode+" ordinary file import recognizes exported "+(asJson?"JSON":"TXT"));
                }
                string updateExport=DomainRuleTransfer.Export([new("domain_suffix","json-import.example.org","Напрямую")],true);int beforeUpdate=backend.ModernStore.Settings.SharedQuickRoutes!.Count;
                AcceptRules(app,window,()=>backend.ModernImportText(updateExport));
                Check(backend.ModernStore.Settings.SharedQuickRoutes!.Count==beforeUpdate&&backend.ModernStore.Settings.SharedQuickRoutes.Single(r=>r.Value=="json-import.example.org").Direct,mode+" reimport updates changed route without duplicate");
                var same=new DomainBatchWindow(updateExport,backend.ModernStore.Settings.SharedQuickRoutes.Select(r=>new RouteRule{Kind=r.Kind,Value=r.Value,Route=r.Direct?"Напрямую":r.Zapret?"Через zapret":"Через VPN"}));same.Show();Pump();Check(Tree<TextBlock>(same).Any(t=>t.Text.Contains("Импорт не требуется")),mode+" identical export explains that rules already match");same.Close();
                Call(window,"SelectPage",3);Pump();Check(Tree<Button>(window).Any(b=>b.Content?.ToString()=="Переустановить ядра Tunnelito…"),mode+" offers scoped core repair");
                Check(Tree<Button>(window).Any(b=>b.Content?.ToString()=="Применить HTTPS-настройки")&&!backend.ModernStore.Settings.HttpsInspection,mode+" HTTPS feature is visible and off by default");
                Check(Tree<Button>(window).Any(b=>b.Content?.ToString()=="Удалить доверие")&&Tree<Button>(window).Any(b=>b.Content?.ToString()=="Очистить временные правила"),mode+" certificate and transient rule removal are accessible");
                bool noTrustBlocked=false;try{Wait(backend.ModernSetHttpsInspection(true,[]));}catch(InvalidOperationException){noTrustBlocked=true;}Check(noTrustBlocked&&!backend.ModernStore.Settings.HttpsInspection,mode+" untrusted inspection cannot be enabled accidentally");
                if(mode=="orbit"&&args.Contains("--process"))
                {
                    backend.ModernSaveRoutes([]);BundledCores.Bind(backend.ModernStore,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RelayDesk"));
                    Wait(backend.ModernSession.Connect(backend.ModernProfile!,false));
                    Check(JsonTools.Object(backend.ModernSession.ActiveRuntimeConfig)["route"]!["find_process"]!.GetValue<bool>(),"Live sing-box config enables lookup with zero application rules");
                    var target=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);target.Start();int targetPort=((System.Net.IPEndPoint)target.LocalEndpoint).Port;
                    using var handler=new System.Net.Http.HttpClientHandler{Proxy=new System.Net.WebProxy("http://127.0.0.1:"+port),UseProxy=true};using var client=new System.Net.Http.HttpClient(handler){Timeout=TimeSpan.FromSeconds(12)};using var cancel=new CancellationTokenSource();
                    var accepted=target.AcceptTcpClientAsync();var request=client.GetAsync("http://127.0.0.1:"+targetPort+"/",cancel.Token);Wait(accepted);using var upstream=accepted.Result;
                    using var monitor=new ConnectionMonitor();List<ConnectionRow> observed=[];
                    var deadline=DateTime.UtcNow.AddSeconds(5);
                    do{var read=monitor.Read(backend.ModernSession.ActiveRuntimeConfig,false,[],CancellationToken.None);Wait(read);observed=read.Result;Pump(100);}while(!observed.Any(r=>r.ProcessPath.Length>0)&&DateTime.UtcNow<deadline);
                    Check(observed.Any(r=>r.ProcessPath.EndsWith("FeatureChecks.exe",StringComparison.OrdinalIgnoreCase)),"Actual sing-box API identifies the test application without EXE routing rules");
                    cancel.Cancel();try{Wait(request);}catch(OperationCanceledException){}target.Stop();Wait(backend.ModernSession.StopAll());
                }
                if(mode=="orbit"&&(args.Contains("--repair")||args.Contains("--validate")))
                {
                    Check(backend.ModernStore.Settings.SharedQuickRoutes!.Any(r=>r.Outbound=="@proxy"),"Repair fixture retains normalized common routes");
                    backend.ModernStore.Profiles.Add(new Profile{Core="Xray",Name="Xray validation fixture",QuickRoutes=backend.ModernStore.Settings.SharedQuickRoutes.ToList(),Config="{\"outbounds\":[{\"protocol\":\"socks\",\"tag\":\"proxy\",\"settings\":{\"servers\":[{\"address\":\"127.0.0.1\",\"port\":9}]}}]}"});backend.ModernStore.Save();
                    string installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RelayDesk");BundledCores.Bind(backend.ModernStore,installed);
                    if(args.Contains("--validate"))
                    {
                        Wait((Task)Call(backend,"ValidateNewSingBox",backend.ModernStore.Settings.Engines["sing-box"],CancellationToken.None)!);
                        Wait((Task)Call(backend,"ValidateNewProxyCore","Xray",backend.ModernStore.Settings.Engines["Xray"],CancellationToken.None)!);
                        Check(true,"Sing-box and Xray validators accept normalized @proxy rules after runtime resolution");
                        backend.ModernStore.Profiles.Add(new Profile{Name="Unfinished profile",Config="{\"outbounds\":[{\"type\":\"direct\"}]}"});
                        backend.ModernStore.Settings.RoutingPreset="proxy";backend.ModernStore.Settings.FallbackRoute="proxy";
                        bool reproduced=false;try{_=RuntimePolicy.Config(backend.ModernStore.Profiles.Last(),backend.ModernStore.Settings,ConnectionMode.VpnOnly);}catch(InvalidOperationException ex){reproduced=ex.Message.Contains("нет выхода VPN");}
                        Check(reproduced,"Reproduces missing VPN output in unfinished dormant profile");
                        foreach(string core in new[]{"sing-box","Xray","mihomo"})
                        {
                            Wait((Task)Call(backend,"CheckRepairProxy",core,backend.ModernStore.Settings.Engines[core],null!)!);
                            Check(true,core+" repair self-test ignores unfinished profiles and user proxy preset");
                        }
                        window.Close();window=null;Pump();continue;
                    }
                    Wait(backend.ModernSession.Connect(backend.ModernProfile!,false));int beforePid=backend.ModernSession.Proxy!.Id;
                    backend.ModernStore.Profiles.Add(new Profile{Name="Unfinished profile",Config="{\"outbounds\":[{\"type\":\"direct\"}]}"});backend.ModernStore.Settings.RoutingPreset="proxy";backend.ModernStore.Save();
                    var originalProfiles=File.ReadAllText(Path.Combine(folder,"profiles.json"));
                    Wait(backend.ModernRepairCores(Console.WriteLine));
                    Check(backend.ModernSession.ProxyRunning&&backend.ModernSession.Proxy!.Id!=beforePid,"Repair resumes only its own local test core after replacement");
                    Check(File.ReadAllText(Path.Combine(folder,"profiles.json"))==originalProfiles,"Core repair preserves profiles byte-for-byte");
                    Check(backend.ModernStore.Settings.ComponentInstalls.Count==4&&backend.ModernStore.Settings.ComponentInstalls.Values.All(r=>r.Executable.StartsWith(folder,StringComparison.OrdinalIgnoreCase)),"Four downloaded pinned cores are bound to the isolated store");
                    Check(!backend.ModernStore.Settings.Connection!.Tun&&!backend.ModernStore.Settings.Connection.SystemProxy,"Repair preserves local-only test transport");
                    Wait(backend.ModernSession.StopAll());
                }
                window.Close();window=null;Pump();
            }
            Console.WriteLine($"COMPLETE: {count} feature checks passed.");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
        finally{window?.Close();app.Shutdown();}
    }
}

sealed class DisposableSubscriptionDialog:IDisposable
{
    public SubscriptionWindow Window{get;}
    public bool Accepted{get;}
    public DisposableSubscriptionDialog(Window owner)
    {
        Window=new SubscriptionWindow([],"vless://11111111-1111-4111-8111-111111111111@node.example.invalid:8446?type=ws&security=none#QA","QA"){Owner=owner};
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        timer.Tick+=(_,_)=>{timer.Stop();var button=Find<Button>(Window).Single(b=>b.Content?.ToString()=="Загрузить предпросмотр");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
        timer.Start();try{Accepted=Window.ShowDialog()==true;}finally{timer.Stop();}
    }
    static IEnumerable<T> Find<T>(DependencyObject root)where T:DependencyObject{if(root is T item)yield return item;for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Find<T>(System.Windows.Media.VisualTreeHelper.GetChild(root,i)))yield return child;}
    public void Dispose(){if(Window.IsVisible)Window.Close();}
}
