using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.UI;

/// <summary>
/// The font of overlays and HUD text: Segoe UI, Windows' interface font, made into a TextMeshPro
/// font asset once from the installed font. Where that can't be done (another OS, a game build
/// without the calls), the game's own font, as before. Main thread only.
/// </summary>
internal static unsafe class UiFont
{
    const string Family = "Segoe UI";
    const int SamplingSize = 90;
    const int DontUnloadUnusedAsset = 32; // HideFlags: a scene change's asset cleanup keeps it

    static GameRef? system;
    static bool tried;

    /// <summary>The font to give a new text, or 0 while the game hasn't loaded any font yet.</summary>
    public static nint Get()
    {
        if (system is not null && !Unity.IsNull(system.Target))
            return system.Target;
        if (!tried)
        {
            tried = true;
            try
            {
                var made = Create();
                if (made != 0)
                {
                    system = new GameRef(made);
                    return made;
                }
                Log.Info("overlay", $"{Family} isn't available; overlays use the game's font");
            }
            catch (Exception e) when (e is InvalidOperationException or MissingMethodException or Il2CppException or ArgumentException)
            {
                Log.Info("overlay", $"{Family} couldn't be loaded ({e.Message}); overlays use the game's font");
            }
        }
        return GameFont();
    }

    static nint GameFont() =>
        Unity.FindObjects(Unity.Class("TMPro.TMP_Text")).Where(t => !Unity.IsNull(t)).Select(t => Unity.Call(t, "get_font")).FirstOrDefault(f => !Unity.IsNull(f));

    static nint Create()
    {
        var assets = Unity.Class("TMPro.TMP_FontAsset");
        if (assets.IsNull)
            return 0;
        nint asset = 0;
        // TextMeshPro 3.2+ reads a system font by family name directly.
        var byName = assets.Method("CreateFontAsset", "System.String", "System.String", "System.Int32");
        if (!byName.IsNull)
        {
            var size = SamplingSize;
            asset = byName.Invoke(0, Il2Cpp.String(Family), Il2Cpp.String("Regular"), (nint)(&size));
        }
        // Older: a dynamic Unity font from the OS font, then a font asset from it.
        if (asset == 0 || Unity.IsNull(asset))
        {
            var fromOs = Unity.Class("UnityEngine.Font").Method("CreateDynamicFontFromOSFont", "System.String", "System.Int32");
            var fromFont = assets.Method("CreateFontAsset", "UnityEngine.Font");
            if (fromOs.IsNull || fromFont.IsNull)
                return 0;
            var size = SamplingSize;
            var font = fromOs.Invoke(0, Il2Cpp.String(Family), (nint)(&size));
            if (font == 0 || Unity.IsNull(font))
                return 0;
            Keep(font);
            asset = fromFont.Invoke(0, font);
        }
        if (asset == 0 || Unity.IsNull(asset))
            return 0;
        Keep(asset);
        // Characters the system font lacks (symbols, other scripts) come from the game's font.
        try
        {
            var game = GameFont();
            var fallbacks = Unity.Call(asset, "get_fallbackFontAssetTable");
            if (game != 0 && fallbacks != 0)
                Il2Cpp.ClassOf(fallbacks).Method("Add", 1).Invoke(fallbacks, game);
        }
        catch (Exception e) when (e is InvalidOperationException or MissingMethodException or Il2CppException) { }
        Log.Info("overlay", $"overlays use {Family}");
        return asset;
    }

    static void Keep(nint obj)
    {
        try { UnityProperties.Set(obj, "set_hideFlags", DontUnloadUnusedAsset); }
        catch (MissingMethodException) { }
    }
}
