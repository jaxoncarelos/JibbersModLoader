JIBBERS ATMOSPHERE
==================

Weather for Jibbers: height fog and haze, falling snow with speed streaks,
drifting clouds in the valleys, and colour grading. It only changes how the
world looks. Terrain, maps and gameplay are untouched.

INSTALL
1. Open the Jibbers Playtest game folder (the one with Jibbers.exe in it).
   In Steam: right-click Jibbers Playtest, Manage, Browse local files.
2. Install JibbersModLoader.zip once, then copy these from the plugin archive:
     jibbers_atmosphere.bundle
     the JibbersMods folder (it holds jibbers_atmosphere.dll)
  The Standalone archive includes version.dll too; the Complete archive
  includes the shared loader plus both Atmosphere and Reshred plugins.
3. Start the game. The effects switch on about 45 seconds after launch.

version.dll is the shared loader for Jibbers mods. Other Jibbers mods that
use it (they ship as DLLs in JibbersMods) can share the same version.dll, so
only one copy is needed in the game folder.
The release loader provides Options > Mods with independent plugin settings.
Valley clouds follow the rider's camera height, including on elevated imported
maps. "Cloud distance below" sets the gap to the cloud tops (180 metres by
default); a smaller value brings the cloud layer closer. In the settings file
this is "cloudBankDistance". "cloudBankThickness" controls the layer's depth.
Snow glitter stays enabled. A character silhouette mask prevents its sun glints
from being added to clothing, including upward-facing shoulders and sleeves.

Do not install this together with the developer core (jibbers_hotload_core.dll).
Both use version.dll, and the developer core already includes the atmosphere.

CONTROLS (while the game window is focused)
  F9   atmosphere on / off
  F10  next preset: Foggy day, Clear, Snowstorm

SETTINGS
jibbers_atmosphere.json is created in the game folder on the first run. Edit
it in a text editor, then press F9 twice (off, then on) to apply the changes,
or restart the game. F9 and F10 save the file, so presets you choose are kept.
The keys can be changed there too: "hotkeys": false turns them off, and
"toggleKey" / "presetKey" take Windows virtual-key codes (F9 = 120, F10 = 121).

UNINSTALL
Delete these from the game folder:
  jibbers_atmosphere.bundle
  JibbersMods\jibbers_atmosphere.dll
  jibbers_atmosphere.json
If other Jibbers mods still sit in JibbersMods, keep version.dll and that folder.
Remove the loader's version.dll only if removing all plugins (or restore a backup).

CREDITS
The snow textures are CC0 from ambientCG (ambientcg.com).
