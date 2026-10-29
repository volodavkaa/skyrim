"""Experimental model-to-vanilla-weapon export; Skyrim game acceptance remains pending."""

import json
import math
import os
import sys
import traceback

import bmesh
import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(__file__))
from model_preparation_probe import inspect_archive, select_weapon_mesh


def bounds(obj):
    points = [obj.matrix_world @ v.co for v in obj.data.vertices]
    return ([min(p[i] for p in points) for i in range(3)],
            [max(p[i] for p in points) for i in range(3)])


def select_source_mesh():
    source = select_weapon_mesh()
    if not source.data.vertices or not source.data.polygons:
        raise ValueError("Selected mesh has no usable geometry")
    if source.parent and source.parent.type == "ARMATURE":
        raise ValueError("Skinned weapon meshes are not supported")
    return source


def replace_geometry(target, source, fit, material_slots, blade_start_y=None, offset=0.0,
                     blade_end_y=None, blade_side=None):
    geometry = source.data.copy()
    geometry.transform(target.matrix_world.inverted_safe() @ fit @ source.matrix_world)
    if blade_start_y is not None:
        geometry.transform(target.matrix_world)
        mesh = bmesh.new()
        mesh.from_mesh(geometry)
        def clip(plane_co, plane_no, clear_inner=False, clear_outer=False):
            bmesh.ops.bisect_plane(mesh,
                                   geom=list(mesh.verts) + list(mesh.edges) + list(mesh.faces),
                                   dist=1e-5, plane_co=plane_co, plane_no=plane_no,
                                   clear_inner=clear_inner, clear_outer=clear_outer)
        clip((0, blade_start_y, 0), (0, 1, 0), clear_inner=True)
        if blade_end_y is not None:
            clip((0, blade_end_y, 0), (0, 1, 0), clear_outer=True)
        if blade_side is not None:
            clip((blade_side[1], 0, 0), (1, 0, 0),
                 clear_inner=blade_side[0] == "right",
                 clear_outer=blade_side[0] == "left")
        unused = [vertex for vertex in mesh.verts if not vertex.link_faces]
        if unused:
            bmesh.ops.delete(mesh, geom=unused, context="VERTS")
        if not mesh.faces:
            mesh.free()
            raise ValueError("No weapon working-surface faces remain for blood geometry")
        mesh.normal_update()
        for vertex in mesh.verts:
            vertex.co += vertex.normal * offset
        mesh.to_mesh(geometry)
        mesh.free()
        geometry.transform(target.matrix_world.inverted_safe())
    geometry.materials.clear()
    for material in material_slots:
        if material is not None:
            geometry.materials.append(material)
    for polygon in geometry.polygons:
        polygon.material_index = 0
    target.data = geometry
    return len(geometry.vertices), len(geometry.polygons)


def split_weapon_materials(weapon, source):
    """Keep the fitted whole for blood/collision, then partition visible faces by slot."""
    slot_count = len(source.material_slots)
    if slot_count <= 1:
        return [weapon]
    if slot_count > 8:
        raise ValueError("At most eight source material slots are supported")
    if len(weapon.data.polygons) != len(source.data.polygons):
        raise ValueError("Weapon face order changed before material partition")
    original = weapon.data
    for fitted_face, source_face in zip(original.polygons, source.data.polygons):
        fitted_face.material_index = source_face.material_index
    shader = weapon.active_material
    if shader is None:
        raise ValueError("Weapon template has no material to duplicate")
    result = []
    for slot in range(slot_count):
        part = weapon if slot == 0 else weapon.copy()
        if slot:
            name = weapon.name
            part.name = (name[:-2] + "_Material" + str(slot) + ":0"
                         if name.endswith(":0") else name + "_Material" + str(slot))
            bpy.context.collection.objects.link(part)
        mesh = original.copy()
        edit = bmesh.new()
        edit.from_mesh(mesh)
        unwanted = [face for face in edit.faces if face.material_index != slot]
        bmesh.ops.delete(edit, geom=unwanted, context="FACES")
        edit.to_mesh(mesh)
        edit.free()
        if not mesh.polygons:
            raise ValueError("Source material slot has no weapon faces: " + str(slot))
        mesh.materials.clear()
        mesh.materials.append(shader if slot == 0 else shader.copy())
        for face in mesh.polygons:
            face.material_index = 0
        part.data = mesh
        result.append(part)
    return result


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
        half_step = (blade_tip - blade_start_y) / (2 * segments) + 1e-6
        for triangle in triangles:
            section.extend(point for point in triangle if abs(point.y - y) <= half_step)
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
        radius_x = max((x_max - x_min) / 2 + 0.5, 0.35)
        radius_z = max((z_max - z_min) / 2 + 0.5, 0.35)
        for side in range(sides):
            angle = 2 * math.pi * side / sides
            sheath_y = y - 0.15 if level == 0 else y + 0.15 if level == segments else y
            point = Vector((center_x + radius_x * math.cos(angle), sheath_y,
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


def fit_axe_scabbard(scabbard, old_weapon_bounds, new_weapon, blade_side):
    """Scale the axe's native holster shape around the fitted head and shaft."""
    old_min, old_max = old_weapon_bounds
    new_min, new_max = bounds(new_weapon)
    old_center = [(old_min[i] + old_max[i]) / 2 for i in range(3)]
    new_center = [(new_min[i] + new_max[i]) / 2 for i in range(3)]
    scales = [(new_max[i] - new_min[i]) / max(old_max[i] - old_min[i], 1e-6)
              for i in range(3)]
    if any(not 0.25 <= value <= 4 for value in scales):
        raise ValueError("Axe scabbard scale is outside supported range")
    result = scabbard.data.copy()
    inverse = scabbard.matrix_world.inverted_safe()
    for vertex in result.vertices:
        point = scabbard.matrix_world @ vertex.co
        fitted = Vector(tuple(new_center[i] + (point[i] - old_center[i]) * scales[i] *
                              (-1 if i == 0 and blade_side and blade_side[0] == "right" else 1)
                              for i in range(3)))
        vertex.co = inverse @ fitted
    result.update()
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
    # Preserve PyNifly's imported box vertex and face order: its exporter infers
    # orientation from the first three orthogonal face normals.
    world_vertices = [(x0, y1, z1), (x0, y0, z1), (x0, y0, z0), (x0, y1, z0),
                      (x1, y1, z1), (x1, y0, z1), (x1, y0, z0), (x1, y1, z0)]
    inverse = collider.matrix_world.inverted_safe()
    vertices = [inverse @ Vector(point) for point in world_vertices]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
             (2, 3, 7, 6), (0, 4, 7, 3), (5, 1, 2, 6)]
    result = bpy.data.meshes.new("Fitted" + collider.name)
    result.from_pydata(vertices, [], faces)
    result.update()
    collider.data = result
    return {"before": [old_min, old_max], "after": [minimum, maximum]}


def fit_collision_convex(collider, weapon):
    """Fit the template Havok shape to a convex hull of the adjusted weapon."""
    old_min, old_max = bounds(collider)
    weapon_min, weapon_max = bounds(weapon)
    mesh = weapon.data.copy()
    mesh.transform(collider.matrix_world.inverted_safe() @ weapon.matrix_world)
    bm = bmesh.new()
    bm.from_mesh(mesh)
    hull = bmesh.ops.convex_hull(bm, input=list(bm.verts), use_existing_faces=False)
    interior = [element for element in hull["geom_interior"]
                if isinstance(element, bmesh.types.BMVert) and element.is_valid]
    if interior:
        bmesh.ops.delete(bm, geom=interior, context="VERTS")
    unused = [element for element in hull["geom_unused"]
              if isinstance(element, bmesh.types.BMVert) and element.is_valid]
    if unused:
        bmesh.ops.delete(bm, geom=unused, context="VERTS")
    bm.to_mesh(mesh)
    bm.free()
    mesh.update()
    collider.data = mesh
    return {"before": [old_min, old_max], "after": [weapon_min, weapon_max]}


def find_axe_blade_side(weapon, head_start_y):
    points = [weapon.matrix_world @ vertex.co for vertex in weapon.data.vertices]
    lower = [point.x for point in points if point.y < head_start_y]
    upper = [point.x for point in points if point.y >= head_start_y]
    if not lower or not upper:
        raise ValueError("Axe needs distinct shaft and head regions")
    shaft_x = sorted(lower)[len(lower) // 2]
    left = shaft_x - min(upper)
    right = max(upper) - shaft_x
    if min(left, right) <= 0 or max(left, right) < 1.2 * min(left, right):
        raise ValueError("Axe blade side is ambiguous; adjust orientation or choose another model")
    if left > right:
        return ("left", shaft_x - 0.16 * left)
    return ("right", shaft_x + 0.16 * right)


def detect_head_band(weapon):
    """Locate the broad striking region from horizontal sections of the fitted mesh."""
    mesh = weapon.data
    world = weapon.matrix_world
    mesh.calc_loop_triangles()
    triangles = [[world @ mesh.vertices[index].co for index in tri.vertices]
                 for tri in mesh.loop_triangles]
    bottom, top = bounds(weapon)[0][1], bounds(weapon)[1][1]
    span = top - bottom
    sections = []
    steps = 48
    for level in range(1, steps):
        y = bottom + span * level / steps
        section = []
        for tri in triangles:
            for a, b in ((0, 1), (1, 2), (2, 0)):
                start, end = tri[a], tri[b]
                if min(start.y, end.y) <= y <= max(start.y, end.y) and abs(end.y - start.y) > 1e-8:
                    section.append(start.lerp(end, (y - start.y) / (end.y - start.y)))
        if len(section) >= 2:
            width = max(max(point.x for point in section) - min(point.x for point in section),
                        max(point.z for point in section) - min(point.z for point in section))
            sections.append((level, y, width))
    shaft_widths = sorted(width for _, y, width in sections if y < bottom + 0.4 * span)
    if len(shaft_widths) < 3 or shaft_widths[len(shaft_widths) // 2] <= 0:
        raise ValueError("Cannot measure weapon shaft for blood placement")
    shaft_width = shaft_widths[len(shaft_widths) // 2]
    candidates = [(level, y, width) for level, y, width in sections
                  if y >= bottom + 0.35 * span and width >= 2 * shaft_width]
    runs = []
    for entry in candidates:
        if not runs or entry[0] > runs[-1][-1][0] + 1:
            runs.append([])
        runs[-1].append(entry)
    runs = [run for run in runs if len(run) >= 2]
    if not runs:
        raise ValueError("Selected weapon type needs a wider striking head than its shaft")
    run = max(runs, key=lambda group: sum(item[2] for item in group))
    margin = span / steps
    return (max(bottom, run[0][1] - margin), min(top, run[-1][1] + margin),
            max(item[2] for item in run) / shaft_width)


def apply_default_weapon_shader(weapon):
    material = weapon.active_material
    if material is None or not material.use_nodes:
        raise ValueError("Weapon template has no usable shader material")
    from io_scene_nifly.pyn.nifconstants import ShaderFlags1, ShaderFlags2
    shader = material.pyn_shader
    shader.Shader_Type = "Default"
    shader.Shader_Flags_1 = ShaderFlags1(
        ShaderFlags1.parse(shader.Shader_Flags_1) & ~ShaderFlags1.ENVIRONMENT_MAPPING).fullname
    shader.Shader_Flags_2 = ShaderFlags2(
        ShaderFlags2.parse(shader.Shader_Flags_2) & ~ShaderFlags2.ENVMAP_LIGHT_FADE).fullname
    for slot in ("EnvMap", "EnvMask"):
        key = "BSShaderTextureSet_" + slot
        if key in material:
            del material[key]
        for node in material.node_tree.nodes:
            socket = node.inputs.get(slot)
            if socket:
                for link in list(socket.links):
                    material.node_tree.links.remove(link)


def apply_vertex_color(obj, value):
    colors = obj.data.color_attributes
    layer = colors.get("VERTEX_COLOR")
    created = layer is None
    if layer is None:
        layer = colors.new(name="VERTEX_COLOR", type="FLOAT_COLOR", domain="POINT")
    for item in layer.data:
        # The separately stored VERTEX_ALPHA map remains untouched.
        item.color = (value, value, value, 1.0 if created else item.color[3])
    colors.active_color = layer


def apply_shader_settings(weapon, config):
    material = weapon.active_material
    if material is None:
        raise ValueError("Weapon template has no shader material")
    shader = material.pyn_shader
    env = config.get("environment_strength")
    if env is not None:
        if shader.Shader_Type != "Environment_Map":
            raise ValueError("The selected weapon template does not use Environment Map; disable its strength override or choose another template")
        shader.Env_Map_Scale = float(env)
    spec = config.get("specular_strength")
    if spec is not None:
        from io_scene_nifly.pyn.nifconstants import ShaderFlags1
        shader.Shader_Flags_1 = ShaderFlags1(
            ShaderFlags1.parse(shader.Shader_Flags_1) | ShaderFlags1.SPECULAR).fullname
        shader.Spec_Str = float(spec)
        shader.Spec_Color = tuple(config["specular_color"])


def apply_weapon_textures(weapon, config):
    diffuse = config.get("diffuse_texture")
    normal = config.get("normal_texture")
    env_mask = config.get("env_mask_texture")
    env_map = config.get("env_map_texture")
    if not diffuse and not normal:
        return None
    if not diffuse or not normal or not config.get("texture_root"):
        raise ValueError("Diffuse and normal textures must be supplied together")
    material = weapon.active_material
    if material is None or not material.use_nodes:
        raise ValueError("Weapon template has no usable shader material")
    result = {}
    for slot, relative, node_name in (("Diffuse", diffuse, "Diffuse_Texture"),
                                      ("Normal", normal, "Normal_Texture")):
        absolute = os.path.join(config["texture_root"], *relative.split("/"))
        if not os.path.isfile(absolute):
            raise FileNotFoundError(absolute)
        material["BSShaderTextureSet_" + slot] = relative.replace("/", "\\")
        node = material.node_tree.nodes.get(node_name)
        if node is None or node.type != "TEX_IMAGE":
            raise ValueError("Template lacks texture node: " + node_name)
        node.image = bpy.data.images.load(absolute, check_existing=True)
        if slot == "Normal":
            node.image.colorspace_settings.name = "Non-Color"
        result[slot] = relative
    if env_mask:
        if material.pyn_shader.Shader_Type != "Environment_Map" or "ENVIRONMENT_MAPPING" not in material.pyn_shader.Shader_Flags_1:
            raise ValueError("Weapon template needs an Environment Map shader for metallic masks")
        absolute = os.path.join(config["texture_root"], *env_mask.split("/"))
        if not os.path.isfile(absolute):
            raise FileNotFoundError(absolute)
        material["BSShaderTextureSet_EnvMask"] = env_mask.replace("/", "\\")
        result["EnvMask"] = env_mask
        if env_map:
            absolute = os.path.join(config["texture_root"], *env_map.split("/"))
            if not os.path.isfile(absolute):
                raise FileNotFoundError(absolute)
            material["BSShaderTextureSet_EnvMap"] = env_map.replace("/", "\\")
        inherited = material.get("BSShaderTextureSet_EnvMap")
        if not inherited:
            raise ValueError("Environment Map shader has no cubemap")
        result["EnvMap"] = inherited.replace("\\", "/")
    return result


def prepare(addon_parent, archive_path, template_path, output_path, scabbard_mode="keep",
            adjustments=None, template_config=None):
    adjustments = adjustments or {}
    template_config = template_config or {"weapon_mesh": "IronLongSword01:0",
                                          "blood_effect": "BloodEffects",
                                          "blood_lighting": "BloodLighting"}
    if scabbard_mode not in {"keep", "omit"}:
        raise ValueError("Scabbard mode must be 'keep' or 'omit'")
    sys.path.insert(0, addon_parent)
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    archive_hash, selected_model = inspect_archive(
        archive_path, addon_parent, template_config.get("selected_model_entry"))
    source = select_source_mesh()
    selected_mesh = source.name
    source_objects = list(bpy.context.scene.objects)
    for index, obj in enumerate(source_objects):
        obj.name = "SourceObject_" + str(index)
    bpy.ops.import_scene.pynifly(filepath=template_path, import_collisions=True)
    template_objects = [obj for obj in bpy.context.scene.objects if obj not in source_objects]
    collision_names = [obj.name for obj in template_objects if obj.name.startswith("bhk")]
    weapon = next((obj for obj in template_objects
                   if obj.type == "MESH" and obj.name == template_config["weapon_mesh"]), None)
    if weapon is None:
        raise ValueError("Vanilla sword mesh was not found")

    source_min, source_max = bounds(source)
    target_min, target_max = bounds(weapon)
    if not all(math.isfinite(value) for value in source_min + source_max):
        raise ValueError("Source needs finite mesh coordinates")
    dimensions = [source_max[axis] - source_min[axis] for axis in range(3)]
    long_axis = max(range(3), key=lambda axis: dimensions[axis])
    if dimensions[long_axis] <= 1e-6:
        raise ValueError("Source needs a nonzero length")
    if long_axis != 2 and dimensions[long_axis] < 1.4 * max(
            dimensions[axis] for axis in range(3) if axis != long_axis):
        raise ValueError("Cannot determine the weapon long axis unambiguously")
    scale = (target_max[1] - target_min[1]) / dimensions[long_axis]
    rotation_axis, rotation_degrees = (("Z", 90) if long_axis == 0 else
                                       ("Z", 0) if long_axis == 1 else ("X", -90))
    if adjustments.get("ReverseLongAxis", False):
        rotation_axis, rotation_degrees = (("Z", -90) if long_axis == 0 else
                                           ("Z", 180) if long_axis == 1 else ("X", 90))
    rotate = Matrix.Rotation(math.radians(rotation_degrees), 4, rotation_axis)
    scaled = rotate @ Matrix.Scale(scale, 4)
    rotated_points = [scaled @ (source.matrix_world @ v.co) for v in source.data.vertices]
    rotated_min = [min(p[i] for p in rotated_points) for i in range(3)]
    rotated_max = [max(p[i] for p in rotated_points) for i in range(3)]
    offset = Vector((
        (target_min[0] + target_max[0] - rotated_min[0] - rotated_max[0]) / 2,
        target_min[1] - rotated_min[1],
        (target_min[2] + target_max[2] - rotated_min[2] - rotated_max[2]) / 2,
    ))
    grip_fraction = float(adjustments.get("SourceGripFraction", 0.11))
    if not math.isfinite(grip_fraction) or not 0 <= grip_fraction <= 1:
        raise ValueError("Grip fraction must be between 0 and 1")
    handle_collision = min((obj for obj in template_objects
                            if obj.type == "MESH" and obj.name.startswith("bhkBoxShape")),
                           key=lambda obj: bounds(obj)[0][1], default=None)
    if handle_collision is None or not template_config.get("grip_from_collision", True):
        template_grip_y = target_min[1] + grip_fraction * (target_max[1] - target_min[1])
    else:
        handle_min, handle_max = bounds(handle_collision)
        template_grip_y = (handle_min[1] + handle_max[1]) / 2
    source_grip_y = rotated_min[1] + grip_fraction * (rotated_max[1] - rotated_min[1])
    offset.y = template_grip_y - source_grip_y
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
    actual_min, actual_max = bounds(weapon)
    collision_bounds = {}
    for collider in template_objects:
        if collider.type == "MESH" and collider.name.startswith("bhkBoxShape"):
            collision_bounds[collider.name] = fit_collision_box(collider, weapon)
        elif collider.type == "MESH" and collider.name == "bhkConvexVerticesShape":
            collision_bounds[collider.name] = fit_collision_convex(collider, weapon)
    blood_names = (template_config["blood_effect"], template_config["blood_lighting"])
    template_blood_start = min(bounds(obj)[0][1] for obj in template_objects
                               if obj.name in blood_names)
    blood_mode = template_config.get("blood_mode", "blade")
    head_band = (detect_head_band(weapon)
                 if blood_mode in {"head", "bounded_head", "side_head"} else None)
    head_width_ratio = head_band[2] if head_band else None
    blood_cut_start = head_band[0] if head_band else template_blood_start
    blood_cut_end = head_band[1] if blood_mode == "bounded_head" else None
    blade_side = (find_axe_blade_side(weapon, blood_cut_start)
                  if blood_mode == "side_head" else None)
    blood_counts = {}
    for name, offset_distance in zip(blood_names, (0.025, 0.045)):
        effect = next((obj for obj in template_objects if obj.name == name), None)
        if effect is None or effect.type != "MESH":
            raise ValueError("Template lacks required blood effect: " + name)
        blood_counts[name] = replace_geometry(effect, source, fit,
                                              [slot.material for slot in effect.material_slots],
                                              blade_start_y=blood_cut_start,
                                              blade_end_y=blood_cut_end,
                                              blade_side=blade_side,
                                              offset=offset_distance)
    blood_start = min(bounds(obj)[0][1] for obj in template_objects
                      if obj.name in blood_names)

    scabbard = next((obj for obj in template_objects if obj.name == "Scb"), None)
    if scabbard_mode == "keep" and (scabbard is None or scabbard.type != "MESH"):
        raise ValueError("Selected template has no scabbard to keep")
    scabbard_counts = None
    if scabbard_mode == "keep":
        if template_config.get("scabbard_fit") == "template_scaled":
            scabbard_counts = fit_axe_scabbard(scabbard, (target_min, target_max), weapon, blade_side)
        else:
            scabbard_counts = fit_scabbard(scabbard, weapon, blood_start)
    scabbard_bounds = bounds(scabbard) if scabbard_counts else None
    if scabbard_mode == "omit" and scabbard is not None:
        bpy.data.objects.remove(scabbard, do_unlink=True)
    mappings = template_config.get("material_textures")
    if len(source.material_slots) > 1 and (not mappings or
            len(mappings) != len(source.material_slots)):
        raise ValueError("Every source material slot needs a separate texture mapping")
    parts = split_weapon_materials(weapon, source)
    if sum(len(part.data.polygons) for part in parts) != weapon_counts[1]:
        raise ValueError("Material partition lost or duplicated weapon faces")
    texture_paths = []
    vertex_color = template_config.get("vertex_color")
    if vertex_color is not None:
        vertex_color = float(vertex_color)
        if not math.isfinite(vertex_color) or not 0 <= vertex_color <= 1:
            raise ValueError("Vertex color must be between 0 and 1")
        for obj in parts + ([scabbard] if scabbard_mode == "keep" and scabbard is not None else []):
            apply_vertex_color(obj, vertex_color)
    for slot, part in enumerate(parts):
        config = dict(template_config)
        if mappings:
            config.update(mappings[slot])
        if config.get("default_shader"):
            apply_default_weapon_shader(part)
        apply_shader_settings(part, config)
        texture_paths.append(apply_weapon_textures(part, config))
    for obj in source_objects:
        bpy.data.objects.remove(obj, do_unlink=True)

    bpy.ops.object.select_all(action="SELECT")
    bpy.context.view_layer.objects.active = weapon

    result = bpy.ops.export_scene.pynifly(filepath=output_path, target_game="SKYRIMSE",
                                           write_tris=False, write_bodytri=False,
                                           export_animations=False)
    if "CANCELLED" in result or not os.path.isfile(output_path):
        raise RuntimeError("NIF export did not produce an output file")
    return {"archive_sha256": archive_hash, "selected_model": selected_model,
            "selected_mesh": selected_mesh, "template": template_path,
            "source_bounds": [source_min, source_max],
            "template_weapon_bounds": [target_min, target_max],
            "fit_scale": scale, "source_long_axis": "XYZ"[long_axis],
            "fit_rotation_axis": rotation_axis, "fit_rotation_degrees": rotation_degrees,
            "fit_rotation_x_degrees": rotation_degrees if long_axis == 2 else 0,
            "effective_fit_scale": scale * multiplier,
            "fit_translation": list(offset),
            "source_grip_fraction": grip_fraction,
            "template_grip_y": template_grip_y,
            "adjustments": adjustments,
            "exported_weapon_bounds": [actual_min, actual_max],
            "weapon_geometry": {"vertices": weapon_counts[0], "polygons": weapon_counts[1]},
            "weapon_parts": [{"name": part.name, "vertices": len(part.data.vertices),
                              "polygons": len(part.data.polygons), "uv_layers": len(part.data.uv_layers)}
                             for part in parts],
            "weapon_textures": texture_paths,
            "blood_geometry": {name: {"vertices": value[0], "polygons": value[1]}
                               for name, value in blood_counts.items()},
            "blood_start_y": blood_start,
            "detected_head_band": head_band,
            "blood_side": blade_side,
            "head_to_shaft_width_ratio": head_width_ratio,
            "scabbard_mode": scabbard_mode,
            "scabbard_geometry": ({"vertices": scabbard_counts[0],
                                    "polygons": scabbard_counts[1]}
                                   if scabbard_counts else None),
            "scabbard_bounds": scabbard_bounds,
            "collision_objects": collision_names,
            "collision_bounds": collision_bounds,
            "output_nif": output_path,
            "output_bytes": os.path.getsize(output_path),
            "export_operator_result": list(result)}


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    if len(args) not in (6, 7, 8):
        raise ValueError("Expected addon, archive, template, output, report, scabbard mode, [adjustments JSON], [template config JSON]")
    addon, archive, template, output, report, scabbard_mode = args[:6]
    adjustments = {}
    if len(args) == 7:
        with open(args[6], "r", encoding="utf-8") as stream:
            adjustments = json.load(stream)
    elif len(args) == 8:
        with open(args[6], "r", encoding="utf-8") as stream:
            adjustments = json.load(stream)
    config = None
    if len(args) == 8:
        with open(args[7], "r", encoding="utf-8") as stream:
            config = json.load(stream)
    result = prepare(addon, archive, template, output, scabbard_mode, adjustments, config)
    with open(report, "w", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, ensure_ascii=False)
    print("MODEL_PREPARATION_EXPORT_PROBE_OK " + output)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        sys.exit(1)
