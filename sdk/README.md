# Jibbers Mod SDK

Write mods for Jibbers Playtest in **C#**. A mod is a .NET class library; the mod loader starts .NET
inside the game and runs it, with its own section in **Options > Mods**, saved settings, the game's
events, typed access to every game class, hooks and detours, input, and raw IL2CPP/Unity access.
Unofficial; not affiliated with the game's developer.

**Docs and searchable API reference:** the [`docs/`](../docs/index.html) site (published with GitHub
Pages): guides, plus every SDK type and member, every game class and event, with arguments and
descriptions.

If you use an AI agent, point it at [AGENTS.md](AGENTS.md) first.

## Set up (once)

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Close the game and extract `JibbersCSharp.zip` into the game folder (the one with `Jibbers.exe`):
   it adds `version.dll` (the loader), `JibbersMods\jibbers_dotnet.dll`, `JibbersMods\dotnet\` (the C#
   SDK and a bundled .NET runtime, so players need no install) and `JibbersMods\cs\` (your mods).
3. Start the game once and check `jibbers_hotload.log` beside `Jibbers.exe` for `dotnet: .NET 8... running`.

## Make a mod

```powershell
Copy-Item -Recurse dotnet\template\MyMod MyCoolMod   # rename the .csproj and the class too
cd MyCoolMod
dotnet build -c Release                              # copies the DLL to <game>\JibbersMods\cs\
```

Restart the game to load it. See [Getting started](../docs/guides/getting-started.md).

## What is here

| Path | What |
| --- | --- |
| `dotnet/Jibbers.Sdk/` | **The C# SDK** mods reference (`Jibbers.Sdk.dll`), including the generated game bindings in `Generated/` |
| `dotnet/template/MyMod/` | Starter mod: typed settings, a rebindable hotkey, and camera distance |
| `dotnet/examples/` | `SlowMo` (high-level physics speed control), `Inspector` (developer probes) |
| `dotnet/Jibbers.Sdk.Generator/` | `jibbers-gen`: regenerates the game bindings and the docs data from a game dump |
| `dotnet/Jibbers.Sdk.Tests/` | Tests (`dotnet run`): symbol matcher, settings, Mods-tab protocol, the real dump |
| `crates/jibbers-mod-sdk/symbols.json` | The symbol catalogue: stable names for the game's obfuscated classes |
| `crates/jibbers-dotnet-bridge/` | Native bridge that starts .NET in the game (`jibbers_dotnet.dll`) |
| `crates/jibbers-mod-sdk/`, `crates/jibbers-sdk-*` | The native layer: the plugin ABI shared with the loader, the runtime/updater, maintainer tools. The original mods are written against it; new mods should use C#. |
| `compat/compat.json` | Game builds the mods are validated for |
| `docs/` | Native-layer guides: `GAME-BUILDS.md` (build guard, publishing), `SYMBOLS.md`, `RUNTIME.md` |

## Building the SDK

```powershell
tools\package_dotnet.ps1 -BundleRuntime    # tests, builds, dist\JibbersCSharp.zip + dist\JibbersCSharpSDK.zip
```

After a game update: dump the game (Inspector's `inspector.dump`), then
`dotnet run --project dotnet\Jibbers.Sdk.Generator -c Release -- resolve --dump <dump>`; fix any
symbol that fails, regenerate (`bindings`, `docs`), and validate the build (`docs/GAME-BUILDS.md`).
