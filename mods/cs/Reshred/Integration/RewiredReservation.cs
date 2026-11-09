using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Reshred;

/// <summary>Owns reversible gameplay-binding masks; discovery and native invocation have separate owners.</summary>
internal sealed class RewiredReservation
{
    readonly BindingRefreshPolicy refresh = new();
    bool topologyAvailable = true;
    int connectedJoystickCount, coveredControllerCount;
    bool controllersConnected;
    readonly Func<object> topologyProbe;
    public bool HasControllerCoverage => topologyAvailable && controllersConnected && connectedJoystickCount > 0 && coveredControllerCount >= connectedJoystickCount;
    readonly record struct Topology(nint Players, nint Mapping, int Controllers, bool Paused, bool Focused, bool CursorVisible, int CursorLock);
    object ProbeTopology()
    {
        var count = 0;
        if (topologyAvailable)
        {
            try { connectedJoystickCount = count = api.Int(api.ReInput("get_controllers"), "get_joystickCount"); }
            catch { topologyAvailable = false; }
        }
        if (!topologyAvailable) count = unchecked((int)(Environment.TickCount64 / 10000));
        var cursor = Unity.Class("UnityEngine.Cursor");
        return new Topology(api.ReInput("get_players"), api.ReInput("get_mapping"), count,
            Jibbers.Sdk.Game.Physics.Scene.IsPaused, Input.GameFocused,
            Il2Cpp.Unbox<bool>(Unity.CallStatic(cursor, "get_visible")), Il2Cpp.Unbox<int>(Unity.CallStatic(cursor, "get_lockState")));
    }
    sealed record Mask(GameRef Element, bool Original)
    {
        public bool Active = true;
    }

    readonly RewiredApi api;
    readonly RewiredBindingInventory inventory;
    readonly List<Mask> masks = new();
    List<RewiredButton> controllers = new();
    (int Key, bool Controller)? configurationKey;
    string? scanError;
    public string? LastError { get; private set; }

    public RewiredReservation()
    {
        api = new RewiredApi();
        inventory = new RewiredBindingInventory(api);
        topologyProbe = ProbeTopology;
    }

    void RestoreMasks()
    {
        var failures = new List<Mask>();
        string? error = null;
        foreach (var mask in masks)
        {
            try
            {
                api.SetEnabled(mask.Element.Target, mask.Original);
                mask.Element.Dispose();
            }
            catch (Exception exception)
            {
                error = exception.Message;
                failures.Add(mask);
            }
        }

        masks.Clear();
        masks.AddRange(failures);
        foreach (var pad in controllers)
            pad.Dispose();
        controllers.Clear();
        coveredControllerCount = 0;
        controllersConnected = false;
        refresh.ForgetControllers();
        scanError = null;
        if (error is not null)
        {
            throw new InvalidOperationException("Rewired restoration pending: " + error);
        }
    }

    void DoRescan((int Key, bool Controller) configuration)
    {
        using var inventorySnapshot = inventory.Scan(configuration.Controller);
        var bindings = inventorySnapshot.Bindings;
        var errors = inventorySnapshot.ControllerErrors;
        var key = Input.UnityKeyCode(configuration.Key);
        var selected = bindings.Where(binding => (binding.Kind == 0 && key == binding.Key && binding.Modifiers == 0) || (configuration.Controller && binding.Kind == 2 && binding.West)).ToList();
        var desired = selected.Select(binding => binding.Element.Target).ToHashSet();
        // Maps can be removed or rebound without a config change: restore obsolete masks.
        string? restoreError = null;
        foreach (var mask in masks.ToList())
        {
            mask.Active = desired.Contains(mask.Element.Target);
            if (!mask.Active)
            {
                try
                {
                    api.SetEnabled(mask.Element.Target, mask.Original);
                    masks.Remove(mask);
                    mask.Element.Dispose();
                }
                catch (Exception exception)
                {
                    restoreError = exception.Message;
                }
            }
        }

        var seen = masks.Select(mask => mask.Element.Target).ToHashSet();
        foreach (var binding in selected)
        {
            var elementAddress = binding.Element.Target;
            if (!seen.Add(elementAddress))
            {
                continue;
            }

            // Save before changing, including bindings that were already disabled.
            masks.Add(new Mask(new GameRef(elementAddress), api.Bool(elementAddress, "get_enabled")));
            api.SetEnabled(elementAddress, false);
        }

        foreach (var controller in controllers)
        {
            controller.Button.Dispose();
            controller.Controller.Dispose();
        }

        controllers = configuration.Controller ? inventorySnapshot.TakeControllers() : new List<RewiredButton>();
        coveredControllerCount = controllers.Select(button => button.Controller.Target).Distinct().Count();
        if (restoreError is not null)
        {
            throw new InvalidOperationException("Rewired obsolete map restoration pending: " + restoreError);
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Rewired controller inventory incomplete: " + string.Join("; ", errors));
        }
    }

    /// <summary>Maintains the masks for the current settings; call every frame before reading input.</summary>
    public void Update(bool enabled, int key, bool controller)
    {
        try
        {
            if (!enabled && configurationKey is null && masks.Count == 0 && controllers.Count == 0)
            { LastError = null; return; }
            if (!enabled || configurationKey != (key, controller))
            {
                configurationKey = null;
                RestoreMasks();
                if (!enabled)
                {
                    LastError = null;
                    return;
                }

                configurationKey = (key, controller);
            }

            if (refresh.ShouldRefresh(Environment.TickCount64, topologyProbe))
            {
                try
                {
                    DoRescan((key, controller));
                    scanError = null;
                }
                catch (Exception exception)
                {
                    scanError = exception.Message;
                    refresh.RetryAfter(Environment.TickCount64);
                }
            }

            // A game context change may re-enable an element: reassert cheaply.
            foreach (var mask in masks)
            {
                if (!mask.Active) continue;
                if (api.Bool(mask.Element.Target, "get_enabled"))
                {
                    api.SetEnabled(mask.Element.Target, false);
                }
            }

            LastError = scanError;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
    }

    /// <summary>Restores every binding. Call on disable and before unload.</summary>
    public void Restore()
    {
        configurationKey = null;
        try
        {
            RestoreMasks();
            LastError = null;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
    }

    /// <summary>Held state of the raw west buttons (reading the raw value bypasses the masked bindings).</summary>
    public bool PadDown()
    {
        controllersConnected = false;
        if (configurationKey is not { Controller: true })
        {
            return false;
        }

        foreach (var controller in controllers)
        {
            try
            {
                var connected = api.Bool(controller.Controller.Target, "get_isConnected");
                refresh.ObserveController(controller.Controller.Target, connected);
                if (!connected) continue;
                controllersConnected = true;
                if (api.Bool(controller.Button.Target, "get_value"))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                refresh.ObserveController(controller.Controller.Target, false);
            }
        }

        return false;
    }

    public int MaskedCount { get { var count = 0; foreach (var mask in masks) if (mask.Active) count++; return count; } }
    public int PadCount => controllers.Count;
}
