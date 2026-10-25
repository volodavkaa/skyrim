"""Experimental FBX-to-vanilla-sword export; Skyrim game acceptance remains pending."""

import json
import math
import os
import re
import sys
import traceback

import bmesh
import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(__file__))
from model_preparation_probe import inspect_archive


def bounds(obj):
    points = [obj.matrix_world @ v.co for v in obj.data.vertices]
    return ([min(p[i] for p in points) for i in range(3)],
            [max(p[i] for p in points) for i in range(3)])


def select_source_mesh():
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    level_zero = [obj for obj in meshes if re.search(r"LOD0$", obj.name, re.IGNORECASE)]
    selected = level_zero if level_zero else meshes
    if len(selected) != 1:
        raise ValueError("Choose an FBX with one mesh or one LOD0 mesh; found " +
                         ", ".join(obj.name for obj in selected))
    source = selected[0]
    if not source.data.vertices or not source.data.polygons:
        raise ValueError("Selected mesh has no usable geometry")
    if source.parent and source.parent.type == "ARMATURE":
        raise ValueError("Skinned weapon meshes are not supported")
    return source


def replace_geometry(target, source, fit, material_slots, blade_start_y=None, offset=0.0):
    geometry = source.data.copy()
    geometry.transform(target.matrix_world.inverted_safe() @ fit @ source.matrix_world)
    if blade_start_y is not None:
        mesh = bmesh.new()
        mesh.from_mesh(geometry)
        discarded = [face for face in mesh.faces
                     if any((target.matrix_world @ vertex.co).y < blade_start_y
                            for vertex in face.verts)]
        bmesh.ops.delete(mesh, geom=discarded, context="FACES")
        unused = [vertex for vertex in mesh.verts if not vertex.link_faces]
        if unused:
            bmesh.ops.delete(mesh, geom=unused, context="VERTS")
        if not mesh.faces:
            mesh.free()
            raise ValueError("No rapier blade faces remain for blood geometry")
        mesh.normal_update()
        for vertex in mesh.verts:
            vertex.co += vertex.normal * offset
        mesh.to_mesh(geometry)
        mesh.free()
    geometry.materials.clear()
    for material in material_slots:
        if material is not None:
            geometry.materials.append(material)
    for polygon in geometry.polygons:
        polygon.material_index = 0
    target.data = geometry
    return len(geometry.vertices), len(geometry.polygons)


def fit_scabbard(scabbard, blade, blade_start_y):
    """Build an open sheath around blade cross sections, retaining template shader data."""
    mesh = blade.data
    world = blade.matrix_world
    triangles = []
    mesh.calc_loop_triangles()
    for triangle in mesh.loop_triangles:
        points = [world @ mesh.vertices[index].co for index in triangle.vertices]
        if max(point.y for point in points) >= blade_start_y:
            triangles.append(points)
    if not triangles:
        raise ValueError("No blade triangles available for scabbard")
    blade_tip = max(point.y for triangle in triangles for point in triangle)
    if blade_tip - blade_start_y < 1:
        raise ValueError("Blade is too short for a scabbard")

    segments, sides = 32, 12
    vertices, faces = [], []
    inverse = scabbard.matrix_world.inverted_safe()
    for level in range(segments + 1):
        y = blade_start_y + (blade_tip - blade_start_y) * level / segments
        section = []
        for triangle in triangles:
            for first, second in ((0, 1), (1, 2), (2, 0)):
                a, b = triangle[first], triangle[second]
                if min(a.y, b.y) <= y <= max(a.y, b.y):
                    if abs(b.y - a.y) < 1e-8:
                        section.extend((a, b))
                    else:
                        section.append(a.lerp(b, (y - a.y) / (b.y - a.y)))
        if not section:
            raise ValueError("Blade has a gap at scabbard section " + str(level))
        x_min, x_max = min(p.x for p in section), max(p.x for p in section)
        z_min, z_max = min(p.z for p in section), max(p.z for p in section)
        center_x, center_z = (x_min + x_max) / 2, (z_min + z_max) / 2
        radius_x = max((x_max - x_min) / 2 + 0.25, 0.35)
        radius_z = max((z_max - z_min) / 2 + 0.25, 0.35)
        for side in range(sides):
            angle = 2 * math.pi * side / sides
            point = Vector((center_x + radius_x * math.cos(angle), y,
                            center_z + radius_z * math.sin(angle)))
            vertices.append(inverse @ point)
        if level:
            for side in range(sides):
                next_side = (side + 1) % sides
                a = (level - 1) * sides + side
                b = (level - 1) * sides + next_side
                faces.append((a, b, level * sides + next_side, level * sides + side))
    faces.append(tuple(reversed(range(sides))))
    faces.append(tuple(segments * sides + side for side in range(sides)))
    result = bpy.data.meshes.new("FittedScabbard")
    result.from_pydata(vertices, [], faces)
    result.update()
    for slot in scabbard.material_slots:
        if slot.material is not None:
            result.materials.append(slot.material)
    uv = result.uv_layers.new(name="UVMap")
    for polygon in result.polygons:
        for loop_index in polygon.loop_indices:
            index = result.loops[loop_index].vertex_index
            uv.data[loop_index].uv = ((index % sides) / sides,
                                      (index // sides) / segments)
    scabbard.data = result
    return len(result.vertices), len(result.polygons)


def fit_collision_box(collider, weapon):
    """Retain the template's Havok metadata while fitting its box to one sword section."""
    old_min, old_max = bounds(collider)
    lower, upper = old_min[1], old_max[1]
    weapon.data.calc_loop_triangles()
    points = []
    for triangle in weapon.data.loop_triangles:
        corners = [weapon.matrix_world @ weapon.data.vertices[i].co for i in triangle.vertices]
        points.extend(point for point in corners if lower <= point.y <= upper)
        for a, b in ((0, 1), (1, 2), (2, 0)):
            start, end = corners[a], corners[b]
            for plane in (lower, upper):
                if min(start.y, end.y) <= plane <= max(start.y, end.y) and abs(end.y - start.y) > 1e-8:
                    points.append(start.lerp(end, (plane - start.y) / (end.y - start.y)))
    if not points:
        raise ValueError("No weapon geometry intersects collision box: " + collider.name)
    margin = 0.12
    minimum = [min(p[i] for p in points) - margin for i in range(3)]
    maximum = [max(p[i] for p in points) + margin for i in range(3)]
    minimum[1] = max(minimum[1], lower)
    maximum[1] = min(maximum[1], upper)
    if any(maximum[i] - minimum[i] < 0.2 for i in range(3)):
        raise ValueError("Degenerate fitted collision box: " + collider.name)
    x0, y0, z0 = minimum
    x1, y1, z1 = maximum
    world_vertices = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
                      (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    inverse = collider.matrix_world.inverted_safe()
    vertices = [inverse @ Vector(point) for point in world_vertices]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
             (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    result = bpy.data.meshes.new("Fitted" + collider.name)
    result.from_pydata(vertices, [], faces)
    result.update()
    collider.data = result
    return {"before": [old_min, old_max], "after": [minimum, maximum]}


def prepare(addon_parent, archive_path, template_path, output_path, scabbard_mode="keep",
            adjustments=None):
    adjustments = adjustments or {}
    if scabbard_mode not in {"keep", "omit"}:
        raise ValueError("Scabbard mode must be 'keep' or 'omit'")
    sys.path.insert(0, addon_parent)
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    archive_hash, selected_model = inspect_archive(archive_path)
    source = select_source_mesh()
    selected_mesh = source.name
    source_objects = list(bpy.context.scene.objects)
    bpy.ops.import_scene.pynifly(filepath=template_path, import_collisions=True)
    template_objects = [obj for obj in bpy.context.scene.objects if obj not in source_objects]
    collision_names = [obj.name for obj in template_objects if obj.name.startswith("bhk")]
    weapon = next((obj for obj in template_objects
                   if obj.type == "MESH" and obj.name.startswith("IronLongSword")), None)
    if weapon is None:
        raise ValueError("Vanilla sword mesh was not found")

    source_min, source_max = bounds(source)
    target_min, target_max = bounds(weapon)
    if source_max[2] - source_min[2] <= 1e-6 or not all(
            math.isfinite(value) for value in source_min + source_max):
        raise ValueError("Source needs a finite, nonzero Z length")
    scale = (target_max[1] - target_min[1]) / (source_max[2] - source_min[2])
    rotate = Matrix.Rotation(-math.pi / 2, 4, "X")
    scaled = rotate @ Matrix.Scale(scale, 4)
    rotated_points = [scaled @ (source.matrix_world @ v.co) for v in source.data.vertices]
    rotated_min = [min(p[i] for p in rotated_points) for i in range(3)]
    rotated_max = [max(p[i] for p in rotated_points) for i in range(3)]
    offset = Vector((
        (target_min[0] + target_max[0] - rotated_min[0] - rotated_max[0]) / 2,
        target_min[1] - rotated_min[1],
        (target_min[2] + target_max[2] - rotated_min[2] - rotated_max[2]) / 2,
    ))
    fit = Matrix.Translation(offset) @ scaled
    multiplier = float(adjustments.get("ScaleMultiplier", 1))
    rotations = [math.radians(float(adjustments.get("Rotation" + axis + "Degrees", 0)))
                 for axis in "XYZ"]
    displacement = Vector(tuple(float(adjustments.get("Offset" + axis, 0)) for axis in "XYZ"))
    if not 0.25 <= multiplier <= 4 or any(not math.isfinite(value) or abs(value) > math.pi
                                          for value in rotations) or any(not math.isfinite(value) or abs(value) > 100
                                                                         for value in displacement):
        raise ValueError("Model adjustments are out of range")
    pivot = Vector(((target_min[0] + target_max[0]) / 2, 0,
                    (target_min[2] + target_max[2]) / 2))
    adjustment_fit = (Matrix.Translation(displacement) @ Matrix.Translation(pivot) @
                      Matrix.Rotation(rotations[2], 4, "Z") @
                      Matrix.Rotation(rotations[1], 4, "Y") @
                      Matrix.Rotation(rotations[0], 4, "X") @
                      Matrix.Scale(multiplier, 4) @ Matrix.Translation(-pivot))
    fit = adjustment_fit @ fit
    weapon_counts = replace_geometry(weapon, source, fit,
                                     [slot.material for slot in weapon.material_slots])
    collision_bounds = {}
    for collider in template_objects:
        if collider.type == "MESH" and collider.name.startswith("bhkBoxShape"):
            collision_bounds[collider.name] = fit_collision_box(collider, weapon)
    blood_start = min(bounds(obj)[0][1] for obj in template_objects
                      if obj.name in {"BloodEffects", "BloodLighting"})
    blood_counts = {}
    for name, offset_distance in (("BloodEffects", 0.025), ("BloodLighting", 0.045)):
        effect = next((obj for obj in template_objects if obj.name == name), None)
        if effect is None or effect.type != "MESH":
            raise ValueError("Template lacks required blood effect: " + name)
        blood_counts[name] = replace_geometry(effect, source, fit,
                                              [slot.material for slot in effect.material_slots],
                                              blade_start_y=blood_start,
                                              offset=offset_distance)

    scabbard = next((obj for obj in template_objects if obj.name == "Scb"), None)
    if scabbard_mode == "keep" and (scabbard is None or scabbard.type != "MESH"):
        raise ValueError("Selected template has no scabbard to keep")
    scabbard_counts = fit_scabbard(scabbard, weapon, blood_start) if scabbard_mode == "keep" else None
    if scabbard_mode == "omit" and scabbard is not None:
        bpy.data.objects.remove(scabbard, do_unlink=True)
    for obj in source_objects:
        bpy.data.objects.remove(obj, do_unlink=True)

    bpy.ops.object.select_all(action="SELECT")
    bpy.context.view_layer.objects.active = weapon

    result = bpy.ops.export_scene.pynifly(filepath=output_path, target_game="SKYRIMSE",
                                           write_tris=False, write_bodytri=False,
                                           export_animations=False)
    if "CANCELLED" in result or not os.path.isfile(output_path):
        raise RuntimeError("NIF export did not produce an output file")
    actual_min, actual_max = bounds(weapon)
    return {"archive_sha256": archive_hash, "selected_model": selected_model,
            "selected_mesh": selected_mesh, "template": template_path,
            "source_bounds": [source_min, source_max],
            "template_weapon_bounds": [target_min, target_max],
            "fit_scale": scale, "fit_rotation_x_degrees": -90,
            "effective_fit_scale": scale * multiplier,
            "fit_translation": list(offset),
            "adjustments": adjustments,
            "exported_weapon_bounds": [actual_min, actual_max],
            "weapon_geometry": {"vertices": weapon_counts[0], "polygons": weapon_counts[1]},
            "blood_geometry": {name: {"vertices": value[0], "polygons": value[1]}
                               for name, value in blood_counts.items()},
            "blood_start_y": blood_start,
            "scabbard_mode": scabbard_mode,
            "scabbard_geometry": ({"vertices": scabbard_counts[0],
                                    "polygons": scabbard_counts[1]}
                                   if scabbard_counts else None),
            "collision_objects": collision_names,
            "collision_bounds": collision_bounds,
            "output_nif": output_path,
            "output_bytes": os.path.getsize(output_path),
            "export_operator_result": list(result)}


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    if len(args) not in (6, 7):
        raise ValueError("Expected addon, archive, template, output, report, scabbard mode, [adjustments JSON]")
    addon, archive, template, output, report, scabbard_mode = args[:6]
    adjustments = {}
    if len(args) == 7:
        with open(args[6], "r", encoding="utf-8") as stream:
            adjustments = json.load(stream)
    result = prepare(addon, archive, template, output, scabbard_mode, adjustments)
    with open(report, "w", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, ensure_ascii=False)
    print("MODEL_PREPARATION_EXPORT_PROBE_OK " + output)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        sys.exit(1)
