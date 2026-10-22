using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Jibbers.Sdk.Features.Fpv;

/// <summary>Owns camera mode and lens restoration. Flight decisions never inspect Unity fields directly.</summary>
internal sealed unsafe class UnityDroneCamera : IDroneCamera
{
    static readonly FieldRef CameraMode = new("camera.follow.mode");
    static readonly FieldRef OrbitMode = new("fpv.cam_state.orbit");
    static readonly MethodRef ChangeMode = new("fpv.follow.set_mode");
    readonly GameRef followCamera;
    readonly GameRef camera;
    readonly GameRef transform;
    readonly int orbitMode;
    (float FieldOfView, float NearClip, int CameraMode)? original;
    bool disposed;
    Follow Follow => new(followCamera.Target);

    int CurrentMode => CameraMode.Get<int>(followCamera.Target);
    public bool InputBlocked => Follow.LockInput;
    public bool FlightModeActive => CurrentMode == orbitMode;
    public bool CanTakeOff => !InputBlocked && !Follow.PhotoMode && (CurrentMode == (int)CamState.Follow || FlightModeActive);

    public UnityDroneCamera(Follow follow)
    {
        orbitMode = OrbitMode.Get<int>(0);
        var cameraObject = Unity.GetComponent(follow.Ptr, Unity.Class("UnityEngine.Camera"));
        if (Unity.IsNull(cameraObject))
            throw new InvalidOperationException("FollowCam has no Camera.");
        followCamera = new GameRef(follow.Ptr);
        try
        {
            camera = new GameRef(cameraObject);
            try
            {
                transform = new GameRef(Unity.Transform(follow.Ptr));
            }
            catch
            {
                camera.Dispose();
                throw;
            }
        }
        catch
        {
            followCamera.Dispose();
            throw;
        }
    }

    public DronePose Pose
    {
        get
        {
            var position = Unity.Call<Vector3>(transform.Target, "get_position");
            var rotation = Unity.Call<Quaternion>(transform.Target, "get_rotation");
            return new(new(position.X, position.Y, position.Z), new DroneRotation(rotation.X, rotation.Y, rotation.Z, rotation.W).Normalized);
        }
    }

    void SetMode(int mode)
    {
        try
        {
            ChangeMode.Invoke(followCamera.Target, (nint)(&mode));
        }
        catch (SymbolException)
        {
        }

        if (CurrentMode != mode)
            CameraMode.Set(followCamera.Target, mode);
    }

    public void BeginFlight()
    {
        original = (Unity.Call<float>(camera.Target, "get_fieldOfView"), Unity.Call<float>(camera.Target, "get_nearClipPlane"), CurrentMode);
        if (!FlightModeActive)
            SetMode(orbitMode);
    }

    public void Apply(DronePose pose, float fieldOfView)
    {
        if (Unity.IsNull(transform.Target) || Unity.IsNull(camera.Target))
            throw new InvalidOperationException("Camera was destroyed.");
        var position = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
        var rotation = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
        Unity.Call(transform.Target, "SetPositionAndRotation", (nint)(&position), (nint)(&rotation));
        UnityProperties.Set(camera.Target, "set_fieldOfView", fieldOfView);
        UnityProperties.Set(camera.Target, "set_nearClipPlane", 0.05f);
    }

    public void Restore(bool restoreCameraMode)
    {
        if (original is not { } saved)
            return;
        try
        {
            if (!Unity.IsNull(camera.Target))
            {
                UnityProperties.Set(camera.Target, "set_fieldOfView", saved.FieldOfView);
                UnityProperties.Set(camera.Target, "set_nearClipPlane", saved.NearClip);
            }

            if (restoreCameraMode && !Unity.IsNull(followCamera.Target) && FlightModeActive && saved.CameraMode != orbitMode)
                SetMode(saved.CameraMode);
        }
        finally
        {
            original = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Cleanup.Run(() => Restore(true), camera.Dispose, transform.Dispose, followCamera.Dispose);
    }
}
