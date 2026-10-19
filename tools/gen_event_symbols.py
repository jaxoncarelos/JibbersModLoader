"""Adds events.bus, game.vars and one event.* symbol per game event struct to the SDK catalogue.

    python -I tools/gen_event_symbols.py <dump.json> sdk/crates/jibbers-mod-sdk/symbols.json

The dump (from the runtime's sdk.symbols.dump) is read only to list the event structs and their
fields. Event structs keep their real names, so the generated rules use names, not obfuscated
ones. Existing ids are left alone; rerun after a game update to pick up new events.
"""

import json
import re
import sys

# Multiplayer events are out of scope (see sdk/docs/SYMBOLS.md).
SKIP = {"ChatLineEvent", "PlayerListChangedEvent"}

# Field types that have their own symbol.
REF = {
    "on": "@replay.clip",
    "RagdollController": "@player.ragdoll",
    "Jibbers.MapTools.SpawnPointData": "@map.spawn_point",
    "Jibbers.MapTools.MapData": "@map.data",
}

SUMMARY = {
    "PlayerSpawnEvent": "The local rider spawned; ragdoll is its RagdollController.",
    "PauseEvent": "The game paused (pause menu opened).",
    "UnpauseEvent": "The game resumed.",
    "ResetEvent": "The rider is being reset to the spawn point.",
    "RagdollResetFinishedEvent": "A rider reset finished.",
    "RespawnEvent": "The rider respawned.",
    "EnterReplayEvent": "Replay playback started.",
    "ExitReplayEvent": "Replay playback ended.",
    "LoadReplayClipEvent": "A replay clip was loaded; clip is the clip object.",
    "OutfitChangeEvent": "The rider changed outfit.",
    "GearChangedEvent": "The rider changed gear.",
    "CharacterChangeEvent": "The rider changed character.",
    "ChangeSceneEvent": "The game is changing scene.",
    "BodyShapeChangedEvent": "The body shape of the rider changed.",
    "MjSceneRebuildEvent": "The MuJoCo physics scene is about to be rebuilt.",
    "MjSceneRebuiltEvent": "The MuJoCo physics scene was rebuilt.",
    "ResetEnvironmentEvent": "The environment (time of day, weather) was reset.",
    "MapLoadEvent": "A map finished loading: map is its MapData, terrains its terrains.",
    "SessionMarkerSetEvent": "The player set a session marker (custom spawn) at spawnPoint.",
    "SpawnChangedEvent": "The selected spawn point changed.",
    "EnterOrbitCamEvent": "The orbit camera was entered.",
    "ExitOrbitCamEvent": "The orbit camera was left.",
    "PlacedObjectsChangedEvent": "Objects placed with the object placer changed.",
    "EnterObjectPlacerEvent": "The object placer was opened.",
    "ExitObjectPlacerEvent": "The object placer was closed.",
    "ToggleClothesEvent": "Cloth simulation was switched on or off.",
    "ToggleReplayInputVisualizerEvent": "The replay input visualizer was toggled.",
    "AmbientColorChangedEvent": "The ambient light colour and intensity changed.",
    "TimeChangedEvent": "The time of day changed.",
    "HideMouseCursorEvent": "The mouse cursor was hidden.",
    "ShowMouseCursorEvent": "The mouse cursor was shown.",
    "TrickPerformedEvent": "The rider performed (or extended) a trick: name, details, sequenceId, isUpdate.",
}

BUS_TYPE = "System.Collections.Generic.Dictionary<System.Type,System.Collections.Generic.List<System.ValueTuple<System.Object,System.Delegate>>>"
TABLE = "System.Collections.Generic.Dictionary<System.String,{}>"
LISTENERS = "System.Collections.Generic.Dictionary<System.String,System.Collections.Generic.List<System.ValueTuple<System.Object,System.Action<{}>>>>"


def snake(name: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "_", name).lower()


def fixed_symbols() -> list:
    vars_members = {}
    for kind, ty in [("bool", "System.Boolean"), ("float", "System.Single"), ("int", "System.Int32"), ("string", "System.String")]:
        vars_members[kind + "s"] = {"field": {"type": TABLE.format(ty), "static": True}}
        vars_members[kind + "_listeners"] = {"field": {"type": LISTENERS.format(ty), "static": True}}
    return [
        {
            "id": "events.bus",
            "summary": "The game's local event bus: listeners per event type, as (owner, Action<T>) pairs. Use the SDK's events module rather than this directly.",
            "tags": ["shape", "events"],
            "notes": "Obfuscated class (`baa` on 2026-10-18). NetBus has a field of the same type but keeps its name: it is the network bus and is not covered.",
            "match": {"obfuscated": True, "fields": [{"type": BUS_TYPE, "static": True}]},
            "members": {"handlers": {"field": {"type": BUS_TYPE, "static": True}}},
        },
        {
            "id": "game.vars",
            "summary": "The game's global data registry: named settings (bool, float, int and string tables with change listeners), plus maps, outfits, characters and poses. Use the SDK's vars module for the settings.",
            "tags": ["shape", "settings"],
            "notes": "Obfuscated static class (`ia` on 2026-10-18).",
            "match": {
                "obfuscated": True,
                "fields": [
                    {"type": TABLE.format("System.Boolean"), "static": True},
                    {"type": TABLE.format("System.Single"), "static": True},
                    {"type": LISTENERS.format("System.Boolean"), "static": True},
                ],
            },
            "members": vars_members,
        },
    ]


def event_symbols(dump: dict, have: set) -> list:
    out = []
    for k in dump["classes"]:
        n = k["name"]
        if k["image"] != "Assembly-CSharp.dll" or k["namespace"] or not k["valuetype"] or not n.endswith("Event") or n in SKIP:
            continue
        sid = "event." + snake(n[: -len("Event")])
        if sid in have or n == "MapLoadEvent":
            continue
        members = {}
        for f in k["fields"]:
            if f.get("static"):
                continue
            rule = {"name": f["name"]}
            if f["type"] in REF:
                rule["type"] = REF[f["type"]]
            members[snake(f["name"])] = {"field": rule}
        debug = n.endswith("DebugEvent")
        summary = SUMMARY.get(n) or ("Debug input: " + n[: -len("DebugEvent")] + "." if debug else n + ".")
        out.append({"id": sid, "summary": summary, "tags": ["event", "debug"] if debug else ["event"], "match": {"name": n, "valuetype": True}, "members": members})
    return out


def render(sym: dict) -> str:
    """One line per key and per member, the way the file is written by hand."""
    lines = ["    {"]
    for k, v in sym.items():
        if k != "members":
            lines.append(f"      {json.dumps(k)}: {json.dumps(v, ensure_ascii=False)},")
    members = sym.get("members", {})
    if members:
        lines.append('      "members": {')
        lines.append(",\n".join(f"        {json.dumps(n)}: {json.dumps(r, ensure_ascii=False)}" for n, r in members.items()))
        lines.append("      }")
    else:
        lines.append('      "members": {}')
    lines.append("    }")
    return "\n".join(lines)


def main(argv: list) -> int:
    if len(argv) != 2:
        print(__doc__)
        return 2
    dump = json.load(open(argv[0], encoding="utf8"))
    cat = json.load(open(argv[1], encoding="utf8"))
    have = {s["id"] for s in cat["symbols"]}
    new = [s for s in fixed_symbols() if s["id"] not in have] + event_symbols(dump, have)
    for s in cat["symbols"]:
        if s["id"] == "map.load_event" and "event" not in s["tags"]:
            s["tags"].append("event")
    cat["symbols"].extend(new)
    with open(argv[1], "w", encoding="utf8", newline="\n") as f:
        f.write('{\n  "symbols": [\n' + ",\n".join(render(s) for s in cat["symbols"]) + "\n  ]\n}\n")
    print(f"{len(new)} symbols added")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
