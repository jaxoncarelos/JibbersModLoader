using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Features.Rewind;

/// <summary>Owns reversible gameplay-binding masks; discovery and native invocation have separate owners.</summary>
internal sealed class RewiredReservation
{
    static readonly TimeSpan Rescan = TimeSpan.FromMilliseconds(750);
    sealed record Mask(GameRef Element, bool Original)
    {
        public bool Active = true;
    }

    readonly RewiredApi api;
    readonly RewiredBindingInventory inventory;
    readonly List<Mask> masks = new();
    List<RewiredButton> controllers = new();
    (int Key, bool Controller)? configurationKey;
    DateTime? lastScanTime;
    string? scanError;
    public string? LastError { get; private set; }

    public RewiredReservation()
    {
        api = new RewiredApi();
        inventory = new RewiredBindingInventory(api);
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
        lastScanTime = null;
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
        var key = RewiredPolicy.KeyboardCode(configuration.Key);
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

            if (lastScanTime is not { } scanTime || DateTime.UtcNow - scanTime >= Rescan)
            {
                lastScanTime = DateTime.UtcNow;
                try
                {
                    DoRescan((key, controller));
                    scanError = null;
                }
                catch (Exception exception)
                {
                    scanError = exception.Message;
                }
            }

            // A game context change may re-enable an element: reassert cheaply.
            foreach (var mask in masks.Where(mask => mask.Active))
            {
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
        if (configurationKey is not { Controller: true })
        {
            return false;
        }

        foreach (var controller in controllers)
        {
            try
            {
                if (api.Bool(controller.Controller.Target, "get_isConnected") && api.Bool(controller.Button.Target, "get_value"))
                {
                    return true;
                }
            }
            catch (Exception)
            {
            // a disconnected controller
            }
        }

        return false;
    }

    public int MaskedCount => masks.Count(mask => mask.Active);
    public int PadCount => controllers.Count;
}
