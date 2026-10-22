# C# mod rewrite

The C# SDK, native .NET bridge and three gameplay mods are implemented: FPV, Atmosphere and
Rewind. Their controllers connect lifecycle and settings to focused gameplay services. Native
calls, hardware polling and game-object ownership live in separate integration adapters. The
other gameplay mod folders have been removed.

Read the [public mod walkthrough](../sdk/dotnet/Jibbers.Sdk/Features/README.md) for the complete
source, responsibilities and learning paths.

`docs/guides/` explains the SDK; `sdk/AGENTS.md` contains the game-thread and native-call rules.
The Rust layer remains responsible for loading .NET, native hooks and the stable update bootstrap.
The rewrite concerns gameplay mods and their SDK, not replacing the native proxy with managed code.

## Build and verify

From the repository root:

```powershell
dotnet run --project sdk/dotnet/Jibbers.Sdk.Tests -c Release -p:JibbersNoInstall=true
tools/package_dotnet.ps1 -BundleRuntime
```

The test suite covers settings/menu protocol, symbol rules, all three gameplay services, FPV flight,
collisions, follow pursuit, mode transitions, input focus/edges and synthesized sound. Optional
real-game dump tests report a skip if the dump is absent; they are not gameplay acceptance.

The three packages are:

- `JibbersCSharp.zip`: updater bootstrap, loader implementation, .NET bridge, SDK and runtime.
- `JibbersCSharpMods.zip`: C# FPV, Atmosphere and Rewind plus their two asset bundles and a
  backup/rollback installer that avoids running native and managed versions together.
- `JibbersCSharpSDK.zip`: managed SDK sources, bindings, generator, examples, tests, guides and
  the three public mod sources. Local-only mods and Rust SDK source are excluded.

Player/mod packages contain compiled code and license notices. The SDK package intentionally
contains C# source. Local-only ports still build in the repository and participate in tests, but
are excluded from both public mod binaries and the public source kit.

## Distribution and acceptance

The public catalog still contains the previously tested native gameplay DLLs. Building these ZIPs
does not publish a release or migrate other players. Loader updates remain enabled by the stable
bootstrap. The C# mods installer retires the three native mod subscriptions; selecting the native
versions in the public menu would install them again. Publishing managed gameplay packages and
the bundled runtime through the catalog is a separate release migration after live acceptance.

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
Inspector delegates to the SDK diagnostics controller. The public FPV, Atmosphere and Rewind
projects contain only feature entry points; their integration and pure logic live in the SDK's
`Features` folders, with private implementation helpers. See [Writing C# mods](guides/csharp-authoring.md).
Settings keys and public command identifiers are preserved for existing player configuration.
The old Rust starter is removed; the native loader and asset build tools remain maintained.
