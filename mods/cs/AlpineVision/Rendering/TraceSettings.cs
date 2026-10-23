namespace JibbersAlpineVision.Rendering;
public readonly record struct TraceSettings(float ShadowStrength,float AmbientOcclusion,float OcclusionRadius,
    float SunConeRadians,float ResolutionScale,int OcclusionRays,bool DebugView);
