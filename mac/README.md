# Jibbers native Mac preview 0.1.0

Experimental native macOS package for tester feedback. This is not the Windows ZIP and does not need CrossOver. Both Apple Silicon and Intel payloads and the required .NET runtime are included. No separate .NET installation is needed.

1. Close Jibbers. Open its Steam installation folder (Manage > Browse local files).
2. Extract `Run Jibbers.command` and `JibbersMods` beside the Jibbers `.app`, not inside it.
3. Keep Steam open. Right-click `Run Jibbers.command` and choose Open. Use this launcher when playing with mods. Normal Steam launch remains unmodified.
4. Open Options > Mods. F9 toggles the FPV drone, F10 cycles weather; Rewind's binding is in its Mods section. A Mac may require Fn with function keys.

To launch through Steam instead, set launch options to the quoted absolute path of `Run Jibbers.command`, followed by `%command%`.

The preview requires an IL2CPP game build with managed metadata matching the shipped compatibility baseline. A mismatch stops before hooks or mods: it does not force unsupported builds to load. The launch log records the actual metadata SHA256. The Windows compatibility checks and catalog are unchanged. Native Mac previews bundle mods and never install files from the Windows update catalog.

Send `jibbers_hotload.log`, `JibbersMods/mac-launch.log`, and, if generated, `JibbersMods/mac-symbols.json`, plus your Mac model and macOS version. Look for `READY: native Mac mods loaded`. Check title screen/Mods navigation, weather presets and falling snow, visual filters, drone movement/audio, and rewind while skiing. Live gameplay has not been verified on a Mac by the maintainer yet; this package is for that first test.

Uninstall: close the game and remove this preview's `Run Jibbers.command` and `JibbersMods`. No original game app files are patched or re-signed. Existing saves and custom maps are outside this package.

Source: https://github.com/jaxoncarelos/JibbersModLoader (see BUILD.json for the exact commit). Project code GPL-3.0-only; Doorstop LGPL-2.1; Dobby Apache-2.0; .NET retains Microsoft's included licenses and notices. Source and exact third-party revisions are shipped in the associated source archive.
