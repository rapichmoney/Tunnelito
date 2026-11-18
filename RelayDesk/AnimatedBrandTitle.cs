using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace RelayDesk;
public sealed class AnimatedBrandTitle : Grid
{
    readonly TextBlock current=new(){Text=BrandIdentity.Name,FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(236,238,242)),VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock previous=new(){Text="RelayDesk",FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(236,238,242)),VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed};
    readonly RouteMark mark=new(){Width=30,Height=30,VerticalAlignment=VerticalAlignment.Center};
    readonly Grid next=new();
    readonly TextBlock saturated=new(){Text=BrandIdentity.Name,FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(117,199,147)),VerticalAlignment=VerticalAlignment.Center,Opacity=0};
    readonly ScaleTransform fill=new(0,1);
    readonly System.Windows.Shapes.Rectangle progress=new(){Height=2,VerticalAlignment=VerticalAlignment.Bottom,Fill=new SolidColorBrush(Color.FromRgb(117,199,147)),Opacity=0,IsHitTestVisible=false};
    bool wasBusy,wasRunning;
    int connectionAnimation;
    public double FillProgress=>fill.ScaleX;
    public double NameSaturation=>saturated.Opacity;
    bool motion=true,shown;
    public bool IntroductionPlayed{get;private set;}
    public int FrameCount{get;private set;}
    public bool AnimationEnabled {get=>motion;set{motion=value;if(!value)Finish();}}
    public AnimatedBrandTitle()
    {
        Width=178;Height=34;IsHitTestVisible=false;
        ColumnDefinitions.Add(new(){Width=new GridLength(30)});ColumnDefinitions.Add(new(){Width=new GridLength(10)});ColumnDefinitions.Add(new());
        Children.Add(mark);SetColumn(previous,2);SetColumn(next,2);Children.Add(previous);Children.Add(next);next.Children.Add(current);next.Children.Add(saturated);
        progress.RenderTransform=fill;SetColumnSpan(progress,3);Children.Add(progress);
        next.RenderTransform=new TranslateTransform();previous.RenderTransform=new TranslateTransform();
    }
    public void Present(bool upgrade)
    {
        if(shown)return;shown=true;IntroductionPlayed=upgrade;
        if(!motion||!SystemParameters.ClientAreaAnimation){Finish();return;}
        if(upgrade)
        {
            next.Opacity=0;
            previous.Visibility=Visibility.Visible;previous.BeginAnimation(OpacityProperty,new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(820)));
            ((TranslateTransform)previous.RenderTransform).BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(0,-6,TimeSpan.FromMilliseconds(820)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseInOut}});
            var reveal=new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(650)){BeginTime=TimeSpan.FromMilliseconds(200)};reveal.Completed+=(_,_)=>{previous.Visibility=Visibility.Collapsed;FrameCount++;};next.BeginAnimation(OpacityProperty,reveal);
            ((TranslateTransform)next.RenderTransform).BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(6,0,TimeSpan.FromMilliseconds(850)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
            mark.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(850)));
        }
        else {BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(220)));next.BeginAnimation(OpacityProperty,null);next.Opacity=1;FrameCount++;}
    }
    void Finish()
    {
        BeginAnimation(OpacityProperty,null);Opacity=1;previous.BeginAnimation(OpacityProperty,null);previous.Visibility=Visibility.Collapsed;
        next.BeginAnimation(OpacityProperty,null);next.Opacity=1;((TranslateTransform)next.RenderTransform).BeginAnimation(TranslateTransform.YProperty,null);
        ((TranslateTransform)next.RenderTransform).Y=0;mark.BeginAnimation(OpacityProperty,null);mark.Opacity=1;mark.Motion=false;SetConnection(false);
    }
    public void ConnectionState(bool busy,bool running)
    {
        mark.Motion=motion&&SystemParameters.ClientAreaAnimation;mark.SetState(busy,running);
        if(wasBusy==busy&&wasRunning==running)return;wasBusy=busy;wasRunning=running;SetConnection(motion&&SystemParameters.ClientAreaAnimation);
    }
    void SetConnection(bool animate)
    {
        int stamp=++connectionAnimation;
        double target=wasBusy?.65:wasRunning?1:0,saturation=!wasBusy&&wasRunning?1:0,opacity=wasBusy?.65:wasRunning?1:0;
        if(!animate){fill.BeginAnimation(ScaleTransform.ScaleXProperty,null);fill.ScaleX=target;saturated.BeginAnimation(OpacityProperty,null);saturated.Opacity=saturation;progress.BeginAnimation(OpacityProperty,null);progress.Opacity=opacity;return;}
        void Run(DependencyObject element,DependencyProperty property,double value,int duration)
        {
            var animation=new DoubleAnimation(value,TimeSpan.FromMilliseconds(duration)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
            animation.Completed+=(_,_)=>{if(stamp!=connectionAnimation)return;((IAnimatable)element).BeginAnimation(property,null);element.SetValue(property,value);progress.InvalidateVisual();};
            ((IAnimatable)element).BeginAnimation(property,animation);
        }
        Run(fill,ScaleTransform.ScaleXProperty,target,wasBusy?650:400);Run(saturated,OpacityProperty,saturation,520);Run(progress,OpacityProperty,opacity,260);
    }
    protected override AutomationPeer OnCreateAutomationPeer()=>new BrandPeer(this);
    sealed class BrandPeer(AnimatedBrandTitle owner):FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore()=>BrandIdentity.Name;
        protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Text;
    }
}
