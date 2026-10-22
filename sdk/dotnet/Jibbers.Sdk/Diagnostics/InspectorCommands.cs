using System.Text.Json.Nodes;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Jibbers.Sdk.Diagnostics;

/// <summary>Main-thread developer probes with owned event subscriptions.</summary>
public sealed class InspectorCommands : IDisposable
{
    readonly string prefix;
    readonly object gate = new();
    readonly Dictionary<string, (int Count, string Last)> watched = new();
    readonly List<Subscription> subscriptions = new();
    bool disposed;

    public InspectorCommands(string prefix = "inspector") => this.prefix = prefix + ".";

    /// <summary>Handles a probe command, or returns null for commands belonging to another mod.</summary>
    public JsonNode? Handle(string command, JsonObject request)
    {
        if (!command.StartsWith(prefix, StringComparison.Ordinal)) return null;
        ObjectDisposedException.ThrowIf(disposed, this);
        var operation = command[prefix.Length..];
        if (operation == "watched") return Snapshot();
        return MainThread.Run(() => Execute(operation, request));
    }

    static string RequiredString(JsonObject request, string key) =>
        request[key]?.GetValue<string>() ?? throw new ArgumentException($"Missing {key}.");

    JsonNode? Execute(string operation, JsonObject request) => operation switch
    {
        "symbols" => SymbolTable.Report(),
        "dump" => Dump(request),
        "images" => new JsonArray(Il2Cpp.Assemblies().Select(assembly => (JsonNode)assembly.Name!).ToArray()),
        "live" => Live(request),
        "class" => DescribeClass(request),
        "vars" => Variables(request["kind"]?.GetValue<string>() ?? "float"),
        "actions" => new JsonArray(Input.ActionNames().Select(action => (JsonNode)action!).ToArray()),
        "watch" => Watch(RequiredString(request, "event")),
        _ => null,
    };

    static JsonNode Dump(JsonObject request)
    {
        var images = request["images"]?.AsArray().Select(image => image!.GetValue<string>()).ToArray() ?? Array.Empty<string>();
        var path = request["path"]?.GetValue<string>() ?? Path.Combine(Bootstrap.GameDir, "jibbers_symbols_dump.json");
        return new JsonObject { ["path"] = path, ["classes"] = SymbolTable.Dump(path, images) };
    }

    static JsonNode Live(JsonObject request)
    {
        var depth = request["depth"]?.GetValue<int>() ?? 0;
        var instances = SymbolTable.Live(RequiredString(request, "symbol"));
        return new JsonObject
        {
            ["count"] = instances.Length,
            ["objects"] = new JsonArray(instances.Take(32).Select(instance => Inspect.Describe(instance, depth)).ToArray()),
        };
    }

    static JsonNode DescribeClass(JsonObject request)
    {
        var gameClass = Il2Cpp.Class(RequiredString(request, "image"), request["namespace"]?.GetValue<string>() ?? "", RequiredString(request, "name"));
        if (gameClass.IsNull) throw new ArgumentException("No such class.");
        return new JsonObject
        {
            ["class"] = gameClass.FullName,
            ["parent"] = gameClass.Parent.IsNull ? null : gameClass.Parent.FullName,
            ["fields"] = new JsonArray(gameClass.Fields.Select(field => (JsonNode)field.ToString()!).ToArray()),
            ["methods"] = new JsonArray(gameClass.Methods.Select(method => (JsonNode)method.ToString()!).ToArray()),
        };
    }

    static JsonNode Variables(string kind)
    {
        var result = new JsonObject();
        switch (kind)
        {
            case "bool": foreach (var name in GameVars.BoolNames) result[name] = GameVars.GetBool(name); break;
            case "int": foreach (var name in GameVars.IntNames) result[name] = GameVars.GetInt(name); break;
            case "string": foreach (var name in GameVars.StringNames) result[name] = GameVars.GetString(name); break;
            case "float": foreach (var name in GameVars.FloatNames) result[name] = GameVars.GetFloat(name); break;
            default: throw new ArgumentException("Variable kind must be bool, int, string or float.");
        }
        return result;
    }

    JsonNode Watch(string eventId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (watched.ContainsKey(eventId)) return new JsonObject { ["already"] = true };
            var subscription = Events.Subscribe(eventId, gameEvent =>
            {
                lock (gate)
                {
                    if (!disposed && watched.TryGetValue(eventId, out var previous))
                        watched[eventId] = (previous.Count + 1, gameEvent.Describe(2));
                }
            });
            watched[eventId] = (0, "");
            subscriptions.Add(subscription);
            return new JsonObject { ["watching"] = eventId };
        }
    }

    JsonNode Snapshot()
    {
        lock (gate)
            return new JsonObject(watched.Select(entry => KeyValuePair.Create(entry.Key,
                (JsonNode?)new JsonObject { ["count"] = entry.Value.Count, ["last"] = entry.Value.Last })));
    }

    public void Dispose()
    {
        Subscription[] owned;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            owned = subscriptions.ToArray();
            subscriptions.Clear();
            watched.Clear();
        }
        foreach (var subscription in owned) subscription.Dispose();
    }
}
