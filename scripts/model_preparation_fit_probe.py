"""Inspect FBX and vanilla sword coordinates together without writing NIF."""

import json
import os
import sys
import traceback

import bpy
sys.path.insert(0, os.path.dirname(__file__))
from model_preparation_probe import inspect_archive


def object_info(obj):
    row = {"name": obj.name, "type": obj.type, "location": list(obj.location),
           "rotation_euler": list(obj.rotation_euler), "scale": list(obj.scale)}
    if obj.type == "MESH" and obj.data.vertices:
        points = [obj.matrix_world @ vertex.co for vertex in obj.data.vertices]
        row.update(vertices=len(obj.data.vertices), polygons=len(obj.data.polygons),
                   uv_layers=len(obj.data.uv_layers),
                   bounds_min=[min(p[i] for p in points) for i in range(3)],
                   bounds_max=[max(p[i] for p in points) for i in range(3)])
    return row


def main():
    addon_parent, archive_path, template_path, report_path = sys.argv[sys.argv.index("--") + 1:]
    sys.path.insert(0, addon_parent)
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    archive_hash, selected_model = inspect_archive(archive_path)
    fbx_objects = list(bpy.context.scene.objects)
    imported = bpy.ops.import_scene.pynifly(filepath=template_path, import_collisions=True)
    if "CANCELLED" in imported:
        raise RuntimeError("Template NIF import cancelled")
    template_objects = [obj for obj in bpy.context.scene.objects if obj not in fbx_objects]
    report = {"archive_sha256": archive_hash, "selected_model": selected_model,
              "template": template_path,
              "fbx_objects": [object_info(obj) for obj in fbx_objects],
              "template_objects": [object_info(obj) for obj in template_objects]}
    with open(report_path, "w", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, ensure_ascii=False)
    print("MODEL_PREPARATION_FIT_PROBE_OK " + report_path)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        sys.exit(1)
