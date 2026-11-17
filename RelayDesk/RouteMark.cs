using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
namespace RelayDesk;
public sealed class RouteMark : Viewbox
{
    readonly Path route=new(){Data=Geometry.Parse("M5,23 L11,23 Q16,23 16,18 L16,14 Q16,9 21,9 L27,9"),StrokeThickness=2.3,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round};
    readonly Ellipse end=new(){Width=6,Height=6,StrokeThickness=2};
    readonly TranslateTransform reveal=new();
    bool wasBusy,wasRunning;
    bool motion=true;
    public bool Motion{get=>motion;set{motion=value;if(!value){reveal.BeginAnimation(TranslateTransform.XProperty,null);reveal.X=0;IsDrawing=false;}}}
    public bool IsDrawing{get;private set;}
    public RouteMark()
    {
        var canvas=new Canvas{Width=32,Height=32};Child=canvas;Stretch=Stretch.Uniform;route.Clip=new RectangleGeometry(new Rect(-2,-2,36,36)){Transform=reveal};
        var start=new Ellipse{Width=6,Height=6,Fill=new SolidColorBrush(Color.FromRgb(229,229,229))};Canvas.SetLeft(start,2);Canvas.SetTop(start,20);
        Canvas.SetLeft(end,24);Canvas.SetTop(end,6);canvas.Children.Add(route);canvas.Children.Add(start);canvas.Children.Add(end);Paint(false);
    }
    void Paint(bool running){var ink=new SolidColorBrush(running?Color.FromRgb(117,199,147):Color.FromRgb(222,222,222));route.Stroke=ink;end.Stroke=ink;}
    public void SetState(bool busy,bool running)
    {
        if(busy==wasBusy&&running==wasRunning)return;
        bool start=busy&&!wasBusy;wasBusy=busy;wasRunning=running;Paint(running&&!busy);
        if(start&&Motion)
        {
            IsDrawing=true;
            var draw=new DoubleAnimation(-36,0,TimeSpan.FromMilliseconds(620)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
            draw.Completed+=(_,_)=>IsDrawing=false;reveal.BeginAnimation(TranslateTransform.XProperty,draw);
        }
        if(!busy||!Motion){reveal.BeginAnimation(TranslateTransform.XProperty,null);reveal.X=0;IsDrawing=false;}
        ToolTip=busy?"Переключение подключения":running?"Ядро запущено. Интернет проверяется отдельно через HTTPS.":"Подключение выключено";
    }
}
