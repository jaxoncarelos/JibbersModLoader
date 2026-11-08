using Jibbers.Sdk;

namespace RickBackflip;

/// <summary>Le Canvas Unity commun au GIF et à la vidéo : plein écran, au-dessus du jeu, avec une RawImage ancrée dans un coin.</summary>
internal static class OverlayCanvas
{
    /// <summary>Un Canvas plein écran au-dessus du jeu (ScreenSpaceOverlay, 1920x1080 de référence), sans contenu.</summary>
    public static OwnedGameObject BuildRoot(string name)
    {
        var root = OwnedGameObject.Create(name);
        try
        {
            var canvas = root.AddComponent("UnityEngine.Canvas");
            UnityProperties.Set(canvas, "set_renderMode", 0); // ScreenSpaceOverlay
            UnityProperties.Set(canvas, "set_sortingOrder", 930); // au-dessus des overlays du SDK (920)
            var scaler = root.AddComponent("UnityEngine.UI.CanvasScaler");
            UnityProperties.Set(scaler, "set_uiScaleMode", 1); // ScaleWithScreenSize
            UnityProperties.Set(scaler, "set_referenceResolution", new Vector2(1920, 1080));
            UnityProperties.Set(scaler, "set_matchWidthOrHeight", 0.5f);
            try { Unity.Class("UnityEngine.Object").Method("DontDestroyOnLoad", 1).Invoke(0, root.Target); }
            catch { /* reste seulement pour cette map */ }
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    /// <summary>À appeler sur le thread principal. Renvoie le Canvas (à détruire) et la RawImage où poser la texture.</summary>
    public static (OwnedGameObject Root, GameRef Image) Build(string name, GifOverlay.Placement placement, float width, float height)
    {
        var root = BuildRoot(name);
        try
        {
            using var child = OwnedGameObject.Create("Media");
            var raw = child.AddComponent("UnityEngine.UI.RawImage");
            UnityProperties.Set(raw, "set_raycastTarget", false);
            var rect = Unity.Call(raw, "get_rectTransform");
            var anchor = new Vector2(placement.AnchorX, placement.AnchorY);
            UnityProperties.Set(rect, "set_anchorMin", anchor);
            UnityProperties.Set(rect, "set_anchorMax", anchor);
            UnityProperties.Set(rect, "set_pivot", placement.PivotX < 0 ? anchor : new Vector2(placement.PivotX, placement.PivotY));
            UnityProperties.Set(rect, "set_anchoredPosition", new Vector2(placement.OffsetX, placement.OffsetY));
            UnityProperties.Set(rect, "set_sizeDelta", new Vector2(width, height));
            var image = new GameRef(raw);
            child.AttachTo(root.Transform);
            return (root, image);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }
}
