"""Read map/prefab lighting inputs without modifying the game or its assets.

Requires UnityPy. The report describes potential instantiated lights, not a live
GPU capture: scripts may change their enabled state, colour, range or intensity.
"""

from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path


def file_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read_light_templates(resources_path: Path) -> dict[str, list[dict]]:
    import UnityPy

    resources = UnityPy.load(str(resources_path))
    templates: dict[str, list[dict]] = {}
    for asset in resources.objects:
        if asset.type.name != "Light":
            continue
        light = asset.read_typetree()
        game_object = asset.read().m_GameObject.read()
        transform = next(
            component.component.read()
            for component in game_object.m_Component
            if component.component.type.name == "Transform"
        )
        hierarchy = []
        while True:
            transform_data = transform.object_reader.read_typetree()
            hierarchy.append({
                "name": transform.m_GameObject.read().m_Name,
                "position": transform_data["m_LocalPosition"],
                "rotation": transform_data["m_LocalRotation"],
                "scale": transform_data["m_LocalScale"],
            })
            if not transform.m_Father.path_id:
                break
            transform = transform.m_Father.read()
        hierarchy.reverse()
        prefab_name = hierarchy[0]["name"]
        templates.setdefault(prefab_name, []).append({
            "hierarchy": hierarchy,
            "type": light["m_Type"],
            "enabled_in_prefab": bool(light["m_Enabled"]),
            "colour": light["m_Color"],
            "intensity": light["m_Intensity"],
            "range": light["m_Range"],
            "spot_angle": light["m_SpotAngle"],
            "inner_spot_angle": light["m_InnerSpotAngle"],
            "shadow_type": light["m_Shadows"]["m_Type"],
            "is_baked": light["m_BakingOutput"]["isBaked"],
        })
    return templates


def map_light_instances(map_data: dict, templates: dict) -> list[dict]:
    instances = []
    for chunk_id, chunk in map_data.get("chunks", {}).items():
        for object_id, placed_object in chunk.get("objects", {}).items():
            prefab_id = placed_object.get("id")
            if prefab_id not in templates:
                continue
            # Keep the authored transforms and repetition offsets separately.
            # Runtime capture must confirm their interpretation before baking.
            instances.append({
                "chunk": chunk_id,
                "object": object_id,
                "placement": placed_object,
                "repeat_count": max(1, int(chunk.get("repeats", 1))),
                "repeat_offset": {
                    axis: chunk.get(f"repeat-offset-{axis}", 0)
                    for axis in "xyz"
                },
            })
    return instances


def build_report(map_path: Path, resources_path: Path) -> dict:
    map_data = json.loads(map_path.read_text(encoding="utf-8-sig"))
    templates = read_light_templates(resources_path)
    instances = map_light_instances(map_data, templates)
    prefab_counts: Counter[str] = Counter()
    realtime_spotlights = 0
    shadow_spotlights = 0
    for instance in instances:
        prefab_id = instance["placement"]["id"]
        repeats = instance["repeat_count"]
        prefab_counts[prefab_id] += repeats
        for light in templates[prefab_id]:
            if light["type"] == 0 and not light["is_baked"]:
                realtime_spotlights += repeats
                if light["shadow_type"] != 0:
                    shadow_spotlights += repeats
    return {
        "schema": 1,
        "scope": "Authored map and prefab data; live script state and GPU cost unmeasured",
        "map": {
            "id": map_data.get("id"),
            "name": map_data.get("name"),
            "sha256": file_hash(map_path),
        },
        "resources_sha256": file_hash(resources_path),
        "potential_lighting": {
            "prefab_instances": dict(prefab_counts),
            "realtime_spotlights": realtime_spotlights,
            "shadow_casting_spotlights": shadow_spotlights,
        },
        "light_templates": {
            prefab_id: templates[prefab_id] for prefab_id in prefab_counts
        },
        "instances": instances,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--map", type=Path, required=True, dest="map_path")
    parser.add_argument("--resources", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    arguments = parser.parse_args()
    report = build_report(arguments.map_path, arguments.resources)
    arguments.out.parent.mkdir(parents=True, exist_ok=True)
    arguments.out.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(report["map"]["name"])
    print(json.dumps(report["potential_lighting"], indent=2))
    print(f"Report: {arguments.out.resolve()}")


if __name__ == "__main__":
    main()
