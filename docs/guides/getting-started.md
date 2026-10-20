# Getting started

Jibbers mods are written in C#. You write a class library, drop the DLL in the game folder, and the
mod loader runs it inside the game, with its own section in **Options > Mods**.

## What you need

- **Windows**, the game (Steam: *Jibbers Playtest*), and the **.NET 8 SDK** ([download](https://dotnet.microsoft.com/download/dotnet/8.0)).
- The **Jibbers mod loader and C# SDK** installed in the game folder:
  - `version.dll` next to `Jibbers.exe` (the loader)
  - `JibbersMods\jibbers_dotnet.dll` (starts .NET inside the game)
  - `JibbersMods\dotnet\Jibbers.Sdk.dll` (this SDK; players get the same files)

  Players need no .NET install: the package ships the runtime in `JibbersMods\dotnet\runtime\`.

## Your first mod

1. Copy `sdk/dotnet/template/MyMod` and rename the folder, the `.csproj` and the class.
2. Change `Id` (lowercase letters, digits, underscores) and `Name`.
3. Build:

   ```
   dotnet build -c Release
   ```

   With the game at the default Steam path, the build copies the DLL to `JibbersMods\cs\`. Set
   `JibbersGame` in the `.csproj` if your game is elsewhere.
4. Start the game, open **Options > Mods**: your mod has a section with its settings.
5. Read `jibbers_hotload.log` beside `Jibbers.exe` for your `Log(...)` lines and any errors.

A new DLL needs a game restart.

## A minimal mod

```csharp
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Event;

public sealed class Hello : JibbersMod
{
    public override string Id => "hello";
    public override string Name => "Hello";

    public override IEnumerable<Setting> Settings => new[]
    {
        Setting.Toggle("tricks", "Log tricks", true),
    };

    public override void OnLoad()
    {
        TrickPerformed.Subscribe(t =>
        {
            if (GetBool("tricks") && !t.IsUpdate)
            {
                Log($"{t.Name}: {t.Details}");
            }
        });
    }
}
```

## Where to go next

- [Concepts](#guide/concepts): the main thread, game objects, events, hooks and detours, settings, input.
- [Coverage](#guide/coverage): how every existing mod's technique maps to the C# API.
- The **API reference** (search box above): every type, member, game class and event, with its arguments.
- [Reverse engineering](#guide/reverse-engineering): finding and naming new game members.
- [Native code](#guide/native): detours of any function, calling native libraries, raw pointers.
