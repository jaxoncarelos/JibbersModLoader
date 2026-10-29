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
    public const float Width = 380;
    /// <summary>Room around the panel.</summary>
    public const float Margin = 8;
    const float Pad = 16, RowMember = 40, RowAction = 34;
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
        var showCode = s.State == PartyState.InParty && s.Code.Length > 0;

        var height = 92f;
        if (members.Count > 0) height += 18 + members.Count * RowMember + 8;
        if (busy) height += 52;
        else if (ui.EnteringCode) height += 18 + 58 + 26;
        else height += (status ? 24 : 0) + (shownActions > 0 ? 6 + shownActions * RowAction : 0);
        height += 40;

        var f = new OverlayFrame(Width + Margin * 2, height + Margin * 2).Origin(Margin, Margin);
        f.Card(0, 0, Width, height);

        // Caption, what you're in, and the code to share.
        f.Text("PARTY", Pad, 12, 70, 14, 11, Theme.Text, bold: true, spacing: Theme.Tracked);
        var role = s.State == PartyState.InParty && s.Host?.IsLocal == true ? (s.Locked ? "HOST · LOCKED" : "HOST") : "";
        if (role.Length > 0) f.Text(role, Pad + 62, 12, 140, 14, 11, Theme.Mint, spacing: Theme.Tracked);
        var title = s.State switch
        {
            PartyState.InParty => members.Count == 1 ? "Just you so far" : $"{members.Count} players",
            PartyState.Creating => "Making a party",
            PartyState.Joining => "Joining",
            PartyState.Unavailable => "Waiting for Steam",
            _ => "Not in a party",
        };
        f.Text(title, Pad, 34, 220, 30, 23, Theme.Text);
        var sub = s.State switch
        {
            PartyState.InParty when s.Host?.IsLocal == true => "Share the code or invite friends",
            PartyState.InParty => $"Hosted by {s.Host?.Name ?? "someone"}",
            PartyState.Unavailable => "Steam is starting with the game",
            _ => "Play with friends who have the same mods",
        };
        f.Text(sub, Pad, 64, showCode ? 220 : Width - Pad * 2, 18, 13, Theme.Caption);
        if (showCode)
        {
            const float w = 120, x = Width - Pad - w;
            f.Rect(x, 14, w, 60, Theme.ChipCorner, Theme.Inset);
            f.Text("CODE", x, 20, w, 14, 10, Theme.Caption, HudTextAlignment.Center, spacing: Theme.Tracked);
            f.Text(s.Code, x, 36, w, 32, 24, Theme.Mint, HudTextAlignment.Center, spacing: 12);
        }
        var y = 92f;

        if (members.Count > 0)
        {
            f.Text("PLAYERS", Pad, y, 120, 14, 10, Theme.Caption, spacing: Theme.Tracked);
            y += 18;
            foreach (var m in members)
            {
                var nameWidth = Width - Pad * 2 - 110;
                f.Text(m.Name, Pad, y + 2, nameWidth, 20, 15, Theme.Text, bold: m.IsLocal);
                var mods = m.Mods.Keys.Select(Pretty).ToList();
                f.Text(mods.Count == 0 ? "No multiplayer mods" : string.Join(" · ", mods), Pad, y + 21, nameWidth, 14, 11, Theme.Dim);
                if (!m.IsLocal)
                {
                    var (text, c) = m.PingMs switch { < 0 => ("…", Theme.Dim), < 80 => ($"{m.PingMs} ms", Theme.Good), < 160 => ($"{m.PingMs} ms", Theme.Warn), _ => ($"{m.PingMs} ms", Theme.Bad) };
                    f.Text(text, Width - Pad - 60, y + 3, 60, 18, 13, c, HudTextAlignment.Right);
                }
                var tag = m.IsLocal ? (m.IsHost ? "YOU · HOST" : "YOU") : m.IsHost ? "HOST" : "";
                if (tag.Length > 0) f.Text(tag, Width - Pad - 110, y + (m.IsLocal ? 5 : 22), 110, 14, 10, m.IsHost ? Theme.Mint : Theme.Caption, HudTextAlignment.Right, spacing: Theme.Tracked);
                y += RowMember;
            }
            y += 8;
        }

        if (busy)
        {
            // A segment sliding along a thin track, in place of a spinner.
            var track = Width - Pad * 2 - 80;
            f.Rect(Pad, y + 8, Width - Pad * 2, 2, 0, Theme.Inset);
            f.Rect(Pad + (float)(time * 0.8 % 1) * track, y + 8, 80, 2, 0, Theme.Mint);
            f.Text(s.Status.Length > 0 ? s.Status : title, Pad, y + 18, Width - Pad * 2, 20, 14, Theme.Text);
            y += 52;
        }
        else if (ui.EnteringCode)
        {
            f.Text("ENTER PARTY CODE", Pad, y, 300, 14, 10, Theme.Caption, spacing: Theme.Tracked);
            y += 18;
            const float box = 44, gap = 8;
            for (var i = 0; i < 6; i++)
            {
                var x = Pad + i * (box + gap);
                var next = i == ui.Code.Length;
                f.Rect(x, y, box, 50, Theme.ChipCorner, next ? Theme.InsetLit : Theme.Inset);
                if (next) f.Rect(x, y + 48, box, 2, 0, Theme.Mint);
                if (i < ui.Code.Length) f.Text(ui.Code[i].ToString(), x, y + 9, box, 32, 26, Theme.Text, HudTextAlignment.Center);
            }
            y += 58;
            f.Text(ui.Code.Length == 6 ? "Enter to join, Backspace to fix" : "Type the code, Backspace to go back", Pad, y + 4, Width - Pad * 2, 16, 12, Theme.Caption);
            y += 26;
        }
        else
        {
            if (status)
            {
                f.Text(s.Status, Pad, y, Width - Pad * 2, 18, 13, Theme.Warn);
                y += 24;
            }
            if (shownActions > 0)
            {
                y += 6;
                var selected = Math.Clamp(ui.Selected, 0, actions.Count - 1);
                var first = Math.Clamp(selected - MaxActionRows + 1, 0, Math.Max(0, actions.Count - MaxActionRows));
                for (var i = first; i < first + shownActions; i++)
                {
                    var a = actions[i];
                    var on = i == selected;
                    if (on)
                    {
                        f.Rect(Pad - 8, y, Width - Pad * 2 + 16, RowAction - 4, Theme.ChipCorner, Theme.Inset);
                        f.Rect(Pad - 8, y, 3, RowAction - 4, 0, a.Danger ? Theme.Bad : Theme.Mint);
                    }
                    f.Text(a.Label, Pad, y + 6, 210, 20, 15, a.Danger ? Theme.Bad : on ? Theme.Text : Theme.Text.WithAlpha(0.8f), bold: on);
                    if (a.Detail.Length > 0) f.Text(a.Detail, Width - Pad - 150, y + 9, 150, 16, 12, Theme.Caption, HudTextAlignment.Right);
                    y += RowAction;
                }
                if (actions.Count > MaxActionRows) f.Text($"{selected + 1} / {actions.Count}", Width - Pad - 80, y - 2, 80, 14, 10, Theme.Dim, HudTextAlignment.Right);
            }
        }

        Hints(f, height - 32, ui.EnteringCode ? new[] { ("Enter", "Join"), ("Back", "Fix"), (closeKey, "Close") } : new[] { ("Up/Down", "Select"), ("Enter", "Choose"), (closeKey, "Close") });
        return f;
    }

    /// <summary>Key caps (like the HUD's "1 2 3 4") with their action in tracked capitals.</summary>
    static void Hints(OverlayFrame f, float y, (string Key, string What)[] hints)
    {
        static float KeyWidth(string key) => Math.Max(22, 10 + key.Length * 8f);
        static float WhatWidth(string what) => 10 + what.Length * 8.2f;
        var x = Pad;
        foreach (var (key, what) in hints)
        {
            var kw = KeyWidth(key);
            f.Rect(x, y, kw, 22, Theme.ChipCorner, Theme.InsetLit);
            f.Text(key, x, y + 4, kw, 14, 11, Theme.Text, HudTextAlignment.Center, bold: true);
            f.Text(what.ToUpperInvariant(), x + kw + 7, y + 4, WhatWidth(what), 14, 11, Theme.Caption, spacing: Theme.Tracked);
            x += kw + 7 + WhatWidth(what) + 12;
        }
    }

    /// <summary>Stacked notices, newest first; each fades out over its last half second.</summary>
    public static OverlayFrame Toasts(IReadOnlyList<(string Text, double Age)> toasts, double life)
    {
        const float w = 360, h = 38, gap = 6;
        var f = new OverlayFrame(w + 16, Math.Max(1, toasts.Count) * (h + gap) + 16);
        var y = 8f;
        foreach (var (text, age) in toasts)
        {
            var alpha = (float)Math.Clamp(Math.Min(age / 0.2, (life - age) / 0.5), 0, 1);
            if (alpha <= 0) continue;
            f.Rect(8, y, w, h, Theme.Corner, Theme.Panel.WithAlpha(Theme.Panel.A * alpha));
            f.Rect(8, y, 3, h, 0, Theme.Mint.WithAlpha(alpha));
            f.Text(text, 24, y + 9, w - 28, 20, 15, Theme.Text.WithAlpha(alpha));
            y += h + gap;
        }
        return f;
    }

    /// <summary>"slvsh" → "SLVSH", "my_mod" → "My Mod": short ids read as names.</summary>
    internal static string Pretty(string modId) => modId.Length <= 5 && !modId.Contains('_')
        ? modId.ToUpperInvariant()
        : string.Join(' ', modId.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
