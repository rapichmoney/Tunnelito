using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RelayDesk;
using RelayDesk.Orbit;

static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
    static int checks;
    static readonly string output=Path.GetFullPath("RelayDesk.BrandChecks/artifacts");
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);checks++;}
    static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    static void Capture(Visual visual,string file,int width,int height,Rect? crop=null)
    {
        if(visual is FrameworkElement element)element.UpdateLayout();
        var target=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        if(crop.HasValue){var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){dc.DrawRectangle(new VisualBrush(visual){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=crop.Value,Stretch=Stretch.Fill},null,new Rect(0,0,width,height));}target.Render(drawing);}else target.Render(visual);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(target));using var stream=File.Create(Path.Combine(output,file));png.Save(stream);
    }
    static Workspace Backend(MainWindow window)=>(Workspace)typeof(MainWindow).GetField("backend",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    static void CaptureWindow(Window window,string name,bool header=false)
    {
        using var image=new System.Drawing.Bitmap((int)window.ActualWidth,(int)window.ActualHeight);using(var graphics=System.Drawing.Graphics.FromImage(image)){var dc=graphics.GetHdc();try{if(!PrintWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle,dc,2))throw new Exception("Could not capture QA window");}finally{graphics.ReleaseHdc(dc);}}
        if(header){using var crop=image.Clone(new System.Drawing.Rectangle(0,0,500,60),image.PixelFormat);crop.Save(Path.Combine(output,name),System.Drawing.Imaging.ImageFormat.Png);}else image.Save(Path.Combine(output,name),System.Drawing.Imaging.ImageFormat.Png);
    }
    [STAThread]static int Main(string[] args)
    {
        if(args.Contains("--origin-direct-probe"))
        {
            try{var network=DirectNetwork.Interface();var location=OriginLocator.Locate(network,CancellationToken.None).GetAwaiter().GetResult();Console.WriteLine("PASS direct interface geolocation returned country "+location.Code+"; no IP address stored");return 0;}catch(Exception ex){Console.WriteLine(ex.Message);return 1;}
        }
        Directory.CreateDirectory(output);var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/RelayDesk;component/Theme.xaml")});SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window=null;
        try
        {
            foreach(string mode in new[]{"orbit","classic"})foreach(bool motion in new[]{true,false})
            {
                string folder=Path.Combine(Path.GetTempPath(),"Tunnelito-brand-QA-"+Guid.NewGuid().ToString("N"));Environment.SetEnvironmentVariable("RELAYDESK_DATA_DIR",folder);Environment.SetEnvironmentVariable("RELAYDESK_INLINE_OVERLAY","1");
                var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
                var store=new Store(folder);var profile=new Profile{Name="Netherlands · Test subscription",Core="sing-box",LocalPort=port,ProxyUri="http://127.0.0.1:"+port,Config="{\"inbounds\":[{\"type\":\"mixed\",\"listen\":\"127.0.0.1\",\"listen_port\":"+port+"}],\"outbounds\":[{\"type\":\"direct\"}]}",SubscriptionGroup="Test subscription"};store.Profiles.Add(profile);store.Settings.Connection=new(false,false,port);store.Settings.LastConnectedProfileId=profile.Id;store.Settings.UiMode=mode;store.Settings.TrayEnabled=false;store.Settings.QuietMode=false;store.Settings.CheckComponentUpdates=false;store.Settings.InterfaceMotion=motion;store.Settings.AsciiBackground=false;store.Settings.GraphiteGlass=false;store.Save();
                window=new MainWindow();window.Show();window.UpdateLayout();var brand=(AnimatedBrandTitle)window.FindName("BrandTitle");
                Check(brand.IntroductionPlayed,mode+" claims one-time RelayDesk migration");Check(window.Title.StartsWith("Tunnelito"),mode+" window has new title");
                if(mode=="orbit"&&motion)
                {
                    for(int i=0;i<42;i++){Capture(window,$"intro-{i:000}.png",500,60,new Rect(0,0,500,60));Pump(33);}
                }
                else Pump(motion?950:30);
                Check(new Store(folder).Settings.BrandingVersion==1,"Introduction is persisted without changing data folder");
                var old=(TextBlock)typeof(AnimatedBrandTitle).GetField("previous",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(brand)!;
                Check(old.Visibility==Visibility.Collapsed,"Old name is hidden after intro or reduced motion");
                Capture(window,$"{mode}-{(motion?"motion":"reduced")}.png",(int)window.ActualWidth,(int)window.ActualHeight);
                if(mode=="orbit"&&motion)
                {
                    var scene=(OrbitScene)window.FindName("OrbitScene");Check(scene.SelectedCountry=="NL"&&scene.OutlineVertices>100,"VPN view contains a real Netherlands border mesh");
                    Check(!((TextBlock)window.FindName("MaterialLabel")).Text.Contains("приблиз"),"Earth footer has no approximation label");
                    var core=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RelayDesk","bundled-cores","sing-box","sing-box-1.14.3-windows-amd64","sing-box.exe");
                    if(File.Exists(core))
                    {
                        var backend=Backend(window);backend.ModernStore.Settings.Engines["sing-box"]=core;var connection=backend.ModernSession.Connect(backend.ModernProfile!,false);
                        var timing=System.Diagnostics.Stopwatch.StartNew();var times=new List<long>();for(int i=0;i<38;i++){CaptureWindow(window,$"connect-{i:000}.png",true);times.Add(timing.ElapsedMilliseconds);Pump(33);}File.WriteAllText(Path.Combine(output,"connect-times.json"),System.Text.Json.JsonSerializer.Serialize(times));
                        while(!connection.IsCompleted)Pump(30);connection.GetAwaiter().GetResult();Pump(600);Check(backend.ModernSession.ProxyRunning&&brand.FillProgress>.99&&brand.NameSaturation>.99,"Actual core startup completes header fill and green saturation");
                        CaptureWindow(window,"orbit-connected-country.png");using(var pixels=new System.Drawing.Bitmap(Path.Combine(output,"orbit-connected-country.png"))){int green=0;for(int x=22;x<198;x++)for(int y=39;y<43;y++){var c=pixels.GetPixel(x,y);if(c.G>c.R*1.2&&c.G>c.B*1.1)green++;}Check(green>100,"Native QA window visibly renders the completed green strip");}
                        var stop=backend.ModernSession.StopProxy();while(!stop.IsCompleted)Pump(30);stop.GetAwaiter().GetResult();Pump(550);Check(brand.FillProgress<.01&&brand.NameSaturation<.01,"Disconnect clears the connection strip and saturation");
                    }
                    Backend(window).ModernStore.Settings.OriginLocation=new("FR","Франция","Test city",48.8566,2.3522,"test-network",DateTimeOffset.UtcNow);
                    typeof(MainWindow).GetMethod("OrbitComponentClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[new Button{Tag="zapret"},new RoutedEventArgs()]);Pump(2600);
                    Check(scene.ShowingOrigin&&scene.SelectedCountry==""&&scene.OutlineVertices==0,"Zapret view shows source point and removes the VPN country contour");CaptureWindow(window,"orbit-origin.png");
                    typeof(MainWindow).GetMethod("OrbitComponentClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[new Button{Tag="vpn"},new RoutedEventArgs()]);Pump(600);Check(!scene.ShowingOrigin&&scene.SelectedCountry=="NL"&&scene.OutlineVertices>0,"Returning to VPN restores the selected country outline");
                    foreach(var page in new[]{(3,"settings"),(1,"profiles"),(2,"rules")})
                    {
                        typeof(MainWindow).GetMethod("SelectPage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[page.Item1]);Pump(500);Capture(window,"tunnelito-"+page.Item2+".png",(int)window.ActualWidth,(int)window.ActualHeight);
                    }
                }
                window.Close();Pump(100);window=new MainWindow();window.Show();Pump(260);brand=(AnimatedBrandTitle)window.FindName("BrandTitle");
                Check(!brand.IntroductionPlayed,"Repeated launch does not replay migration");Check(Backend(window).ModernStore.Profiles.Single().Config==profile.Config&&Backend(window).ModernProfile?.Id==profile.Id,"Profile JSON and selected server survive rebrand and second launch");window.Close();window=null;Pump(50);
            }
            var glyph=new RouteMark{Width=32,Height=32};var host=new Window{Content=glyph,Width=100,Height=100};host.Show();glyph.SetState(true,false);Check(glyph.IsDrawing,"Connection begins one finite route drawing");Pump(700);Check(!glyph.IsDrawing,"Drawing ends without an endless spinner while startup is pending");
            glyph.SetState(false,true);Check(!glyph.IsDrawing,"Core success stops drawing");glyph.SetState(false,false);glyph.Motion=false;glyph.SetState(true,false);Check(!glyph.IsDrawing,"Reduced motion disables connection drawing");host.Close();
            var pending=new AnimatedBrandTitle();var pendingHost=new Window{Content=pending,Width=250,Height=100};pendingHost.Show();pending.ConnectionState(true,false);Pump(900);Check(pending.FillProgress is > .6 and < .9&&pending.NameSaturation<.01,"Pending startup never fills the strip or claims green success");pending.ConnectionState(false,false);Pump(600);Check(pending.FillProgress<.01,"Failed startup clears pending fill");pending.AnimationEnabled=false;pending.ConnectionState(false,true);Check(pending.FillProgress==1&&pending.NameSaturation==1,"Reduced motion sets the completed header state immediately");pendingHost.Close();
            var nl=CountryBorders.Mesh("NL");Check(nl!=null&&nl.IsFrozen&&ReferenceEquals(nl,CountryBorders.Mesh("NL")),"Border mesh is frozen and cached between frames");Check(CountryBorders.Mesh("XX")==null,"Unknown countries do not get fabricated boundaries");Check(CountryBorders.Mesh("SG")?.Positions.Count>0,"Small country Singapore has actual geometry");
            var origin=OriginLocator.Parse("{\"success\":true,\"country_code\":\"NL\",\"country\":\"Netherlands\",\"city\":\"Test city\",\"latitude\":52.2,\"longitude\":5.3,\"ip\":\"192.0.2.1\"}","fixture");
            Check(origin.Country=="Нидерланды"&&!System.Text.Json.JsonSerializer.Serialize(origin).Contains("192.0.2.1"),"Origin stores coordinates and country while discarding IP and response extras");
            foreach(var invalid in new[]{"{\"success\":false}","{\"success\":true,\"country_code\":\"NL\",\"latitude\":999,\"longitude\":0}","{\"success\":true,\"country_code\":\"NL\",\"latitude\":\"52\",\"longitude\":0}"}){bool rejected=false;try{OriginLocator.Parse(invalid,"fixture");}catch(InvalidDataException){rejected=true;}Check(rejected,"Invalid or failed origin lookup is rejected");}
            string fresh=Path.Combine(Path.GetTempPath(),"Tunnelito-fresh-QA-"+Guid.NewGuid().ToString("N"));Environment.SetEnvironmentVariable("RELAYDESK_DATA_DIR",fresh);window=new MainWindow();window.Show();Pump(270);Check(!((AnimatedBrandTitle)window.FindName("BrandTitle")).IntroductionPlayed,"Fresh installation shows Tunnelito directly");window.Close();window=null;
            Console.WriteLine("COMPLETE: "+checks+" branding and animation checks passed.");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}finally{window?.Close();app.Shutdown();}
    }
}
