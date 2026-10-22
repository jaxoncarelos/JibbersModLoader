using Jibbers.Sdk;
using Jibbers.Sdk.Game.Camera;

namespace MyMod;

/// <summary>Press a rebindable key to apply your saved camera distance.</summary>
public sealed class MyMod : JibbersMod
{
    public override string Id => "my_mod";
    public override string Name => "My Mod";
    public override string Author => "you";
    public override string Description => "Apply a custom follow-camera distance.";

    [Slider("Camera distance", 3.5, 1, 10, Key = "distance", Section = "Camera")]
    public float CameraDistance { get; private set; }

    [KeyBinding("Apply distance", Key.F9, Key = "hotkey", Section = "Controls")]
    public Key ApplyKey { get; private set; }

    public override void OnLoad() => Input.OnPressed(() => ApplyKey, ApplyCameraDistance);

    void ApplyCameraDistance()
    {
        CameraSettings.FollowDistance = CameraDistance;
        Log($"Camera distance set to {CameraDistance:0.0}.");
    }
}
