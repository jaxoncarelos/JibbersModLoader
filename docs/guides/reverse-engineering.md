# Reverse engineering the game

The friendly `Game.*` API comes from a **catalogue** of symbols
(`sdk/crates/jibbers-mod-sdk/symbols.json`). Each symbol describes one game class by facts that
survive a rebuild (field types, method signatures, parent class, references to other symbols) and
gives it and its members stable names. This guide shows how to add one.

## 1. Dump the game's metadata

With the developer loader and the Inspector example mod installed, start the game and send:

```
python tools\hotload.py raw '{"cmd":"inspector.dump","images":["Assembly-CSharp.dll","fun.jibbers.map-tools.dll","Mujoco.Runtime.dll"]}'
```

It writes `jibbers_symbols_dump.json` beside `Jibbers.exe`: every class with its fields (types,
offsets) and methods (signatures). Metadata only: nothing in the game changes.

## 2. Explore

- The **API reference**'s raw entries (tick *raw classes*) list every class and member of the dump.
- `inspector.class` prints one class; `inspector.live` describes live instances with their values,
  so you can see which obfuscated field holds what.

## 3. Write a rule

```json
{
  "id": "player.ragdoll_spawner",
  "summary": "RagdollSpawner: spawns the rider at the map's spawn points.",
  "tags": ["order"],
  "match": { "name": "RagdollSpawner" },
  "members": {
    "rider": { "field": { "type": "@player.ragdoll", "static": false } },
    "selected_spawn": { "method": { "obfuscated": true, "params": [], "return": "@map.spawn_point", "nth": 0 } }
  }
}
```

- **Class conditions**: `image`, `namespace`, `name` (only for classes that keep their name),
  `obfuscated`, `parent`, `declaring`, `valuetype`, `interfaces`, `fields`, `methods`, `instance_fields`.
- **Member rules**: a `field` (`name`, `type`, `static`, `obfuscated`, `backing`) or a `method`
  (`name`, `params`, `argc`, `return`, `static`, `obfuscated`), plus `nth` (the n-th match in
  declaration order) and `inherited` (search parent classes).
- **Types** are IL2CPP names (`System.Int32`, `UnityEngine.Terrain[]`). `@id` inside a type stands for
  another symbol's class, so obfuscated classes can refer to each other.
- A class rule must match exactly one class and a member exactly one member (unless `nth`).
  Ambiguous or missing is a failure, never a guess.

Prefer unique facts (a parameter list, a return type, a field type unique in the class) over
declaration order; tag rules that rely on `nth` with `order`.

## 4. Check and regenerate

```
cd sdk/dotnet
dotnet run --project Jibbers.Sdk.Generator -c Release -- lint
dotnet run --project Jibbers.Sdk.Generator -c Release -- resolve --dump <dump.json>
dotnet run --project Jibbers.Sdk.Generator -c Release -- bindings --dump <dump.json> --out Jibbers.Sdk/Generated
dotnet run --project Jibbers.Sdk.Tests -c Release
```

`bindings` writes the C# for your symbol (`Game.<Area>.<Name>`); `docs` refreshes this site's data.

## After a game update

Dump the new build and run `resolve`. Renamed members are found again by shape; anything that
fails says why (no match, ambiguous with the candidates listed, or which member broke). Fix those
rules, regenerate, and validate the build for players (`sdk/docs/GAME-BUILDS.md`).

## Your own symbols

A mod can register private symbols at runtime: `SymbolTable.Register(json)` in `OnLoad`, then
`new ClassRef("my_mod.thing")`, `new FieldRef("my_mod.thing.value")`. Prefix ids with your mod id.
