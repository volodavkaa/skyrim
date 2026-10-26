"""Read-only model geometry probe for the model-preparation NIF export gate.

Run with Blender in background mode:
  blender -b --factory-startup --python scripts/model_preparation_probe.py -- input.zip report.json

The script extracts the selected model and verified local dependencies into a temporary directory. It does
not save a blend file, change the archive, or claim to produce a game-ready NIF.
"""

import hashlib
import json
import math
import os
import posixpath
from pathlib import PurePosixPath
import re
import stat
import sys
import tempfile
import zipfile
from urllib.parse import unquote, urlsplit

import bpy


MODEL_SUFFIXES = {".blend", ".fbx", ".obj", ".glb", ".gltf", ".nif"}
MAX_ENTRIES = 10_000
MAX_ENTRY_BYTES = 512 * 1024 * 1024
MAX_TOTAL_BYTES = 2 * 1024 * 1024 * 1024
MAX_GLTF_JSON_BYTES = 32 * 1024 * 1024
MAX_MODEL_DEPENDENCIES = 256
STATIC_MODIFIERS = {"MIRROR", "ARRAY", "BEVEL", "SOLIDIFY", "SUBSURF",
                    "TRIANGULATE", "WELD", "WEIGHTED_NORMAL", "DECIMATE"}


def bake_static_modifiers(obj):
    if obj.data.shape_keys and len(obj.data.shape_keys.key_blocks) > 1:
        raise ValueError("Shape-key weapon meshes need a separate preparation route")
    if not obj.modifiers:
        return obj
    names = [modifier.type for modifier in obj.modifiers]
    unsupported = sorted(set(names) - STATIC_MODIFIERS)
    if unsupported:
        raise ValueError("Unsupported source modifiers: " + ", ".join(unsupported))
    materials = [slot.material for slot in obj.material_slots]
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = bpy.data.meshes.new_from_object(evaluated, preserve_all_data_layers=True,
                                           depsgraph=depsgraph)
    if not mesh or not mesh.vertices or not mesh.polygons:
        raise ValueError("Source modifiers produced no usable mesh")
    if len(mesh.materials) != len(materials) or any(
            mesh.materials[index] != material for index, material in enumerate(materials)):
        raise ValueError("Source modifier changed material slots")
    if any(face.material_index >= len(materials) for face in mesh.polygons) and materials:
        raise ValueError("Source modifier generated an unmapped material slot")
    obj.data = mesh
    obj.modifiers.clear()
    obj["ModelPreparationAppliedModifiers"] = names
    return obj


def dependency_name(model_name, uri):
    if not isinstance(uri, str) or not uri:
        raise ValueError("Empty model dependency URI")
    if uri.lower().startswith("data:"):
        return None
    parsed = urlsplit(uri)
    if parsed.scheme or parsed.netloc or parsed.query or parsed.fragment:
        raise ValueError("External or ambiguous model dependency URI: " + uri)
    decoded = unquote(parsed.path).replace("\\", "/")
    if (not decoded or decoded.startswith("/") or
            any(ord(char) < 32 or char in '<>:"|?*' for char in decoded)):
        raise ValueError("Unsafe model dependency URI: " + uri)
    joined = posixpath.normpath(posixpath.join(posixpath.dirname(model_name), decoded))
    if joined in ("", ".", "..") or joined.startswith("../"):
        raise ValueError("Model dependency escapes ZIP root: " + uri)
    return joined


def gltf_uris(value):
    if isinstance(value, dict):
        for key, child in value.items():
            if key == "uri":
                yield child
            else:
                yield from gltf_uris(child)
    elif isinstance(value, list):
        for child in value:
            yield from gltf_uris(child)


def extract_checked(archive, entry, temp, relative, max_bytes=MAX_ENTRY_BYTES, content=None):
    if entry.file_size > max_bytes:
        raise ValueError("Model dependency exceeds size limit: " + entry.filename)
    target = os.path.abspath(os.path.join(temp, *relative.split("/")))
    if os.path.commonpath((temp, target)) != temp:
        raise ValueError("Model dependency escaped temporary directory")
    os.makedirs(os.path.dirname(target), exist_ok=True)
    if content is not None:
        with open(target, "xb") as output:
            output.write(content)
        return target
    with archive.open(entry) as source, open(target, "xb") as output:
        copied = 0
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            copied += len(chunk)
            if copied > entry.file_size:
                raise ValueError("ZIP entry expanded beyond declared size")
            output.write(chunk)
        if copied != entry.file_size:
            raise ValueError("ZIP entry size mismatch")
    return target


def select_weapon_mesh():
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    level_zero = [obj for obj in meshes if re.search(r"LOD0$", obj.name, re.IGNORECASE)]
    if len(level_zero) == 1:
        return bake_static_modifiers(level_zero[0])
    if len(meshes) == 1:
        return bake_static_modifiers(meshes[0])
    accessories = re.compile(r"sheat(?:h)?|scabbard|holster|(^|[_ .])scb($|[_ .])",
                             re.IGNORECASE)
    weapons = re.compile(r"sword|dagger|rapier|blade|waraxe|battleaxe|axe|mace|hammer|weapon",
                         re.IGNORECASE)
    candidates = [obj for obj in meshes if weapons.search(obj.name) and
                  not accessories.search(obj.name)]
    if len(candidates) == 1:
        return bake_static_modifiers(candidates[0])
    if 1 < len(candidates) <= 8 and len({re.sub(r":\d+$", "", obj.name).casefold()
                                             for obj in candidates}) == 1:
        primary = max(candidates, key=lambda obj: len(obj.data.polygons))
        bpy.ops.object.select_all(action="DESELECT")
        for obj in candidates:
            if obj.parent and obj.parent.type == "ARMATURE":
                raise ValueError("Skinned weapon meshes are not supported")
            bake_static_modifiers(obj)
            obj.select_set(True)
        bpy.context.view_layer.objects.active = primary
        bpy.ops.object.join()
        return primary
    raise ValueError("Choose a model with one weapon mesh or one LOD0 mesh; found " +
                     ", ".join(obj.name for obj in meshes))


def inspect_archive(path, addon_parent=None, model_entry=None):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    with zipfile.ZipFile(path) as archive:
        if len(archive.infolist()) > MAX_ENTRIES:
            raise ValueError("ZIP entry count exceeds limit")
        names = set()
        entries = {}
        candidates = []
        total = 0
        for entry in archive.infolist():
            name = entry.filename.replace("\\", "/")
            parts = name.rstrip("/").split("/")
            if (name.startswith("/") or len(parts) > 64
                    or any(not p or p in (".", "..") or p.endswith((" ", ".")) for p in parts)
                    or any(ord(char) < 32 or char in '<>:"|?*' for char in name)
                    or stat.S_ISLNK(entry.external_attr >> 16)):
                raise ValueError("Unsafe ZIP entry: " + entry.filename)
            for part in parts:
                stem = part.split(".")[0].upper()
                if stem in {"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"} or (
                    len(stem) == 4 and stem[:3] in {"COM", "LPT"} and stem[3] in "123456789"
                ):
                    raise ValueError("Reserved ZIP entry name: " + entry.filename)
            folded = name.rstrip("/").casefold()
            if folded in names:
                raise ValueError("Duplicate ZIP entry: " + entry.filename)
            names.add(folded)
            if entry.is_dir():
                continue
            entries[folded] = entry
            if entry.file_size > MAX_ENTRY_BYTES:
                raise ValueError("ZIP entry exceeds size limit: " + name)
            total += entry.file_size
            if total > MAX_TOTAL_BYTES:
                raise ValueError("ZIP expanded size exceeds limit")
            if PurePosixPath(name).suffix.casefold() in MODEL_SUFFIXES:
                candidates.append(entry)
        if model_entry:
            chosen = model_entry.replace("\\", "/").casefold()
            matches = [entry for entry in candidates
                       if entry.filename.replace("\\", "/").casefold() == chosen]
            if len(matches) != 1:
                raise ValueError("Selected model entry is not a unique model in ZIP: " + model_entry)
            selected = matches[0]
        elif len(candidates) == 1:
            selected = candidates[0]
        else:
            raise ValueError("Choose one model entry from ZIP; found " + str(len(candidates)))
        suffix = PurePosixPath(selected.filename).suffix.casefold()
        if suffix not in {".blend", ".fbx", ".glb", ".obj", ".gltf", ".nif"}:
            raise ValueError("This probe currently imports BLEND, FBX, OBJ, GLB, glTF or NIF only")
        if suffix == ".nif" and not addon_parent:
            raise ValueError("NIF import requires the PyNifly add-on folder")
        with tempfile.TemporaryDirectory(prefix="skyrim-model-probe-") as temp:
            model_name = selected.filename.replace("\\", "/")
            target = extract_checked(archive, selected, temp, model_name,
                                     MAX_GLTF_JSON_BYTES if suffix == ".gltf" else MAX_ENTRY_BYTES)
            extracted = {model_name.casefold()}
            if suffix == ".gltf":
                with open(target, "r", encoding="utf-8-sig") as model_file:
                    document = json.load(model_file)
                if not isinstance(document, dict):
                    raise ValueError("Invalid glTF dependency list")
                references = list(gltf_uris(document))
                if len(references) > MAX_MODEL_DEPENDENCIES:
                    raise ValueError("glTF dependency count exceeds limit")
                for uri in references:
                    name = dependency_name(model_name, uri)
                    if name is not None and name.casefold() not in extracted:
                        dependency = entries.get(name.casefold())
                        if dependency is None:
                            raise ValueError("Missing glTF dependency in ZIP: " + name)
                        extract_checked(archive, dependency, temp, name)
                        extracted.add(name.casefold())
            if suffix == ".obj":
                with open(target, "r", encoding="utf-8", errors="replace") as model_file:
                    material_refs = [line.split(None, 1)[1].strip().strip('"')
                                     for line in model_file if line.lstrip().lower().startswith("mtllib ")]
                if len(material_refs) > MAX_MODEL_DEPENDENCIES:
                    raise ValueError("OBJ material-library count exceeds limit")
                for ref in material_refs:
                    name = dependency_name(model_name, ref)
                    if name is not None and name.casefold() in extracted:
                        continue
                    dependency = entries.get(name.casefold()) if name is not None else None
                    if dependency is None or PurePosixPath(name).suffix.casefold() != ".mtl":
                        raise ValueError("Missing OBJ material library in ZIP: " + ref)
                    if dependency.file_size > 8 * 1024 * 1024:
                        raise ValueError("OBJ material library exceeds size limit")
                    with archive.open(dependency) as source:
                        raw = source.read(8 * 1024 * 1024 + 1)
                    if len(raw) != dependency.file_size:
                        raise ValueError("OBJ material library size mismatch")
                    material_names = [line.split(None, 1)[1].strip() for line in
                                      raw.decode("utf-8", errors="replace").splitlines()
                                      if line.lstrip().lower().startswith("newmtl ")]
                    safe_material = "# Material names only; texture maps are chosen from ZIP in the app.\n"
                    safe_material += "".join("newmtl " + material + "\n" for material in material_names)
                    extract_checked(archive, dependency, temp, name,
                                    content=safe_material.encode("utf-8"))
                    extracted.add(name.casefold())
            if suffix == ".blend":
                bpy.ops.wm.open_mainfile(filepath=target, load_ui=False, use_scripts=False)
                if bpy.data.libraries:
                    raise ValueError("Linked external Blender libraries are not supported")
            else:
                bpy.ops.object.select_all(action="SELECT")
                bpy.ops.object.delete(use_global=False)
            if suffix == ".fbx":
                bpy.ops.import_scene.fbx(filepath=target)
            elif suffix == ".obj":
                bpy.ops.wm.obj_import(filepath=target)
            elif suffix == ".nif":
                sys.path.insert(0, addon_parent)
                bpy.ops.preferences.addon_enable(module="io_scene_nifly")
                bpy.ops.import_scene.pynifly(filepath=target, import_collisions=False)
            elif suffix in (".glb", ".gltf"):
                bpy.ops.import_scene.gltf(filepath=target)
        return digest.hexdigest().upper(), selected.filename


def nif_export_available():
    for name in ("nif", "pynifly"):
        try:
            getattr(bpy.ops.export_scene, name).get_rna_type()
            return True
        except (AttributeError, KeyError, RuntimeError):
            pass
    return False


def geometry_report():
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    if not meshes:
        raise ValueError("Source model contains no mesh objects")
    bounds = [float("inf")] * 3, [float("-inf")] * 3
    result = []
    for obj in meshes:
        mesh = obj.data
        points = [obj.matrix_world @ vertex.co for vertex in mesh.vertices]
        for point in points:
            if not all(math.isfinite(value) for value in point):
                raise ValueError("Non-finite mesh coordinate")
            for axis in range(3):
                bounds[0][axis] = min(bounds[0][axis], point[axis])
                bounds[1][axis] = max(bounds[1][axis], point[axis])
        result.append({
            "name": obj.name,
            "vertices": len(mesh.vertices),
            "polygons": len(mesh.polygons),
            "uv_layers": len(mesh.uv_layers),
            "has_custom_normals": bool(mesh.has_custom_normals) if hasattr(mesh, "has_custom_normals") else None,
            "bounds_min": [min((p[i] for p in points), default=None) for i in range(3)],
            "bounds_max": [max((p[i] for p in points), default=None) for i in range(3)],
            "modifiers": [modifier.type for modifier in obj.modifiers],
            "armature_parent": obj.parent is not None and obj.parent.type == "ARMATURE",
        })
    if not sum(item["vertices"] for item in result):
        raise ValueError("Source model mesh is empty")
    minimum, maximum = bounds
    return {
        "meshes": result,
        "bounds_min": minimum,
        "bounds_max": maximum,
        "dimensions": [maximum[i] - minimum[i] for i in range(3)],
        "long_axis": "XYZ"[max(range(3), key=lambda i: maximum[i] - minimum[i])],
    }


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    if len(args) not in (2, 3, 4):
        raise ValueError("Usage: blender -b --python model_preparation_probe.py -- input.zip report.json [PyNifly folder] [model entry]")
    input_path, report_path = args[:2]
    archive_hash, selected = inspect_archive(input_path,
        args[2] if len(args) >= 3 else None, args[3] if len(args) == 4 else None)
    report = {
        "archive_sha256": archive_hash,
        "selected_model": selected,
        "blender_version": bpy.app.version_string,
        "nif_export_operator_available": nif_export_available(),
        "geometry": geometry_report(),
    }
    with open(report_path, "w", encoding="utf-8") as output:
        json.dump(report, output, indent=2, ensure_ascii=False)
    print("MODEL_PREPARATION_PROBE_OK " + report_path)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("MODEL_PREPARATION_PROBE_FAILED " + str(error), file=sys.stderr)
        sys.exit(1)
