"""Developer-only: create a temporary FBX, OBJ or GLB fixture from a local vanilla NIF."""

import math
import sys

import bpy
from mathutils import Matrix


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    if len(args) not in (4, 5, 6):
        raise ValueError("Expected addon, NIF, mesh name, output, [mirror_x] [mirror_modifier] [nodes_modifier] [two_materials] [obj_with_mtl]")
    addon_parent, nif_path, mesh_name, fbx_path = args[:4]
    flags = set(args[4:])
    sys.path.insert(0, addon_parent)
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    bpy.ops.import_scene.pynifly(filepath=nif_path, import_collisions=False)
    source = bpy.data.objects.get(mesh_name)
    if source is None or source.type != "MESH":
        raise ValueError("Vanilla fixture mesh was not found: " + mesh_name)
    mesh = source.data.copy()
    mesh.transform(Matrix.Rotation(math.pi / 2, 4, "X") @ source.matrix_world)
    if "mirror_x" in flags:
        mesh.transform(Matrix.Diagonal((-1, 1, 1, 1)))
        mesh.flip_normals()
    if "two_materials" in flags:
        if not mesh.materials or mesh.materials[0] is None:
            raise ValueError("Fixture template has no first material")
        mesh.materials.append(mesh.materials[0].copy())
        for index, polygon in enumerate(mesh.polygons):
            polygon.material_index = index % 2
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    fixture = bpy.data.objects.new("WeaponLOD0", mesh)
    bpy.context.collection.objects.link(fixture)
    fixture.select_set(True)
    bpy.context.view_layer.objects.active = fixture
    if "mirror_modifier" in flags:
        fixture.modifiers.new("FixtureMirror", "MIRROR")
    if "nodes_modifier" in flags:
        fixture.modifiers.new("FixtureNodes", "NODES")
    if fbx_path.lower().endswith(".blend"):
        bpy.ops.wm.save_as_mainfile(filepath=fbx_path, compress=True)
    elif fbx_path.lower().endswith(".glb"):
        bpy.ops.export_scene.gltf(filepath=fbx_path, export_format="GLB", use_selection=True)
    elif fbx_path.lower().endswith(".gltf"):
        bpy.ops.export_scene.gltf(filepath=fbx_path, export_format="GLTF_SEPARATE", use_selection=True)
    elif fbx_path.lower().endswith(".obj"):
        bpy.ops.wm.obj_export(filepath=fbx_path, export_selected_objects=True,
                              export_materials="obj_with_mtl" in flags)
    else:
        bpy.ops.export_scene.fbx(filepath=fbx_path, use_selection=True,
                                 add_leaf_bones=False, bake_anim=False)


if __name__ == "__main__":
    main()
