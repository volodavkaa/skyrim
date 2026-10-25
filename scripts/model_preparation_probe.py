"""Read-only FBX geometry probe for the model-preparation NIF export gate.

Run with Blender in background mode:
  blender -b --factory-startup --python scripts/model_preparation_probe.py -- input.zip report.json

The script extracts only the selected model into a temporary directory. It does
not save a blend file, change the archive, or claim to produce a game-ready NIF.
"""

import hashlib
import json
import math
import os
from pathlib import PurePosixPath
import stat
import sys
import tempfile
import zipfile

import bpy


MODEL_SUFFIXES = {".blend", ".fbx", ".obj", ".glb", ".gltf", ".nif"}
MAX_ENTRIES = 10_000
MAX_ENTRY_BYTES = 512 * 1024 * 1024
MAX_TOTAL_BYTES = 2 * 1024 * 1024 * 1024


def inspect_archive(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    with zipfile.ZipFile(path) as archive:
        if len(archive.infolist()) > MAX_ENTRIES:
            raise ValueError("ZIP entry count exceeds limit")
        names = set()
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
            if entry.file_size > MAX_ENTRY_BYTES:
                raise ValueError("ZIP entry exceeds size limit: " + name)
            total += entry.file_size
            if total > MAX_TOTAL_BYTES:
                raise ValueError("ZIP expanded size exceeds limit")
            if PurePosixPath(name).suffix.casefold() in MODEL_SUFFIXES:
                candidates.append(entry)
        if len(candidates) != 1:
            raise ValueError("Expected exactly one model candidate; found " + str(len(candidates)))
        selected = candidates[0]
        if PurePosixPath(selected.filename).suffix.casefold() != ".fbx":
            raise ValueError("This probe currently imports FBX only")
        with tempfile.TemporaryDirectory(prefix="skyrim-model-probe-") as temp:
            target = os.path.join(temp, "model.fbx")
            with archive.open(selected) as source, open(target, "xb") as output:
                copied = 0
                for chunk in iter(lambda: source.read(1024 * 1024), b""):
                    copied += len(chunk)
                    if copied > selected.file_size:
                        raise ValueError("ZIP entry expanded beyond declared size")
                    output.write(chunk)
                if copied != selected.file_size:
                    raise ValueError("ZIP entry size mismatch")
            bpy.ops.object.select_all(action="SELECT")
            bpy.ops.object.delete(use_global=False)
            bpy.ops.import_scene.fbx(filepath=target)
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
        raise ValueError("FBX contains no mesh objects")
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
        raise ValueError("FBX mesh is empty")
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
    if len(args) != 2:
        raise ValueError("Usage: blender -b --python model_preparation_probe.py -- input.zip report.json")
    input_path, report_path = args
    archive_hash, selected = inspect_archive(input_path)
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
