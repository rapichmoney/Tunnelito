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
                        window.Close();window=null;Pump();continue;
                    }
                    Wait(backend.ModernSession.Connect(backend.ModernProfile!,false));int beforePid=backend.ModernSession.Proxy!.Id;
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
