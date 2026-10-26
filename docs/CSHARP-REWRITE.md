# C# mod rewrite

The C# SDK, native .NET bridge and the gameplay mods are implemented: Alpine Vision, FPV, Rewind
and Atmosphere. Each is a standalone project in `mods/cs`; the SDK contains no gameplay code. A
mod's entry point connects lifecycle and settings to focused gameplay services. Native calls,
hardware polling and game-object ownership live in separate integration adapters. The Rust mods
have been removed.

Read the [public mod walkthrough](../mods/cs/README.md) for the complete
source, responsibilities and learning paths.

`docs/guides/` explains the SDK; `sdk/AGENTS.md` contains the game-thread and native-call rules.
The Rust layer remains responsible for loading .NET, native hooks and the stable update bootstrap.
The rewrite concerns gameplay mods and their SDK, not replacing the native proxy with managed code.

## Build and verify

From the repository root:

```powershell
dotnet run --project sdk/dotnet/Jibbers.Sdk.Tests -c Release -p:JibbersNoInstall=true
dotnet run --project mods/cs/FPV.Tests -c Release -p:JibbersNoInstall=true   # likewise AlpineVision, Atmosphere, Reshred
tools/package_dotnet.ps1 -BundleRuntime
```

The SDK and mod test suites cover settings/menu protocol, symbol rules, all three gameplay services, FPV flight,
collisions, follow pursuit, mode transitions, input focus/edges and synthesized sound. Optional
real-game dump tests report a skip if the dump is absent; they are not gameplay acceptance.

The three packages are:

- `JibbersCSharp.zip`: updater bootstrap, loader implementation, .NET bridge, SDK and runtime.
- `JibbersCSharpMods.zip`: C# FPV, Alpine Vision and Rewind plus their two asset bundles and a
  backup/rollback installer that avoids running native and managed versions together.
- `JibbersCSharpSDK.zip`: managed SDK sources, bindings, generator, examples, tests, guides and
  the three public mod sources. Local-only mods and Rust SDK source are excluded.

Player/mod packages contain compiled code and license notices. The SDK package intentionally
contains C# source. Only these three gameplay mod projects remain in the repository.

## Distribution and acceptance

The public catalog distributes C# FPV, Alpine Vision and Rewind, plus a shared SDK and bundled .NET
runtime. Existing selected package IDs and DLL ownership paths are retained. On startup, the
bootstrap installs the signed update atomically; the new loader recognizes the managed assemblies
and loads them through the C# host. Existing updater installations do not need another download.
Unselected mods remain unselected. A newly installed manager starts with the install menu.

The runtime archive uses a package path accepted by existing bootstraps. Its contents are expanded
into a cache named by the archive hash. A signed per-file manifest verifies and repairs that cache;
a signed catalog receipt allows the previous installed runtime to work after a later update fails.
Native gameplay DLLs are replaced in place, and manual C# duplicates are skipped by mod ID.

For local acceptance, install the runtime package while the game is closed, then run the mods
installer from an extracted folder. In-game checks still required: one section per C# mod; toggles
and settings persist; Atmosphere effects render; rewind restores physics and input; FPV takes off,
flies/collides in all modes, pauses on focus/menu loss and restores camera/lens on landing/disable;
audio/OSD appear and disappear; changing maps does not leave handles or sounds behind.
Do not report those checks as passed from unit tests alone.

Package installation is exercised against a temporary fixture, including preservation of local mods,
settings, loader ownership and rollback after a locked native DLL. No game is started by packaging.

## Authoring cleanup

The starter and SlowMo use typed setting properties and high-level camera/physics APIs.
Inspector delegates to the SDK diagnostics controller. Each public mod project contains its whole
implementation: entry point, gameplay services and integration adapters, built against the same
public SDK as any other mod. See [Writing C# mods](guides/csharp-authoring.md).
Settings keys and public command identifiers are preserved for existing player configuration.
The Rust starter and Rust mods are removed; the native loader, bridge and updater remain Rust.
