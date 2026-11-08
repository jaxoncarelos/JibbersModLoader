using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace JoinSpawn;

/// <summary>
/// Désactive temporairement les commandes de jeu (catégorie « Ragdoll ») des manettes dans Rewired, pour que
/// naviguer dans le panneau à la manette ne fasse pas bouger le skieur. Tout est remis comme avant à la fermeture.
/// </summary>
internal sealed unsafe class RewiredMute
{
    readonly Dictionary<(nint, string, int, string), Il2CppMethod> methods = new();
    readonly List<(GameRef Map, IDisposable Lease)> muted = new();
    static readonly HankModPack.StateOverrides<nint, bool> overrides = new();

    public bool Active => muted.Count > 0;
    public string? LastError { get; private set; }

    Il2CppMethod Resolve(Il2CppClass k, string name, int argc, string returnType)
    {
        var key = (k.Ptr, name, argc, returnType);
        if (methods.TryGetValue(key, out var hit))
            return hit;
        Il2CppMethod? explicitImpl = null;
        for (var c = k; !c.IsNull; c = c.Parent)
            foreach (var m in c.Methods)
            {
                if (m.ParameterCount != argc)
                    continue;
                var actual = m.Name;
                if (actual != name && !actual.EndsWith("." + name))
                    continue;
                var result = m.ReturnType;
                if ((returnType.Length > 0 && result != returnType) || result.Contains("<T>"))
                    continue;
                if (actual == name)
                    return methods[key] = m;
                explicitImpl ??= m;
            }
        return explicitImpl is { } e ? methods[key] = e : throw new InvalidOperationException($"Rewired : méthode {name}/{argc} introuvable");
    }

    nint Call(nint instance, string name, string returnType = "", params nint[] args)
    {
        if (instance == 0)
            throw new InvalidOperationException($"Rewired : {name} sur null");
        return Resolve(Il2Cpp.ClassOf(instance), name, args.Length, returnType).Invoke(instance, args);
    }

    bool AsBool(nint boxed)
    {
        if (boxed == 0 || Il2Cpp.ClassOf(boxed).FullName != "System.Boolean")
            throw new InvalidOperationException("Rewired : booléen inattendu");
        return *(byte*)Il2CppApi.object_unbox(boxed) == 1;
    }

    int AsInt(nint boxed)
    {
        if (boxed == 0)
            throw new InvalidOperationException("Rewired : entier nul");
        var k = Il2Cpp.ClassOf(boxed);
        if (!k.IsValueType || k.ValueSize != 4)
            throw new InvalidOperationException("Rewired : entier inattendu");
        return *(int*)Il2CppApi.object_unbox(boxed);
    }

    string Text(nint instance, string name) => Il2Cpp.ReadString(Call(instance, name, "System.String"));

    nint Field(nint instance, string name)
    {
        var f = Il2Cpp.ClassOf(instance).Field(name);
        if (f.IsNull || f.IsStatic || f.TypeClass.IsValueType)
            throw new InvalidOperationException($"Rewired : champ {name} introuvable");
        return f.GetObject(instance);
    }

    nint ReInput(string name) => Unity.CallStatic(Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ReInput"), name);

    /// <summary>Parcourt un IEnumerable ; chaque élément est gardé vivant (GameRef).</summary>
    List<GameRef> Items(nint collection)
    {
        using var root = new GameRef(collection);
        var k = Il2Cpp.ClassOf(collection);
        Il2CppMethod getEnumerator;
        try { getEnumerator = Resolve(k, "GetEnumerator", 0, "System.Collections.IEnumerator"); }
        catch (InvalidOperationException) { getEnumerator = Resolve(k, "GetEnumerator", 0, ""); }
        var enumerator = getEnumerator.Invoke(root.Target);
        using var enumeratorRoot = new GameRef(enumerator);
        var ek = Il2Cpp.ClassOf(enumerator);
        var instance = ek.IsValueType ? (nint)Il2CppApi.object_unbox(enumerator) : enumerator;
        var moveNext = Resolve(ek, "MoveNext", 0, "System.Boolean");
        Il2CppMethod current;
        try { current = Resolve(ek, "get_Current", 0, "System.Object"); }
        catch (InvalidOperationException) { current = Resolve(ek, "get_Current", 0, ""); }
        var items = new List<GameRef>();
        try
        {
            while (AsBool(moveNext.Invoke(instance)))
            {
                if (items.Count >= 16384)
                    throw new InvalidOperationException("Rewired : trop d'éléments");
                var item = current.Invoke(instance);
                if (item != 0)
                    items.Add(new GameRef(item));
            }
        }
        catch
        {
            foreach (var i in items) i.Dispose();
            throw;
        }
        return items;
    }

    void SetEnabled(nint map, bool enabled)
    {
        var setter = Resolve(Il2Cpp.ClassOf(map), "set_enabled", 1, "System.Void");
        var value = enabled;
        setter.Invoke(map, (nint)(&value));
    }

    /// <summary>Tout est coupé (manette et clavier) sauf les menus (« UI ») : le jeu a aussi une caméra spectateur et un replay pilotés par ces touches.</summary>
    static bool Mutable(string category)
    {
        var n = new string(category.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return n != "ui";
    }

    /// <summary>Catégories coupées, pour le journal.</summary>
    public SortedSet<string> Categories { get; } = new();

    /// <summary>Coupe les commandes de jeu des manettes. Renvoie le nombre de maps coupées.</summary>
    /// <summary>Also mute the game's menu controls (the game's own panels stop reacting to the gamepad).</summary>
    public bool IncludeUi;

    public int Mute(bool replayControls = false)
    {
        if (Active)
            return muted.Count;
        Categories.Clear();
        try
        {
            var players = ReInput("get_players");
            var mapping = ReInput("get_mapping");
            var categories = new Dictionary<int, string>();
            foreach (var c in Items(Call(mapping, "get_MapCategories")))
            {
                categories[AsInt(Call(c.Target, "get_id"))] = Text(c.Target, "get_name");
                c.Dispose();
            }
            var seen = new HashSet<nint>();
            foreach (var player in Items(Call(players, "get_Players")))
            {
                var maps = Call(Field(Field(player.Target, "controllers"), "maps"), "GetAllMaps", "System.Collections.Generic.IEnumerable<Rewired.ControllerMap>");
                foreach (var map in Items(maps))
                {
                    var keep = false;
                    try
                    {
                        if (!seen.Add(map.Target))
                            continue;
                        var kind = AsInt(Call(map.Target, "get_controllerType"));
                        if (kind is not (0 or 2))
                            continue;
                        if (!categories.TryGetValue(AsInt(Call(map.Target, "get_categoryId")), out var category))
                            continue;
                        var isReplay = replayControls && new string(category.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray()) == "replay";
                        if (!isReplay && !Mutable(category) && !IncludeUi)
                            continue;
                        Categories.Add($"{category}/{(kind == 0 ? "clavier" : "manette")}");
                        var original = AsBool(Call(map.Target, "get_enabled"));
                        // Vue spectateur : les commandes de replay du jeu sont allumées, tout le reste reste coupé.
                        var pointer = map.Target;
                        var lease = overrides.Acquire(pointer, () => original, enabled => SetEnabled(pointer, enabled), isReplay);
                        muted.Add((map, lease));
                        keep = true;
                    }
                    finally
                    {
                        if (!keep)
                            map.Dispose();
                    }
                }
                player.Dispose();
            }
            LastError = null;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Restore(); // Partial setup must not strand a subset of the player's controls.
        }
        return muted.Count;
    }

    /// <summary>Remet les commandes de jeu comme elles étaient.</summary>
    public void Restore()
    {
        foreach (var (map, lease) in muted)
        {
            try { lease.Dispose(); }
            catch (Exception e) { LastError = e.Message; }
            map.Dispose();
        }
        muted.Clear();
    }
}
