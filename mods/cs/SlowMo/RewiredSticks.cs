using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace SlowMo;

/// <summary>
/// The stick clicks of every controller the game's input system (Rewired) knows. XInput sees only
/// Xbox-style controllers; a PlayStation or other controller used without Steam Input reaches the
/// game through Rewired alone, so its buttons are read here, by name.
/// </summary>
internal sealed class RewiredSticks : IDisposable
{
    sealed record Pad(string Name, GameRef Controller, GameRef? Left, GameRef? Right);

    readonly Action<string> log;
    readonly List<Pad> pads = new();
    readonly HashSet<string> reported = new();
    float rescanSeconds;
    bool failed;

    public RewiredSticks(Action<string> log) => this.log = log;

    /// <summary>Whether any controller's left or right stick is clicked. Main thread.</summary>
    public (bool Left, bool Right) Read(float seconds)
    {
        if (failed)
            return default;
        try
        {
            // Controllers come and go; look again every few seconds.
            if ((rescanSeconds -= seconds) <= 0)
            {
                rescanSeconds = 3;
                Scan();
            }
            var (left, right) = (false, false);
            foreach (var pad in pads)
            {
                if (Unity.IsNull(pad.Controller.Target) || !Unity.Call<bool>(pad.Controller.Target, "get_isConnected"))
                    continue;
                left |= Pressed(pad.Left);
                right |= Pressed(pad.Right);
            }
            return (left, right);
        }
        catch (Exception exception)
        {
            failed = true;
            log("Slow Motion: controllers through the game's input system are unavailable: " + exception.Message);
            Release();
            return default;
        }
    }

    static bool Pressed(GameRef? button) => button is not null && !Unity.IsNull(button.Target) && Unity.Call<bool>(button.Target, "get_value");

    void Scan()
    {
        Release();
        var reInput = Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ReInput");
        var ready = Unity.CallStatic(reInput, "get_isReady");
        if (ready == 0 || !Il2Cpp.Unbox<bool>(ready))
            return;
        var joysticks = Unity.Call(Unity.CallStatic(reInput, "get_controllers"), "get_Joysticks");
        foreach (var joystick in Items(joysticks))
        {
            var name = Unity.Call(joystick, "get_name") is var text && text != 0 ? Il2Cpp.ReadString(text) : "controller";
            nint left = 0, right = 0;
            var names = new List<string>();
            foreach (var button in Items(Unity.Call(joystick, "get_Buttons")))
            {
                var identifier = Unity.Call(button, "get_elementIdentifier");
                var buttonName = identifier == 0 ? "" : Il2Cpp.ReadString(Unity.Call(identifier, "get_name"));
                names.Add(buttonName);
                if (left == 0 && StickClicks.IsLeft(buttonName))
                    left = button;
                else if (right == 0 && StickClicks.IsRight(buttonName))
                    right = button;
            }
            pads.Add(new(name, new GameRef(joystick), left == 0 ? null : new GameRef(left), right == 0 ? null : new GameRef(right)));
            if (reported.Add(name))
                log(left != 0
                    ? $"Slow Motion: {name}: left stick click is \"{Il2Cpp.ReadString(Unity.Call(Unity.Call(left, "get_elementIdentifier"), "get_name"))}\"."
                    : $"Slow Motion: {name}: no left stick click found among its buttons: {string.Join(", ", names)}.");
        }
    }

    /// <summary>The items of a managed IList (count and indexer), so no enumerator layout is assumed.</summary>
    static unsafe List<nint> Items(nint list)
    {
        var items = new List<nint>();
        if (list == 0)
            return items;
        var count = Unity.Call<int>(list, "get_Count");
        for (var i = 0; i < Math.Min(count, 256); i++)
        {
            var index = i;
            var item = Unity.Call(list, "get_Item", (nint)(&index));
            if (item != 0)
                items.Add(item);
        }
        return items;
    }

    void Release()
    {
        var owned = pads.ToArray();
        pads.Clear();
        foreach (var pad in owned)
        {
            pad.Left?.Dispose();
            pad.Right?.Dispose();
            pad.Controller.Dispose();
        }
    }

    public void Dispose() => Release();
}

/// <summary>Which controller button names are stick clicks, across the names Rewired gives controllers. Unit tested.</summary>
internal static class StickClicks
{
    static string Normalized(string text) => new(text.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static bool IsLeft(string name) => Is(Normalized(name), "left", "l3", "ls");
    public static bool IsRight(string name) => Is(Normalized(name), "right", "r3", "rs");

    static bool Is(string name, string side, string playStation, string shortName) =>
        name == playStation || name == shortName + "b" || name == shortName + "click" || name == shortName + "button"
        || name == "button" + playStation
        || (name.StartsWith(side) && (name.Contains("stick") || name.Contains("thumb")) && (name.Contains("button") || name.Contains("click") || name.Contains("press")))
        || name == side + "stick" || name == side + "thumb";
}
