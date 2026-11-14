using Spotify;
using Spotify.Integration;

static class SpotifyTests
{
    [Test]
    static void TheVinylIsRoundWithClearCorners()
    {
        var v = RecordArt.Vinyl(120);
        Assert.True(v.AlphaAt(60, 60) > 0.99f);
        Assert.True(v.AlphaAt(1, 1) < 0.01f);
    }

    [Test]
    static void ThePanelHasItsButtonsWhereClicksLookForThem()
    {
        var p = RecordArt.Panel(playing: true, scale: 1);
        Assert.Equal(RecordArt.Width, p.W);
        foreach (var (_, x, y, _, _) in RecordArt.Buttons())
            Assert.True(p.AlphaAt((int)x, (int)y) > 0.5f, $"button at {x},{y}");
    }

    [Test]
    static void AlbumArtIsCutToARoundLabelWithAHole()
    {
        const int size = 64;
        var rgba = Enumerable.Repeat((byte)200, size * size * 4).ToArray();
        RecordArt.RoundLabel(rgba, size);
        Assert.Equal((byte)0, rgba[3]); // corner see-through
        var middle = (size / 2 * size + size / 2) * 4;
        Assert.Equal((byte)20, rgba[middle]); // spindle hole
        var ring = (size / 2 * size + size / 2 + 20) * 4;
        Assert.Equal((byte)200, rgba[ring + 3]);
    }

    [Test]
    static void ClockReadsMinutesAndSeconds()
    {
        Assert.Equal("0:00", RecordArt.Clock(-3));
        Assert.Equal("3:07", RecordArt.Clock(187.9));
    }

    [Test]
    static void HistoryKeepsNewestFirstWithoutRepeats()
    {
        var file = Path.Combine(Path.GetTempPath(), "spotify_history_test.json");
        File.Delete(file);
        var h = new History(file);
        Assert.True(h.Add("One", "A", 100));
        Assert.True(!h.Add("One", "A", 130)); // still the same song
        Assert.True(h.Add("Two", "B", 200));
        Assert.Equal("Two", h.Songs[0].Title);
        Assert.Equal(2, h.SessionSongs);
        for (var i = 0; i < 60; i++) h.Add($"Song {i}", "C", 300 + i);
        Assert.Equal(History.Max, h.Songs.Count);
        // Kept between sessions.
        var again = new History(file);
        Assert.Equal("Song 59", again.Songs[0].Title);
        Assert.Equal(0, again.SessionSongs);
        File.Delete(file);
    }

    [Test]
    static void TimeAgoReadsNaturally()
    {
        Assert.Equal("now", History.Ago(20));
        Assert.Equal("5 min ago", History.Ago(320));
        Assert.Equal("3 h ago", History.Ago(3 * 3600 + 5));
        Assert.Equal("2 d ago", History.Ago(2 * 86400 + 5));
    }

    [Test]
    static void AccentComesFromTheArtsColourAndFallsBackToGreen()
    {
        byte[] Fill(byte r, byte g, byte b) => Enumerable.Range(0, 64 * 64).SelectMany(_ => new byte[] { r, g, b, 255 }).ToArray();
        var red = ArtColour.Accent(Fill(160, 20, 20));
        Assert.True(red.R > 0.8f && red.G < 0.3f, $"{red}");
        var grey = ArtColour.Accent(Fill(90, 90, 90));
        Assert.True(grey.G > 0.8f && grey.R < 0.2f, $"{grey}"); // Spotify green
    }

    [Test]
    static void TheAppHasItsIconsAndBackground()
    {
        var bg = RecordArt.AppBackground(400, 720, 1, 200, 546, 30);
        Assert.True(bg.AlphaAt(200, 360) > 0.99f && bg.AlphaAt(0, 0) < 0.01f);
        foreach (var c in new[] { Command.Previous, Command.PlayPause, Command.Next })
            Assert.True(RecordArt.IconPicture(c, false, 48, dark: false).AlphaAt(24, 24) > 0.5f, $"{c}");
    }

    /// <summary>Asks Windows what's playing on this PC (passes either way; prints what it found).</summary>
    [Test]
    static void WindowsMediaControlsAnswer()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? said = null;
        using var media = new MediaSession(m => said = m, () => false);
        for (var i = 0; i < 30 && media.Now is null && said is null; i++) Thread.Sleep(100);
        Assert.True(said is null, said ?? "");
        Console.WriteLine(media.Now is { } n
            ? $"      now playing: {n.Title} by {n.Artist} ({(n.Playing ? "playing" : "paused")}, {RecordArt.Clock(n.PositionNow)} / {RecordArt.Clock(n.Duration)}, art {n.Art?.Length ?? 0} bytes, shuffle {n.Shuffle?.ToString() ?? "?"}, repeat {n.Repeat?.ToString() ?? "?"})"
            : "      nothing playing");
    }
}
