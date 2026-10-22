# Atmosphere C# source

`Atmosphere.cs` registers the reusable SDK feature. The complete C# implementation is in
[`sdk/dotnet/Jibbers.Sdk/Features/Atmosphere`](../../../sdk/dotnet/Jibbers.Sdk/Features/Atmosphere).

Read `AtmosphereEffects` and `AtmosphereRuntime` first. The configuration store owns presets and
persistence; typed render settings and shader parameters describe each effect. Scene measurements,
surface materials, quality settings, grading and prefab loading have separate engine adapters.

See the [source walkthrough](../../../sdk/dotnet/Jibbers.Sdk/Features/README.md).
Build with `dotnet build mods/cs/Atmosphere -c Release -p:JibbersNoInstall=true`.
