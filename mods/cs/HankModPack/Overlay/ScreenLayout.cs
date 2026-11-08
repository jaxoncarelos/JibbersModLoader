using Jibbers.Sdk.Interop;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Player;

namespace RickBackflip;

/// <summary>
/// Où se trouve le coureur à l'écran, et où poser une bannière sans le recouvrir.
/// Coordonnées en « pixels du Canvas » (référence 1920x1080, origine en bas à gauche), comme les overlays.
/// </summary>
internal static unsafe class ScreenLayout
{
    /// <summary>Taille du Canvas en pixels de référence (même formule que CanvasScaler en mode ScaleWithScreenSize, match 0,5).</summary>
    public static (float Width, float Height, float Scale) CanvasSize()
    {
        var screen = Unity.Class("UnityEngine.Screen");
        var sw = Il2Cpp.Unbox<int>(Unity.CallStatic(screen, "get_width"));
        var sh = Il2Cpp.Unbox<int>(Unity.CallStatic(screen, "get_height"));
        if (sw <= 0 || sh <= 0) return (1920, 1080, 1);
        var scale = (float)(Math.Sqrt(sw / 1920.0) * Math.Sqrt(sh / 1080.0));
        return (sw / scale, sh / scale, scale);
    }

    /// <summary>Position du coureur dans le Canvas, ou null si on ne la trouve pas.</summary>
    public static (float X, float Y)? PlayerPoint(float canvasScale, float canvasW, float canvasH)
    {
        var rider = RagdollSpawner.First()?.Rider;
        if (rider is not { } r || r.Ptr == 0) return null;
        var world = Unity.Call<Vector3>(Unity.Transform(r.Ptr), "get_position");
        var cam = Unity.MainCamera;
        if (cam == 0 || Unity.IsNull(cam)) return null;
        var v = world;
        var sp = Unity.Call<Vector3>(cam, "WorldToScreenPoint", (nint)(&v));
        if (sp.Z <= 0) return null;
        var (x, y) = (sp.X / canvasScale, sp.Y / canvasScale);
        if (x < -300 || y < -300 || x > canvasW + 300 || y > canvasH + 300) return null;
        return (x, y);
    }

    /// <summary>
    /// Choisit le coin bas-gauche d'un cadre de <paramref name="w"/> x <paramref name="h"/> qui ne touche aucune zone interdite
    /// et s'éloigne de la dernière bannière. Renvoie null si rien ne tient.
    /// </summary>
    public static (float X, float Y)? Choose(Random rnd, float cw, float ch, float w, float h,
        IReadOnlyList<(float X0, float Y0, float X1, float Y1)> forbidden, (float X, float Y)? previousCenter)
    {
        const float margin = 30;
        if (w + 2 * margin > cw || h + 2 * margin > ch) return null;
        (float X, float Y)? fallback = null;
        for (var i = 0; i < 80; i++)
        {
            var x = margin + (float)rnd.NextDouble() * (cw - w - 2 * margin);
            var y = margin + (float)rnd.NextDouble() * (ch - h - 2 * margin);
            if (forbidden.Any(f => x < f.X1 && x + w > f.X0 && y < f.Y1 && y + h > f.Y0)) continue;
            fallback ??= (x, y);
            if (previousCenter is { } p && Math.Abs(x + w / 2 - p.X) < 350 && Math.Abs(y + h / 2 - p.Y) < 220 && i < 60) continue; // pas au même endroit deux fois de suite
            return (x, y);
        }
        return fallback;
    }
}
