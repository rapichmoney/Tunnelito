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
    bool motion=true,shown;
    public bool IntroductionPlayed{get;private set;}
    public int FrameCount{get;private set;}
    public bool AnimationEnabled {get=>motion;set{motion=value;if(!value)Finish();}}
    public AnimatedBrandTitle()
    {
        Width=178;Height=34;IsHitTestVisible=false;
        ColumnDefinitions.Add(new(){Width=new GridLength(30)});ColumnDefinitions.Add(new(){Width=new GridLength(10)});ColumnDefinitions.Add(new());
        Children.Add(mark);SetColumn(previous,2);SetColumn(current,2);Children.Add(previous);Children.Add(current);
        current.RenderTransform=new TranslateTransform();previous.RenderTransform=new TranslateTransform();
    }
    public void Present(bool upgrade)
    {
        if(shown)return;shown=true;IntroductionPlayed=upgrade;
        if(!motion||!SystemParameters.ClientAreaAnimation){Finish();return;}
        if(upgrade)
        {
            current.Opacity=0;
            previous.Visibility=Visibility.Visible;previous.BeginAnimation(OpacityProperty,new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(820)));
            ((TranslateTransform)previous.RenderTransform).BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(0,-6,TimeSpan.FromMilliseconds(820)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseInOut}});
            var reveal=new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(650)){BeginTime=TimeSpan.FromMilliseconds(200)};reveal.Completed+=(_,_)=>{previous.Visibility=Visibility.Collapsed;FrameCount++;};current.BeginAnimation(OpacityProperty,reveal);
            ((TranslateTransform)current.RenderTransform).BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(6,0,TimeSpan.FromMilliseconds(850)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
            mark.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(850)));
        }
        else {BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(220)));current.BeginAnimation(OpacityProperty,null);current.Opacity=1;FrameCount++;}
    }
    void Finish()
    {
        BeginAnimation(OpacityProperty,null);Opacity=1;previous.BeginAnimation(OpacityProperty,null);previous.Visibility=Visibility.Collapsed;
        current.BeginAnimation(OpacityProperty,null);current.Opacity=1;((TranslateTransform)current.RenderTransform).BeginAnimation(TranslateTransform.YProperty,null);
        ((TranslateTransform)current.RenderTransform).Y=0;mark.BeginAnimation(OpacityProperty,null);mark.Opacity=1;mark.Motion=false;
    }
    public void ConnectionState(bool busy,bool running){mark.Motion=motion&&SystemParameters.ClientAreaAnimation;mark.SetState(busy,running);}
    protected override AutomationPeer OnCreateAutomationPeer()=>new BrandPeer(this);
    sealed class BrandPeer(AnimatedBrandTitle owner):FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore()=>BrandIdentity.Name;
        protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Text;
    }
}
