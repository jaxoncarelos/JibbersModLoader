using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Features.Fpv;

/// <summary>Owns only the drone's screen-space HUD objects and text refresh cadence.</summary>
internal sealed class DroneHud : IDisposable
{
    readonly OwnedGameObject root;
    readonly List<GameRef> texts = new();
    float secondsUntilRefresh;
    public DroneHud()
    {
        root = OwnedGameObject.Create("JibbersFPV.Hud");
        try
        {
            Build();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void Build()
    {
        var gameObject = root.Target;
        var canvas = root.AddComponent("UnityEngine.Canvas");
        UnityProperties.Set(canvas, "set_renderMode", 0);
        UnityProperties.Set(canvas, "set_sortingOrder", 900);
        var scaler = root.AddComponent("UnityEngine.UI.CanvasScaler");
        UnityProperties.Set(scaler, "set_uiScaleMode", 1);
        UnityProperties.Set(scaler, "set_referenceResolution", new Vector2(1920, 1080));
        UnityProperties.Set(scaler, "set_matchWidthOrHeight", 0.5f);
        var font = Unity.FindObjects(Unity.Class("TMPro.TMP_Text")).Where(textObject => !Unity.IsNull(textObject)).Select(textObject => Unity.Call(textObject, "get_font")).FirstOrDefault(availableFont => !Unity.IsNull(availableFont));
        var blocks = new[]
        {
            (new Vector2(0, 0), new Vector2(40, 40), "BottomLeft", 30f),
            (new Vector2(1, 0), new Vector2(-40, 40), "BottomRight", 30f),
            (new Vector2(0.5f, 1), new Vector2(0, -40), "Top", 26f),
            (new Vector2(0.5f, 0.5f), new Vector2(0, 0), "Center", 44f)
        };
        foreach (var(anchor, offset, alignment, size)in blocks)
        {
            using var child = OwnedGameObject.Create("FPV OSD");
            var text = child.AddComponent("TMPro.TextMeshProUGUI");
            var rectTransform = Unity.Call(text, "get_rectTransform");
            child.AttachTo(root.Transform);
            texts.Add(new GameRef(text));
            if (font != 0)
                Unity.Call(text, "set_font", font);
            UnityProperties.Set(text, "set_fontSize", size);
            UnityProperties.Set(text, "set_richText", true);
            UnityProperties.Set(text, "set_raycastTarget", false);
            var alignmentValue = Unity.Class("TMPro.TextAlignmentOptions").Field(alignment).Get<int>(0);
            UnityProperties.Set(text, "set_alignment", alignmentValue);
            if (!Il2Cpp.ClassOf(text).Method("set_textWrappingMode", 1).IsNull)
                UnityProperties.Set(text, "set_textWrappingMode", 0);
            else
                UnityProperties.Set(text, "set_enableWordWrapping", false);
            UnityProperties.Set(text, "set_color", new Color(1, 1, 1, 1));
            UnityProperties.Set(rectTransform, "set_anchorMin", anchor);
            UnityProperties.Set(rectTransform, "set_anchorMax", anchor);
            UnityProperties.Set(rectTransform, "set_pivot", anchor);
            UnityProperties.Set(rectTransform, "set_anchoredPosition", offset);
            UnityProperties.Set(rectTransform, "set_sizeDelta", new Vector2(900, 200));
        }
    }

    public void Update(float elapsedSeconds, DroneTelemetry telemetry)
    {
        secondsUntilRefresh -= elapsedSeconds;
        if (secondsUntilRefresh > 0)
            return;
        secondsUntilRefresh = 1 / 15f;
        var blocks = DroneTelemetryFormatter.Format(telemetry);
        for (var index = 0; index < texts.Count; index++)
            Unity.Call(texts[index].Target, "set_text", Il2Cpp.String(blocks[index]));
    }

    public void Dispose()
    {
        var references = texts.ToArray();
        texts.Clear();
        Cleanup.Run(new Action[] { root.Dispose }.Concat(references.Select(reference => (Action)reference.Dispose)).ToArray());
    }
}
