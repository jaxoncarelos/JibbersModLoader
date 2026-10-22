namespace Jibbers.Sdk.Game.Camera;

/// <summary>Typed camera options. Main-thread dispatch and game-setting notifications are automatic.</summary>
public static class CameraSettings
{
    /// <summary>The follow-camera distance in metres.</summary>
    public static float FollowDistance
    {
        get => MainThread.Run(() => GameVars.GetFloat("follow-cam-distance") ?? throw new InvalidOperationException("Follow-camera distance is unavailable."));
        set => MainThread.Run(() => GameVars.Set("follow-cam-distance", value));
    }
}
