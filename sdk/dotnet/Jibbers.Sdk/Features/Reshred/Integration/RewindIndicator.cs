using System.Runtime.InteropServices;
using Jibbers.Sdk.Game.Physics;
using Jibbers.Sdk.Game.Replay;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Jibbers.Sdk.Features.Rewind;

/// <summary>Owns the rewind label and its Unity object lifetime. Rendering failure never blocks rewind.</summary>
internal sealed unsafe class RewindIndicator(Action<string> log) : IRewindIndicator
{
    GameRef? labelReference;
    bool reportedMissingLabel;
    nint FindOrCreateLabel()
    {
        if (labelReference?.Target is { } existingTransform && !Unity.IsNull(existingTransform))
        {
            return existingTransform;
        }

        labelReference?.Dispose();
        labelReference = null;
        var uiRoot = Unity.Find("UI");
        var canvas = Unity.Call(Unity.Transform(uiRoot), "Find", Il2Cpp.String("MainCanvas"));
        var template = canvas == 0 ? 0 : Unity.Call(canvas, "Find", Il2Cpp.String("Respawn"));
        if (Unity.IsNull(template))
        {
            throw new InvalidOperationException("UI/MainCanvas/Respawn not found");
        }

        var labelTransform = Unity.Transform(Unity.CallStatic(Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Object"), "Instantiate", Unity.Call(template, "get_gameObject"), canvas));
        try
        {
            Unity.Call(Unity.Call(labelTransform, "get_gameObject"), "set_name", Il2Cpp.String("Reshred_Label"));
            // Its ReactiveText would keep rewriting the text to the respawn state.
            var reactiveTextClass = Il2Cpp.Class("Assembly-CSharp.dll", "", "ReactiveText");
            if (!reactiveTextClass.IsNull && Unity.GetComponent(labelTransform, reactiveTextClass)is var reactive && reactive != 0)
            {
                Unity.CallStatic(Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Object"), "DestroyImmediate", reactive);
            }

            // The template is parked above the screen until the game moves it in: give the clone its own place.
            var anchor = new Vector2(0.5f, 0.18f);
            Unity.Call(labelTransform, "set_anchorMin", (nint)(&anchor));
            Unity.Call(labelTransform, "set_anchorMax", (nint)(&anchor));
            var pivot = new Vector2(0.5f, 0.5f);
            Unity.Call(labelTransform, "set_pivot", (nint)(&pivot));
            var position = new Vector3(0, 0, 0);
            Unity.Call(labelTransform, "set_anchoredPosition3D", (nint)(&position));
            var dimensions = new Vector2(720, 72);
            Unity.Call(labelTransform, "set_sizeDelta", (nint)(&dimensions));
            var scale = new Vector3(1, 1, 1);
            Unity.Call(labelTransform, "set_localScale", (nint)(&scale));
            var textComponent = Unity.GetComponent(labelTransform, Il2Cpp.RequireClass("Unity.TextMeshPro.dll", "TMPro.TMP_Text"));
            var white = new Color(1, 1, 1, 1);
            Unity.Call(textComponent, "set_color", (nint)(&white));
            // Respawn can be cloned while its text component is hidden or disabled.
            // Activating the GameObject alone does not restore either text property.
            var opacity = 1f;
            Unity.Call(textComponent, "set_alpha", (nint)(&opacity));
            var textEnabled = true;
            Unity.Call(textComponent, "set_enabled", (nint)(&textEnabled));
            var fontSize = 30f;
            Unity.Call(textComponent, "set_fontSize", (nint)(&fontSize));
            var center = 514; // TextAlignmentOptions.Center
            Unity.Call(textComponent, "set_alignment", (nint)(&center));
            var raycastEnabled = false;
            Unity.Call(textComponent, "set_raycastTarget", (nint)(&raycastEnabled));
            Unity.Call(labelTransform, "SetAsLastSibling");
        }
        catch
        {
            Unity.Destroy(Unity.Call(labelTransform, "get_gameObject"));
            throw;
        }

        labelReference = new GameRef(labelTransform);
        return labelTransform;
    }

    public void Show(string text)
    {
        try
        {
            var labelTransform = FindOrCreateLabel();
            var textComponent = Unity.GetComponent(labelTransform, Il2Cpp.RequireClass("Unity.TextMeshPro.dll", "TMPro.TMP_Text"));
            Unity.Call(textComponent, "set_text", Il2Cpp.String(text));
            var visible = true;
            Unity.Call(Unity.Call(labelTransform, "get_gameObject"), "SetActive", (nint)(&visible));
        }
        catch (Exception exception)
        {
            // The rewind itself does not depend on the label: say it once.
            if (!reportedMissingLabel)
            {
                reportedMissingLabel = true;
                log("no indicator: " + exception.Message);
            }
        }
    }

    public void Hide()
    {
        if (labelReference?.Target is { } labelTransform && !Unity.IsNull(labelTransform))
        {
            var visible = false;
            Unity.Call(Unity.Call(labelTransform, "get_gameObject"), "SetActive", (nint)(&visible));
        }
    }

    public void Dispose()
    {
        try
        {
            if (labelReference?.Target is { } transform && !Unity.IsNull(transform))
                Unity.Destroy(Unity.Call(transform, "get_gameObject"));
        }
        finally
        {
            labelReference?.Dispose();
            labelReference = null;
        }
    }
}
