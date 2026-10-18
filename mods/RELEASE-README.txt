JIBBERS MOD LOADER + INDEPENDENT PLUGINS - 0.1.0 RELEASE CANDIDATE
==============================================================
Windows x64 / Jibbers Playtest / Unity 6000.3 / plugin ABI 4
Reshred's contact metadata targets the October 2026 game build.
Unofficial mods; not affiliated with or supported by the game developer.

VALIDATED GAME BUILDS
{{VALIDATED_BUILDS}}

CHOOSE A DOWNLOAD
JibbersModLoader.zip          Shared loader only. Install once.
JibbersAtmosphere.zip         Atmosphere plugin + assets. Requires loader.
JibbersReshred.zip            Reshred plugin. Requires loader.
JibbersMods-Complete.zip      Loader + both independent plugins + assets.
JibbersAtmosphere-Standalone.zip / JibbersReshred-Standalone.zip
                             One plugin plus the same shared loader.

INSTALL
1. Exit the game yourself. Back up any existing version.dll first.
2. Steam: Jibbers Playtest > Manage > Browse local files.
3. Extract the chosen archive(s) into the folder containing Jibbers.exe.
   version.dll must be next to the executable. Plugin DLLs belong in
   JibbersMods. Atmosphere's bundle belongs next to the executable.
4. Start the game. Allow up to 45 seconds for automatic attachment, then
   open Options > Mods. Each installed plugin has an independent section.
   The developer core and a TCP connection are NOT required.
5. Restart after adding, removing or replacing a plugin. Do not attempt
   live DLL replacement/reload as a player installation procedure.

All archives contain identical loader bytes from the same build. When
combining plugins, merge JibbersMods rather than replacing the directory.
Do not overwrite another unrelated version.dll-based loader: these packages
are not an automatic compatibility/chainloading solution.

IMPORTANT: Do not leave the developer core jibbers_hotload_core.dll beside
the executable in a player installation. That developer core includes
Atmosphere, can duplicate effects/hooks, and owns its own Mods tab.
Back it up outside the game folder if switching from developer tools.
No developer core, server, personal settings, saves, game binaries or logs
are included in these archives. Defaults are created on first use.

PLUGINS
Atmosphere: visual fog, snow, clouds and grading. F9 toggles; F10 cycles
presets. See ATMOSPHERE.txt when included. Snow texture credits/license are
in SNOW-TEXTURES-LICENSE.txt. It does not alter terrain or physics.

Reshred: hold controller X (Xbox west button / PlayStation Square), or
keyboard X, for 0.6s. Rewind to snow with recorded pose and speed. Grinding
counts as airborne: rails are NEVER valid checkpoints, even when rail and
ground contact overlap. Default pre-jump approach is 2.5 seconds before
leaving snow; adjust Pre-jump run-up in Options > Mods (0.5-5 seconds).
Each new snow landing starts a new approach: a second jump cannot send you
back across the previous jump. Short landings use their earliest snow frame
instead of selecting an earlier feature. Eight seconds of rolling history
plus a retained checkpoint cover long airtime/grinds. A spawn that has not
touched snow has no checkpoint. See RESHRED.txt when included.

Each plugin has its own JSON settings in the game folder. Existing settings
are not overwritten by these packages. Disable or remove either plugin
without removing the other. Keep version.dll while any plugins remain.

UNINSTALL / TROUBLESHOOTING
Exit the game, remove only the unwanted plugin DLL, then restart.
Atmosphere assets/settings may be removed when Atmosphere is uninstalled.
To remove all mods, also remove this loader's version.dll (or restore your
backup). JIBBERS_HOTLOAD_DISABLE=1 disables attachment as an environment
variable. Diagnostic messages go to jibbers_hotload.log beside Jibbers.exe.

GAME UPDATES: if the game was updated to a build not listed above, the
loader does not hook anything and loads no plugin, so a changed game cannot
crash because of the mods. The log gets one line such as "game build ... not
validated; mods disabled (loader built for ...)" and JibbersMods\MODS-DISABLED.txt
explains it (the Mods tab is not shown). Wait for a release for that build.
For testing only, JIBBERS_HOTLOAD_FORCE=1 loads the mods anyway; a crash on a
build that is not validated is then your own risk and not a bug report.
The same applies if a loader and a plugin come from different releases: the
mod is skipped with one log line instead of being retried.

RELEASE STATUS
Automated Rust tests, release builds, archive hashes, x64 PE/ABI exports and
asset presence are checked offline. The new shared release menu and latest
snow-only, per-landing rewind behavior still require an in-game acceptance
pass. Test on a local/offline map before publication; no online compatibility
or replay/crash-recovery guarantee is claimed. These are release candidates.
Keep a backup of saves/settings and remove the mods if a game update changes
the UI, IL2CPP metadata or physics ABI. Distribution rights for the project
and third-party dependencies should be reviewed before public publication;
no project-wide license is assigned by this packaging work.