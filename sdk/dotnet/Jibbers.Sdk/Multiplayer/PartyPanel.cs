using Jibbers.Sdk.UI;

namespace Jibbers.Sdk.Multiplayer;

/// <summary>What the Party panel is doing: the highlighted row, or typing a code.</summary>
internal sealed class PanelUi
{
    public int Selected;
    public bool EnteringCode;
    public string Code = "";
}

/// <summary>One choosable row of the panel.</summary>
internal sealed record PanelAction(string Label, string Detail, Action Run, bool Danger = false);

/// <summary>
/// Draws the Party panel and the toasts from a party snapshot. Pure: no Steam, no Unity, so the
/// layout is tested and previewed outside the game.
/// </summary>
internal static class PartyPanel
{
    public const float Width = 460;
    /// <summary>Room around the card for its shadow.</summary>
    public const float Margin = 24;
    const float Pad = 24, RowMember = 54, RowAction = 48;
    const int MaxActionRows = 6;

    public static List<PanelAction> Actions(PartySnapshot s, PanelUi ui)
    {
        var list = new List<PanelAction>();
        switch (s.State)
        {
            case PartyState.None:
                list.Add(new("Make a party", "Get a code to share", Party.Create));
                list.Add(new("Join with a code", "6-character code", () => { ui.EnteringCode = true; ui.Code = ""; }));
                foreach (var f in s.Friends.Where(f => f.InParty && !f.Locked).Take(8))
                    list.Add(new($"Join {f.Name}", f.PartySize == 1 ? "1 player" : $"{f.PartySize} players", () => Party.JoinFriend(f.Friend)));
                break;
            case PartyState.InParty:
                list.Add(new("Invite friends", "Opens the Steam overlay", Party.OpenInviteOverlay));
                foreach (var f in s.Friends.Where(f => !f.InParty).Take(4))
                    list.Add(new($"Invite {f.Name}", "Sends a Steam invite", () => Party.Invite(f.Friend)));
                if (s.Host?.IsLocal == true)
                    list.Add(s.Locked ? new("Unlock party", "Let new players join", () => Party.SetLocked(false)) : new("Lock party", "Nobody new can join", () => Party.SetLocked(true)));
                list.Add(new("Leave party", "", Party.Leave, Danger: true));
                break;
        }
        return list;
    }

    public static OverlayFrame Build(PartySnapshot s, PanelUi ui, IReadOnlyList<PanelAction> actions, string closeKey, double time)
    {
        var members = s.State == PartyState.InParty ? s.Members : Array.Empty<PartyMember>();
        var busy = s.State is PartyState.Creating or PartyState.Joining or PartyState.Unavailable;
        var shownActions = Math.Min(actions.Count, MaxActionRows);
        var status = s.State == PartyState.None && s.Status.Length > 0;

        var height = 112f;
        if (members.Count > 0) height += 30 + members.Count * RowMember + 14;
        if (busy) height += 76;
        else if (ui.EnteringCode) height += 30 + 70 + 34;
        else height += (status ? 30 : 0) + (shownActions > 0 ? 14 + shownActions * RowAction : 0);
        height += 46;

        var f = new OverlayFrame(Width + Margin * 2, height + Margin * 2).Origin(Margin, Margin);
        f.Card(0, 0, Width, height, 20);
        f.Gradient(20, 0, Width - 40, 3, 1.5f, Theme.Ice, Theme.Violet, horizontal: true);

        // Header: what you're in, and the code to share.
        f.Text("PARTY", Pad, 22, 200, 18, 13, Theme.Muted, bold: true, spacing: 6);
        var title = s.State switch
        {
            PartyState.InParty => members.Count == 1 ? "Just you so far" : $"{members.Count} players",
            PartyState.Creating => "Making a party",
            PartyState.Joining => "Joining",
            PartyState.Unavailable => "Waiting for Steam",
            _ => "Not in a party",
        };
        f.Text(title, Pad, 44, 260, 36, 27, Theme.Text, bold: true);
        var sub = s.State switch
        {
            PartyState.InParty when s.Host?.IsLocal == true => s.Locked ? "You're the host · locked" : "You're the host",
            PartyState.InParty => $"Hosted by {s.Host?.Name ?? "someone"}",
            PartyState.Unavailable => "Steam is starting with the game",
            _ => "Play with friends who have the same mods",
        };
        f.Text(sub, Pad, 80, 280, 20, 14, Theme.Muted);
        if (s.State == PartyState.InParty && s.Code.Length > 0)
        {
            const float w = 156, x = Width - Pad - w;
            f.Rect(x, 26, w, 62, 16, Theme.Raised);
            f.Border(x, 26, w, 62, 16, 1, Theme.Ice.WithAlpha(0.35f));
            f.Text("CODE", x, 33, w, 14, 11, Theme.Muted, HudTextAlignment.Center, bold: true, spacing: 5);
            f.Text(s.Code, x, 48, w, 34, 27, Theme.Ice, HudTextAlignment.Center, bold: true, spacing: 7);
        }
        var y = 112f;

        if (members.Count > 0)
        {
            f.Text("PLAYERS", Pad, y + 4, 200, 16, 12, Theme.Faint, bold: true, spacing: 4);
            y += 30;
            foreach (var m in members)
            {
                var color = Theme.ForId(m.Id.Value);
                f.Circle(Pad + 19, y + RowMember / 2, 19, color);
                if (m.IsLocal) f.Arc(Pad + 19, y + RowMember / 2, 23, 2, 0, 360, color.WithAlpha(0.5f));
                f.Text(Initials(m.Name), Pad, y + RowMember / 2 - 12, 38, 24, 16, Theme.CardBottom with { A = 1 }, HudTextAlignment.Center, bold: true);
                var nameWidth = Width - Pad * 2 - 54 - 120;
                f.Text(m.IsLocal ? $"{m.Name} (you)" : m.Name, Pad + 54, y + 8, nameWidth, 24, 18, Theme.Text, bold: m.IsLocal);
                var mods = m.Mods.Keys.Select(Pretty).ToList();
                f.Text(mods.Count == 0 ? "No multiplayer mods" : string.Join(" · ", mods), Pad + 54, y + 31, nameWidth, 18, 13, Theme.Muted);
                var right = Width - Pad;
                if (m.IsHost)
                {
                    f.Rect(right - 54, y + 15, 54, 24, 12, Theme.Warn.WithAlpha(0.16f));
                    f.Text("HOST", right - 54, y + 18, 54, 18, 11, Theme.Warn, HudTextAlignment.Center, bold: true, spacing: 3);
                    right -= 64;
                }
                if (!m.IsLocal)
                {
                    var (text, c) = m.PingMs switch { < 0 => ("…", Theme.Faint), < 80 => ($"{m.PingMs} ms", Theme.Good), < 160 => ($"{m.PingMs} ms", Theme.Warn), _ => ($"{m.PingMs} ms", Theme.Bad) };
                    f.Text(text, right - 70, y + 17, 70, 20, 14, c, HudTextAlignment.Right);
                }
                y += RowMember;
            }
            y += 14;
        }

        if (busy)
        {
            f.Rect(Pad, y, Width - Pad * 2, 1, 0, Theme.Hairline);
            var spin = (float)(time * 300 % 360);
            f.Arc(Pad + 16, y + 38, 12, 3, 0, 360, Theme.Raised);
            f.Arc(Pad + 16, y + 38, 12, 3, spin, 100, Theme.Ice);
            f.Text(s.Status.Length > 0 ? s.Status : title, Pad + 44, y + 26, Width - Pad * 2 - 44, 24, 17, Theme.Text);
            y += 76;
        }
        else if (ui.EnteringCode)
        {
            f.Rect(Pad, y, Width - Pad * 2, 1, 0, Theme.Hairline);
            f.Text("ENTER PARTY CODE", Pad, y + 14, 300, 16, 12, Theme.Faint, bold: true, spacing: 4);
            y += 30;
            const float box = 56, gap = 10;
            var x0 = (Width - (6 * box + 5 * gap)) / 2;
            for (var i = 0; i < 6; i++)
            {
                var x = x0 + i * (box + gap);
                var next = i == ui.Code.Length;
                f.Rect(x, y + 6, box, 62, 14, next ? Theme.Ice.WithAlpha(0.12f) : Theme.Raised);
                f.Border(x, y + 6, box, 62, 14, next ? 2 : 1, next ? Theme.Ice : Theme.Hairline);
                if (i < ui.Code.Length) f.Text(ui.Code[i].ToString(), x, y + 17, box, 40, 30, Theme.Text, HudTextAlignment.Center, bold: true);
            }
            y += 70;
            f.Text(ui.Code.Length == 6 ? "Enter to join · Backspace to fix" : "Type the code · Backspace to go back", Pad, y + 10, Width - Pad * 2, 20, 14, Theme.Muted, HudTextAlignment.Center);
            y += 34;
        }
        else
        {
            if (status)
            {
                f.Text(s.Status, Pad, y + 4, Width - Pad * 2, 20, 14, Theme.Warn);
                y += 30;
            }
            if (shownActions > 0)
            {
                f.Rect(Pad, y, Width - Pad * 2, 1, 0, Theme.Hairline);
                y += 14;
                var selected = Math.Clamp(ui.Selected, 0, actions.Count - 1);
                var first = Math.Clamp(selected - MaxActionRows + 1, 0, Math.Max(0, actions.Count - MaxActionRows));
                for (var i = first; i < first + shownActions; i++)
                {
                    var a = actions[i];
                    var on = i == selected;
                    var accent = a.Danger ? Theme.Bad : Theme.Ice;
                    if (on)
                    {
                        f.Rect(14, y, Width - 28, RowAction - 6, 13, accent.WithAlpha(0.13f));
                        f.Rect(14, y + 11, 3, RowAction - 28, 1.5f, accent);
                    }
                    f.Text(a.Label, Pad + 6, y + 10, 250, 24, 17, a.Danger ? Theme.Bad : on ? Theme.Text : Theme.Text.WithAlpha(0.85f), bold: on);
                    if (a.Detail.Length > 0) f.Text(a.Detail, Width - Pad - 170, y + 12, 170, 20, 13, Theme.Muted, HudTextAlignment.Right);
                    y += RowAction;
                }
                if (actions.Count > MaxActionRows) f.Text($"{selected + 1} / {actions.Count}", Width - Pad - 80, y - 4, 80, 16, 11, Theme.Faint, HudTextAlignment.Right);
            }
        }

        f.Rect(0, height - 42, Width, 1, 0, Theme.Hairline);
        Hints(f, height - 32, ui.EnteringCode ? new[] { ("Enter", "join"), ("Back", "fix"), (closeKey, "close") } : new[] { ("Up/Down", "select"), ("Enter", "choose"), (closeKey, "close") });
        return f;
    }

    /// <summary>Key caps with what they do, centred on one line.</summary>
    static void Hints(OverlayFrame f, float y, (string Key, string What)[] hints)
    {
        static float KeyWidth(string key) => 14 + key.Length * 7.5f;
        static float WhatWidth(string what) => 8 + what.Length * 7f;
        const float gap = 18;
        var total = hints.Sum(h => KeyWidth(h.Key) + WhatWidth(h.What)) + gap * (hints.Length - 1);
        var x = (Width - total) / 2;
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

    /// <summary>Stacked notices, newest first; each fades out over its last half second.</summary>
    public static OverlayFrame Toasts(IReadOnlyList<(string Text, double Age)> toasts, double life)
    {
        const float w = 440, h = 50, gap = 10;
        var f = new OverlayFrame(w + 40, Math.Max(1, toasts.Count) * (h + gap) + 30);
        var y = 10f;
        foreach (var (text, age) in toasts)
        {
            var alpha = (float)Math.Clamp(Math.Min(age / 0.2, (life - age) / 0.5), 0, 1);
            if (alpha <= 0) continue;
            f.Shadow(20, y, w, h, h / 2, 16, 5, new Color(0, 0, 0, 0.45f * alpha));
            f.Gradient(20, y, w, h, h / 2, Theme.CardTop.WithAlpha(0.96f * alpha), Theme.CardBottom.WithAlpha(0.96f * alpha));
            f.Border(20, y, w, h, h / 2, 1, Theme.Hairline.WithAlpha(Theme.Hairline.A * alpha));
            f.Circle(20 + 26, y + h / 2, 7, Theme.Ice.WithAlpha(alpha));
            f.Arc(20 + 26, y + h / 2, 12, 2, 0, 360, Theme.Ice.WithAlpha(0.3f * alpha));
            f.Text(text, 20 + 50, y + 13, w - 70, 24, 17, Theme.Text.WithAlpha(alpha));
            y += h + gap;
        }
        return f;
    }

    static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var letters = parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : name.Length > 0 ? name[..Math.Min(2, name.Length)] : "?";
        return letters.ToUpperInvariant();
    }

    /// <summary>"slvsh" → "SLVSH", "my_mod" → "My Mod": short ids read as names.</summary>
    internal static string Pretty(string modId) => modId.Length <= 5 && !modId.Contains('_')
        ? modId.ToUpperInvariant()
        : string.Join(' ', modId.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
