using System.Diagnostics;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.UI;

namespace Jibbers.Sdk;

internal sealed class PerformanceGuard : JibbersMod
{
    public override string Id => "jibbers_performance";
    public override string Name => "Performance protection";
    public override bool EnabledToggle => false;
    public override string Description => "Temporarily disables optional mods repeatedly causing severe game-thread stalls. Saved settings return next game launch.";
    [Toggle("Pause costly mods automatically", true, Key = "auto_balance", Tooltip = "Uses measured mod callback time during sustained stuttering. Protects loading, core services and Play Together. No saved mod enable settings are changed.")]
    public bool AutoBalance { get; private set; }

    readonly PerformancePolicy policy = new();
    readonly Dictionary<string, double> measured = new(StringComparer.Ordinal);
    readonly PerformanceSuspensions suspensions = new(Detour.DisableOwner, ModHost.UnloadMod, ModTiming.Idle,
        error => Sdk.Log.Error("performance protection", error));
    readonly Stopwatch clock = Stopwatch.StartNew();
    Func<string, bool> eligible = null!;
    OverlayPanel? notice;
    long previous, frame;
    double hideNotice;

    public override void OnLoad()
    {
        eligible = Eligible;
        ModTiming.Collecting = true;
        notice = Overlay.Register("performance_protection", new OverlayOptions { Anchor = HudAnchor.TopCenter, Offset = new(0, -80), SortingOrder = 950 });
        Manager.UpdateMethod.Before((_, _) => Tick());
        Game.Event.ChangeScene.Subscribe(_ => policy.Warmup(clock.Elapsed.TotalSeconds));
        Events.Subscribe("event.player_spawn", _ => policy.Warmup(clock.Elapsed.TotalSeconds));
    }

    internal bool Eligible(string id)
    {
        var mod = ModContext.Find(id);
        return mod is { Enabled: true, EnabledToggle: true, AllowPerformanceSuspension: true }
            && id is not ("jibbers_play" or "jibbers_party" or "jibbers_performance" or "async_content")
            && !Multiplayer.Party.HasChannel(id) && !Detour.HasUntrackedOwner(id);
    }

    void Tick()
    {
        MainThread.MarkGameThread();
        frame++;
        var now = clock.Elapsed.TotalSeconds;
        suspensions.Frame(frame);
        if (hideNotice != 0 && now >= hideNotice) { notice?.Hide(); hideNotice = 0; }
        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = previous == 0 ? 0 : Stopwatch.GetElapsedTime(previous, timestamp).TotalMilliseconds;
        previous = timestamp;
        ModTiming.Collecting = AutoBalance;
        ModTiming.Drain(measured);
        var gameplay = AutoBalance && Input.GameFocused && !Game.Physics.Scene.IsPaused;
        var candidate = policy.Frame(now, elapsed, measured, eligible, gameplay);
        if (candidate is null || ModContext.Find(candidate) is not { } mod) return;
        var reason = $"{mod.Name} was disabled for performance reasons. It will be enabled again next game launch.";
        if (!suspensions.Suspend(mod, frame, reason)) { policy.Warmup(now); return; }
        Log(reason);
        notice?.Draw(new OverlayFrame(520, 122).Card(0, 0, 520, 122)
            .Text("PERFORMANCE PROTECTION", 16, 12, 488, 20, 13, Theme.Mint, bold: true)
            .Text(mod.Name, 16, 39, 488, 28, 20, Theme.Text, bold: true)
            .Text("Disabled for performance. Returns next game launch.", 16, 80, 488, 24, 14, Theme.Caption));
        notice?.Show(); hideNotice = now + 12;
    }

    public override System.Text.Json.Nodes.JsonNode? OnCommand(string command, System.Text.Json.Nodes.JsonObject request)
    {
        if (command != "jibbers_performance.status") return null;
        var paused = new System.Text.Json.Nodes.JsonArray();
        foreach (var mod in ModContext.All())
            if (mod.PerformanceSuspended) paused.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = mod.Id, ["name"] = mod.Name, ["reason"] = mod.SuspensionReason });
        return new System.Text.Json.Nodes.JsonObject { ["ok"] = true, ["automatic"] = AutoBalance, ["paused"] = paused };
    }

    public override void OnUnload()
    {
        ModTiming.Collecting = false;
        MainThread.Run(() => { suspensions.Shutdown(); notice?.Dispose(); });
    }
}
