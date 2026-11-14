using Jibbers.Sdk;

namespace Spotify.Integration;

/// <summary>
/// The current song's album art as textures: the square cover, the round label for the record, and
/// its accent colour. Decoded once per song. Main thread only.
/// </summary>
internal sealed class AlbumArt : IDisposable
{
    static readonly Color Green = new(30 / 255f, 215 / 255f, 96 / 255f, 1);
    string key = "\0";
    GameRef? square, round, plain;

    /// <summary>The cover (a plain green disc for songs without art).</summary>
    public nint Square => Alive(square) ? square!.Target : Plain();
    /// <summary>The cover cut round with a spindle hole.</summary>
    public nint Round => Alive(round) ? round!.Target : Plain();
    public bool HasArt => Alive(square);
    public Color Accent { get; private set; } = Green;

    static bool Alive(GameRef? r) => r is not null && !Unity.IsNull(r.Target);

    nint Plain()
    {
        if (!Alive(plain)) plain = new GameRef(Textures.From(RecordArt.PlainLabel(128)));
        return plain!.Target;
    }

    static void Drop(ref GameRef? r)
    {
        if (Alive(r)) Unity.Destroy(r!.Target);
        r?.Dispose();
        r = null;
    }

    public void Update(NowPlaying np)
    {
        if (np.ArtKey == key && (np.Art is null || Alive(square))) return;
        key = np.ArtKey;
        Drop(ref square);
        Drop(ref round);
        Accent = Green;
        if (np.Art is not { } file || Textures.Decode(file) is not { } img || img.W != img.H) return;
        var (r, g, b) = ArtColour.Accent(img.Rgba);
        Accent = new Color(r, g, b, 1);
        var cover = Textures.New(img.W, img.H);
        Textures.Fill(cover, img.Rgba);
        square = new GameRef(cover);
        var label = (byte[])img.Rgba.Clone();
        RecordArt.RoundLabel(label, img.W);
        var disc = Textures.New(img.W, img.H);
        Textures.Fill(disc, label);
        round = new GameRef(disc);
    }

    public void Dispose()
    {
        Drop(ref square);
        Drop(ref round);
        Drop(ref plain);
        key = "\0";
    }
}
