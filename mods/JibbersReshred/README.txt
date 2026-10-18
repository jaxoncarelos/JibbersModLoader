Jibbers Reshred - Jibbers Playtest, Unity 6000.3 / MuJoCo 3.3.6
============================================================

INSTALL
Install JibbersModLoader.zip once, then extract this plugin's JibbersMods folder
into the Jibbers Playtest folder beside the game's executable. The Standalone
archive includes the same loader. Do not replace an unrelated loader without
backing it up. The loader supplies one shared Options > Mods tab for all plugins.
Keep any other plugins already in JibbersMods. Restart the game after replacing
a plugin; developer-core reload does not reload plugins.

USE (OFFLINE)
Hold keyboard X or the controller's west face button (Xbox X / PlayStation
Square). After a short hold (0.2 seconds by default) time runs backwards through
the last 6 seconds of your run, Forza-style, wherever you are: on snow, in the
air or on a rail. The rider's exact pose, speed and primitive rider state are
restored at every step. Let go when you reach the moment you want and you
continue from there; the rewound time is gone, so holding again goes further
back from that point. At the oldest moment (up to 6 seconds back) the rewind
stops and waits for you to release. A bottom-center label shows how far back
you are.
Keyboard/controller bindings for the reserved button are temporarily disabled
in Rewired's Ragdoll/gameplay category, not deleted. UI, Replay, SpawnSelector,
Default and modified keyboard shortcuts are left alone. Disabling Reshred or
changing its key restores the old mapping on the next main-thread update;
normal plugin teardown restores mappings too. Nothing writes binding files.

OPTIONS
The Mods menu exposes enable, key, controller, hold time (delay before the
rewind starts), rewind speed (1 = real time, default 2) and indicator. Settings
persist in jibbers_reshred.json in the game folder. Defaults: enabled=true,
key=88 (X), controller=true, holdTime=0.2, speed=2, indicator=true. Older
files with rewind/runupTime values still load; those two are ignored.
STATUS / LIMITATIONS
This build has passed offline regression tests, not an in-game acceptance test.
Test on a local/offline map first. Controller discovery depends on Rewired
identifying the physical west button; unidentified devices are not guessed.
Input API incompatibilities are logged and shown as an input-conflict warning;
keyboard reservation is independent of controller discovery failures.
No native pointer inspection or developer live probing is needed to use it.

If there is no label, check jibbers_hotload.log for "reshred: no indicator".
If input conflicts persist, look for "reshred: input reservation" in that log.
Validate rewinding from snow, air and rails, crash recovery, replay continuity, key
changes, disable/enable and scene changes before relying on saved clips.

UNINSTALL
Exit the game yourself, remove JibbersMods/jibbers_reshred.dll, and restart.
Keep version.dll if other Jibbers plugins use it. Settings may be retained.