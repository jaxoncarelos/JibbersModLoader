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
    public string GoKey { get; init; } = "F9";
    public string LeaveKey { get; init; } = "F10";
    public string YesKey { get; init; } = "F9";
    public string NoKey { get; init; } = "F8";
}

/// <summary>Draws the SLVSH overlay. Pure, so it is tested and previewed outside the game.</summary>
internal static class Scoreboard
{
    public const float Width = 520, Margin = 24;
    const float Pad = 26, Row = 50, VoteBlock = 92;

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
        var accent = g.Phase switch { Phase.Over => Theme.Warn, Phase.Lobby => Theme.Violet, Phase.Vote => Theme.Violet, _ => mine ? Theme.Ice : Theme.Muted };
        var players = g.Phase == Phase.Over ? g.Players.OrderBy(p => p.Letters).ToList() : g.Players.ToList();
        var height = 96f + 104 + (voting ? VoteBlock : 0) + 22 + players.Count * Row + 12 + (g.News.Length > 0 ? 40 : 0) + 44;
        var f = new OverlayFrame(Width + Margin * 2, height + Margin * 2).Origin(Margin, Margin);
        f.Card(0, 0, Width, height, 22);
        f.Gradient(22, 0, Width - 44, 3, 1.5f, accent, accent == Theme.Ice || accent == Theme.Violet ? (accent == Theme.Ice ? Theme.Violet : Theme.Ice) : accent.WithAlpha(0.4f), horizontal: true);

        // Header: the word as tiles, what the camera is doing, and the phase.
        Wordmark(f, Pad, 24, 30);
        var (pill, pillColor) = g.Phase switch
        {
            Phase.Lobby => ("LOBBY", Theme.Violet),
            Phase.Set => (mine ? "YOUR SET" : "SETTING", mine ? Theme.Ice : Theme.Muted),
            Phase.Match => (mine ? "YOUR TURN" : "MATCHING", mine ? Theme.Ice : Theme.Muted),
            Phase.Vote => (canVote ? "VOTE" : "VOTING", Theme.Violet),
            _ => ("GAME OVER", Theme.Warn),
        };
        var pillW = 22 + pill.Length * 9.5f;
        var right = Width - Pad - pillW;
        f.Rect(right, 27, pillW, 26, 13, pillColor.WithAlpha(0.15f));
        f.Text(pill, right, 30, pillW, 20, 12, pillColor, HudTextAlignment.Center, bold: true, spacing: 4);
        if (v.Watching != SpectateStatus.Off)
        {
            var who = First(g.NameOf(g.Attempter)).ToUpperInvariant();
            var (chip, chipColor) = v.Watching == SpectateStatus.Watching ? ($"WATCHING {who}", Theme.Good) : ("NOT ON YOUR SERVER", Theme.Faint);
            var chipW = Math.Min(200, 34 + chip.Length * 7.6f);
            var x = right - 10 - chipW;
            f.Rect(x, 27, chipW, 26, 13, chipColor.WithAlpha(0.12f));
            f.Circle(x + 14, 40, 4, chipColor);
            f.Text(chip, x + 24, 31, chipW - 30, 18, 11, chipColor, bold: true, spacing: 2);
        }
        f.Rect(Pad, 78, Width - Pad * 2, 1, 0, Theme.Hairline);

        // The big line: what to do now, with the countdown ring.
        var y = 96f;
        var (label, title, sub, titleColor) = Headline(v, g, mine);
        var (left, total) = voting ? (g.VoteSecondsLeft, g.VoteSecondsTotal) : (g.SecondsLeft, g.SecondsTotal);
        var ring = (g.Phase is Phase.Set or Phase.Match or Phase.Vote) && total > 0;
        var textW = Width - Pad * 2 - (ring ? 92 : 0);
        f.Text(label, Pad, y, textW, 18, 12, mine || canVote ? (voting ? Theme.Violet : Theme.Ice) : Theme.Faint, bold: true, spacing: 4);
        f.Text(title, Pad, y + 20, textW, 40, title.Length > 22 ? 25 : 32, titleColor, bold: true);
        f.Text(v.Trying is { } t && mine ? $"Your trick: {t}" : sub, Pad, y + 62, textW, 20, 14, v.Trying is not null && mine ? Theme.Ice : Theme.Muted);
        if (ring)
        {
            var (cx, cy) = (Width - Pad - 36, y + 40);
            var share = Math.Clamp(left / (float)total, 0, 1);
            var color = voting ? Theme.Violet : left <= 5 ? Theme.Bad : left <= 10 ? Theme.Warn : Theme.Ice;
            f.Arc(cx, cy, 32, 6, 0, 360, Theme.Raised);
            f.Arc(cx, cy, 32, 6, 0, 360 * share, color);
            f.Text(left.ToString(), cx - 30, cy - 15, 60, 30, 24, Theme.Text, HudTextAlignment.Center, bold: true);
        }
        y += 104;

        if (voting)
        {
            Vote(f, v, g, y, canVote);
            y += VoteBlock;
        }

        // Players with their letters.
        f.Text("PLAYERS", Pad, y, 200, 16, 11, Theme.Faint, bold: true, spacing: 4);
        y += 22;
        foreach (var p in players)
        {
            var turn = g.Watching(p.Id);
            var outOfGame = g.Out(p);
            if (turn) f.Rect(12, y + 2, Width - 24, Row - 4, 14, (voting ? Theme.Violet : Theme.Ice).WithAlpha(0.11f));
            var color = Theme.ForId(p.Id);
            f.Circle(Pad + 16, y + Row / 2, 16, outOfGame ? color.WithAlpha(0.35f) : color);
            f.Text(Initials(p.Name), Pad, y + Row / 2 - 11, 32, 22, 13, Theme.CardBottom with { A = 1 }, HudTextAlignment.Center, bold: true);
            f.Text(p.Id == v.Me ? $"{p.Name} (you)" : p.Name, Pad + 44, y + 8, 200, 20, 17, outOfGame ? Theme.Faint : Theme.Text, bold: p.Id == v.Me);
            var tag = Tag(g, p, turn, outOfGame);
            if (tag.Length > 0)
                f.Text(tag, Pad + 44, y + 28, 200, 14, 10, tag switch { "WINNER" => Theme.Warn, "OUT" => Theme.Bad.WithAlpha(0.8f), "VOTED" => Theme.Good, "VOTING" => Theme.Violet, _ => Theme.Ice }, bold: true, spacing: 3);
            Tiles(f, p.Letters, Width - Pad - GameState.Word.Length * 34 + 4, y + 9, outOfGame);
            y += Row;
        }
        y += 12;

        if (g.News.Length > 0)
        {
            f.Rect(Pad, y, Width - Pad * 2, 30, 10, Theme.Raised);
            f.Circle(Pad + 15, y + 15, 4, accent);
            f.Text(g.News, Pad + 28, y + 5, Width - Pad * 2 - 38, 20, 14, Theme.Text.WithAlpha(0.9f));
        }

        f.Rect(0, height - 42, Width, 1, 0, Theme.Hairline);
        var hints = g.Phase switch
        {
            Phase.Lobby when g.Host == v.Me => new[] { (v.GoKey, g.Players.Count >= 2 ? "begin" : "begin (2+ players)"), (v.LeaveKey, "end") },
            Phase.Vote when canVote => new[] { (v.YesKey, "it counted"), (v.NoKey, "it didn't"), (v.LeaveKey, "leave") },
            Phase.Over => new[] { (v.GoKey, "new game"), (v.LeaveKey, "close") },
            _ => new[] { (v.LeaveKey, g.Host == v.Me ? "end game" : "leave") },
        };
        Hints(f, height - 32, hints);
        return f;
    }

    static string Tag(GameState g, Player p, bool turn, bool outOfGame)
    {
        if (p.Id == g.Winner && g.Phase == Phase.Over) return "WINNER";
        if (outOfGame) return "OUT";
        if (g.Phase == Phase.Vote)
        {
            if (turn) return "BEING VOTED ON";
            if (g.Voters.Contains(p.Id)) return g.BallotOf(p.Id) is null ? "VOTING" : "VOTED";
        }
        if (turn) return g.Phase == Phase.Set ? "SETTING" : "TRYING";
        return p.Id == g.Setter && g.Phase == Phase.Match ? "SETTER" : "";
    }

    /// <summary>Two big choices with live counts; yours is lit. What trick detection saw is marked, since it breaks ties.</summary>
    static void Vote(OverlayFrame f, SlvshView v, GameState g, float y, bool canVote)
    {
        var mineYes = g.BallotOf(v.Me);
        var yes = g.Ballots.Count(b => b.Yes);
        var no = g.Ballots.Count - yes;
        const float gap = 12;
        var w = (Width - Pad * 2 - gap) / 2;
        Choice(f, Pad, y, w, canVote ? v.YesKey : "", "IT COUNTED", yes, Theme.Good, mineYes == true, g.Detected);
        Choice(f, Pad + w + gap, y, w, canVote ? v.NoKey : "", "IT DIDN'T", no, Theme.Bad, mineYes == false, !g.Detected);
        var waiting = g.Voters.Count - g.Ballots.Count;
        var note = !canVote ? $"{g.Voters.Count} watching players decide"
            : mineYes is not null ? "Voted. You can change it until time's up"
            : v.Watching == SpectateStatus.NotHere ? "You couldn't watch: go with the others or the game's guess" : "You watched it: your call";
        f.Text($"{note} · {(waiting == 0 ? "everyone voted" : $"{waiting} still to vote")}", Pad, y + 66, Width - Pad * 2, 18, 12, Theme.Muted, HudTextAlignment.Center);
    }

    static void Choice(OverlayFrame f, float x, float y, float w, string key, string label, int count, Color color, bool chosen, bool detected)
    {
        const float h = 56;
        f.Rect(x, y, w, h, 14, chosen ? color.WithAlpha(0.22f) : Theme.Raised);
        f.Border(x, y, w, h, 14, chosen ? 2 : 1, chosen ? color : Theme.Hairline);
        var tx = x + 14;
        if (key.Length > 0)
        {
            var kw = 14 + key.Length * 7.5f;
            f.Rect(tx, y + 18, kw, 20, 6, Theme.Raised);
            f.Border(tx, y + 18, kw, 20, 6, 1, Theme.Hairline);
            f.Text(key, tx, y + 19, kw, 18, 11, Theme.Muted, HudTextAlignment.Center, bold: true);
            tx += kw + 10;
        }
        f.Text(label, tx, y + 10, w - (tx - x) - 44, 20, 14, chosen ? Theme.Text : color, bold: true, spacing: 2);
        f.Text(detected ? "game's guess" : "", tx, y + 30, w - (tx - x) - 44, 16, 11, Theme.Faint);
        f.Text(count.ToString(), x + w - 44, y + 12, 32, 32, 26, chosen ? Theme.Text : color, HudTextAlignment.Right, bold: true);
    }

    static (string Label, string Title, string Sub, Color Color) Headline(SlvshView v, GameState g, bool mine)
    {
        var who = g.NameOf(g.Attempter);
        var rule = g.Rule switch { MatchRules.Spins => "Same spins count, any grab", MatchRules.Any => "Any landed trick counts", _ => "Land exactly this trick" };
        var watch = v.Watching == SpectateStatus.Watching ? "Your camera is on them" : rule;
        var (label, title, sub) = g.Phase switch
        {
            Phase.Lobby when g.Host == v.Me => ("YOUR GAME", g.Players.Count >= 2 ? "Ready when you are" : "Waiting for players", $"Party members with SLVSH can press {v.GoKey} to join"),
            Phase.Lobby => ($"{g.NameOf(g.Host).ToUpperInvariant()}'S GAME", "Waiting to start", $"{g.NameOf(g.Host)} starts when everyone's in"),
            Phase.Set when mine => ("YOUR SET", "Land any trick", "Everyone's watching. Then they vote on it"),
            Phase.Set => ($"{who.ToUpperInvariant()} IS SETTING", "Watch the set", v.Watching == SpectateStatus.Watching ? "Your camera is on them" : "The trick they land is the one to match"),
            Phase.Match when mine => ("MATCH THIS", g.Trick, rule),
            Phase.Match => ($"{who.ToUpperInvariant()} IS TRYING", g.Trick, watch),
            Phase.Vote when g.Attempter == v.Me => ("THE OTHERS ARE VOTING", g.Tried.Length > 0 ? g.Tried : "No trick seen", g.VotingOn == Phase.Set ? "Did your set count?" : $"Did you match {g.Trick}?"),
            Phase.Vote => (g.VotingOn == Phase.Set ? $"DID {who.ToUpperInvariant()} LAND IT?" : $"DID {who.ToUpperInvariant()} MATCH IT?", g.Tried.Length > 0 ? g.Tried : "No trick seen",
                g.VotingOn == Phase.Set ? "If it counts, this is the trick to match" : $"The set was {g.Trick}"),
            _ => ("GAME OVER", g.Winner == v.Me ? "You win!" : $"{g.NameOf(g.Winner)} wins!", g.Winner == v.Me ? "Nobody could match you" : "Better luck next game"),
        };
        var color = g.Phase == Phase.Over ? Theme.Warn : mine && g.Phase == Phase.Match ? Theme.Ice : Theme.Text;
        return (label, title, sub, color);
    }

    static OverlayFrame Invite(SlvshView v, (string Host, int Players) invite)
    {
        const float w = 420, h = 132;
        var f = new OverlayFrame(w + Margin * 2, h + Margin * 2).Origin(Margin, Margin);
        f.Card(0, 0, w, h, 22);
        f.Gradient(22, 0, w - 44, 3, 1.5f, Theme.Violet, Theme.Ice, horizontal: true);
        Wordmark(f, 24, 22, 24);
        f.Text($"{invite.Host} started a game", 24, 56, w - 48, 26, 20, Theme.Text, bold: true);
        f.Text(invite.Players == 1 ? "Just them so far" : $"{invite.Players} players in", 24, 82, w - 48, 20, 14, Theme.Muted);
        Hints(f, h - 32, new[] { (v.GoKey, "join") }, w, right: true);
        return f;
    }

    static OverlayFrame Hint(SlvshView v)
    {
        const float w = 440, h = 86;
        var f = new OverlayFrame(w + Margin * 2, h + Margin * 2).Origin(Margin, Margin);
        f.Card(0, 0, w, h, 20);
        Wordmark(f, 22, 20, 20);
        f.Text(v.Hint, 22, 48, w - 44, 22, 15, Theme.Text.WithAlpha(0.9f));
        return f;
    }

    /// <summary>S-L-V-S-H as letter tiles, like a scoreboard.</summary>
    static void Wordmark(OverlayFrame f, float x, float y, float size)
    {
        for (var i = 0; i < GameState.Word.Length; i++)
        {
            var tx = x + i * (size + 5);
            f.Gradient(tx, y, size, size, size * 0.28f, Theme.Ice.WithAlpha(0.95f), Theme.Violet.WithAlpha(0.95f));
            f.Text(GameState.Word[i].ToString(), tx, y + 1, size, size - 2, size * 0.58f, Theme.CardBottom with { A = 1 }, HudTextAlignment.Center, bold: true);
        }
    }

    /// <summary>One tile per letter: earned letters glow red.</summary>
    static void Tiles(OverlayFrame f, int earned, float x, float y, bool dim)
    {
        for (var i = 0; i < GameState.Word.Length; i++)
        {
            var tx = x + i * 34;
            var got = i < earned;
            f.Rect(tx, y, 28, 32, 8, got ? Theme.Bad.WithAlpha(dim ? 0.45f : 0.9f) : Theme.Raised);
            if (!got) f.Border(tx, y, 28, 32, 8, 1, Theme.Hairline);
            f.Text(GameState.Word[i].ToString(), tx, y + 4, 28, 24, 17, got ? Theme.Text : Theme.Faint.WithAlpha(0.7f), HudTextAlignment.Center, bold: true);
        }
    }

    static void Hints(OverlayFrame f, float y, (string Key, string What)[] hints, float width = Width, bool right = false)
    {
        static float KeyWidth(string key) => 14 + key.Length * 7.5f;
        static float WhatWidth(string what) => 8 + what.Length * 7f;
        const float gap = 18;
        var total = hints.Sum(h => KeyWidth(h.Key) + WhatWidth(h.What)) + gap * (hints.Length - 1);
        var x = right ? width - 24 - total : (width - total) / 2;
        foreach (var (key, what) in hints)
        {
            var kw = KeyWidth(key);
            f.Rect(x, y, kw, 20, 6, Theme.Raised);
            f.Border(x, y, kw, 20, 6, 1, Theme.Hairline);
            f.Text(key, x, y + 1, kw, 18, 11, Theme.Muted, HudTextAlignment.Center, bold: true);
            f.Text(what, x + kw + 6, y + 1, WhatWidth(what), 18, 13, Theme.Faint);
            x += kw + WhatWidth(what) + gap;
        }
    }

    static string First(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? parts[0] : name;

    static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : name.Length > 0 ? name[..Math.Min(2, name.Length)] : "?").ToUpperInvariant();
    }
}
