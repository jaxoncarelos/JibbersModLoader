# Concepts

## The game is IL2CPP

Jibbers is a Unity game compiled with IL2CPP: its C# was turned into native code, and most of its
class, field and method names were obfuscated to two or three letters (`uzv`, `bjc`, `<sdj>k__BackingField`).
The names change with every game update.

The SDK gives you two views of the game:

| | `Jibbers.Sdk.Game.*` (friendly) | `Jibbers.Interop.*` (raw) |
| --- | --- | --- |
| Names | stable, readable: `Game.Ui.NavigatableMenu.AddButton` | the game's real names: `NavigatableMenu.gyu` |
| Survives game updates | yes (found by shape: types, signatures) | no: obfuscated names change |
| Coverage | the classes mods use (the catalogue) | every class of the game, MapTools and MuJoCo |

Use `Game.*` whenever it has what you need. Every friendly type has `.Raw` to reach all the other
members. When you rely on a raw member, consider [adding a symbol](#guide/reverse-engineering).

## Game objects are handles

A game object is a pointer into the game's memory. Typed handles (`Game.Camera.Follow`,
`Jibbers.Interop.UnityEngine.Transform`) wrap it:

```csharp
var cam = Follow.First();            // the live camera, or null
if (cam is { } c && !c.IsDead())     // IsDead: null or a destroyed Unity object
{
    var target = c.Target;           // a field, typed
    c.PhotoMode = false;             // write it
}
```

- **Lifetime.** The game's garbage collector does not know about your variables. A handle you keep
  between frames can point at a freed object. Keep it with `.Keep()` (a `GameRef`) or look it up again.
- **Destroyed objects.** A destroyed Unity object is still a managed object; check `IsDead()`.

## The main thread

Unity is single-threaded. Every call that touches the game must run on its main thread:

- hook, detour, event, setting-watch and key-binding callbacks already do;
- from anywhere else, use `MainThread.Run(() => ...)` (waits) or `MainThread.Later(() => ...)` (doesn't).

`OnLoad`, `OnSettingChanged` and `OnCommand` are **not** on the main thread. Subscribing to events
and binding keys from `OnLoad` is fine: the SDK applies them on the main thread for you.

## Reacting to the game

| Tool | Use it for | Limits |
| --- | --- | --- |
| **Events** `TrickPerformed.Subscribe(...)` | spawn, map load, tricks, replay, pause, time of day... (41 events) | none, no slot used |
| **Game settings** `GameVars.Watch("shadow-distance", v => ...)` | any option the game's menus bind | none |
| **Pre-hooks** `Follow.LateUpdateMethod.Before(...)` | run before a game method, every call | void/bool methods, ≤2 non-float params, 12 per game |
| **Around hooks** `Scene.StepSceneMethod.Around(original => ...)` | run, skip or wrap a game method | zero-argument void instance methods; original must be called synchronously |
| **Typed detours** `method.Detour<Signature>(callback)` | replace or wrap any native signature | declare a matching delegate; call `hook.Original(...)` |
| **Input** `Input.OnPressed(() => ApplyKey, ...)` | keys, mouse, the game's controller actions | callbacks only while focused and not typing |

Event, pre-hook, Around, input and setting-watch callbacks are skipped while your mod's **enabled**
toggle is off. Custom typed/raw detour callbacks must check `Enabled` themselves. All SDK-owned
hooks are removed when your mod unloads.

## Settings

Prefer typed properties marked with `[Toggle]`, `[Slider]`, `[Choice]`, or `[KeyBinding]`.
The SDK fills them before `OnLoad` and updates them before `OnSettingChanged`.
For dynamic schemas, declare them in `Settings`; the SDK shows them in the Mods tab, validates and saves them
(`JibbersMods\<id>.settings.json`), and calls `OnSettingChanged`. Read them with `GetBool`,
`GetDouble`, `GetString`, `GetKey`. Kinds: `Toggle`, `Slider`, `Choice`, `KeyBinding`; group them
with `.InSection(...)` and explain them with `.WithTooltip(...)`.

## Commands

`OnCommand` answers JSON requests `{"cmd": "<id>.something", ...}` from other mods and from the
developer loader's localhost port (`tools\hotload.py raw '<json>'`). Prefix commands with your id.

## Errors

- A symbol that does not resolve on the running build throws `SymbolException` with the reason.
- A game method that throws surfaces as `Il2CppException` with the game's message and stack trace.
- Exceptions in your callbacks are caught and logged once; they never crash the game. Native
  mistakes (bad pointers, wrong detour signatures) **do** crash it: see [Native code](#guide/native).

See [C# authoring](csharp-authoring.md) for typed examples and the public gameplay controllers.
