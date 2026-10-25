"""Prototype only: import and re-export a vanilla Skyrim SE NIF with PyNifly.

Run inside Blender with -- <PyNifly parent> <input NIF> <output NIF> <report JSON>.
The output should be in a disposable test directory, never in Skyrim Data.
"""

import json
import os
import sys
import traceback

import bpy


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    if len(args) != 4:
        raise ValueError("Expected PyNifly parent, input NIF, output NIF, report JSON")
    addon_parent, source, target, report_path = args
    sys.path.insert(0, addon_parent)
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    import io_scene_nifly
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    imported = bpy.ops.import_scene.pynifly(filepath=source, import_collisions=True)
    if "FINISHED" not in imported:
        raise RuntimeError("PyNifly did not finish importing the reference NIF")
    objects = []
    for obj in bpy.context.scene.objects:
        row = {"name": obj.name, "type": obj.type}
        if obj.type == "MESH":
            points = [obj.matrix_world @ v.co for v in obj.data.vertices]
            row.update(vertices=len(obj.data.vertices), polygons=len(obj.data.polygons),
                       uv_layers=len(obj.data.uv_layers),
                       has_custom_normals=bool(obj.data.has_custom_normals)
                           if hasattr(obj.data, "has_custom_normals") else None)
            if points:
                row.update(bounds_min=[min(p[i] for p in points) for i in range(3)],
                           bounds_max=[max(p[i] for p in points) for i in range(3)])
        objects.append(row)
    if not any(obj["type"] == "MESH" for obj in objects):
        raise RuntimeError("Reference NIF import has no geometry")
    bpy.ops.object.select_all(action="SELECT")
    exported = bpy.ops.export_scene.pynifly(filepath=target, target_game="SKYRIMSE",
                                             write_tris=False, write_bodytri=False,
                                             export_animations=False)
    print("EXPORT_OPERATOR_RESULT", exported, "FILE_EXISTS", os.path.isfile(target))
    # PyNifly 29.1.0 returns an empty operator status set in background mode
    # even when it logs success. The exported file must be validated separately.
    if "CANCELLED" in exported or not os.path.isfile(target):
        raise RuntimeError("PyNifly did not produce the reference NIF")
    report = {"blender_version": bpy.app.version_string,
              "pynifly_version": list(io_scene_nifly.bl_info["version"]),
              "source": source, "exported_bytes": os.path.getsize(target),
              "objects": objects}
    with open(report_path, "w", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, ensure_ascii=False)
    print("MODEL_PREPARATION_ROUNDTRIP_OK " + target)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        print("MODEL_PREPARATION_ROUNDTRIP_FAILED", file=sys.stderr)
        sys.exit(1)
