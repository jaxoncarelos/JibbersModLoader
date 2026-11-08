using Jibbers.Sdk;
using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Symbols;

namespace HankModPack;

internal static class CameraOwnership
{
    static readonly FieldRef mode = new("camera.follow.mode");
    public static bool InReplay { get; set; }

    public static bool CanControlPlayCamera(nint pointer)
    {
        if (pointer == 0 || InReplay || !Input.GameFocused || Input.Typing) return false;
        var follow = new Follow(pointer);
        return !follow.IsDead() && !follow.PhotoMode && !follow.LockInput
            && mode.Get<int>(pointer) == (int)CamState.Follow;
    }
}
