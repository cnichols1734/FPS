"""
Shared hard-surface helpers for Arena FPS weapon/character authoring.
Pinned to Blender 5.2 LTS. Enforces: chamfers, applied transforms, FBX contract.
"""
from __future__ import annotations

import sys
import bpy
import bmesh
from mathutils import Vector


REQUIRED_MAJOR = 5
REQUIRED_MINOR = 2


def assert_blender_version() -> None:
    v = bpy.app.version
    if v[0] < REQUIRED_MAJOR or (v[0] == REQUIRED_MAJOR and v[1] < REQUIRED_MINOR):
        raise RuntimeError(
            f"Need Blender {REQUIRED_MAJOR}.{REQUIRED_MINOR}+, got {v[0]}.{v[1]}.{v[2]}"
        )
    print(f"[hardsurface] Blender {v[0]}.{v[1]}.{v[2]} OK")


def reset_scene() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)


def apply_object_transforms(obj: bpy.types.Object) -> None:
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    obj.select_set(False)


def add_bevel(obj: bpy.types.Object, width: float = 0.0008, segments: int = 2) -> None:
    """Mandatory — zero razor edges."""
    mod = obj.modifiers.new(name="Chamfer", type="BEVEL")
    mod.width = width
    mod.segments = segments
    mod.limit_method = "ANGLE"
    mod.angle_limit = 0.523599  # 30 deg
    mod.harden_normals = True


def force_material_index_zero(obj: bpy.types.Object) -> None:
    """Booleans merge cutter slots and leave null material indices — fix silently."""
    mesh = obj.data
    if not isinstance(mesh, bpy.types.Mesh):
        return
    while len(obj.material_slots) > 1:
        obj.active_material_index = len(obj.material_slots) - 1
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.material_slot_remove()
    for poly in mesh.polygons:
        poly.material_index = 0


def smart_uv(obj: bpy.types.Object) -> None:
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=66.0, island_margin=0.02)
    bpy.ops.object.mode_set(mode="OBJECT")
    obj.select_set(False)


def export_fbx(filepath: str, objects: list[bpy.types.Object] | None = None) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    if objects:
        for obj in objects:
            obj.select_set(True)
            bpy.context.view_layer.objects.active = obj
    else:
        bpy.ops.object.select_all(action="SELECT")

    bpy.ops.export_scene.fbx(
        filepath=filepath,
        use_selection=True,
        apply_scale_options="FBX_SCALE_NONE",
        global_scale=1.0,
        axis_forward="-Z",
        axis_up="Y",
        object_types={"MESH", "ARMATURE"},
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        bake_space_transform=True,
        path_mode="COPY",
        embed_textures=False,
        add_leaf_bones=False,
    )
    print(f"[hardsurface] exported {filepath}")


def set_cycles_metal() -> None:
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    cycles = scene.cycles
    cycles.device = "GPU"
    prefs = bpy.context.preferences.addons.get("cycles")
    if prefs:
        cprefs = prefs.preferences
        cprefs.compute_device_type = "METAL"
        for device in cprefs.devices:
            device.use = True


if __name__ == "__main__":
    assert_blender_version()
    print("hardsurface.py self-check OK")
