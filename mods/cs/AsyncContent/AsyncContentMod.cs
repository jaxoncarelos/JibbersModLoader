using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Symbols;
using JibbersAsyncContent.Integration;

namespace JibbersAsyncContent;

public sealed class AsyncContentMod : JibbersMod
{
    public override string Id => "async_content";
    public override string Name => "Async Content";
    public override string Description => "Prepares supported session PNGs and OBJ meshes in the background; the game still validates and registers content.";
    public override IEnumerable<Setting> Settings => [Setting.Slider("frame_budget", "Content work budget (ms)", 2, 1, 5, .5)
        .WithTooltip("Defers additional import steps after this frame's budget is used. Individual Unity uploads cannot be interrupted.")];
    GameContentAdapter? adapter;
    public override void OnLoad()
    {
        SymbolTable.Register(ContentBindings.CatalogueJson);
        if (!MainThread.Run(ContentBindings.GameImportsOnMainThread))
        {
            gameOwnsImports = true;
            Log("This game build reads, decodes and parses session content on its own workers; Async Content leaves imports to the game.");
            return;
        }
        MainThread.Run(() => adapter = new GameContentAdapter(() => Enabled, () => GetDouble("frame_budget"), Log));
        Log("Async preparation installed. Native validation and content registration are unchanged.");
    }
    bool gameOwnsImports;
    public override JsonNode? OnCommand(string command, JsonObject request) => command == "async_content.status"
        ? gameOwnsImports ? new JsonObject { ["active"] = false, ["reason"] = "the game imports session content on its own workers" } : MainThread.Run(() => adapter?.Status())
        : null;
    public override void OnSettingChanged(string key, JsonNode value)
    {
        if (key == "enabled" && !Enabled) MainThread.Later(() => adapter?.CancelImports());
    }
    public override void OnUnload() => MainThread.Run(() => { adapter?.Dispose(); adapter = null; });
}
