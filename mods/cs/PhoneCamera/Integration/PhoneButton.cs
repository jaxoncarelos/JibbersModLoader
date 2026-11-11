using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using GameButton = Jibbers.Sdk.Game.Ui.Button;
using GameMenu = Jibbers.Sdk.Game.Ui.NavigatableMenu;

namespace PhoneCamera.Integration;

/// <summary>
/// The Camera button on the game's phone (its pause menu): a copy of the Replay button in
/// <c>UI/PhoneCanvas/PhoneMenu/Content/VList</c>, under other mods' phone buttons when there are some.
/// Pressing it puts the phone away (the game's own Resume button) and opens the camera. Each map
/// builds a new phone, so <see cref="Ensure"/> runs now and then. Main thread only.
/// </summary>
internal static unsafe class PhoneButton
{
    const string List = "PhoneCanvas/PhoneMenu/Content/VList";
    // Not "CameraButton": the game's phone has a hidden button of that name.
    const string Name = "JibbersCameraButton";
    static Action? onPress;

    static nint UiRoot()
    {
        var scene = Unity.CallStatic(Unity.Class("UnityEngine.SceneManagement.SceneManager"), "GetActiveScene");
        var sceneClass = Unity.Class("UnityEngine.SceneManagement.Scene");
        var roots = sceneClass.Method("GetRootGameObjects", 0).Invoke((nint)Il2CppApi.object_unbox(scene));
        foreach (var go in Il2Cpp.ArrayObjects(roots))
            if (!Unity.IsNull(go) && Unity.Name(go) == "UI")
                return Unity.Transform(go);
        return 0;
    }

    static nint Find(nint transform, string path) => transform == 0 ? 0 : Unity.Call(transform, "Find", Il2Cpp.String(path));

    static nint Component(nint transform, Il2CppClass k)
    {
        var all = Unity.Call(Unity.Call(transform, "get_gameObject"), "GetComponents", Unity.Class("UnityEngine.Component").SystemType);
        foreach (var c in Il2Cpp.ArrayObjects(all))
            if (c != 0 && k.IsAssignableFrom(Il2Cpp.ClassOf(c)))
                return c;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void Clicked(nint ctx, nint button, nint method)
    {
        try
        {
            Resume();
            onPress?.Invoke();
        }
        catch (Exception e)
        {
            Log.Info("phone_camera", $"the Camera button failed: {e.Message}");
        }
    }

    /// <summary>Presses the phone's Resume button (the game's own way of putting the phone away).</summary>
    public static void Resume()
    {
        var resume = Find(Find(UiRoot(), List), "ResumeButton");
        if (resume == 0) throw new InvalidOperationException("the phone's Resume button wasn't found");
        var button = Component(resume, GameButton.Class.Class);
        var click = button == 0 ? 0 : new GameButton(button).OnClick;
        if (click == 0) throw new InvalidOperationException("Resume has no action");
        Jibbers.Sdk.Delegates.Invoke(click, button);
    }

    /// <summary>Adds the button if this scene's phone lacks it. Says what happened (for the log).</summary>
    public static string Ensure(Action pressed)
    {
        onPress = pressed;
        var ui = UiRoot();
        if (ui == 0) return "no UI root";
        var list = Find(ui, List);
        if (list == 0) return "the phone's menu isn't built yet";
        if (Find(list, Name) != 0) return "the phone has its Camera button";
        var replay = Find(list, "ReplayButton");
        if (replay == 0) return "the phone's Replay button wasn't found";
        var after = new[] { "SpotifyButton", "BrowserButton", "InstagramButton" }.Select(n => Find(list, n)).FirstOrDefault(t => t != 0);
        if (after == 0) after = replay;

        // A copy of Replay in the same list, right under `after`.
        var objectClass = Unity.Class("UnityEngine.Object");
        var go = objectClass.Method("Instantiate", "UnityEngine.Object", "UnityEngine.Transform").Invoke(0, Unity.Call(replay, "get_gameObject"), list);
        Unity.Call(go, "set_name", Il2Cpp.String(Name));
        var tr = Unity.Transform(go);
        var index = Unity.Call<int>(after, "GetSiblingIndex") + 1;
        Unity.Call(tr, "SetSiblingIndex", (nint)(&index));

        var label = Find(tr, "LabelText");
        if (label != 0 && Component(label, Unity.Class("TMPro.TMP_Text")) is var tmp and not 0)
            Unity.Call(tmp, "set_text", Il2Cpp.String("Camera"));
        var buttonPtr = Component(tr, GameButton.Class.Class);
        if (buttonPtr == 0) throw new InvalidOperationException("the copied button has no Button");
        var button = new GameButton(buttonPtr);
        delegate* unmanaged[Cdecl]<nint, nint, nint, void> fn = &Clicked;
        button.OnClick = Jibbers.Sdk.Delegates.Native(ClickActionClass(buttonPtr), (nint)fn, 0);

        // Lay the list out again and give the button to the cursor and the phone's mouse targets.
        var menuPtr = Component(list, GameMenu.Class.Class);
        if (menuPtr != 0)
        {
            var menu = new GameMenu(menuPtr);
            menu.Layout();
            menu.Collect();
            if (!RowItems(menu.Rows).Contains(buttonPtr))
                menu.AddButton(new Jibbers.Sdk.Game.Ui.Behaviour(buttonPtr));
        }
        RefreshPhoneTargets(ui);
        return "Camera button added to the phone";
    }

    /// <summary>The class of the button's click action (an Action of Button).</summary>
    static Il2CppClass ClickActionClass(nint button)
    {
        for (var k = Il2Cpp.ClassOf(button); !k.IsNull; k = k.Parent)
            foreach (var f in k.Fields)
                if (!f.IsStatic && f.TypeName.StartsWith("System.Action<", StringComparison.Ordinal))
                    return f.TypeClass;
        throw new InvalidOperationException("the Button has no click action");
    }

    static List<nint> ListItems(nint list)
    {
        var k = Il2Cpp.ClassOf(list);
        var size = k.Field("_size").Get<int>(list);
        return Il2Cpp.ArrayObjects(k.Field("_items").GetObject(list)).Take(Math.Max(size, 0)).ToList();
    }

    /// <summary>The rows the cursor walks (List of Lists of widgets).</summary>
    static List<nint> RowItems(nint rows) => rows == 0 ? new() : ListItems(rows).Where(r => r != 0).SelectMany(ListItems).ToList();

    /// <summary>The phone raycasts against its own cached widget array: a new button must be in it to get mouse clicks.</summary>
    static void RefreshPhoneTargets(nint ui)
    {
        var phone = Find(ui, "PhoneCanvas");
        if (phone == 0) return;
        var all = Unity.Call(Unity.Call(phone, "get_gameObject"), "GetComponents", Unity.Class("UnityEngine.Component").SystemType);
        foreach (var c in Il2Cpp.ArrayObjects(all))
        {
            if (c == 0) continue;
            for (var k = Il2Cpp.ClassOf(c); !k.IsNull; k = k.Parent)
                foreach (var f in k.Fields)
                {
                    if (f.IsStatic || f.TypeName != "UIBehaviour[]") continue;
                    var widgetClass = f.TypeClass.ElementClass;
                    var widgets = Unity.GetComponentsInChildren(phone, widgetClass, includeInactive: true);
                    var array = Il2CppApi.array_new(widgetClass.Ptr, (nuint)widgets.Length);
                    for (var i = 0; i < widgets.Length; i++)
                        ((nint*)Il2Cpp.ArrayData(array))[i] = widgets[i];
                    f.SetObject(c, array);
                    return;
                }
        }
    }
}
