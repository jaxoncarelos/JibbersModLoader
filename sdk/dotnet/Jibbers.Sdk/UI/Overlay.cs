namespace Jibbers.Sdk.UI;

/// <summary>Where an overlay sits on screen.</summary>
public sealed record OverlayOptions
{
    /// <summary>Screen corner or edge the overlay is pinned to (its own matching corner sits there).</summary>
    public HudAnchor Anchor { get; init; } = HudAnchor.TopCenter;
    /// <summary>Offset from the anchor in reference pixels (x right, y up).</summary>
    public Vector2 Offset { get; init; }
    /// <summary>Draw order against other canvases (the game's menus are below 500).</summary>
    public int SortingOrder { get; init; } = 920;
    /// <summary>Background pixels per reference pixel: 2 keeps edges crisp on 1440p and 4K.</summary>
    public float Sharpness { get; init; } = 2;
    public Vector2 ReferenceResolution { get; init; } = new(1920, 1080);

    internal void Validate()
    {
        if (!Enum.IsDefined(Anchor)) throw new ArgumentOutOfRangeException(nameof(Anchor));
        if (!float.IsFinite(Sharpness) || Sharpness is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(Sharpness), "between 1 and 4");
        HudValidation.Positive(ReferenceResolution.X, nameof(ReferenceResolution));
        HudValidation.Positive(ReferenceResolution.Y, nameof(ReferenceResolution));
    }
}

/// <summary>
/// One picture of an overlay: shapes (drawn into a texture, anti-aliased) and text (the game's own
/// font) in frame units, top-left origin, y down. Build a new frame whenever something changes and
/// pass it to <see cref="OverlayPanel.Draw"/>; unchanged shapes are not redrawn.
/// </summary>
/// <example>
/// <code>
/// var f = new OverlayFrame(360, 120);
/// f.Card(0, 0, 360, 120);
/// f.Text("SLVSH", 20, 16, 320, 40, 30, Theme.Text, bold: true);
/// panel.Draw(f);
/// </code>
/// </example>
public sealed class OverlayFrame
{
    internal readonly List<object> Shapes = new();
    internal readonly List<OverlayText> Texts = new();

    public OverlayFrame(float width, float height)
    {
        HudValidation.Positive(width, nameof(width));
        HudValidation.Positive(height, nameof(height));
        if (width > 2048 || height > 2048) throw new ArgumentOutOfRangeException(nameof(width), "overlays are at most 2048 units on a side");
        Width = width;
        Height = height;
    }

    public float Width { get; }
    public float Height { get; }
    float ox, oy;

    /// <summary>Moves the origin for the commands that follow (handy for drawing a card inside a margin, or one row at a time).</summary>
    public OverlayFrame Origin(float x, float y)
    {
        (ox, oy) = (x, y);
        return this;
    }

    /// <summary>A filled rounded rectangle.</summary>
    public OverlayFrame Rect(float x, float y, float w, float h, float radius, Color fill) => Add(new RectShape(ox + x, oy + y, w, h, radius, fill, fill, false));
    /// <summary>A rounded rectangle filled with a gradient (top to bottom, or left to right with <paramref name="horizontal"/>).</summary>
    public OverlayFrame Gradient(float x, float y, float w, float h, float radius, Color from, Color to, bool horizontal = false) => Add(new RectShape(ox + x, oy + y, w, h, radius, from, to, horizontal));
    /// <summary>A rounded rectangle outline.</summary>
    public OverlayFrame Border(float x, float y, float w, float h, float radius, float width, Color color) => Add(new BorderShape(ox + x, oy + y, w, h, radius, width, color));
    /// <summary>A soft drop shadow for a rounded rectangle (draw it before the rectangle).</summary>
    public OverlayFrame Shadow(float x, float y, float w, float h, float radius, float blur = 18, float dy = 6, Color? color = null) => Add(new ShadowShape(ox + x, oy + y, w, h, radius, blur, dy, color ?? new Color(0, 0, 0, 0.55f)));
    /// <summary>A filled circle.</summary>
    public OverlayFrame Circle(float cx, float cy, float radius, Color fill) => Add(new CircleShape(ox + cx, oy + cy, radius, fill));
    /// <summary>A ring segment with round ends, from 12 o'clock clockwise (a progress ring is 0 to 360 * progress).</summary>
    public OverlayFrame Arc(float cx, float cy, float radius, float thickness, float startDegrees, float sweepDegrees, Color color) => Add(new ArcShape(ox + cx, oy + cy, radius, thickness, startDegrees, sweepDegrees, color));

    /// <summary>The standard card: shadow, dark glass fill and a hairline border (see <see cref="Theme"/>).</summary>
    public OverlayFrame Card(float x, float y, float w, float h, float radius = 18)
    {
        Shadow(x, y, w, h, radius);
        Gradient(x, y, w, h, radius, Theme.CardTop, Theme.CardBottom);
        return Border(x, y, w, h, radius, 1, Theme.Hairline);
    }

    /// <summary>
    /// Text in a box. Long text is cut with "…" unless <paramref name="wrap"/>. Text is shown as
    /// written: tags like &lt;b&gt; are not interpreted, so player names are safe to show.
    /// </summary>
    public OverlayFrame Text(string text, float x, float y, float w, float h, float size, Color color,
        HudTextAlignment align = HudTextAlignment.Left, bool bold = false, bool wrap = false, float spacing = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        HudValidation.Positive(w, nameof(w));
        HudValidation.Positive(h, nameof(h));
        HudValidation.Positive(size, nameof(size));
        if (Texts.Count >= 128) throw new InvalidOperationException("an overlay frame holds at most 128 texts");
        Texts.Add(new OverlayText(text, ox + x, oy + y, w, h, size, color, align, bold, wrap, spacing));
        return this;
    }

    OverlayFrame Add(object shape)
    {
        if (Shapes.Count >= 512) throw new InvalidOperationException("an overlay frame holds at most 512 shapes");
        Shapes.Add(shape);
        return this;
    }

    internal bool SameShapes(OverlayFrame? other) => other is not null && other.Width == Width && other.Height == Height && other.Shapes.SequenceEqual(Shapes);

    internal Raster Rasterize(float sharpness)
    {
        var r = new Raster(Width, Height, sharpness);
        foreach (var shape in Shapes)
            switch (shape)
            {
                case RectShape s: r.RoundRect(s.X, s.Y, s.W, s.H, s.Radius, s.From, s.To, s.Horizontal); break;
                case BorderShape s: r.Stroke(s.X, s.Y, s.W, s.H, s.Radius, s.Width, s.Color); break;
                case ShadowShape s: r.Shadow(s.X, s.Y, s.W, s.H, s.Radius, s.Blur, s.Dy, s.Color); break;
                case CircleShape s: r.Circle(s.Cx, s.Cy, s.Radius, s.Color); break;
                case ArcShape s: r.Arc(s.Cx, s.Cy, s.Radius, s.Thickness, s.Start, s.Sweep, s.Color); break;
            }
        return r;
    }

    sealed record RectShape(float X, float Y, float W, float H, float Radius, Color From, Color To, bool Horizontal);
    sealed record BorderShape(float X, float Y, float W, float H, float Radius, float Width, Color Color);
    sealed record ShadowShape(float X, float Y, float W, float H, float Radius, float Blur, float Dy, Color Color);
    sealed record CircleShape(float Cx, float Cy, float Radius, Color Color);
    sealed record ArcShape(float Cx, float Cy, float Radius, float Thickness, float Start, float Sweep, Color Color);
}

internal sealed record OverlayText(string Text, float X, float Y, float W, float H, float Size, Color Color, HudTextAlignment Align, bool Bold, bool Wrap, float Spacing);

/// <summary>The SDK's palette, so overlays from different mods look like one family.</summary>
public static class Theme
{
    static Color Rgb(int r, int g, int b, float a = 1) => new(r / 255f, g / 255f, b / 255f, a);
    public static readonly Color CardTop = Rgb(22, 25, 36, 0.94f);
    public static readonly Color CardBottom = Rgb(12, 14, 21, 0.94f);
    public static readonly Color Hairline = Rgb(255, 255, 255, 0.09f);
    public static readonly Color Raised = Rgb(255, 255, 255, 0.06f);
    public static readonly Color Text = Rgb(244, 246, 252);
    public static readonly Color Muted = Rgb(150, 158, 178);
    public static readonly Color Faint = Rgb(96, 104, 124);
    public static readonly Color Ice = Rgb(108, 203, 255);
    public static readonly Color Violet = Rgb(150, 122, 255);
    public static readonly Color Good = Rgb(88, 224, 140);
    public static readonly Color Bad = Rgb(255, 92, 92);
    public static readonly Color Warn = Rgb(255, 196, 77);
    /// <summary>A color with a new alpha.</summary>
    public static Color WithAlpha(this Color c, float a) => c with { A = a };
    /// <summary>A stable, pleasant color for a player (avatars, name accents), picked from their id.</summary>
    public static Color ForId(ulong id)
    {
        Color[] avatars = { Rgb(108, 203, 255), Rgb(150, 122, 255), Rgb(255, 138, 101), Rgb(88, 224, 140), Rgb(255, 196, 77), Rgb(255, 111, 175), Rgb(84, 214, 214), Rgb(196, 160, 255) };
        return avatars[(int)((id * 0x9E3779B97F4A7C15UL) >> 61)];
    }
}

/// <summary>Registers mod-owned overlays: drawn cards with text, above the game.</summary>
public static class Overlay
{
    static readonly object gate = new();
    static readonly List<OverlayPanel> panels = new();
    static readonly AutoResetEvent wake = new(false);
    static Thread? worker;

    /// <summary>Registers a hidden overlay. Draw a frame and Show it; the SDK keeps it on screen across maps.</summary>
    public static OverlayPanel Register(string name, OverlayOptions? options = null) =>
        Register(name, options, () => new UnityOverlaySurface(), MainThread.Run);

    internal static OverlayPanel Register(string name, OverlayOptions? options, Func<IOverlaySurface> surface, Action<Action> dispatch, bool background = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new();
        options.Validate();
        lock (gate)
        {
            var owner = ModContext.CurrentId;
            if (panels.Any(p => p.Owner == owner && p.Name == name)) throw new InvalidOperationException($"overlay '{name}' is already registered by this mod");
            var panel = new OverlayPanel(name, owner, options, surface, dispatch, background ? Wake : null, p => { lock (gate) panels.Remove(p); });
            panels.Add(panel);
            if (background && worker is null)
            {
                worker = new Thread(Work) { IsBackground = true, Name = "Jibbers Overlay" };
                worker.Start();
            }
            return panel;
        }
    }

    static void Wake() => wake.Set();

    static void Work()
    {
        while (true)
        {
            wake.WaitOne(250);
            OverlayPanel[] list;
            lock (gate) list = panels.ToArray();
            foreach (var p in list) p.Pump();
        }
    }

    internal static void RemoveOwner(string owner)
    {
        OverlayPanel[] owned;
        lock (gate) owned = panels.Where(p => p.Owner == owner).ToArray();
        Cleanup.Run(owned.Select(p => (Action)p.Dispose).ToArray());
    }
}

/// <summary>A registered overlay. Draw, Show and Hide are safe from any thread and never block on the game.</summary>
public sealed class OverlayPanel : IDisposable
{
    readonly object gate = new();
    readonly OverlayOptions options;
    readonly Func<IOverlaySurface> createSurface;
    readonly Action<Action> dispatch;
    readonly Action? wake;
    readonly Action<OverlayPanel> unregister;
    IOverlaySurface? surface;
    OverlayFrame? pending, shown;
    bool visible, dirty, disposed;
    long retryAt;

    internal OverlayPanel(string name, string? owner, OverlayOptions options, Func<IOverlaySurface> createSurface, Action<Action> dispatch, Action? wake, Action<OverlayPanel> unregister)
    {
        Name = name;
        Owner = owner;
        this.options = options;
        this.createSurface = createSurface;
        this.dispatch = dispatch;
        this.wake = wake;
        this.unregister = unregister;
    }

    public string Name { get; }
    internal string? Owner { get; }
    /// <summary>True between Show and Hide.</summary>
    public bool Visible { get { lock (gate) return visible; } }

    /// <summary>Replaces what the overlay shows. Cheap to call often: the newest frame wins.</summary>
    public void Draw(OverlayFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending = frame;
            dirty = true;
        }
        Kick();
    }

    public void Show() => SetVisible(true);
    public void Hide() => SetVisible(false);

    void SetVisible(bool value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (visible == value) return;
            visible = value;
            dirty = true;
        }
        Kick();
    }

    void Kick()
    {
        if (wake is not null) wake();
        else Pump();
    }

    /// <summary>Rasterizes the newest frame (off the main thread) and applies it on the main thread.</summary>
    internal void Pump()
    {
        OverlayFrame? frame;
        bool show;
        lock (gate)
        {
            if (disposed || !dirty || Environment.TickCount64 < retryAt) return;
            dirty = false;
            frame = pending;
            show = visible;
        }
        if (!show && surface is null) return;
        byte[]? pixels = null;
        var (pw, ph) = (0, 0);
        if (show && frame is not null && !frame.SameShapes(shown))
        {
            var r = frame.Rasterize(options.Sharpness);
            (pixels, pw, ph) = (r.ToRgba32(), r.Width, r.Height);
        }
        try
        {
            dispatch(() =>
            {
                lock (gate) if (disposed) return;
                if (!show) { surface?.Hide(); return; }
                if (frame is null) return;
                surface ??= createSurface();
                surface.Render(new OverlaySnapshot(Name, options, frame.Width, frame.Height, pixels, pw, ph, frame.Texts.ToArray()));
            });
            if (show && frame is not null) shown = frame;
        }
        catch (Exception e)
        {
            // Busy game (loading) or a failed build: keep the frame and try again shortly.
            lock (gate)
            {
                dirty = true;
                retryAt = Environment.TickCount64 + 500;
            }
            shown = null;
            if (e is not TimeoutException) Log.Info("overlay", $"{Name}: {e.Message}");
        }
    }

    /// <summary>Removes the overlay and its game objects. Also done when your mod unloads.</summary>
    public void Dispose()
    {
        IOverlaySurface? owned;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            owned = surface;
            surface = null;
        }
        unregister(this);
        if (owned is not null)
        {
            try { dispatch(owned.Dispose); }
            catch (TimeoutException) { }
        }
    }
}

/// <summary>What one overlay update applies. <see cref="Pixels"/> is null when the shapes did not change.</summary>
internal sealed record OverlaySnapshot(string Name, OverlayOptions Options, float Width, float Height, byte[]? Pixels, int PixelWidth, int PixelHeight, OverlayText[] Texts);
internal interface IOverlaySurface : IDisposable { void Render(OverlaySnapshot snapshot); void Hide(); }
