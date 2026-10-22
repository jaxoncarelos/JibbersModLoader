using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Features.Rewind;

/// <summary>A screen-space overlay owned by Rewind, independent of the game's fading respawn UI.</summary>
internal sealed class RewindIndicator(Action<string> log) : IRewindIndicator
{
    OwnedGameObject? overlay;
    GameRef? label;
    bool reportedFailure;

    void EnsureCreated()
    {
        if (overlay is not null && !Unity.IsNull(overlay.Target) && label is not null && !Unity.IsNull(label.Target))
            return;
        Dispose();
        var font = Unity.FindObjects(Unity.Class("TMPro.TMP_Text"))
            .Where(text => !Unity.IsNull(text))
            .Select(text => Unity.Call(text, "get_font"))
            .FirstOrDefault(candidate => !Unity.IsNull(candidate));
        if (font == 0)
            throw new InvalidOperationException("No loaded HUD font is available yet.");

        overlay = OwnedGameObject.Create("JibbersRewind.Hud");
        try
        {
            var canvas = overlay.AddComponent("UnityEngine.Canvas");
            UnityProperties.Set(canvas, "set_renderMode", 0); // ScreenSpaceOverlay
            UnityProperties.Set(canvas, "set_sortingOrder", 950);
            var scaler = overlay.AddComponent("UnityEngine.UI.CanvasScaler");
            UnityProperties.Set(scaler, "set_uiScaleMode", 1);
            UnityProperties.Set(scaler, "set_referenceResolution", new Vector2(1920, 1080));
            UnityProperties.Set(scaler, "set_matchWidthOrHeight", 0.5f);

            using var textObject = OwnedGameObject.Create("Rewind progress");
            var textComponent = textObject.AddComponent("TMPro.TextMeshProUGUI");
            textObject.AttachTo(overlay.Transform);
            label = new GameRef(textComponent);
            Unity.Call(textComponent, "set_font", font);
            UnityProperties.Set(textComponent, "set_fontSize", 30f);
            UnityProperties.Set(textComponent, "set_color", new Color(1, 1, 1, 1));
            UnityProperties.Set(textComponent, "set_raycastTarget", false);
            UnityProperties.Set(textComponent, "set_alignment", 514); // Center
            var rectTransform = Unity.Call(textComponent, "get_rectTransform");
            UnityProperties.Set(rectTransform, "set_anchorMin", new Vector2(0.5f, 0.18f));
            UnityProperties.Set(rectTransform, "set_anchorMax", new Vector2(0.5f, 0.18f));
            UnityProperties.Set(rectTransform, "set_pivot", new Vector2(0.5f, 0.5f));
            UnityProperties.Set(rectTransform, "set_anchoredPosition", new Vector2(0, 0));
            UnityProperties.Set(rectTransform, "set_sizeDelta", new Vector2(1400, 100));
            UnityProperties.Set(rectTransform, "set_localScale", new Vector3(1, 1, 1));
            log("indicator overlay created; independent screen-space canvas, font assigned");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Show(string text)
    {
        try
        {
            EnsureCreated();
            Unity.Call(label!.Target, "set_text", Il2Cpp.String(text));
            UnityProperties.Set(overlay!.Target, "SetActive", true);
            reportedFailure = false;
        }
        catch (Exception exception)
        {
            if (!reportedFailure)
            {
                reportedFailure = true;
                log("indicator unavailable: " + exception.Message);
            }
        }
    }

    public void Hide()
    {
        if (overlay is not null && !Unity.IsNull(overlay.Target))
            UnityProperties.Set(overlay.Target, "SetActive", false);
    }

    public void Dispose()
    {
        var ownedOverlay = overlay;
        var ownedLabel = label;
        overlay = null;
        label = null;
        Cleanup.Run(() => ownedOverlay?.Dispose(), () => ownedLabel?.Dispose());
    }
}