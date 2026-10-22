using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Diagnostics;

namespace Inspector;

/// <summary>Developer commands for symbols, metadata, settings and event inspection.</summary>
public sealed class Inspector : JibbersMod
{
    readonly InspectorCommands commands = new();
    public override string Id => "inspector";
    public override string Name => "Inspector";
    public override string Description => "Developer probes for building mods.";
    public override bool EnabledToggle => false;

    public override JsonNode? OnCommand(string command, JsonObject request) => commands.Handle(command, request);
    public override void OnUnload() => commands.Dispose();
}
