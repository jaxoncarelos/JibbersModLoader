# Atmosphere assets

The Unity 6000.3 project that builds `build/jibbers_atmosphere.bundle` (the fog, snow, cloud and
character-mask shaders and their prefab) for the C# [Atmosphere](../cs/Atmosphere) mod. The game's
build strips Unity's fog shader variants, so the effects ship as our own shaders.

The prebuilt bundle is committed. Rebuild it after changing a shader or the prefab:

```powershell
Unity.exe -batchmode -quit -nographics -projectPath mods/JibbersAtmosphere/unity -executeMethod BuildAtmosphere.Build
```

`tools/package_dotnet.ps1` packages the bundle; players install it beside `Jibbers.exe`.
