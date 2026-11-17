using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RelayDesk;

static class Program
{
    static int checks;
    static readonly string output=Path.GetFullPath("RelayDesk.BrandChecks/artifacts");
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);checks++;}
    static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    static void Capture(Visual visual,string file,int width,int height,Rect? crop=null)
    {
        var target=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        if(crop.HasValue){var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){dc.DrawRectangle(new VisualBrush(visual){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=crop.Value,Stretch=Stretch.Fill},null,new Rect(0,0,width,height));}target.Render(drawing);}else target.Render(visual);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(target));using var stream=File.Create(Path.Combine(output,file));png.Save(stream);
    }
    static Workspace Backend(MainWindow window)=>(Workspace)typeof(MainWindow).GetField("backend",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    [STAThread]static int Main()
    {
        Directory.CreateDirectory(output);var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/RelayDesk;component/Theme.xaml")});SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window=null;
        try
        {
            foreach(string mode in new[]{"orbit","classic"})foreach(bool motion in new[]{true,false})
            {
                string folder=Path.Combine(Path.GetTempPath(),"Tunnelito-brand-QA-"+Guid.NewGuid().ToString("N"));Environment.SetEnvironmentVariable("RELAYDESK_DATA_DIR",folder);Environment.SetEnvironmentVariable("RELAYDESK_INLINE_OVERLAY","1");
                var store=new Store(folder);var profile=new Profile{Name="Sweden · Test subscription",Core="sing-box",Config="{\"inbounds\":[],\"outbounds\":[{\"type\":\"direct\"}]}",SubscriptionGroup="Test subscription"};store.Profiles.Add(profile);store.Settings.LastConnectedProfileId=profile.Id;store.Settings.UiMode=mode;store.Settings.TrayEnabled=false;store.Settings.QuietMode=false;store.Settings.CheckComponentUpdates=false;store.Settings.InterfaceMotion=motion;store.Settings.AsciiBackground=false;store.Settings.GraphiteGlass=false;store.Save();
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
            string fresh=Path.Combine(Path.GetTempPath(),"Tunnelito-fresh-QA-"+Guid.NewGuid().ToString("N"));Environment.SetEnvironmentVariable("RELAYDESK_DATA_DIR",fresh);window=new MainWindow();window.Show();Pump(270);Check(!((AnimatedBrandTitle)window.FindName("BrandTitle")).IntroductionPlayed,"Fresh installation shows Tunnelito directly");window.Close();window=null;
            Console.WriteLine("COMPLETE: "+checks+" branding and animation checks passed.");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}finally{window?.Close();app.Shutdown();}
    }
}
