using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace HankModPack;

/// <summary>
/// Reads the game's own console: the list of its commands, the binds that exist, the key names it accepts, and runs a command (used for
/// "bind f6 detach-both"). Everything is found by shape, not by obfuscated name, so a game update does not break it: the command table is the
/// string-keyed dictionary of the Console class that holds the key "bind", the bind table is the one whose values are Console binds,
/// and the key names are the dictionary of strings to UnityEngine.KeyCode. Main thread only. What it finds goes to the log (prefix "binds:").
/// </summary>
static unsafe class GameConsole
{
    public readonly record struct Bind(string KeyName, string Text);

    static readonly Dictionary<(nint, string, int), Il2CppMethod> methods = new();
    static bool probed;
    static nint console;
    static Il2CppField commandsField, completionField, commandListField, bindsField, keysField;
    public static string Problem { get; private set; } = "";

    static Il2CppMethod Resolve(Il2CppClass k, string name, int argc)
    {
        var key = (k.Ptr, name, argc);
        if (methods.TryGetValue(key, out var hit)) return hit;
        for (var c = k; !c.IsNull; c = c.Parent)
            foreach (var m in c.Methods)
                if (m.ParameterCount == argc && (m.Name == name || m.Name.EndsWith("." + name)) && !m.ReturnType.Contains("<T>"))
                    return methods[key] = m;
        throw new InvalidOperationException($"console: method {name}/{argc} not found on {k.FullName}");
    }

    static nint Call(nint obj, string name, params nint[] args) => Resolve(Il2Cpp.ClassOf(obj), name, args.Length).Invoke(obj, args);

    static bool AsBool(nint boxed) => boxed != 0 && *(byte*)Il2CppApi.object_unbox(boxed) == 1;

    static bool IsDelegate(Il2CppClass k)
    {
        for (var c = k; !c.IsNull; c = c.Parent)
            if (c.FullName is "System.MulticastDelegate" or "System.Delegate") return true;
        return false;
    }

    /// <summary>The elements of an IEnumerable (each one kept alive while the walk lasts).</summary>
    static List<nint> Walk(nint collection, List<GameRef> keep)
    {
        var items = new List<nint>();
        if (collection == 0) return items;
        keep.Add(new GameRef(collection));
        var getEnumerator = Resolve(Il2Cpp.ClassOf(collection), "GetEnumerator", 0);
        var enumerator = getEnumerator.Invoke(collection);
        keep.Add(new GameRef(enumerator));
        var ek = Il2Cpp.ClassOf(enumerator);
        var self = ek.IsValueType ? (nint)Il2CppApi.object_unbox(enumerator) : enumerator;
        var moveNext = Resolve(ek, "MoveNext", 0);
        var current = Resolve(ek, "get_Current", 0);
        while (AsBool(moveNext.Invoke(self)))
        {
            if (items.Count >= 8192) break;
            var it = current.Invoke(self);
            if (it != 0) { items.Add(it); keep.Add(new GameRef(it)); }
        }
        return items;
    }

    static List<(string Key, nint Value)> Entries(nint dict, List<GameRef> keep)
    {
        var list = new List<(string, nint)>();
        if (dict == 0) return list;
        var keys = Call(dict, "get_Keys");
        foreach (var k in Walk(keys, keep))
        {
            var name = Il2Cpp.ReadString(k);
            nint value;
            try { value = Call(dict, "get_Item", k); } catch { value = 0; }
            if (value != 0) keep.Add(new GameRef(value));
            list.Add((name, value));
        }
        return list;
    }

    static string Short(string s) => s.Length > 120 ? s[..120] + "..." : s;

    static void Probe(Action<string> log)
    {
        if (probed && console != 0 && !Unity.IsNull(console)) return;
        probed = true; Problem = "";
        commandsField = completionField = commandListField = bindsField = keysField = default;
        try
        {
            console = Jibbers.Interop.Game.Console.Live().Select(c => c.Ptr).FirstOrDefault();
            var k = Il2Cpp.Class("Assembly-CSharp.dll", "", "Console");
            if (console == 0 || k.IsNull) { Problem = "The game's console was not found."; log("binds: " + Problem); probed = false; return; }
            var keep = new List<GameRef>();
            try
            {
                foreach (var f in k.Fields)
                {
                    var type = f.TypeName;
                    if (commandListField.IsNull && type.StartsWith("System.Collections.Generic.List<") && !type.Contains("System.String") && !type.Contains("ValueTuple"))
                    {
                        var list = f.GetObject(f.IsStatic ? 0 : console);
                        var items = list == 0 ? new List<nint>() : Walk(list, keep);
                        if (items.Count > 3 && Il2Cpp.ClassOf(items[0]).Fields.Any(x => !x.IsStatic && x.TypeName == "System.Action<System.String[]>"))
                        {
                            commandListField = f;
                            log($"binds: command list = {f.Name} ({type}, {items.Count} commands)");
                        }
                    }
                    if (!type.StartsWith("System.Collections.Generic.Dictionary<System.String,")) continue;
                    var dict = f.GetObject(f.IsStatic ? 0 : console);
                    if (dict == 0) continue;
                    var entries = Entries(dict, keep);
                    log($"binds: console dictionary {f.Name} {type}: {entries.Count} entries, e.g. {string.Join(", ", entries.Take(10).Select(e => e.Key))}");
                    if (commandsField.IsNull && entries.Any(e => e.Key == "bind")) { commandsField = f; log($"binds: command table = {f.Name}"); }
                    else if (completionField.IsNull && type.Contains("Func<System.Collections.Generic.IEnumerable<System.String>>") && entries.Any(e => e.Key == "cmd")) { completionField = f; log($"binds: completion lists = {f.Name} (the 'cmd' list holds the command names)"); }
                    else if (keysField.IsNull && type.EndsWith("UnityEngine.KeyCode>")) { keysField = f; log($"binds: key names = {f.Name}"); }
                    else if (bindsField.IsNull && type.Contains("Console.") && !type.Contains("Func<") && !type.Contains("List<"))
                    {
                        // The macros (name -> script) and the binds both look alike: the binds have a KeyCode inside.
                        var first = entries.Select(e => e.Value).FirstOrDefault(v => v != 0);
                        if (first != 0 && Il2Cpp.ClassOf(first).Fields.Any(x => x.TypeName == "UnityEngine.KeyCode")) { bindsField = f; log($"binds: bind table = {f.Name}"); }
                    }
                }
            }
            finally { foreach (var g in keep) g.Dispose(); }
            if (commandListField.IsNull && commandsField.IsNull && completionField.IsNull) { Problem = "The command list was not found in the game's console (see the log)."; log("binds: " + Problem); }
        }
        catch (Exception e) { Problem = "Could not read the game's console: " + e.Message; log("binds: " + Problem); probed = false; }
    }

    static nint DelegateOf(nint value)
    {
        if (value == 0) return 0;
        var vk = Il2Cpp.ClassOf(value);
        if (IsDelegate(vk)) return value;
        foreach (var f in vk.Fields)
            if (!f.IsStatic && IsDelegate(f.TypeClass)) return f.GetObject(value);
        return 0;
    }

    /// <summary>The commands of the console's own list: name and handler (an Action of string[]).</summary>
    static List<(string Name, nint Handler)> CommandTable(List<GameRef> keep)
    {
        var table = new List<(string, nint)>();
        if (commandListField.IsNull) return table;
        var list = commandListField.GetObject(commandListField.IsStatic ? 0 : console);
        foreach (var item in Walk(list, keep))
        {
            string name = ""; nint handler = 0;
            foreach (var f in Il2Cpp.ClassOf(item).Fields)
            {
                if (f.IsStatic) continue;
                if (name.Length == 0 && f.TypeName == "System.String") name = f.GetString(item);
                else if (f.TypeName == "System.Action<System.String[]>") handler = f.GetObject(item);
            }
            if (name.Length > 0 && handler != 0) { keep.Add(new GameRef(handler)); table.Add((name, handler)); }
        }
        return table;
    }

    /// <summary>The names of every command of the game's console, sorted. They come from the list the console itself offers when you type a command.</summary>
    public static List<string> Commands(Action<string> log)
    {
        Probe(log);
        var names = new List<string>();
        var keep = new List<GameRef>();
        try
        {
            if (!commandListField.IsNull)
            {
                foreach (var (name, _) in CommandTable(keep)) names.Add(name);
            }
            else if (!completionField.IsNull)
            {
                var dict = completionField.GetObject(completionField.IsStatic ? 0 : console);
                var entry = Entries(dict, keep).FirstOrDefault(e => e.Key == "cmd");
                var handler = DelegateOf(entry.Value);
                if (handler != 0)
                {
                    keep.Add(new GameRef(handler));
                    var list = Resolve(Il2Cpp.ClassOf(handler), "Invoke", 0).Invoke(handler);
                    foreach (var n in Walk(list, keep)) { var name = Il2Cpp.ReadString(n); if (!string.IsNullOrWhiteSpace(name)) names.Add(name); }
                }
            }
            else if (!commandsField.IsNull)
            {
                var dict = commandsField.GetObject(commandsField.IsStatic ? 0 : console);
                foreach (var (key, _) in Entries(dict, keep)) if (!string.IsNullOrWhiteSpace(key)) names.Add(key);
            }
        }
        catch (Exception e) { log("binds: could not list the commands: " + e.Message); }
        finally { foreach (var g in keep) g.Dispose(); }
        names = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        names.Sort(StringComparer.OrdinalIgnoreCase);
        log($"binds: commands: {string.Join(", ", names.Take(40))}{(names.Count > 40 ? ", ..." : "")}");
        return names;
    }

    static Il2CppMethod keyNameMethod;
    static bool keyNameLooked;

    /// <summary>The name the game uses for a Unity KeyCode (for example 287 -> "f6"); null when it has none.</summary>
    public static string? KeyName(int unityKeyCode, Action<string> log)
    {
        try
        {
            if (!keyNameLooked)
            {
                keyNameLooked = true;
                var k = Il2Cpp.Class("Assembly-CSharp.dll", "", "Console");
                foreach (var m in k.Methods)
                    if (m.IsStatic && m.ParameterCount == 1 && m.ParameterTypes[0] == "UnityEngine.KeyCode" && m.ReturnType == "System.String") { keyNameMethod = m; break; }
                log(keyNameMethod.IsNull ? "binds: the game's key name function was not found" : $"binds: key names come from the game ({keyNameMethod.Name})");
            }
            if (keyNameMethod.IsNull) return null;
            var code = unityKeyCode;
            var s = keyNameMethod.Invoke(0, (nint)(&code));
            var name = s == 0 ? null : Il2Cpp.ReadString(s);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception e) { log("binds: key name failed: " + e.Message); return null; }
    }

    /// <summary>The binds that exist now (the key name and what it runs).</summary>
    public static List<Bind> Binds(Action<string> log)
    {
        Probe(log);
        var result = new List<Bind>();
        if (bindsField.IsNull) return result;
        var keep = new List<GameRef>();
        try
        {
            var dict = bindsField.GetObject(bindsField.IsStatic ? 0 : console);
            foreach (var (key, v) in Entries(dict, keep))
            {
                var texts = new List<string>(); var dump = new List<string>();
                if (v != 0)
                    foreach (var f in Il2Cpp.ClassOf(v).Fields)
                    {
                        if (f.IsStatic) continue;
                        if (f.TypeName == "System.String") { var t = f.GetString(v); dump.Add($"{f.Name}='{t}'"); if (!string.IsNullOrEmpty(t) && !t.Equals(key, StringComparison.OrdinalIgnoreCase)) texts.Add(t); }
                        else if (f.TypeName == "UnityEngine.KeyCode" || f.TypeName == "System.Int32") dump.Add($"{f.Name}={f.Get<int>(v)}");
                        else if (f.TypeName == "System.Boolean") dump.Add($"{f.Name}={f.Get<byte>(v) != 0}");
                    }
                result.Add(new Bind(key, string.Join(" | ", texts)));
                if (result.Count <= 3) log($"binds: bind '{key}': {string.Join(", ", dump)}");
            }
        }
        catch (Exception e) { log("binds: could not read the binds: " + e.Message); }
        finally { foreach (var g in keep) g.Dispose(); }
        return result;
    }

    static bool Applied(string key, string rest)
        => Binds(_ => { }).Any(b => b.KeyName.Equals(key, StringComparison.OrdinalIgnoreCase) && b.Text.Contains(rest.Split(' ')[0], StringComparison.OrdinalIgnoreCase));

    /// <summary>Calls the handler of a console command with these arguments.</summary>
    static void Invoke(nint handler, string[] args)
        => Resolve(Il2Cpp.ClassOf(handler), "Invoke", 1).Invoke(handler, Il2Cpp.StringArray(args));

    /// <summary>Runs a line like the console would ("detach", "bind f6 detach-both"): the first word is the command, the rest are its arguments.
    /// A line starting with / goes to the chat command. Returns a message for the screen, empty when all went well.</summary>
    public static string Run(string line, Action<string> log)
    {
        Probe(log);
        if (console == 0) return Problem.Length > 0 ? Problem : "The game's console was not found.";
        if (!line.TrimStart().StartsWith('/') && line.Contains(';') && !line.TrimStart().StartsWith("bind ", StringComparison.OrdinalIgnoreCase))
        {
            // Several commands in one line ("detach left ; detach right"): each one runs in turn.
            var message = "";
            foreach (var part in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { var r = Run(part, log); if (message.Length == 0) message = r; }
            return message;
        }
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";
        var keep = new List<GameRef>();
        try
        {
            var table = CommandTable(keep);
            if (table.Count == 0) return "The console's command list was not found (see the log).";
            var name = words[0]; var args = words.Skip(1).ToArray();
            if (name.StartsWith('/')) { args = new[] { line.Trim() }; name = "chat"; }
            var entry = table.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry.Handler == 0) return $"'{name}' is not a console command.";
            Invoke(entry.Handler, args);
            log($"binds: ran '{name} {string.Join(' ', args)}'");
            if (name.Equals("detach", StringComparison.OrdinalIgnoreCase)) LookAroundMod.NoteDetach();
            // A bind is checked in the table; if the handler wanted the command name in front of its arguments, it is tried that way.
            if (name == "bind" && args.Length >= 2 && !Applied(args[0], args[1]))
            {
                log("binds: the bind did not appear, trying with the command name in front");
                Invoke(entry.Handler, new[] { "bind" }.Concat(args).ToArray());
                if (!Applied(args[0], args[1])) return "The bind did not appear (see the log).";
            }
            return "";
        }
        catch (Exception e) { log($"binds: '{line}' failed: {e.Message}"); return "Failed: " + e.Message; }
        finally { foreach (var g in keep) g.Dispose(); }
    }
}
