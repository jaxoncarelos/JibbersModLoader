// Inspector: developer probes as commands, for writing mods and symbol rules. Not for players.
//
// Send them through the developer loader (127.0.0.1:47820, tools\hotload.py raw '<json>') or from
// another mod. Every probe runs on the main thread.
//
//   {"cmd":"inspector.symbols"}                                  every symbol's status on this build
//   {"cmd":"inspector.dump","images":["Assembly-CSharp.dll"]}    metadata dump for jibbers-gen
//   {"cmd":"inspector.live","symbol":"camera.follow","depth":1}  live instances, described
//   {"cmd":"inspector.class","image":"Assembly-CSharp.dll","name":"FollowCam"}   one class's members
//   {"cmd":"inspector.vars","kind":"float"}                      the game's own settings
//   {"cmd":"inspector.actions"}                                  the game's input action names
//   {"cmd":"inspector.watch","event":"event.trick_performed"}   count an event; "inspector.watched" shows them

using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Inspector;

public sealed class Inspector : JibbersMod
{
    public override string Id => "inspector";
    public override string Name => "Inspector";
    public override string Description => "Developer probes (symbols, live objects, settings, events).";
    public override bool EnabledToggle => false;

    readonly Dictionary<string, (int Count, string Last)> watched = new();

    static string Str(JsonObject r, string k) => r[k]?.GetValue<string>() ?? throw new ArgumentException($"missing \"{k}\"");

    public override JsonNode? OnCommand(string cmd, JsonObject r) => cmd switch
    {
        "inspector.symbols" => MainThread.Run(() => (JsonNode)SymbolTable.Report()),
        "inspector.dump" => MainThread.Run(() =>
        {
            var images = r["images"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? Array.Empty<string>();
            var path = r["path"]?.GetValue<string>() ?? Path.Combine(Bootstrap.GameDir, "jibbers_symbols_dump.json");
            return (JsonNode)new JsonObject { ["path"] = path, ["classes"] = SymbolTable.Dump(path, images) };
        }),
        "inspector.images" => MainThread.Run(() => (JsonNode)new JsonArray(Il2Cpp.Assemblies().Select(a => (JsonNode)a.Name!).ToArray())),
        "inspector.live" => MainThread.Run(() =>
        {
            var depth = r["depth"]?.GetValue<int>() ?? 0;
            var objs = SymbolTable.Live(Str(r, "symbol"));
            return (JsonNode)new JsonObject { ["count"] = objs.Length, ["objects"] = new JsonArray(objs.Take(32).Select(o => Inspect.Describe(o, depth)).ToArray()) };
        }),
        "inspector.class" => MainThread.Run(() =>
        {
            var k = Il2Cpp.Class(Str(r, "image"), r["namespace"]?.GetValue<string>() ?? "", Str(r, "name"));
            if (k.IsNull)
            {
                throw new ArgumentException("no such class");
            }
            return (JsonNode)new JsonObject
            {
                ["class"] = k.FullName,
                ["parent"] = k.Parent.IsNull ? null : k.Parent.FullName,
                ["fields"] = new JsonArray(k.Fields.Select(f => (JsonNode)f.ToString()!).ToArray()),
                ["methods"] = new JsonArray(k.Methods.Select(m => (JsonNode)m.ToString()!).ToArray()),
            };
        }),
        "inspector.vars" => MainThread.Run(() =>
        {
            var o = new JsonObject();
            switch (r["kind"]?.GetValue<string>() ?? "float")
            {
                case "bool": foreach (var n in GameVars.BoolNames) o[n] = GameVars.GetBool(n); break;
                case "int": foreach (var n in GameVars.IntNames) o[n] = GameVars.GetInt(n); break;
                case "string": foreach (var n in GameVars.StringNames) o[n] = GameVars.GetString(n); break;
                default: foreach (var n in GameVars.FloatNames) o[n] = GameVars.GetFloat(n); break;
            }
            return (JsonNode)o;
        }),
        "inspector.actions" => MainThread.Run(() => (JsonNode)new JsonArray(Input.ActionNames().Select(a => (JsonNode)a!).ToArray())),
        "inspector.watch" => Watch(Str(r, "event")),
        "inspector.watched" => new JsonObject(watched.Select(w => KeyValuePair.Create(w.Key, (JsonNode?)new JsonObject { ["count"] = w.Value.Count, ["last"] = w.Value.Last }))),
        _ => null,
    };

    JsonNode Watch(string id)
    {
        lock (watched)
        {
            if (watched.ContainsKey(id))
            {
                return new JsonObject { ["already"] = true };
            }
            watched[id] = (0, "");
        }
        Events.Subscribe(id, e =>
        {
            lock (watched)
            {
                watched[id] = (watched[id].Count + 1, e.Describe(2));
            }
        });
        return new JsonObject { ["watching"] = id };
    }
}
