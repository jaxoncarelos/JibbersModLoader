namespace Jibbers.Sdk.UI;

/// <summary>Position relative to the screen, independent of display resolution.</summary>
public enum HudAnchor { BottomLeft, BottomCenter, BottomRight, MiddleLeft, Center, MiddleRight, TopLeft, TopCenter, TopRight }
/// <summary>Text alignment inside its layout rectangle.</summary>
public enum HudTextAlignment { BottomLeft, Bottom, BottomRight, Left, Center, Right, TopLeft, Top, TopRight }

/// <summary>Screen overlay settings. Positions and sizes use reference-resolution pixels.</summary>
public sealed record HudOptions
{
    public Vector2 ReferenceResolution { get; init; } = new(1920, 1080);
    public int SortingOrder { get; init; } = 900;
    public float MatchWidthOrHeight { get; init; } = 0.5f;
    internal void Validate()
    {
        HudValidation.Positive(ReferenceResolution.X, nameof(ReferenceResolution));
        HudValidation.Positive(ReferenceResolution.Y, nameof(ReferenceResolution));
        if (!float.IsFinite(MatchWidthOrHeight) || MatchWidthOrHeight is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MatchWidthOrHeight));
    }
}

/// <summary>Typed text appearance and layout. Fonts, native components and input passthrough are SDK-owned.</summary>
public sealed record HudTextOptions
{
    public HudAnchor Anchor { get; init; } = HudAnchor.Center;
    public Vector2 Offset { get; init; }
    public Vector2 Size { get; init; } = new(900, 200);
    public float FontSize { get; init; } = 30;
    public Color Color { get; init; } = new(1, 1, 1, 1);
    public HudTextAlignment Alignment { get; init; } = HudTextAlignment.Center;
    public bool RichText { get; init; }
    public bool Wrap { get; init; }
    internal void Validate()
    {
        if (!Enum.IsDefined(Anchor)) throw new ArgumentOutOfRangeException(nameof(Anchor));
        if (!Enum.IsDefined(Alignment)) throw new ArgumentOutOfRangeException(nameof(Alignment));
        HudValidation.Positive(Size.X, nameof(Size));
        HudValidation.Positive(Size.Y, nameof(Size));
        HudValidation.Positive(FontSize, nameof(FontSize));
        foreach (var value in new[] { Offset.X, Offset.Y, Color.R, Color.G, Color.B, Color.A })
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(Offset), "HUD layout and color must be finite.");
    }
}

internal static class HudValidation
{
    public static void Positive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }
    public static Vector2 Anchor(HudAnchor anchor) => anchor switch
    {
        HudAnchor.BottomLeft => new(0, 0), HudAnchor.BottomCenter => new(0.5f, 0), HudAnchor.BottomRight => new(1, 0),
        HudAnchor.MiddleLeft => new(0, 0.5f), HudAnchor.Center => new(0.5f, 0.5f), HudAnchor.MiddleRight => new(1, 0.5f),
        HudAnchor.TopLeft => new(0, 1), HudAnchor.TopCenter => new(0.5f, 1), HudAnchor.TopRight => new(1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(anchor)),
    };
}
