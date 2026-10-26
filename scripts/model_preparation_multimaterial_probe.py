"""Developer probe: export two weapon shapes under one Skyrim SE NIF root."""

import json
import sys

import bmesh
import bpy


def main():
    addon_parent, template_path, output_path, report_path = sys.argv[sys.argv.index("--") + 1:]
    sys.path.insert(0, addon_parent)
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    bpy.ops.import_scene.pynifly(filepath=template_path, import_collisions=True)
    weapon = bpy.data.objects["IronLongSword01:0"]
    original = weapon.data
    rows = []
    for slot in range(2):
        obj = weapon if slot == 0 else weapon.copy()
        if slot:
            obj.name = "IronLongSword01_Material1:0"
            bpy.context.collection.objects.link(obj)
        mesh = original.copy()
        bm = bmesh.new()
        bm.from_mesh(mesh)
        bm.faces.ensure_lookup_table()
        remove = [face for index, face in enumerate(bm.faces) if index % 2 != slot]
        bmesh.ops.delete(bm, geom=remove, context="FACES")
        bm.to_mesh(mesh)
        bm.free()
        obj.data = mesh
        if slot:
            material = original.materials[0].copy()
            material.name = "ProbeSecondMaterial"
            mesh.materials.clear()
            mesh.materials.append(material)
        rows.append({"name": obj.name, "faces": len(mesh.polygons),
                     "materials": [item.name for item in mesh.materials]})
    bpy.ops.object.select_all(action="SELECT")
    bpy.context.view_layer.objects.active = weapon
    bpy.ops.export_scene.pynifly(filepath=output_path, target_game="SKYRIMSE",
                                  write_tris=False, write_bodytri=False,
                                  export_animations=False)
    with open(report_path, "w", encoding="utf-8") as file:
        json.dump(rows, file, indent=2)
    print("MULTIMATERIAL_PROBE_OK", output_path)


if __name__ == "__main__":
    main()
