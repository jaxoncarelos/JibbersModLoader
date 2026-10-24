using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk;

namespace JibbersAlpineVision;

/// <summary>Reads the game's clock; visual styles never change the time of day.</summary>
internal static class NightCycle
{
    static GameRef? environmentReference;
    public static float NightBlend { get; private set; }
    public static float NormalizedTime { get; private set; }
    public static float NightStart { get; private set; }
    public static float TwilightWidth { get; private set; }

    public static float CalculateNightBlend(float time, float nightStart, float twilightWidth)
    {
        if(!float.IsFinite(time) || !float.IsFinite(nightStart) || !float.IsFinite(twilightWidth))return 0;
        var width=Math.Clamp(twilightWidth,.001f,.25f);
        var progress=Math.Clamp((time-nightStart)/width,0,1);
        return progress*progress*(3-2*progress);
    }

    public static void Observe(nint environmentManager)
    {
        if(environmentReference?.Target!=environmentManager)
        {
            environmentReference?.Dispose();
            environmentReference=new GameRef(environmentManager);
        }
        Refresh();
    }

    public static void Refresh()
    {
        var pointer=environmentReference?.Target ?? 0;
        if(Unity.IsNull(pointer)){Reset();return;}
        var environment=new Manager(pointer).Raw;
        NormalizedTime=environment.dayNightT;
        NightStart=environment.nightStartT;
        TwilightWidth=environment.twilightWidth;
        NightBlend=CalculateNightBlend(NormalizedTime,NightStart,TwilightWidth);
    }

    public static void Reset()
    {
        environmentReference?.Dispose();environmentReference=null;
        NightBlend=0;NormalizedTime=0;NightStart=0;TwilightWidth=0;
    }
}
