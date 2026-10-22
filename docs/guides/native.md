# Native code

Mods can wrap game methods with ordinary callbacks. Typed delegates cover custom native signatures;
raw pointers remain available for direct memory access and engine internal calls.

## Detours

A zero-argument void instance method can be wrapped directly:

```csharp
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Physics;

MethodHook? step;

public override void OnLoad()
{
    step = Scene.StepSceneMethod.Around(original =>
    {
        if (!paused)
            original();
    });
}
```

`original()` runs the game method with the instance and metadata of the current invocation. Omitting
it skips that call. Hooks start enabled, bypass disabled mods, and are removed on unload. Use
`Around((instance, original) => ...)` when you also need the instance pointer.

Callbacks run on the intercepted method's calling thread. Call `original()` synchronously there;
do not save it or pass it to another thread. A callback exception is logged once, the hook switches
to pass-through, and the original runs if it was not already called. `Around` rejects other signatures
before installing anything. The [SlowMo example](../../../sdk/dotnet/examples/SlowMo/SlowMo.cs)
shows a full mod without unsafe code.

## Typed detours for other signatures

Declare the native signature once; the original is then an ordinary callable delegate:

```csharp
using System.Runtime.InteropServices;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void StepScene(nint scene, nint methodInfo);

Detour<StepScene>? step;

public override void OnLoad()
{
    step = Scene.StepSceneMethod.Detour<StepScene>(Step);
    step.Enable();
}

void Step(nint scene, nint methodInfo) => step!.Original(scene, methodInfo);
```

**Typed detour signatures must still match exactly.** A game method compiled by IL2CPP takes
`(this, arguments..., MethodInfo*)`; a static method has no `this`. Arguments use the Windows x64
convention: integers, pointers and object references are `nint`/`int`/`long`; `float`/`double`
are themselves; structs of 1, 2, 4 or 8 bytes are passed by value, bigger ones as a pointer. A
wrong signature crashes the game.

- `Detour<TDelegate>.Create(target, replacement)` also accepts an engine or native-library address.
- Keep exceptions inside custom native delegate callbacks; `Around` supplies its own exception boundary.
- `Detour.Create(target, replacement)` remains available for function pointers: `MethodRef.NativeCode`,
  `Il2CppMethod.NativeCode`, `NativeLibrary.GetExport(...)`, `Il2Cpp.ICall("...")`.
- Do not use a saved original delegate after disposing the detour.
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
