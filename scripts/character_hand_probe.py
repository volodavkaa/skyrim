"""Read-only probe of game skeleton and skin import for a hand preview."""
import bpy
import json
import os
import sys

args = sys.argv[sys.argv.index('--') + 1:]
addon, directory, output = args[:3]
gender = args[3] if len(args) > 3 else 'male'
animation = args[4] if len(args) > 4 else '1hm_idle'
if gender not in ('male', 'female'):
    raise ValueError('Gender must be male or female')
sys.path.insert(0, addon)
bpy.ops.preferences.addon_enable(module='io_scene_nifly')
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
for name in ('skeleton', gender + 'body_1', gender + 'hands_1'):
    print('IMPORT', name, flush=True)
    if name != 'skeleton':
        bpy.ops.object.select_all(action='DESELECT')
        rig = next(obj for obj in bpy.context.scene.objects if obj.type == 'ARMATURE')
        rig.select_set(True)
        bpy.context.view_layer.objects.active = rig
    print(bpy.ops.import_scene.pynifly(filepath=os.path.join(directory, name + '.nif')), flush=True)
bpy.ops.object.select_all(action='DESELECT')
rig = next(obj for obj in bpy.context.scene.objects if obj.type == 'ARMATURE')
rig.select_set(True)
bpy.context.view_layer.objects.active = rig
if not hasattr(bpy.types.Action, 'fcurve_ensure_for_datablock'):
    def legacy_fcurve(action, datablock, path, index):
        return action.fcurves.find(path, index=index) or action.fcurves.new(path, index=index)
    bpy.types.Action.fcurve_ensure_for_datablock = legacy_fcurve
print('ANIMATION', bpy.ops.import_scene.pynifly_hkx(filepath=os.path.join(directory, animation + '.hkx'),
      reference_skel=os.path.join(directory, 'skeleton.hkx')), flush=True)
print('ACTION', rig.animation_data.action.name if rig.animation_data and rig.animation_data.action else None, flush=True)
bpy.context.scene.frame_set(1)
for name in ('WEAPON', 'NPC Hand.R'):
    bone = rig.pose.bones.get(name)
    if bone:
        print('POSE_BONE', name, 'matrix', [list(row) for row in (rig.matrix_world @ bone.matrix)], flush=True)
objects = []
for obj in bpy.context.scene.objects:
    item = {'name': obj.name, 'type': obj.type, 'parent': obj.parent.name if obj.parent else None,
            'matrix': [list(row) for row in obj.matrix_world]}
    if obj.type == 'ARMATURE':
        item['bones'] = [{'name': bone.name, 'head': list(bone.head_local),
                          'tail': list(bone.tail_local)} for bone in obj.data.bones]
    if obj.type == 'MESH':
        item['vertices'] = len(obj.data.vertices)
        item['modifiers'] = [(mod.type, mod.object.name if mod.type == 'ARMATURE' and mod.object else '') for mod in obj.modifiers]
        item['groups'] = [group.name for group in obj.vertex_groups]
    objects.append(item)
with open(output, 'w', encoding='utf-8') as stream:
    json.dump(objects, stream, indent=2)
print('CHARACTER_PROBE_OK', flush=True)
