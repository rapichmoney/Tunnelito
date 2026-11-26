namespace RelayDesk.Orbit;

public sealed class GlobeInertia
{
    const double Friction=1.6,MaxSpeed=6;
    double lastSample,distance;
    bool sampled;
    public double X{get;private set;}
    public double Y{get;private set;}
    public bool Active{get;private set;}
    public void Begin(double time){Stop();lastSample=time;}
    public void Sample(double dx,double dy,double time)
    {
        if(!double.IsFinite(dx)||!double.IsFinite(dy)||!double.IsFinite(time)||time<=lastSample)return;
        double dt=Math.Max(.001,time-lastSample),blend=sampled?1-Math.Exp(-dt/.045):1;
        X+=(Math.Clamp(dx/dt,-MaxSpeed,MaxSpeed)-X)*blend;
        Y+=(Math.Clamp(dy/dt,-MaxSpeed,MaxSpeed)-Y)*blend;
        distance+=Math.Sqrt(dx*dx+dy*dy);lastSample=time;sampled=true;
    }
    public void Release(double time)
    {
        double gap=Math.Max(0,time-lastSample);
        if(!sampled||gap>.12||distance<.006){Stop();return;}
        double decay=Math.Exp(-gap/.06);X*=decay;Y*=decay;
        Active=Math.Sqrt(X*X+Y*Y)>.06;if(!Active)Stop();
    }
    public (double X,double Y) Advance(double dt)
    {
        if(!Active||!double.IsFinite(dt)||dt<=0)return(0,0);
        double decay=Math.Exp(-Friction*dt),integral=(1-decay)/Friction;
        var delta=(X*integral,Y*integral);X*=decay;Y*=decay;
        if(Math.Sqrt(X*X+Y*Y)<.01)Stop();return delta;
    }
    public void StopVertical(){Y=0;}
    public void Stop(){X=Y=distance=0;Active=sampled=false;}
}
