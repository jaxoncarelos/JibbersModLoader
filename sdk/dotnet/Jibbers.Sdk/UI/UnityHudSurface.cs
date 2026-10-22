using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.UI;

/// <summary>Unity/TMP implementation of the HUD rendering boundary. Called only on the main thread.</summary>
internal sealed class UnityHudSurface : IHudSurface
{
    OwnedGameObject? root;
    readonly Dictionary<string, GameRef> texts = new();
    nint font;

    public void Render(HudSnapshot snapshot)
    {
        try
        {
            if (root is null || Unity.IsNull(root.Target) || texts.Values.Any(text => Unity.IsNull(text.Target)))
                CreateCanvas(snapshot);
            foreach (var text in snapshot.Texts)
            {
                if (!texts.TryGetValue(text.Name, out var reference))
                {
                    reference = CreateText(text);
                    texts.Add(text.Name, reference);
                }
                Unity.Call(reference.Target, "set_text", Il2Cpp.String(text.Text));
            }
            UnityProperties.Set(root!.Target, "SetActive", true);
        }
        catch
        {
            // No partial canvas survives a failed build. The next update can retry safely.
            Dispose();
            throw;
        }
    }

    void CreateCanvas(HudSnapshot snapshot)
    {
        Dispose();
        font = Unity.FindObjects(Unity.Class("TMPro.TMP_Text"))
            .Where(text => !Unity.IsNull(text))
            .Select(text => Unity.Call(text, "get_font"))
            .FirstOrDefault(candidate => !Unity.IsNull(candidate));
        if (font == 0) throw new InvalidOperationException("HUD is waiting for the game to load a font.");
        root = OwnedGameObject.Create("Jibbers.Hud." + snapshot.Name);
        var canvas = root.AddComponent("UnityEngine.Canvas");
        UnityProperties.Set(canvas, "set_renderMode", 0); // ScreenSpaceOverlay, independent of game UI fades.
        UnityProperties.Set(canvas, "set_sortingOrder", snapshot.Options.SortingOrder);
        var scaler = root.AddComponent("UnityEngine.UI.CanvasScaler");
        UnityProperties.Set(scaler, "set_uiScaleMode", 1);
        UnityProperties.Set(scaler, "set_referenceResolution", snapshot.Options.ReferenceResolution);
        UnityProperties.Set(scaler, "set_matchWidthOrHeight", snapshot.Options.MatchWidthOrHeight);
        Log.Info("HUD", $"{snapshot.Name}: independent screen overlay created");
    }

    GameRef CreateText(HudTextSnapshot snapshot)
    {
        using var child = OwnedGameObject.Create(snapshot.Name);
        var text = child.AddComponent("TMPro.TextMeshProUGUI");
        Unity.Call(text, "set_font", font);
        UnityProperties.Set(text, "set_fontSize", snapshot.Options.FontSize);
        UnityProperties.Set(text, "set_color", snapshot.Options.Color);
        UnityProperties.Set(text, "set_alpha", snapshot.Options.Color.A);
        UnityProperties.Set(text, "set_enabled", true);
        UnityProperties.Set(text, "set_raycastTarget", false);
        UnityProperties.Set(text, "set_richText", snapshot.Options.RichText);
        var alignment = Unity.Class("TMPro.TextAlignmentOptions").Field(snapshot.Options.Alignment.ToString()).Get<int>(0);
        UnityProperties.Set(text, "set_alignment", alignment);
        if (!Il2Cpp.ClassOf(text).Method("set_textWrappingMode", 1).IsNull)
            UnityProperties.Set(text, "set_textWrappingMode", snapshot.Options.Wrap ? 1 : 0);
        else
            UnityProperties.Set(text, "set_enableWordWrapping", snapshot.Options.Wrap);
        var rectangle = Unity.Call(text, "get_rectTransform");
        var anchor = HudValidation.Anchor(snapshot.Options.Anchor);
        UnityProperties.Set(rectangle, "set_anchorMin", anchor);
        UnityProperties.Set(rectangle, "set_anchorMax", anchor);
        UnityProperties.Set(rectangle, "set_pivot", anchor);
        UnityProperties.Set(rectangle, "set_anchoredPosition", snapshot.Options.Offset);
        UnityProperties.Set(rectangle, "set_sizeDelta", snapshot.Options.Size);
        UnityProperties.Set(rectangle, "set_localScale", new Vector3(1, 1, 1));
        // Transfer ownership only after all component setup succeeds.
        var reference = new GameRef(text);
        try { child.AttachTo(root!.Transform); return reference; }
        catch { reference.Dispose(); throw; }
    }

    public void Hide()
    {
        if (root is not null && !Unity.IsNull(root.Target)) UnityProperties.Set(root.Target, "SetActive", false);
    }

    public void Dispose()
    {
        var ownedRoot = root;
        var ownedTexts = texts.Values.ToArray();
        root = null;
        font = 0;
        texts.Clear();
        Cleanup.Run(new Action[] { () => ownedRoot?.Dispose() }.Concat(ownedTexts.Select(text => (Action)text.Dispose)).ToArray());
    }
}
