"""Developer-only: create a temporary FBX test fixture from a locally installed vanilla NIF."""

import math
import sys

import bpy
from mathutils import Matrix


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    if len(args) not in (4, 5):
        raise ValueError("Expected addon, NIF, mesh name, FBX, [mirror_x|two_materials]")
    addon_parent, nif_path, mesh_name, fbx_path = args[:4]
    sys.path.insert(0, addon_parent)
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    bpy.ops.import_scene.pynifly(filepath=nif_path, import_collisions=False)
    source = bpy.data.objects.get(mesh_name)
    if source is None or source.type != "MESH":
        raise ValueError("Vanilla fixture mesh was not found: " + mesh_name)
    mesh = source.data.copy()
    mesh.transform(Matrix.Rotation(math.pi / 2, 4, "X") @ source.matrix_world)
    if len(args) == 5 and args[4] == "mirror_x":
        mesh.transform(Matrix.Diagonal((-1, 1, 1, 1)))
        mesh.flip_normals()
    if len(args) == 5 and args[4] == "two_materials":
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
    if fbx_path.lower().endswith(".glb"):
        bpy.ops.export_scene.gltf(filepath=fbx_path, export_format="GLB", use_selection=True)
    else:
        bpy.ops.export_scene.fbx(filepath=fbx_path, use_selection=True,
                                 add_leaf_bones=False, bake_anim=False)


if __name__ == "__main__":
    main()
