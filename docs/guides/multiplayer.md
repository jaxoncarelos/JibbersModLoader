# Multiplayer: parties and Steam messages

Mods can play together over Steam. Players gather in a **party**, and each mod talks to its own
copy on the other players' PCs through a **channel**. [SLVSH](../../mods/cs/Slvsh) is a complete
example: a turn-based trick game with a host that runs the rules and votes on every try.

```csharp
using Jibbers.Sdk.Multiplayer;

PartyChannel channel = null!;

public override void OnLoad()
{
    channel = Party.Channel(this);
    channel.Received += message => Log($"{message.From.Name}: {message.Text}");
    Input.OnPressed(Key.F9, () => channel.Broadcast("hello"));
}
```

## The party

A party is a Steam lobby that the SDK keeps for the player. Every multiplayer mod shares it:
players join once, and every mod they have works with everyone else in it.

- The player opens the **Party panel** (default **F7**, rebindable under Mods > Party). From there
  they can make a party, join one with its 6-character code, join a friend's party, invite friends
  through the Steam overlay, lock the party, or leave it. Toasts appear when people join or leave.
- The panel and its Mods-tab section only appear when an installed mod calls `Party.Channel` in
  its `OnLoad`. Players without multiplayer mods never see them.
- The **host** is the Steam lobby owner (`Party.Host`, `Party.IsHost`). If the host leaves,
  Steam hands the role to someone else and everyone gets a `Changed` event. A game can follow the
  party host, or, like SLVSH, make whoever starts a game its host.
- `Party.Members` lists everyone. `member.HasMod("slvsh")` and `member.ModVersion("slvsh")` say
  who runs your mod. `channel.Peers` lists the others who have it, and `channel.Players` includes you.
- Mods may also drive the party themselves: `Party.Create()`, `Party.Join(code)`,
  `Party.JoinFriend(id)`, `Party.Leave()`, `Party.Invite(friend)`, `Party.OpenInviteOverlay()`.
  `Party.PanelKey` is the panel's key, for your own hints ("press F7 to make a party").

## Messages

```csharp
channel.Send(member.Id, new JsonObject { ["t"] = "result", ["landed"] = true });
channel.Broadcast(state, Delivery.Unreliable);   // frequent updates: timers, positions
channel.SendToHost(request);                     // to the party host (you, if you host)
channel.Received += m => { var json = m.Json(); /* ... */ };
```

- **Bytes, text or JSON**, at most 64 KB per message. `Delivery.Reliable` (the default) arrives
  once and in order. `Delivery.Unreliable` may drop a message but never waits on a lost one.
- **Only your mod receives your messages.** Each message is tagged with your mod id, and only the
  same mod on the other end gets it.
- **Sending to yourself works.** It is delivered on the next party tick, so a host can treat
  itself like any other player.
- **`Received` runs on the SDK's party thread,** one message at a time and in order. It is skipped
  while your mod is turned off. Wrap game calls in `MainThread.Run`, or (better) queue the message
  and handle it on your own thread, as SLVSH does.
- **Treat every message as untrusted.** It came from another PC: check its shape, clamp numbers
  and lengths, and ignore results for turns that aren't current. The SDK already drops messages
  from anyone outside the party and malformed frames.

## How it works

Messages use Steam's `ISteamNetworkingMessages` through the game's own `steam_api64.dll`. Valve
relays them, so there are no ports to open and players never see each other's IP address. The
SDK uses one message channel for every mod and frames each message with the mod's id. A party is
a public Steam lobby that is only listed under its code (`jb_code`). Each member's multiplayer
mods are kept in lobby member data (`jb_mods`).

The game dispatches Steam's callbacks itself, so the SDK polls call results and checks lobby
membership directly instead of waiting for callbacks. This is the approach the SLVSH and Voice
Chat mods proved in this game.

This is separate from the game's own networking (FishNet), which the SDK still does not touch.
See [coverage](coverage.md).

## Overlays for multiplayer UI

`Jibbers.Sdk.UI.Overlay` draws cards with anti-aliased shapes (rounded rectangles, gradients,
soft shadows, circles, progress arcs) and text in the game's font. It is what the Party panel and
SLVSH's scoreboard use. `Theme` keeps mods visually consistent.

```csharp
var panel = Overlay.Register("scoreboard", new OverlayOptions { Anchor = HudAnchor.TopRight });
var f = new OverlayFrame(400, 120).Origin(20, 20);   // a margin for the shadow
f.Card(0, 0, 360, 80);
f.Text("Your turn", 20, 16, 320, 30, 24, Theme.Text, bold: true);
f.Arc(320, 40, 22, 5, 0, 360 * secondsLeft / total, Theme.Ice);
panel.Draw(f);
panel.Show();
```

Draw a new frame whenever something changes; it is cheap. The newest frame wins, shapes are only
re-rasterized when they change, and all Unity work happens on the main thread. To design without
the game, save frames with `OverlayPreview.Save(frame, "preview.html")` and open the page in a
browser (`mods/cs/Slvsh.Tests` does this when `SLVSH_PREVIEW` is set).
