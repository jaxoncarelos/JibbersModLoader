using Jibbers.Sdk;
using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Fpv;

/// <summary>The composition root for Unity camera sessions and the SDK-owned camera update hook.</summary>
internal sealed class UnityDroneGameLoop(Action<string> log) : IDroneGameLoop, IDroneSceneSource
{
    Follow currentCamera;
    bool active;
    public DroneFrameContext CurrentFrame { get; private set; }

    public void Start(Action frame)
    {
        using var symbols = typeof(UnityDroneGameLoop).Assembly.GetManifestResourceStream("Fpv.symbols.json") ?? throw new InvalidOperationException("Embedded FPV symbols are missing.");
        using var reader = new StreamReader(symbols);
        SymbolTable.Register(reader.ReadToEnd());
        Follow.LateUpdateMethod.Before((instance, _) =>
        {
            if (!active)
                return;
            currentCamera = new Follow(instance);
            if (currentCamera.IsNull)
                return;
            var identity = Unity.Call<int>(currentCamera.Ptr, "GetInstanceID");
            var elapsed = Il2Cpp.Unbox<float>(Unity.CallStatic(Unity.Class("UnityEngine.Time"), "get_unscaledDeltaTime"));
            CurrentFrame = new(identity, Unity.DeltaTime, float.IsFinite(elapsed) ? Math.Clamp(elapsed, 0, 0.1f) : 0, Input.GameFocused && !Input.Typing);
            frame();
        });
        active = true;
    }

    public DroneSessionServices OpenSession()
    {
        using var ownership = new ResourceScope();
        var camera = ownership.Own(new UnityDroneCamera(currentCamera));
        var targets = ownership.Own(new UnityDroneTargets(currentCamera));
        var presentation = ownership.Own(new DronePresentation(log));
        var collisions = new UnityCollisionWorld(new FieldRef("camera.follow.ground_layer").Get<int>(currentCamera.Ptr));
        var services = new DroneSessionServices(camera, targets, new DroneInputReader(log), collisions, presentation);
        ownership.TransferOwnership();
        return services;
    }

    public void Dispose() => active = false;
}
