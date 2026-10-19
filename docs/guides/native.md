# Native code

Some mods need to go below game objects: replace a function, call a native library, read memory.
C# does all of it with `unsafe` code and function pointers. Add
`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` to your project (the template has it).

## Detours

A detour replaces a native function with yours. You call the original through the trampoline.

```csharp
using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Physics;

static Detour? step;

public override unsafe void OnLoad()
{
    step = Scene.StepSceneMethod.Detour((nint)(delegate* unmanaged<nint, nint, void>)&Step);
    step.Enable();
}

[UnmanagedCallersOnly]
static unsafe void Step(nint scene, nint method)
{
    // ... your code ...
    ((delegate* unmanaged<nint, nint, void>)step!.Original)(scene, method);
}
```

**The signature must match exactly.** A game method compiled by IL2CPP takes
`(this, arguments..., MethodInfo*)`; a static method has no `this`. Arguments use the Windows x64
convention: integers, pointers and object references are `nint`/`int`/`long`; `float`/`double`
are themselves; structs of 1, 2, 4 or 8 bytes are passed by value, bigger ones as a pointer. A
wrong signature crashes the game.

- `Detour.Create(target, replacement)` takes any address: `MethodRef.NativeCode`,
  `Il2CppMethod.NativeCode`, `NativeLibrary.GetExport(...)`, `Il2Cpp.ICall("...")`.
- `Detour.Create<TDelegate>(target, delegate)` takes a C# delegate instead (simpler, slower).
- Detours are removed when your mod unloads. `Disable()` / `Enable()` toggle one.

## Calling native libraries

The game ships MuJoCo as `pcore.dll`; Unity's engine functions are in `UnityPlayer.dll`. Call
them like any native library:

```csharp
[DllImport("pcore.dll")] static extern int mj_version();
[DllImport("pcore.dll")] static extern unsafe void mj_getState(nint model, nint data, double* state, int sig);
```

Check versions first (`mj_version() == 336`): a game update can change the library.

## Internal calls

Engine methods whose C# wrapper was stripped from the build are still reachable as internal calls:

```csharp
var set = (delegate* unmanaged<float, void>)Il2Cpp.ICall("UnityEngine.RenderSettings::set_fogDensity");
set(0.02f);
```

## Raw IL2CPP

`Jibbers.Sdk.Interop` has the whole IL2CPP API: `Il2CppClass`, `Il2CppField`, `Il2CppMethod` (find
members, read and write, invoke), `Il2Cpp` (strings, arrays, boxing), and `Il2CppApi` (every export
as a function pointer). Field offsets include the 16-byte object header; inside a struct's data,
subtract 16.

## Rules that keep the game alive

1. Touch game objects only on the main thread.
2. Never hard-code addresses or obfuscated names: use symbols, or raw bindings you re-check after
   every game update.
3. Root objects you keep (`GameRef`), check `Unity.IsNull` before using Unity objects you kept.
4. Match native signatures exactly; never let an exception escape an `[UnmanagedCallersOnly]` function.
5. Remove what you created in `OnUnload` (the SDK removes your hooks, detours, events and watches).
