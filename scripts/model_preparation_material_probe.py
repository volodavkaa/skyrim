"""Read-only inventory of source material slots and archive image candidates."""

import json
import os
import sys
import traceback
import zipfile

import bpy

sys.path.insert(0, os.path.dirname(__file__))
from model_preparation_probe import inspect_archive, select_weapon_mesh


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    if len(args) not in (2, 3, 4):
        raise ValueError("Expected archive, report, [PyNifly folder], [model entry]")
    archive_path, output_path = args[:2]
    archive_hash, model = inspect_archive(archive_path,
        args[2] if len(args) >= 3 else None, args[3] if len(args) == 4 else None)
    selected_mesh = select_weapon_mesh().name
    objects = []
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        materials = []
        for slot in obj.material_slots:
            material = slot.material
            nodes = []
            if material and material.use_nodes:
                for node in material.node_tree.nodes:
                    row = {"name": node.name, "type": node.bl_idname}
                    if node.type == "TEX_IMAGE" and node.image:
                        row["image"] = node.image.filepath
                    nodes.append(row)
            materials.append({"name": material.name if material else None,
                              "nodes": nodes})
        objects.append({"name": obj.name, "polygons": len(obj.data.polygons),
                        "uv_layers": len(obj.data.uv_layers),
                        "materials": materials,
                        "applied_modifiers": list(obj.get("ModelPreparationAppliedModifiers", [])),
                        "polygon_material_indices": sorted(set(poly.material_index
                                                               for poly in obj.data.polygons))})
    with zipfile.ZipFile(archive_path) as archive:
        images = [{"name": item.filename, "bytes": item.file_size}
                  for item in archive.infolist() if not item.is_dir() and
                  os.path.splitext(item.filename)[1].lower() in
                  {".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds"}]
    with open(output_path, "w", encoding="utf-8") as output:
        json.dump({"archive_sha256": archive_hash, "selected_model": model,
                   "selected_mesh": selected_mesh,
                   "meshes": objects, "images": images}, output,
                  indent=2, ensure_ascii=False)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        sys.exit(1)
