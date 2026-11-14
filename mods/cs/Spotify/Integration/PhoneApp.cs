using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Spotify.Integration;

/// <summary>
/// The mini Spotify app: opened by the phone's Spotify button, drawn over the phone (a canvas of our
/// own, 400 x 720 units scaled to the screen; the phone's own buttons are hidden meanwhile, and the
/// mouse works because the phone is the game's pause menu). Two tabs: Now playing (cover, title,
/// artist, album, a progress bar you can click to seek, back / play / skip, -10 s / +10 s, shuffle
/// and repeat) and History (the songs played lately and this session's listening). It works through
/// Windows' media controls: no Spotify login. Main thread only.
/// </summary>
internal sealed unsafe class PhoneApp : IDisposable
{
    const float W = 400, H = 720, BarX = 40, BarW = 320, BarY = 462, PlayX = 200, PlayY = 546;
    const int Rows = 9;
    const string Content = "PhoneCanvas/PhoneMenu/Content", Menu = "PhoneCanvas/PhoneMenu";
    static readonly Color White = new(1, 1, 1, 1), Grey = new(179 / 255f, 179 / 255f, 179 / 255f, 1), Dim = new(120 / 255f, 120 / 255f, 125 / 255f, 1), Green = new(30 / 255f, 215 / 255f, 96 / 255f, 1);

    readonly History history;
    OwnedGameObject? canvas;
    GameRef? root, back, cover, barBack, barFill, prev, play, next;
    GameRef? backText, brand, title, artist, album, timeLeft, timeRight, seekBack, shuffle, repeat, seekForward, tabNow, tabHistory, heading, stats, nothing;
    readonly List<(GameRef Song, GameRef Detail)> rows = new();
    GameRef? backTex, prevTex, playTex, pauseTex, nextTex;
    float drawnScale;
    int tab;
    float left, bottom, scale;

    public bool Open { get; private set; }

    public PhoneApp(History history) => this.history = history;

    // ---- opening and closing (the phone's own buttons hide while the app shows) ----

    static void PhoneButtons(bool show)
    {
        var content = PhoneButton.Find(PhoneButton.UiRoot(), Content);
        if (content != 0) UnityProperties.Set(Unity.Call(content, "get_gameObject"), "SetActive", show);
    }

    static bool PhoneIsOut()
    {
        var menu = PhoneButton.Find(PhoneButton.UiRoot(), Menu);
        return menu != 0 && Unity.Call<bool>(Unity.Call(menu, "get_gameObject"), "get_activeInHierarchy");
    }

    public void Show()
    {
        if (Open) return;
        Open = true;
        tab = 0;
        PhoneButtons(false);
    }

    public void Close()
    {
        if (!Open) return;
        Open = false;
        if (root is not null && !Unity.IsNull(root.Target)) Ui.Active(root, false);
        PhoneButtons(true);
    }

    // ---- building ----

    bool Build()
    {
        if (canvas is not null && !Unity.IsNull(canvas.Target)) return true;
        Free();
        if (Ui.Font() == 0) return false;
        (canvas, root) = Ui.Canvas("Mod_Spotify_App", 520);
        var r = root.Target;
        back = Ui.Raw(r, "Back");
        cover = Ui.Raw(r, "Cover");
        barBack = Ui.Raw(r, "BarBack", new Color(80 / 255f, 80 / 255f, 84 / 255f, 1));
        barFill = Ui.Raw(r, "BarFill", Green);
        prev = Ui.Raw(r, "Prev");
        play = Ui.Raw(r, "Play");
        next = Ui.Raw(r, "Next");
        backText = Ui.Text(r, "BackText", White, bold: true);
        brand = Ui.Text(r, "Brand", Green, bold: true, align: "Right");
        title = Ui.Text(r, "Title", White, bold: true, align: "Center");
        artist = Ui.Text(r, "Artist", Grey, align: "Center");
        album = Ui.Text(r, "Album", Dim, align: "Center");
        timeLeft = Ui.Text(r, "TimeLeft", Grey);
        timeRight = Ui.Text(r, "TimeRight", Grey, align: "Right");
        seekBack = Ui.Text(r, "SeekBack", White, align: "Center");
        shuffle = Ui.Text(r, "Shuffle", Grey, bold: true, align: "Center");
        repeat = Ui.Text(r, "Repeat", Grey, bold: true, align: "Center");
        seekForward = Ui.Text(r, "SeekForward", White, align: "Center");
        tabNow = Ui.Text(r, "TabNow", White, bold: true, align: "Center");
        tabHistory = Ui.Text(r, "TabHistory", Grey, bold: true, align: "Center");
        heading = Ui.Text(r, "Heading", White, bold: true);
        stats = Ui.Text(r, "Stats", Grey, align: "Center");
        nothing = Ui.Text(r, "Nothing", Grey, align: "Center");
        for (var i = 0; i < Rows; i++)
            rows.Add((Ui.Text(r, $"Song{i}", White, bold: true), Ui.Text(r, $"Detail{i}", Grey)));
        Ui.Active(root, false);
        return true;
    }

    static void Swap(ref GameRef? held, nint tex)
    {
        if (held is not null && !Unity.IsNull(held.Target)) Unity.Destroy(held.Target);
        held?.Dispose();
        held = new GameRef(tex);
    }

    /// <summary>Places something by its box in app units (y down from the top).</summary>
    void Put(GameRef o, float x, float y, float w, float h) =>
        Ui.Place(o.Target, new(0, 0), new(0, 0), new(x * scale, (H - y - h) * scale), new(w * scale, h * scale));

    void Words(GameRef t, string text, float size, float x, float y, float w, float h)
    {
        Put(t, x, y, w, h);
        Ui.SetText(t, text, size * scale);
        Ui.Visible(t, true);
    }

    // ---- every frame while open ----

    /// <summary>Draws the app (or closes it when the phone was put away).</summary>
    public void Frame(NowPlaying? np, AlbumArt art, bool artColour)
    {
        if (!Open) return;
        if (!PhoneIsOut())
        {
            Close();
            return;
        }
        if (!Build()) return;
        float sh = Ui.Screen("get_height"), sw = Ui.Screen("get_width");
        scale = sh * 0.66f / H;
        float w = W * scale, h = H * scale;
        (left, bottom) = ((sw - w) / 2, (sh - h) / 2);
        Ui.Place(root!.Target, new(0.5f, 0.5f), new(0.5f, 0.5f), new(0, 0), new(w, h));
        if (drawnScale != scale)
        {
            Swap(ref backTex, Textures.From(RecordArt.AppBackground((int)w, (int)h, scale, PlayX, PlayY, 30)));
            var icon = (int)(48 * scale);
            Swap(ref prevTex, Textures.From(RecordArt.IconPicture(Command.Previous, false, icon, dark: false)));
            Swap(ref nextTex, Textures.From(RecordArt.IconPicture(Command.Next, false, icon, dark: false)));
            Swap(ref playTex, Textures.From(RecordArt.IconPicture(Command.PlayPause, false, icon, dark: true)));
            Swap(ref pauseTex, Textures.From(RecordArt.IconPicture(Command.PlayPause, true, icon, dark: true)));
            Unity.Call(back!.Target, "set_texture", backTex!.Target);
            Unity.Call(prev!.Target, "set_texture", prevTex!.Target);
            Unity.Call(next!.Target, "set_texture", nextTex!.Target);
            drawnScale = scale;
        }
        Put(back!, 0, 0, W, H);
        Words(backText!, "‹ Back", 18, 22, 18, 120, 30);
        Words(brand!, "Spotify", 20, W - 142, 16, 120, 32);
        var accent = artColour ? art.Accent : Green;

        var now = tab == 0;
        var playing = now && np is not null;
        foreach (var o in new[] { cover!, barBack!, barFill!, prev!, play!, next!, title!, artist!, album!, timeLeft!, timeRight!, seekBack!, shuffle!, repeat!, seekForward! })
            Ui.Visible(o, playing);
        Ui.Visible(nothing!, now && np is null);
        Ui.Visible(heading!, !now);
        Ui.Visible(stats!, !now);
        foreach (var (song, detail) in rows)
        {
            Ui.Visible(song, false);
            Ui.Visible(detail, false);
        }

        if (now && np is null)
            Words(nothing!, "Play something in Spotify", 18, 20, 320, W - 40, 30);
        if (playing)
        {
            Unity.Call(cover!.Target, "set_texture", art.Square);
            Put(cover, 60, 66, 280, 280);
            Words(title!, np!.Title, 22, 24, 362, W - 48, 32);
            Words(artist!, np.Artist, 16, 24, 396, W - 48, 24);
            Words(album!, np.Album, 13, 24, 422, W - 48, 20);
            var fraction = np.Duration > 0 ? (float)Math.Clamp(np.PositionNow / np.Duration, 0, 1) : 0;
            Put(barBack!, BarX, BarY, BarW, 5);
            Put(barFill!, BarX, BarY, BarW * fraction, 5);
            Ui.Colour(barFill!, accent);
            Words(timeLeft!, RecordArt.Clock(np.PositionNow), 12, BarX, BarY + 10, 100, 18);
            Words(timeRight!, np.Duration > 0 ? RecordArt.Clock(np.Duration) : "", 12, BarX + BarW - 100, BarY + 10, 100, 18);
            Put(prev!, PlayX - 90 - 24, PlayY - 24, 48, 48);
            Put(next!, PlayX + 90 - 24, PlayY - 24, 48, 48);
            Unity.Call(play!.Target, "set_texture", (np.Playing ? pauseTex : playTex)!.Target);
            Put(play, PlayX - 24, PlayY - 24, 48, 48);
            Words(seekBack!, "-10s", 15, 30, 606, 70, 26);
            Words(seekForward!, "+10s", 15, W - 100, 606, 70, 26);
            Words(shuffle!, "SHUFFLE", 13, 110, 608, 86, 22);
            Words(repeat!, np.Repeat == 1 ? "REPEAT 1" : "REPEAT", 13, 204, 608, 86, 22);
            Ui.Colour(shuffle!, np.Shuffle == true ? accent : Grey);
            Ui.Colour(repeat!, np.Repeat is 1 or 2 ? accent : Grey);
        }
        if (!now)
        {
            Words(heading!, "Recently played", 20, 24, 64, W - 48, 30);
            var songs = history.Songs;
            var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (var i = 0; i < Rows && i < songs.Count; i++)
            {
                var y = 104 + i * 54;
                Words(rows[i].Song, songs[i].Title, 15, 24, y, W - 48, 24);
                Words(rows[i].Detail, $"{songs[i].Artist} • {History.Ago(unix - songs[i].At)}", 12, 24, y + 24, W - 48, 20);
            }
            if (songs.Count == 0) Words(rows[0].Song, "Nothing yet", 15, 24, 104, W - 48, 24);
            Words(stats!, $"This session: {history.SessionSongs} songs • {(int)(history.SessionSeconds / 60)} min", 13, 20, 618, W - 40, 22);
        }
        Words(tabNow!, "Now playing", 15, 0, 672, W / 2, 30);
        Words(tabHistory!, "History", 15, W / 2, 672, W / 2, 30);
        Ui.Colour(tabNow!, now ? White : Dim);
        Ui.Colour(tabHistory!, now ? Dim : White);
        Ui.Active(root, true);
    }

    /// <summary>
    /// A click at <paramref name="mouse"/> (screen pixels from the bottom left): switches tabs or closes
    /// itself, or says what to tell the player. <paramref name="handled"/> is whether the app took it.
    /// </summary>
    public (Command Command, double Value)? Click(Vector3 mouse, NowPlaying? np, out bool handled)
    {
        handled = false;
        if (!Open || scale <= 0) return null;
        float x = (mouse.X - left) / scale, y = H - (mouse.Y - bottom) / scale;
        if (x < 0 || y < 0 || x > W || y > H) return null;
        handled = true;
        bool In(float x0, float y0, float x1, float y1) => x >= x0 && x <= x1 && y >= y0 && y <= y1;
        if (In(0, 0, 150, 56)) { Close(); return null; }
        if (In(0, 656, W / 2, H)) { tab = 0; return null; }
        if (In(W / 2, 656, W, H)) { tab = 1; return null; }
        if (tab != 0 || np is null) return null;
        if (In(BarX - 6, BarY - 14, BarX + BarW + 6, BarY + 20) && np.Duration > 0)
            return (Command.SeekTo, Math.Clamp((x - BarX) / BarW, 0, 1) * np.Duration);
        if (In(PlayX - 34, PlayY - 34, PlayX + 34, PlayY + 34)) return (Command.PlayPause, 0);
        if (In(PlayX - 124, PlayY - 30, PlayX - 56, PlayY + 30)) return (Command.Previous, 0);
        if (In(PlayX + 56, PlayY - 30, PlayX + 124, PlayY + 30)) return (Command.Next, 0);
        if (In(24, 596, 106, 642)) return (Command.SeekBy, -10);
        if (In(W - 106, 596, W - 24, 642)) return (Command.SeekBy, 10);
        if (In(108, 596, 198, 642)) return (Command.Shuffle, 0);
        if (In(202, 596, 292, 642)) return (Command.Repeat, 0);
        return null;
    }

    void Free()
    {
        var all = new[] { backText, brand, title, artist, album, timeLeft, timeRight, seekBack, shuffle, repeat, seekForward, tabNow, tabHistory, heading, stats, nothing }
            .Concat(rows.SelectMany(r => new GameRef?[] { r.Song, r.Detail })).ToList();
        Ui.Forget(all);
        foreach (var t in new[] { backTex, prevTex, playTex, pauseTex, nextTex })
            if (t is not null && !Unity.IsNull(t.Target)) Unity.Destroy(t.Target);
        var held = all.Concat(new[] { root, back, cover, barBack, barFill, prev, play, next, backTex, prevTex, playTex, pauseTex, nextTex }).ToList();
        Cleanup.Run(held.Select(g => (Action)(() => g?.Dispose())).Append(() => canvas?.Dispose()).ToArray());
        rows.Clear();
        canvas = null;
        root = back = cover = barBack = barFill = prev = play = next = null;
        backText = brand = title = artist = album = timeLeft = timeRight = seekBack = shuffle = repeat = seekForward = tabNow = tabHistory = heading = stats = nothing = null;
        backTex = prevTex = playTex = pauseTex = nextTex = null;
        drawnScale = 0;
    }

    public void Dispose()
    {
        try { Close(); } catch (Exception e) when (e is Il2CppException or InvalidOperationException) { }
        Free();
    }
}
