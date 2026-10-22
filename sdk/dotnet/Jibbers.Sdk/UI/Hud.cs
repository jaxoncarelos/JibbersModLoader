namespace Jibbers.Sdk.UI;

/// <summary>Registers mod-owned screen overlays. Registration and layout declarations need no game objects.</summary>
public static class Hud
{
    static readonly object gate = new();
    static readonly List<HudPanel> panels = new();

    /// <summary>Registers a hidden HUD. Show it when ready; the SDK creates and recreates its native UI on demand.</summary>
    public static HudPanel Register(string name, HudOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new();
        options.Validate();
        lock (gate)
        {
            var owner = ModContext.CurrentId;
            if (panels.Any(panel => panel.Owner == owner && panel.Name == name))
                throw new InvalidOperationException($"HUD '{name}' is already registered by this mod.");
            var panel = new HudPanel(name, owner, options, () => new UnityHudSurface(), MainThread.Run, Unregister);
            panels.Add(panel);
            return panel;
        }
    }

    static void Unregister(HudPanel panel) { lock (gate) panels.Remove(panel); }
    internal static void RemoveOwner(string owner)
    {
        HudPanel[] owned;
        lock (gate) owned = panels.Where(panel => panel.Owner == owner).ToArray();
        Cleanup.Run(owned.Select(panel => (Action)panel.Dispose).ToArray());
    }
}

/// <summary>A registered HUD. All updates dispatch to the game's main thread automatically.</summary>
public sealed class HudPanel : IDisposable
{
    readonly object gate = new();
    readonly HudOptions options;
    readonly Func<IHudSurface> createSurface;
    readonly Action<Action> dispatch;
    readonly Action<HudPanel> unregister;
    readonly List<HudLabel> labels = new();
    IHudSurface? surface;
    bool visible;
    bool disposed;
    public string Name { get; }
    internal string? Owner { get; }

    internal HudPanel(string name, string? owner, HudOptions options, Func<IHudSurface> createSurface,
        Action<Action> dispatch, Action<HudPanel> unregister)
    {
        Name = name; Owner = owner; this.options = options;
        this.createSurface = createSurface; this.dispatch = dispatch; this.unregister = unregister;
    }

    /// <summary>Adds a text element. Keep the returned handle to change its text.</summary>
    public HudLabel AddText(string name, HudTextOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new();
        options.Validate();
        HudLabel label;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (labels.Any(existing => existing.Name == name)) throw new ArgumentException("Text names must be unique within a HUD.", nameof(name));
            label = new HudLabel(this, name, options);
            labels.Add(label);
        }
        Render();
        return label;
    }

    /// <summary>Displays the HUD, creating its canvas and text as needed.</summary>
    public void Show() => SetVisible(true);
    /// <summary>Hides the HUD without losing its text or layout.</summary>
    public void Hide() => SetVisible(false);
    void SetVisible(bool value)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); visible = value; }
        Render();
    }

    internal string ReadText(HudLabel label) { lock (gate) return label.Content; }
    internal void SetText(HudLabel label, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            label.Content = text;
        }
        // Render even when text is unchanged: a scene change may have destroyed the canvas.
        Render();
    }

    void Render()
    {
        lock (gate) { if (disposed || (!visible && surface is null)) return; }
        dispatch(() =>
        {
            lock (gate)
            {
                if (disposed) return;
                if (!visible) { surface?.Hide(); return; }
                surface ??= createSurface();
                surface.Render(new HudSnapshot(Name, options, labels.Select(label => new HudTextSnapshot(label.Name, label.Options, label.Content)).ToArray()));
            }
        });
    }

    /// <summary>Unregisters the HUD and releases native objects. Also called automatically on mod unload.</summary>
    public void Dispose()
    {
        IHudSurface? owned;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            owned = surface;
            surface = null;
        }
        unregister(this);
        if (owned is not null) dispatch(owned.Dispose);
    }
}

/// <summary>A text element in a registered HUD. Changing Text is safe from any thread.</summary>
public sealed class HudLabel
{
    readonly HudPanel panel;
    internal string Content = "";
    internal string Name { get; }
    internal HudTextOptions Options { get; }
    internal HudLabel(HudPanel panel, string name, HudTextOptions options) { this.panel = panel; Name = name; Options = options; }
    public string Text { get => panel.ReadText(this); set => panel.SetText(this, value); }
}

internal sealed record HudSnapshot(string Name, HudOptions Options, HudTextSnapshot[] Texts);
internal sealed record HudTextSnapshot(string Name, HudTextOptions Options, string Text);
internal interface IHudSurface : IDisposable { void Render(HudSnapshot snapshot); void Hide(); }
