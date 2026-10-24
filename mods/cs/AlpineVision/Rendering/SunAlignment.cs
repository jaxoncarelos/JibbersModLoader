using Jibbers.Sdk;

namespace JibbersAlpineVision.Rendering;

internal static class SunAlignment
{
    /// <summary>xyz points towards the game's sun; w enables its visual enhancement.</summary>
    public static Vector4 FromLightForward(Vector3 forward)
    {
        var length=forward.Magnitude;
        if(!float.IsFinite(length) || length<0.0001f)return default;
        return new(-forward.X/length,-forward.Y/length,-forward.Z/length,1);
    }
}
