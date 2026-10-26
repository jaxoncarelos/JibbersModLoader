# FPV assets

The Unity 6000.3 project that builds `build/jibbers_fpv.bundle` (the drone's motor, wind and crash
audio sources) for the C# [FPV](../cs/FPV) mod. The game's build strips `AudioClip.Create`, so the
clips ship as assets rendered from the mod's synth (`DroneSounds`).

The prebuilt bundle is committed. After changing the synth, re-render the clips and rebuild:

```powershell
$env:JIBBERS_WRITE_SOUNDS = '1'; dotnet run --project mods/cs/FPV.Tests -c Release -p:JibbersNoInstall=true; Remove-Item Env:\JIBBERS_WRITE_SOUNDS
Unity.exe -batchmode -quit -nographics -projectPath mods/JibbersFPV/unity -executeMethod BuildFpv.Build
```

`tools/package_dotnet.ps1` packages the bundle; players install it in `JibbersMods`.
