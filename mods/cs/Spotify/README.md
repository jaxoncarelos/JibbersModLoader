# Spotify

See and control what's playing in Spotify without leaving the game. No Spotify login, account or
key: the mod reads and controls Spotify through Windows' own media controls (the system behind the
volume flyout and your keyboard's media keys).

Not made by or affiliated with Spotify.

## The record

While a song plays, a vinyl record spins in a corner of the screen with the album art as its label,
the title, artist, time (1:23 / 3:45) and a progress bar in the album art's colour, plus back /
play-pause / skip buttons you can click. It pops when a new song starts.

## The mini Spotify app

Open your phone and press **Spotify**:

- **Now playing:** the cover, title, artist and album; a progress bar you can click to jump anywhere
  in the song; back, play/pause and skip; -10 s / +10 s; shuffle; repeat (off, the playlist, the song).
- **History:** the songs you played lately with how long ago (kept between sessions), and this
  session's song count and minutes listened.

## Controls

| | Keyboard | Controller |
| --- | --- | --- |
| Skip / previous song | `]` / `[` | R3 / L3 (click a stick) |
| Play / pause | `\` | R3 + L3 together |
| Back / forward 10 seconds | `;` / `'` | |

All keys can be changed.

## Settings

Options > Mods > Spotify: show the record always, only for a few seconds when the song changes, or
never; the full record or the record alone; its corner, size, opacity and spin speed; hide it while
paused; colour from the album art; the pop on a new song; only Spotify (or any media player), and the
keys and controller.

## How it works

| File | What it does |
| --- | --- |
| [SpotifyMod](SpotifyMod.cs) | Settings, the record loop (30 times a second), controls, song history |
| [RecordArt](RecordArt.cs) | Drawing the record's panel, vinyl, label, icons and the app background; the art's accent colour (tested) |
| [Pixels](Pixels.cs) | A small anti-aliased drawing surface (tested) |
| [History](History.cs) | Songs played lately, kept in `%LOCALAPPDATA%\JibbersSpotify\history.json` (tested) |
| [Integration/MediaSession](Integration/MediaSession.cs) | Windows' media controls through WinRT, on a thread of its own |
| [Integration/RecordHud](Integration/RecordHud.cs) | The record on screen |
| [Integration/PhoneApp](Integration/PhoneApp.cs) | The mini Spotify app on the phone |
| [Integration/PhoneButton](Integration/PhoneButton.cs) | The Spotify button on the game's phone |
| [Integration/AlbumArt](Integration/AlbumArt.cs), [Textures](Integration/Textures.cs), [Ui](Integration/Ui.cs) | Album art textures (decoded with the game's own ImageConversion), Unity UI helpers |

Notes for reviewers: native code is Windows' WinRT media controls (`combase`: RoGetActivationFactory
and HSTRINGs; the GlobalSystemMediaTransportControls interfaces and a DataReader for the album art,
called by method slot) on the mod's own thread, and nothing else. No network access. The only file it
writes is the song history in `%LOCALAPPDATA%\JibbersSpotify`. Every exception on its threads is
caught and logged, so a failure can't end the game.
