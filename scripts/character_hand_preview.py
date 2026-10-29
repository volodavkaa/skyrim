"""Build a temporary, posed preview from locally installed Skyrim assets."""
import json
import os
import sys
import bpy

addon, directory, output, gender, animation, attachment = sys.argv[sys.argv.index('--') + 1:]
if gender not in ('male', 'female') or animation not in ('1hm_idle', '2hm_idle', 'sneak1hm_idle', 'sheathed') or attachment not in ('WEAPON', 'WeaponSword', 'WeaponDagger', 'WeaponAxe', 'WeaponMace', 'WeaponBack'):
    raise ValueError('Unsupported character or pose')
sys.path.insert(0, addon)
bpy.ops.preferences.addon_enable(module='io_scene_nifly')
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
if 'FINISHED' not in bpy.ops.import_scene.pynifly(filepath=os.path.join(directory, 'skeleton.nif'), import_collisions=False):
    raise RuntimeError('Skeleton import failed')
rigs = [obj for obj in bpy.context.scene.objects if obj.type == 'ARMATURE']
if len(rigs) != 1:
    raise RuntimeError('Expected one Skyrim armature')
rig = rigs[0]
meshes = []
for name in (gender + 'body_1', gender + 'hands_1', gender + 'feet_1', gender + 'head'):
    old = set(bpy.context.scene.objects)
    bpy.ops.object.select_all(action='DESELECT')
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    if 'FINISHED' not in bpy.ops.import_scene.pynifly(filepath=os.path.join(directory, name + '.nif'), import_collisions=False):
        raise RuntimeError('Character mesh import failed: ' + name)
    meshes.extend(obj for obj in bpy.context.scene.objects if obj not in old and obj.type == 'MESH')
if not hasattr(bpy.types.Action, 'fcurve_ensure_for_datablock'):
    def legacy_fcurve(action, datablock, path, index):
        return action.fcurves.find(path, index=index) or action.fcurves.new(path, index=index)
    bpy.types.Action.fcurve_ensure_for_datablock = legacy_fcurve
bpy.ops.object.select_all(action='DESELECT')
rig.select_set(True)
bpy.context.view_layer.objects.active = rig
bpy.ops.import_scene.pynifly_hkx(filepath=os.path.join(directory, animation + '.hkx'),
                                 reference_skel=os.path.join(directory, 'skeleton.hkx'))
action = rig.animation_data.action if rig.animation_data else None
if action is None or len(action.fcurves) == 0:
    raise RuntimeError('Animation import failed')
bpy.context.scene.frame_set(max(1, int(action.frame_range[1] / 2)))
depsgraph = bpy.context.evaluated_depsgraph_get()
weapon = rig.pose.bones.get(attachment)
if weapon is None:
    raise RuntimeError('Skyrim skeleton has no attachment node: ' + attachment)
rows = []
for obj in meshes:
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        mesh.calc_loop_triangles()
        if not mesh.vertices or not mesh.loop_triangles:
            continue
        matrix = evaluated.matrix_world
        for material_index in range(max(1, len(mesh.materials))):
            faces = [face for face in mesh.loop_triangles if face.material_index == material_index]
            if not faces:
                continue
            uv_layer = mesh.uv_layers.active
            points, uvs, indices, lookup = [], [], [], {}
            for face in faces:
                for loop_index in face.loops:
                    loop = mesh.loops[loop_index]
                    uv = uv_layer.data[loop_index].uv if uv_layer else (0, 0)
                    key = (loop.vertex_index, round(uv[0], 6), round(uv[1], 6))
                    if key not in lookup:
                        lookup[key] = len(points)
                        points.append(list(matrix @ mesh.vertices[loop.vertex_index].co))
                        uvs.append([uv[0], 1 - uv[1]])
                    indices.append(lookup[key])
            material = mesh.materials[material_index] if len(mesh.materials) > material_index else None
            texture = material.get('BSShaderTextureSet_Diffuse') if material else None
            rows.append({'name': obj.name, 'positions': points, 'uv': uvs,
                         'indices': indices, 'diffuse': texture})
    finally:
        evaluated.to_mesh_clear()
if not rows:
    raise RuntimeError('No character geometry')
with open(output, 'w', encoding='utf-8') as stream:
    json.dump({'meshes': rows, 'weapon_matrix': [list(row) for row in (rig.matrix_world @ weapon.matrix)]}, stream)
print('CHARACTER_PREVIEW_OK', flush=True)
