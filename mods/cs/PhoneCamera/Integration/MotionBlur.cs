using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace PhoneCamera.Integration;

/// <summary>
/// Motion blur on the game's view around the phone while filming: a global URP Volume of our own
/// whose profile holds only MotionBlur. Post-processing runs on the game's camera; the phone's camera
/// is kept from post-processing meanwhile and the phone is drawn on top, so the phone's picture and
/// the clips stay sharp. Main thread only.
/// </summary>
internal sealed unsafe class MotionBlur : IDisposable
{
    const string CoreRp = "Unity.RenderPipelines.Core.Runtime";
    const string Urp = "Unity.RenderPipelines.Universal.Runtime";
    readonly Action<string> log;
    OwnedGameObject? holder;
    GameRef? volume, motion, phoneData;
    bool on, failed, phoneWas;

    public MotionBlur(Action<string> log) => this.log = log;

    static Il2CppClass Class(string image, string fullName) => Il2Cpp.RequireClass(image + ".dll", fullName);

    /// <summary>Sets <paramref name="name"/> whichever way this Unity version has it: a property, a field, or its m_ backing field.</summary>
    static void SetMember<T>(nint o, string name, T value) where T : unmanaged
    {
        var k = Il2Cpp.ClassOf(o);
        if (!k.Method("set_" + name, 1).IsNull)
        {
            UnityProperties.Set(o, "set_" + name, value);
            return;
        }
        for (var c = k; !c.IsNull; c = c.Parent)
            foreach (var n in new[] { name, "m_" + char.ToUpperInvariant(name[0]) + name[1..] })
                if (c.Field(n) is { IsNull: false } f)
                {
                    f.Set(o, value);
                    return;
                }
        throw new MissingMemberException(k.Name, name);
    }

    /// <summary>A volume parameter (<paramref name="field"/> of <paramref name="component"/>) set to <paramref name="value"/>, overriding.</summary>
    static void SetParam<T>(nint component, string field, T value) where T : unmanaged
    {
        nint p = 0;
        for (var c = Il2Cpp.ClassOf(component); !c.IsNull && p == 0; c = c.Parent)
            if (c.Field(field) is { IsNull: false } f) p = f.GetObject(component);
        if (p == 0) throw new MissingMemberException("MotionBlur", field);
        UnityProperties.Set(p, "set_overrideState", true);
        UnityProperties.Set(p, "set_value", value);
    }

    static nint CreateInstance(Il2CppClass k) =>
        Unity.Class("UnityEngine.ScriptableObject").Method("CreateInstance", "System.Type").Invoke(0, k.SystemType);

    void Build()
    {
        if (volume is not null && !Unity.IsNull(volume.Target) && motion is not null && !Unity.IsNull(motion.Target)) return;
        Dispose();
        holder = OwnedGameObject.Create("Mod_PhoneCamera_Blur");
        Unity.CallStatic(Unity.Class("UnityEngine.Object"), "DontDestroyOnLoad", holder.Target);
        var vol = Il2Cpp.ClassOf(holder.Target).Method("AddComponent", "System.Type").Invoke(holder.Target, Class(CoreRp, "UnityEngine.Rendering.Volume").SystemType);
        UnityProperties.Set(vol, "set_enabled", false);
        SetMember(vol, "isGlobal", true);
        SetMember(vol, "priority", 1000f);
        SetMember(vol, "weight", 1f);
        var profile = CreateInstance(Class(CoreRp, "UnityEngine.Rendering.VolumeProfile"));
        var blur = CreateInstance(Class(Urp, "UnityEngine.Rendering.Universal.MotionBlur"));
        if (profile == 0 || blur == 0) throw new InvalidOperationException("couldn't make the blur profile");
        try { SetMember(blur, "active", true); } catch (MissingMemberException) { }
        SetParam(blur, "quality", 2); // High
        var components = Il2Cpp.ClassOf(profile).Field("components").GetObject(profile);
        Unity.Call(components, "Add", blur);
        try { SetMember(profile, "isDirty", true); } catch (MissingMemberException) { }
        var shared = Il2Cpp.ClassOf(vol).Field("sharedProfile");
        if (!shared.IsNull) shared.SetObject(vol, profile);
        else Unity.Call(vol, "set_sharedProfile", profile);
        volume = new GameRef(vol);
        motion = new GameRef(blur);
        log("motion blur ready");
    }

    /// <summary>Keeps the phone's camera from post-processing (so the blur can't reach its picture), or puts it back.</summary>
    void PhoneSharp(nint phoneCamera, bool sharp)
    {
        if (!sharp)
        {
            if (phoneData is not null && !Unity.IsNull(phoneData.Target)) UnityProperties.Set(phoneData.Target, "set_renderPostProcessing", phoneWas);
            phoneData?.Dispose();
            phoneData = null;
            return;
        }
        if (phoneData is not null || phoneCamera == 0) return;
        var data = Unity.GetComponent(phoneCamera, Class(Urp, "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData"));
        if (data == 0 || Unity.IsNull(data)) return; // none: URP's default doesn't post-process
        phoneWas = Unity.Call<bool>(data, "get_renderPostProcessing");
        UnityProperties.Set(data, "set_renderPostProcessing", false);
        phoneData = new GameRef(data);
    }

    /// <summary>Blur on at <paramref name="strength"/> (0..1), or off.</summary>
    public void Set(bool want, float strength, nint phoneCamera)
    {
        if (!want)
        {
            if (on && volume is not null && !Unity.IsNull(volume.Target)) UnityProperties.Set(volume.Target, "set_enabled", false);
            if (on) PhoneSharp(phoneCamera, false);
            on = false;
            return;
        }
        if (failed) return;
        try
        {
            Build();
            // How much of the frame's movement smears, and how far a fast swing may (URP's clamp is up to 0.2).
            SetParam(motion!.Target, "intensity", Math.Clamp(strength, 0, 1));
            SetParam(motion.Target, "clamp", Math.Clamp(0.05f + 0.15f * strength, 0, 0.2f));
            if (!on)
            {
                PhoneSharp(phoneCamera, true);
                UnityProperties.Set(volume!.Target, "set_enabled", true);
                on = true;
            }
        }
        catch (Exception e) when (e is Il2CppException or InvalidOperationException or MissingMemberException)
        {
            failed = true;
            log($"couldn't add motion blur: {e.Message}");
        }
    }

    public void Dispose()
    {
        on = false;
        Cleanup.Run(() => PhoneSharp(0, false), () => holder?.Dispose(), () => volume?.Dispose(), () => motion?.Dispose());
        holder = null;
        volume = motion = null;
    }
}
