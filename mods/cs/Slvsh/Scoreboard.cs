using Jibbers.Sdk;
using Jibbers.Sdk.UI;
using Slvsh.Integration;

namespace Slvsh;

/// <summary>Everything the SLVSH overlay shows.</summary>
internal sealed record SlvshView
{
    public ulong Me { get; init; }
    public GameState? Game { get; init; }
    /// <summary>A game someone in the party started that you can join.</summary>
    public (string Host, int Players)? Invite { get; init; }
    /// <summary>Your trick so far, while it's your attempt.</summary>
    public string? Trying { get; init; }
    /// <summary>A short line when there's nothing else to show ("Make a party first").</summary>
    public string Hint { get; init; } = "";
    /// <summary>Whether your camera is following the player taking their turn.</summary>
    public SpectateStatus Watching { get; init; }
    public string GoKey { get; init; } = "Ctrl+Enter";
    public string LeaveKey { get; init; } = "Ctrl+Q";
    public string YesKey { get; init; } = "Y";
    public string NoKey { get; init; } = "N";
}

/// <summary>
/// The game's own HUD look (the spawn point box, the trick buttons, the speedometer): flat
/// translucent grey panels with square corners, small tracked grey captions over white values,
/// lighter inset chips, and mint as the one accent.
/// </summary>
internal static class Look
{
    static Color Rgb(int r, int g, int b, float a = 1) => new(r / 255f, g / 255f, b / 255f, a);
    public static readonly Color Panel = Rgb(30, 30, 33, 0.78f);
    public static readonly Color Inset = Rgb(255, 255, 255, 0.11f);
    public static readonly Color InsetLit = Rgb(255, 255, 255, 0.2f);
    public static readonly Color Text = Rgb(242, 242, 242);
    public static readonly Color Caption = Rgb(168, 168, 172);
    public static readonly Color Dim = Rgb(112, 112, 118);
    public static readonly Color Mint = Rgb(142, 226, 206);
    public static readonly Color Good = Rgb(98, 222, 142);
    public static readonly Color Bad = Rgb(228, 88, 74);
    public static readonly Color Gold = Rgb(236, 198, 96);
    public const float Corner = 3, ChipCorner = 2;
    /// <summary>Caption tracking, like the HUD's "SPAWNPOINT".</summary>
    public const float Tracked = 10;
}

/// <summary>Draws the SLVSH overlay. Pure, so it is tested and previewed outside the game.</summary>
internal static class Scoreboard
{
    public const float Width = 380, Margin = 8;
    const float Pad = 16, Row = 32, VoteBlock = 76, Tile = 20, TileGap = 4;

    public static OverlayFrame? Build(SlvshView v)
    {
        if (v.Game is { } g) return Game(v, g);
        if (v.Invite is { } invite) return Invite(v, invite);
        if (v.Hint.Length > 0) return Hint(v);
        return null;
    }

    static OverlayFrame Game(SlvshView v, GameState g)
    {
        var mine = g.Attempter == v.Me && g.Phase is Phase.Set or Phase.Match;
        var voting = g.Phase == Phase.Vote;
        var canVote = voting && g.Voters.Contains(v.Me);
        var players = g.Phase == Phase.Over ? g.Players.OrderBy(p => p.Letters).ToList() : g.Players.ToList();
        var height = 104f + (voting ? VoteBlock : 0) + 18 + players.Count * Row + 8 + (g.News.Length > 0 ? 24 : 0) + 38;
        var f = new OverlayFrame(Width + Margin * 2, height + Margin * 2).Origin(Margin, Margin);
        f.Rect(0, 0, Width, height, Look.Corner, Look.Panel);

        // Caption row: the mod and the phase, and where the camera is.
        f.Text("SLVSH", Pad, 12, 60, 14, 11, Look.Text, bold: true, spacing: Look.Tracked);
        f.Text(PhaseLabel(g, mine, canVote), Pad + 58, 12, 150, 14, 11, mine || canVote ? Look.Mint : Look.Caption, spacing: Look.Tracked);
        if (v.Watching != SpectateStatus.Off)
        {
            var chip = v.Watching == SpectateStatus.Watching ? $"WATCHING {First(g.NameOf(g.Attempter)).ToUpperInvariant()}" : "NOT ON YOUR SERVER";
            var chipW = Math.Min(170, 16 + chip.Length * 7.2f);
            f.Rect(Width - Pad - chipW, 9, chipW, 20, Look.ChipCorner, Look.Inset);
            f.Text(chip, Width - Pad - chipW, 12, chipW, 14, 10, v.Watching == SpectateStatus.Watching ? Look.Mint : Look.Dim, HudTextAlignment.Center, spacing: 6);
        }

        // What to do now, with the countdown drawn like the speedometer.
        var (title, sub, titleColor) = Headline(v, g, mine);
        var (left, total) = voting ? (g.VoteSecondsLeft, g.VoteSecondsTotal) : (g.SecondsLeft, g.SecondsTotal);
        var timed = g.Phase is Phase.Set or Phase.Match or Phase.Vote && total > 0;
        var textW = Width - Pad * 2 - (timed ? 96 : 0);
        f.Text(title, Pad, 36, textW, 30, title.Length > 24 ? 19 : 23, titleColor);
        f.Text(v.Trying is { } t && mine ? $"Your trick: {t}" : sub, Pad, 66, textW, 18, 13, v.Trying is not null && mine ? Look.Mint : Look.Caption);
        if (timed)
        {
            var timeColor = voting ? Look.Text : left <= 5 ? Look.Bad : left <= 10 ? Look.Gold : Look.Text;
            f.Text(left.ToString(), Width - Pad - 96, 30, 64, 48, 42, timeColor, HudTextAlignment.Right);
            f.Text("SEC", Width - Pad - 28, 54, 28, 16, 11, Look.Caption, spacing: Look.Tracked);
            var share = Math.Clamp(left / (float)total, 0, 1);
            f.Rect(Pad, 92, Width - Pad * 2, 2, 0, Look.Inset);
            if (share > 0) f.Rect(Pad, 92, (Width - Pad * 2) * share, 2, 0, timeColor == Look.Text ? Look.Mint : timeColor);
        }
        var y = 104f;

        if (voting)
        {
            Vote(f, v, g, y, canVote);
            y += VoteBlock;
        }

        // Players with their letters.
        f.Text("PLAYERS", Pad, y, 120, 14, 10, Look.Caption, spacing: Look.Tracked);
        y += 18;
        var tilesX = Width - Pad - GameState.Word.Length * (Tile + TileGap) + TileGap;
        foreach (var p in players)
        {
            var turn = g.Watching(p.Id);
            var outOfGame = g.Out(p);
            if (turn)
            {
                f.Rect(Pad - 8, y, Width - Pad * 2 + 16, Row - 2, Look.ChipCorner, Look.Inset);
                f.Rect(Pad - 8, y, 3, Row - 2, 0, Look.Mint);
            }
            f.Text(p.Name, Pad, y + 6, tilesX - Pad - 96, 20, 15, outOfGame ? Look.Dim : Look.Text, bold: p.Id == v.Me);
            var tag = p.Id == v.Me ? (Tag(g, p, turn, outOfGame) is { Length: > 0 } s ? s : "YOU") : Tag(g, p, turn, outOfGame);
            if (tag.Length > 0)
                f.Text(tag, tilesX - 106, y + 9, 96, 14, 10, tag switch { "WINNER" => Look.Gold, "OUT" => Look.Bad, "VOTED" => Look.Good, "SETTING" or "TRYING" => Look.Mint, _ => Look.Caption }, HudTextAlignment.Right, spacing: Look.Tracked);
            Tiles(f, p.Letters, tilesX, y + 4, outOfGame);
            y += Row;
        }
        y += 8;

        if (g.News.Length > 0)
        {
            f.Text(g.News, Pad, y, Width - Pad * 2, 18, 13, Look.Text.WithAlpha(0.85f));
            y += 24;
        }

        var hints = g.Phase switch
        {
            Phase.Lobby when g.Host == v.Me => new[] { (v.GoKey, g.Players.Count >= 2 ? "Begin" : "Begin (2+ players)"), (v.LeaveKey, "End") },
            Phase.Vote when canVote => new[] { (v.YesKey, "Yes"), (v.NoKey, "No"), (v.LeaveKey, "Leave") },
            Phase.Over => new[] { (v.GoKey, "New game"), (v.LeaveKey, "Close") },
            _ => new[] { (v.LeaveKey, g.Host == v.Me ? "End game" : "Leave") },
        };
        Hints(f, height - 32, hints);
        return f;
    }

    static string PhaseLabel(GameState g, bool mine, bool canVote) => g.Phase switch
    {
        Phase.Lobby => "LOBBY",
        Phase.Set => mine ? "YOUR SET" : "SET",
        Phase.Match => mine ? "YOUR TURN" : "MATCH",
        Phase.Vote => canVote ? "YOUR VOTE" : "VOTING",
        _ => "GAME OVER",
    };

    static string Tag(GameState g, Player p, bool turn, bool outOfGame)
    {
        if (p.Id == g.Winner && g.Phase == Phase.Over) return "WINNER";
        if (outOfGame) return "OUT";
        if (g.Phase == Phase.Vote)
        {
            if (turn) return "ON TRIAL";
            if (g.Voters.Contains(p.Id)) return g.BallotOf(p.Id) is null ? "VOTING" : "VOTED";
        }
        if (turn) return g.Phase == Phase.Set ? "SETTING" : "TRYING";
        return p.Id == g.Setter && g.Phase == Phase.Match ? "SETTER" : "";
    }

    /// <summary>Two choices with live counts; yours is lit. What trick detection saw is marked, since it breaks ties.</summary>
    static void Vote(OverlayFrame f, SlvshView v, GameState g, float y, bool canVote)
    {
        var mineYes = g.BallotOf(v.Me);
        var yes = g.Ballots.Count(b => b.Yes);
        var no = g.Ballots.Count - yes;
        const float gap = 8;
        var w = (Width - Pad * 2 - gap) / 2;
        Choice(f, Pad, y, w, canVote ? v.YesKey : "", "COUNTED", yes, Look.Good, mineYes == true, g.Detected);
        Choice(f, Pad + w + gap, y, w, canVote ? v.NoKey : "", "DIDN'T", no, Look.Bad, mineYes == false, !g.Detected);
        var waiting = g.Voters.Count - g.Ballots.Count;
        var note = !canVote ? $"{g.Voters.Count} watching players decide"
            : mineYes is not null ? "Voted. You can change it until time's up"
            : v.Watching == SpectateStatus.NotHere ? "Couldn't watch? Go with the game's guess" : "You watched it: your call";
        f.Text(note, Pad, y + 52, Width - Pad * 2 - 100, 16, 11, Look.Caption);
        f.Text(waiting == 0 ? "everyone voted" : $"{waiting} still to vote", Width - Pad - 100, y + 52, 100, 16, 11, Look.Caption, HudTextAlignment.Right);
    }

    static void Choice(OverlayFrame f, float x, float y, float w, string key, string label, int count, Color color, bool chosen, bool detected)
    {
        const float h = 44;
        f.Rect(x, y, w, h, Look.ChipCorner, chosen ? Look.InsetLit : Look.Inset);
        if (chosen) f.Rect(x, y + h - 2, w, 2, 0, color);
        var tx = x + 10;
        if (key.Length > 0)
        {
            var kw = KeyWidth(key);
            Key(f, key, tx, y + 11);
            tx += kw + 8;
        }
        f.Text(label, tx, y + (detected ? 7 : 14), w - (tx - x) - 28, 16, 12, chosen ? Look.Text : Look.Caption, bold: true, spacing: 6);
        if (detected) f.Text("game's guess", tx, y + 24, w - (tx - x) - 28, 14, 10, Look.Dim);
        f.Text(count.ToString(), x + w - 30, y + 6, 22, 32, 26, count > 0 || chosen ? color : Look.Dim, HudTextAlignment.Right);
    }

    static (string Title, string Sub, Color Color) Headline(SlvshView v, GameState g, bool mine)
    {
        var who = g.NameOf(g.Attempter);
        var rule = g.Rule switch { MatchRules.Spins => "Same spins count, any grab", MatchRules.Any => "Any landed trick counts", _ => "Land exactly this trick" };
        var watch = v.Watching == SpectateStatus.Watching ? $"Watching {First(who)}" : rule;
        var (title, sub) = g.Phase switch
        {
            Phase.Lobby when g.Host == v.Me => (g.Players.Count >= 2 ? "Ready when you are" : "Waiting for players", $"Party members with SLVSH press {v.GoKey} to join"),
            Phase.Lobby => ($"{g.NameOf(g.Host)}'s game", $"{First(g.NameOf(g.Host))} starts when everyone's in"),
            Phase.Set when mine => ("Land any trick", "Everyone's watching, then they vote on it"),
            Phase.Set => ($"{who} is setting", v.Watching == SpectateStatus.Watching ? "The trick they land is the one to match" : "Watch for the trick to match"),
            Phase.Match when mine => (g.Trick, rule),
            Phase.Match => (g.Trick, $"{First(who)} is trying · {watch}"),
            Phase.Vote when g.Attempter == v.Me => (g.Tried.Length > 0 ? g.Tried : "No trick seen", g.VotingOn == Phase.Set ? "The others are voting on your set" : "The others are voting on your match"),
            Phase.Vote => (g.Tried.Length > 0 ? g.Tried : "No trick seen",
                g.VotingOn == Phase.Set ? $"Did {First(who)} land it?" : $"Did {First(who)} match {g.Trick}?"),
            _ => (g.Winner == v.Me ? "You win" : $"{g.NameOf(g.Winner)} wins", g.Winner == v.Me ? "Nobody could match you" : "Better luck next game"),
        };
        var color = g.Phase == Phase.Over ? Look.Gold : mine && g.Phase == Phase.Match ? Look.Mint : Look.Text;
        return (title, sub, color);
    }

    static OverlayFrame Invite(SlvshView v, (string Host, int Players) invite)
    {
        const float w = 340, h = 108;
        var f = new OverlayFrame(w + Margin * 2, h + Margin * 2).Origin(Margin, Margin);
        f.Rect(0, 0, w, h, Look.Corner, Look.Panel);
        f.Text("SLVSH", Pad, 12, 60, 14, 11, Look.Text, bold: true, spacing: Look.Tracked);
        f.Text("INVITE", Pad + 58, 12, 120, 14, 11, Look.Mint, spacing: Look.Tracked);
        f.Text($"{invite.Host} started a game", Pad, 32, w - Pad * 2, 26, 19, Look.Text);
        f.Text(invite.Players == 1 ? "Just them so far" : $"{invite.Players} players in", Pad, 56, w - Pad * 2, 18, 13, Look.Caption);
        Hints(f, h - 32, new[] { (v.GoKey, "Join") });
        return f;
    }

    static OverlayFrame Hint(SlvshView v)
    {
        const float w = 340, h = 82;
        var f = new OverlayFrame(w + Margin * 2, h + Margin * 2).Origin(Margin, Margin);
        f.Rect(0, 0, w, h, Look.Corner, Look.Panel);
        f.Text("SLVSH", Pad, 12, 60, 14, 11, Look.Text, bold: true, spacing: Look.Tracked);
        f.Text(v.Hint, Pad, 32, w - Pad * 2, 40, 14, Look.Text.WithAlpha(0.9f), wrap: true);
        return f;
    }

    /// <summary>One square per letter, like the HUD's number keys: earned letters turn red.</summary>
    static void Tiles(OverlayFrame f, int earned, float x, float y, bool dim)
    {
        for (var i = 0; i < GameState.Word.Length; i++)
        {
            var tx = x + i * (Tile + TileGap);
            var got = i < earned;
            f.Rect(tx, y, Tile, 22, Look.ChipCorner, got ? Look.Bad.WithAlpha(dim ? 0.45f : 0.9f) : Look.Inset);
            f.Text(GameState.Word[i].ToString(), tx, y + 3, Tile, 16, 12, got ? Look.Text : Look.Dim, HudTextAlignment.Center, bold: true);
        }
    }

    static float KeyWidth(string key) => Math.Max(22, 10 + key.Length * (key.Length > 3 ? 6.4f : 8f));

    /// <summary>A key cap like the HUD's "1 2 3 4" (combos like "Ctrl+Y" in a smaller size).</summary>
    static void Key(OverlayFrame f, string key, float x, float y)
    {
        var kw = KeyWidth(key);
        f.Rect(x, y, kw, 22, Look.ChipCorner, Look.InsetLit);
        f.Text(key, x, y + (key.Length > 3 ? 5 : 4), kw, 14, key.Length > 3 ? 10 : 11, Look.Text, HudTextAlignment.Center, bold: true);
    }

    /// <summary>Key caps with their action in tracked capitals, along the bottom edge.</summary>
    static void Hints(OverlayFrame f, float y, (string Key, string What)[] hints)
    {
        static float WhatWidth(string what) => 10 + what.Length * 8.2f;
        var x = Pad;
        foreach (var (key, what) in hints)
        {
            Key(f, key, x, y);
            var kw = KeyWidth(key);
            f.Text(what.ToUpperInvariant(), x + kw + 7, y + 4, WhatWidth(what), 14, 11, Look.Caption, spacing: Look.Tracked);
            x += kw + 7 + WhatWidth(what) + 12;
        }
    }

    static string First(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? parts[0] : name;
}
