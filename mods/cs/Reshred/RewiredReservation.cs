// Reversible Rewired gameplay-binding masks and raw west-face-button polling. Port of the Rust
// reshred_input.rs. MAIN THREAD ONLY, including Restore before unload. No binding is deleted or
// reassigned; unknown categories and controllers fail closed.

using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Reshred;

internal sealed unsafe class RewiredReservation
{
    static readonly TimeSpan Rescan = TimeSpan.FromMilliseconds(750);
    const int MaxItems = 16384;

    sealed record Mask(GameRef Element, bool Original)
    {
        public bool Active = true;
    }

    sealed record Pad(GameRef Controller, GameRef Button);

    sealed record Binding(GameRef Element, int Kind, int Key, int Modifiers, bool West);

    (int Key, bool Controller)? config;
    readonly List<Mask> masks = new();
    List<Pad> pads = new();
    DateTime? scanned;
    string? scanError;
    public string? LastError { get; private set; }
    readonly Dictionary<(nint, string, int, string), Il2CppMethod> methods = new();

    /// <summary>Exact name first, explicit-interface suffix second, inherited methods included.</summary>
    Il2CppMethod Resolve(Il2CppClass k, string name, int argc, string ret)
    {
        var key = (k.Ptr, name, argc, ret);
        if (methods.TryGetValue(key, out var hit))
        {
            return hit;
        }
        Il2CppMethod? explicitImpl = null;
        for (var c = k; !c.IsNull; c = c.Parent)
        {
            foreach (var m in c.Methods)
            {
                if (m.ParameterCount != argc)
                {
                    continue;
                }
                var actual = m.Name;
                if (actual != name && !actual.EndsWith("." + name))
                {
                    continue;
                }
                var result = m.ReturnType;
                if ((ret.Length > 0 && result != ret) || result.Contains("<T>"))
                {
                    continue;
                }
                if (actual == name)
                {
                    return methods[key] = m;
                }
                explicitImpl ??= m;
            }
        }
        return explicitImpl is { } e ? methods[key] = e : throw new InvalidOperationException($"Rewired method {name}/{argc} -> {ret} not found");
    }

    nint Call(nint obj, string name, string ret = "", params nint[] args)
    {
        if (obj == 0)
        {
            throw new InvalidOperationException($"Rewired {name} on null");
        }
        return Resolve(Il2Cpp.ClassOf(obj), name, args.Length, ret).Invoke(obj, args);
    }

    /// <summary>A bool result, checked: never construct a bool from unchecked bytes.</summary>
    static bool AsBool(nint boxed, string what)
    {
        var k = Il2Cpp.ClassOf(boxed);
        if (boxed == 0 || k.FullName != "System.Boolean")
        {
            throw new InvalidOperationException($"Rewired {what} returned an unexpected type");
        }
        var b = *(byte*)Il2CppApi.object_unbox(boxed);
        return b switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidOperationException("Rewired returned an invalid Boolean"),
        };
    }

    /// <summary>An int (or int-backed enum) result, checked.</summary>
    static int AsInt(nint boxed, string what)
    {
        var k = Il2Cpp.ClassOf(boxed);
        var ok = boxed != 0 && k.IsValueType && k.ValueSize == 4
            && (k.FullName == "System.Int32" || (k.IsEnum && k.Field("value__").TypeName == "System.Int32"));
        return ok ? *(int*)Il2CppApi.object_unbox(boxed) : throw new InvalidOperationException($"Rewired {what} returned an unexpected scalar type");
    }

    bool Bool(nint obj, string name) => AsBool(Call(obj, name), name);
    int Int(nint obj, string name) => AsInt(Call(obj, name), name);
    string Text(nint obj, string name) => Il2Cpp.ReadString(Call(obj, name, "System.String"));

    /// <summary>An instance object-reference field, validated before reading.</summary>
    static nint ReferenceField(nint obj, string name)
    {
        var f = Il2Cpp.ClassOf(obj).Field(name);
        if (f.IsNull || f.IsStatic || f.TypeClass.IsValueType)
        {
            throw new InvalidOperationException($"Rewired field {name} is not an instance object reference");
        }
        return f.GetObject(obj);
    }

    static nint ReInput(string name) => Unity.CallStatic(Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ReInput"), name);

    /// <summary>Walks a managed IEnumerable; never assumes a List or array layout. Every item is rooted.</summary>
    List<GameRef> Items(nint collection)
    {
        using var coll = new GameRef(collection);
        var k = Il2Cpp.ClassOf(collection);
        Il2CppMethod get;
        try
        {
            get = Resolve(k, "GetEnumerator", 0, "System.Collections.IEnumerator");
        }
        catch (InvalidOperationException)
        {
            get = Resolve(k, "GetEnumerator", 0, "");
        }
        var enumerator = get.Invoke(coll.Target);
        using var en = new GameRef(enumerator);
        var ek = Il2Cpp.ClassOf(enumerator);
        var self = ek.IsValueType ? (nint)Il2CppApi.object_unbox(enumerator) : enumerator;
        var next = Resolve(ek, "MoveNext", 0, "System.Boolean");
        Il2CppMethod current;
        try
        {
            current = Resolve(ek, "get_Current", 0, "System.Object");
        }
        catch (InvalidOperationException)
        {
            current = Resolve(ek, "get_Current", 0, "");
        }
        var list = new List<GameRef>();
        try
        {
            while (AsBool(next.Invoke(self), "MoveNext"))
            {
                if (list.Count >= MaxItems)
                {
                    throw new InvalidOperationException("Rewired enumeration limit exceeded");
                }
                var item = current.Invoke(self);
                if (item != 0)
                {
                    list.Add(new GameRef(item));
                }
            }
        }
        finally
        {
            try
            {
                Resolve(ek, "Dispose", 0, "System.Void").Invoke(self);
            }
            catch (Exception)
            {
                // optional
            }
        }
        return list;
    }

    List<Pad> DiscoverPads(nint controller)
    {
        string Safe(string m)
        {
            try
            {
                return Text(controller, m);
            }
            catch (Exception)
            {
                return "";
            }
        }
        var hardware = $"{Safe("get_name")} {Safe("get_hardwareName")} {Safe("get_hardwareIdentifier")}";
        var found = new List<Pad>();
        foreach (var button in Items(Call(controller, "get_Buttons")))
        {
            var identifier = Call(button.Target, "get_elementIdentifier");
            if (RewiredPolicy.WestName(Text(identifier, "get_name"), hardware))
            {
                found.Add(new Pad(new GameRef(controller), button));
            }
            else
            {
                button.Dispose();
            }
        }
        return found;
    }

    (List<Binding> Bindings, List<Pad> Pads, List<string> ControllerErrors) Inventory(bool controllers)
    {
        var players = ReInput("get_players");
        var mapping = ReInput("get_mapping");
        var categories = new Dictionary<int, string>();
        foreach (var c in Items(Call(mapping, "get_MapCategories")))
        {
            categories[Int(c.Target, "get_id")] = Text(c.Target, "get_name");
            c.Dispose();
        }
        var bindings = new List<Binding>();
        var foundPads = new List<Pad>();
        var errors = new List<string>();
        var seenControllers = new HashSet<nint>();
        var seenMaps = new HashSet<nint>();
        foreach (var player in Items(Call(players, "get_Players")))
        {
            var pc = ReferenceField(player.Target, "controllers");
            var maps = Call(ReferenceField(pc, "maps"), "GetAllMaps", "System.Collections.Generic.IEnumerable<Rewired.ControllerMap>");
            foreach (var mapRef in Items(maps))
            {
                var map = mapRef.Target;
                if (!seenMaps.Add(map))
                {
                    continue;
                }
                var kind = Int(map, "get_controllerType");
                if (kind != 0 && !(kind == 2 && controllers))
                {
                    continue;
                }
                try
                {
                    var id = Int(map, "get_categoryId");
                    if (!categories.TryGetValue(id, out var category))
                    {
                        var m = Il2Cpp.ClassOf(mapping).Method("GetMapCategory", "System.Int32");
                        var idArg = id;
                        category = Text(m.Invoke(mapping, (nint)(&idArg)), "get_name");
                    }
                    var gameplay = RewiredPolicy.GameplayCategory(category);
                    var controller = kind == 2 ? Call(map, "get_controller") : 0;
                    if (controller != 0 && seenControllers.Add(controller))
                    {
                        foundPads.AddRange(DiscoverPads(controller));
                    }
                    if (!gameplay)
                    {
                        continue;
                    }
                    var westIds = new HashSet<int>();
                    foreach (var pad in foundPads.Where(p => p.Controller.Target == controller))
                    {
                        westIds.Add(Int(Call(pad.Button.Target, "get_elementIdentifier"), "get_id"));
                    }
                    foreach (var element in Items(Call(map, "get_AllMaps")))
                    {
                        var e = element.Target;
                        var elementId = kind == 2 ? Int(e, "get_elementIdentifierId") : 0;
                        var key = kind == 0 ? Int(e, "get_keyboardKeyCode") : 0;
                        var mods = kind == 0 ? Int(e, "get_modifierKeyFlags") : 0;
                        bindings.Add(new Binding(element, kind, key, mods, kind == 2 && westIds.Contains(elementId)));
                    }
                }
                catch (Exception e) when (kind == 2)
                {
                    // A joystick API mismatch must not discard keyboard bindings already found.
                    errors.Add(e.Message);
                }
            }
        }
        return (bindings, foundPads, errors);
    }

    void SetEnabled(nint obj, bool enabled)
    {
        var m = Resolve(Il2Cpp.ClassOf(obj), "set_enabled", 1, "System.Void");
        if (m.ParameterTypes[0] != "System.Boolean")
        {
            throw new InvalidOperationException("Rewired set_enabled parameter is not Boolean");
        }
        var v = enabled;
        m.Invoke(obj, (nint)(&v));
    }

    void RestoreMasks()
    {
        var failures = new List<Mask>();
        string? error = null;
        foreach (var mask in masks)
        {
            try
            {
                SetEnabled(mask.Element.Target, mask.Original);
                mask.Element.Dispose();
            }
            catch (Exception e)
            {
                error = e.Message;
                failures.Add(mask);
            }
        }
        masks.Clear();
        masks.AddRange(failures);
        pads.Clear();
        scanned = null;
        scanError = null;
        if (error is not null)
        {
            throw new InvalidOperationException("Rewired restoration pending: " + error);
        }
    }

    void DoRescan((int Key, bool Controller) cfg)
    {
        var (bindings, foundPads, errors) = Inventory(cfg.Controller);
        var key = RewiredPolicy.KeyboardCode(cfg.Key);
        var selected = bindings.Where(b => (b.Kind == 0 && key == b.Key && b.Modifiers == 0) || (cfg.Controller && b.Kind == 2 && b.West)).ToList();
        var desired = selected.Select(b => b.Element.Target).ToHashSet();
        // Maps can be removed or rebound without a config change: restore obsolete masks.
        string? restoreError = null;
        foreach (var mask in masks.ToList())
        {
            mask.Active = desired.Contains(mask.Element.Target);
            if (!mask.Active)
            {
                try
                {
                    SetEnabled(mask.Element.Target, mask.Original);
                    masks.Remove(mask);
                    mask.Element.Dispose();
                }
                catch (Exception e)
                {
                    restoreError = e.Message;
                }
            }
        }
        var seen = masks.Select(m => m.Element.Target).ToHashSet();
        foreach (var b in selected)
        {
            var obj = b.Element.Target;
            if (!seen.Add(obj))
            {
                continue;
            }
            // Save before changing, including bindings that were already disabled.
            masks.Add(new Mask(new GameRef(obj), Bool(obj, "get_enabled")));
            SetEnabled(obj, false);
        }
        foreach (var b in bindings)
        {
            b.Element.Dispose();
        }
        foreach (var p in pads)
        {
            p.Button.Dispose();
            p.Controller.Dispose();
        }
        pads = cfg.Controller ? foundPads : new List<Pad>();
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
            if (!enabled || config != (key, controller))
            {
                config = null;
                RestoreMasks();
                if (!enabled)
                {
                    LastError = null;
                    return;
                }
                config = (key, controller);
            }
            if (scanned is not { } t || DateTime.UtcNow - t >= Rescan)
            {
                scanned = DateTime.UtcNow;
                try
                {
                    DoRescan((key, controller));
                    scanError = null;
                }
                catch (Exception e)
                {
                    scanError = e.Message;
                }
            }
            // A game context change may re-enable an element: reassert cheaply.
            foreach (var mask in masks.Where(m => m.Active))
            {
                if (Bool(mask.Element.Target, "get_enabled"))
                {
                    SetEnabled(mask.Element.Target, false);
                }
            }
            LastError = scanError;
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
    }

    /// <summary>Restores every binding. Call on disable and before unload.</summary>
    public void Restore()
    {
        config = null;
        try
        {
            RestoreMasks();
            LastError = null;
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
    }

    /// <summary>Held state of the raw west buttons (reading the raw value bypasses the masked bindings).</summary>
    public bool PadDown()
    {
        if (config is not { Controller: true })
        {
            return false;
        }
        foreach (var p in pads)
        {
            try
            {
                if (Bool(p.Controller.Target, "get_isConnected") && Bool(p.Button.Target, "get_value"))
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

    public int MaskedCount => masks.Count(m => m.Active);
    public int PadCount => pads.Count;
}
