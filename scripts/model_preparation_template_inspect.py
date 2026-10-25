"""Inspect template shape relationships without writing a NIF."""

import json
import sys
import traceback

import bpy


def main():
    addon_parent, template_path, report_path = sys.argv[sys.argv.index("--") + 1:]
    sys.path.insert(0, addon_parent)
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    bpy.ops.import_scene.pynifly(filepath=template_path, import_collisions=True)
    rows = []
    for obj in bpy.context.scene.objects:
        row = {"name": obj.name, "type": obj.type,
               "parent": obj.parent.name if obj.parent else None,
               "materials": [slot.material.name if slot.material else None for slot in obj.material_slots],
               "custom_properties": sorted(key for key in obj.keys() if not key.startswith("_"))}
        if obj.type == "MESH" and not obj.name.startswith("bhk"):
            row["material_nodes"] = [{"material": slot.material.name,
                                      "nodes": [{"name": node.name, "type": node.bl_idname,
                                                 "image": node.image.filepath if node.type == "TEX_IMAGE" and node.image else None}
                                                for node in slot.material.node_tree.nodes]}
                                     for slot in obj.material_slots
                                     if slot.material and slot.material.use_nodes]
        if obj.type == "MESH":
            points = [obj.matrix_world @ vertex.co for vertex in obj.data.vertices]
            row.update(vertices=len(obj.data.vertices), polygons=len(obj.data.polygons),
                       uv_layers=len(obj.data.uv_layers),
                       polygon_material_indices=sorted(set(p.material_index for p in obj.data.polygons)),
                       bounds_min=[min(p[i] for p in points) for i in range(3)],
                       bounds_max=[max(p[i] for p in points) for i in range(3)])
        if obj.name.startswith("bhk"):
            row["matrix_world"] = [list(row) for row in obj.matrix_world]
            row["dimensions"] = list(obj.dimensions)
            row["scale"] = list(obj.scale)
            row["rigid_body"] = ({"type": obj.rigid_body.type,
                                  "collision_shape": obj.rigid_body.collision_shape}
                                 if obj.rigid_body else None)
            row["properties"] = {key: (obj[key].to_dict() if hasattr(obj[key], "to_dict")
                                       else str(obj[key])[:200]) for key in obj.keys()
                                 if not key.startswith("_")}
        rows.append(row)
    with open(report_path, "w", encoding="utf-8") as stream:
        json.dump(rows, stream, indent=2, ensure_ascii=False)
    print("MODEL_PREPARATION_TEMPLATE_INSPECT_OK " + report_path)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        sys.exit(1)
