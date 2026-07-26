"""
Procedural M4-style carbine viewmodel for Arena FPS.
Barrel along +Y in Blender; FBX export remaps to Unity +Z forward.
Includes a large rear ghost-ring aperture so ADS DOF reads as see-through irons.
"""
from __future__ import annotations

import math
import os
import sys

import bpy
import bmesh
from mathutils import Vector, Matrix

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, ROOT)

from pipeline.hardsurface import (  # noqa: E402
    assert_blender_version,
    reset_scene,
    add_bevel,
    apply_object_transforms,
    export_fbx,
    force_material_index_zero,
    smart_uv,
)

MM = 0.001
OUT_DIR = os.path.abspath(
    os.path.join(ROOT, "..", "Assets", "_Project", "Art", "Models", "Weapons", "M4")
)


def mat(name: str, color, rough=0.45, metal=0.85):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = metal
    return m


def box(name, size, loc=(0, 0, 0), rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    o.scale = Vector(size)
    bpy.ops.object.transform_apply(scale=True)
    return o


def cyl(name, r, depth, loc=(0, 0, 0), rot=(0, 0, 0), verts=32):
    bpy.ops.mesh.primitive_cylinder_add(
        radius=r, depth=depth, location=loc, rotation=rot, vertices=verts
    )
    o = bpy.context.active_object
    o.name = name
    return o


def torus(name, major, minor, loc=(0, 0, 0), rot=(0, 0, 0), major_seg=28, minor_seg=12):
    bpy.ops.mesh.primitive_torus_add(
        major_radius=major,
        minor_radius=minor,
        major_segments=major_seg,
        minor_segments=minor_seg,
        location=loc,
        rotation=rot,
    )
    o = bpy.context.active_object
    o.name = name
    return o


def empty(name, loc, parent=None):
    bpy.ops.object.empty_add(type="PLAIN_AXES", location=loc)
    o = bpy.context.active_object
    o.name = name
    o.empty_display_size = 0.02
    if parent:
        o.parent = parent
    return o


def join(objects, name):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    objects[0].name = name
    return objects[0]


def finish(obj, bw=0.4 * MM):
    add_bevel(obj, width=bw, segments=2)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35))
    force_material_index_zero(obj)
    smart_uv(obj)


def build():
    assert_blender_version()
    reset_scene()

    metal = mat("M4_Metal", (0.08, 0.085, 0.09), rough=0.42, metal=0.92)
    dark = mat("M4_Dark", (0.04, 0.042, 0.045), rough=0.55, metal=0.75)
    poly = mat("M4_Polymer", (0.035, 0.036, 0.038), rough=0.72, metal=0.05)
    bronze = mat("M4_Barrel", (0.12, 0.11, 0.09), rough=0.38, metal=0.95)

    root = bpy.data.objects.new("M4_Viewmodel", None)
    bpy.context.collection.objects.link(root)
    root.empty_display_size = 0.05

    parts = []

    # ---- Lower receiver -------------------------------------------------
    lower = box("lower", (0.038, 0.165, 0.055), loc=(0, 0.02, -0.01))
    lower.data.materials.append(dark)
    parts.append(lower)

    # Magwell flare
    magwell = box("magwell", (0.034, 0.055, 0.04), loc=(0, 0.005, -0.045))
    magwell.data.materials.append(dark)
    parts.append(magwell)

    # Trigger guard
    guard = box("trigger_guard", (0.018, 0.045, 0.022), loc=(0, -0.055, -0.04))
    guard.data.materials.append(dark)
    parts.append(guard)

    # ---- Upper receiver / rail -----------------------------------------
    upper = box("upper", (0.042, 0.195, 0.048), loc=(0, 0.03, 0.03))
    upper.data.materials.append(metal)
    parts.append(upper)

    rail = box("top_rail", (0.022, 0.28, 0.01), loc=(0, 0.08, 0.058))
    rail.data.materials.append(metal)
    parts.append(rail)

    # Rail serrations (visual blocks)
    for i in range(14):
        y = -0.04 + i * 0.018
        tooth = box(f"rail_t{i}", (0.024, 0.006, 0.006), loc=(0, y, 0.066))
        tooth.data.materials.append(dark)
        parts.append(tooth)

    # ---- Handguard ------------------------------------------------------
    hg = box("handguard", (0.046, 0.195, 0.05), loc=(0, 0.22, 0.01))
    hg.data.materials.append(poly)
    parts.append(hg)

    for side, sx in (("L", 0.028), ("R", -0.028)):
        sr = box(f"side_rail_{side}", (0.008, 0.16, 0.018), loc=(sx, 0.22, 0.01))
        sr.data.materials.append(metal)
        parts.append(sr)

    # ---- Barrel + flash hider ------------------------------------------
    barrel = cyl(
        "barrel",
        0.0095,
        0.28,
        loc=(0, 0.42, 0.012),
        rot=(math.radians(90), 0, 0),
        verts=24,
    )
    barrel.data.materials.append(bronze)
    parts.append(barrel)

    gas = cyl(
        "gas_block",
        0.014,
        0.028,
        loc=(0, 0.355, 0.012),
        rot=(math.radians(90), 0, 0),
        verts=16,
    )
    gas.data.materials.append(metal)
    parts.append(gas)

    flash = cyl(
        "flash_hider",
        0.013,
        0.055,
        loc=(0, 0.575, 0.012),
        rot=(math.radians(90), 0, 0),
        verts=20,
    )
    flash.data.materials.append(metal)
    parts.append(flash)

    # Slots in flash hider (visual cutters left as dark rings)
    for i, oy in enumerate((-0.012, 0.0, 0.012)):
        ring = cyl(
            f"flash_slot_{i}",
            0.0145,
            0.006,
            loc=(0, 0.575 + oy, 0.012),
            rot=(math.radians(90), 0, 0),
            verts=16,
        )
        ring.data.materials.append(dark)
        parts.append(ring)

    # ---- Stock + buffer -------------------------------------------------
    buffer = cyl(
        "buffer",
        0.016,
        0.14,
        loc=(0, -0.145, 0.015),
        rot=(math.radians(90), 0, 0),
        verts=20,
    )
    buffer.data.materials.append(metal)
    parts.append(buffer)

    stock = box("stock", (0.04, 0.155, 0.07), loc=(0, -0.265, -0.005))
    stock.data.materials.append(poly)
    parts.append(stock)

    stock_pad = box("stock_pad", (0.042, 0.012, 0.075), loc=(0, -0.345, -0.005))
    stock_pad.data.materials.append(poly)
    parts.append(stock_pad)

    # ---- Grip + mag -----------------------------------------------------
    grip = box(
        "grip",
        (0.028, 0.03, 0.085),
        loc=(0, -0.05, -0.085),
        rot=(math.radians(18), 0, 0),
    )
    grip.data.materials.append(poly)
    parts.append(grip)

    mag = box("mag", (0.025, 0.04, 0.11), loc=(0, 0.01, -0.11))
    mag.data.materials.append(poly)
    parts.append(mag)

    # ---- Rear ghost-ring iron sight (critical for ADS see-through) ------
    # Large aperture ring facing the eye — when DOF softens it, the hole stays usable.
    rear_base = box("rear_sight_base", (0.028, 0.022, 0.018), loc=(0, -0.02, 0.072))
    rear_base.data.materials.append(metal)
    parts.append(rear_base)

    # Ring in the YZ plane looking down the barrel (+Y).
    rear_ring = torus(
        "rear_ghost_ring",
        major=0.0135,
        minor=0.0022,
        loc=(0, -0.008, 0.086),
        rot=(0, math.radians(90), 0),
        major_seg=32,
        minor_seg=10,
    )
    rear_ring.data.materials.append(dark)
    parts.append(rear_ring)

    # Thin protective ears that don't close the aperture.
    for side, sx in (("L", 0.016), ("R", -0.016)):
        ear = box(f"rear_ear_{side}", (0.004, 0.01, 0.02), loc=(sx, -0.01, 0.086))
        ear.data.materials.append(dark)
        parts.append(ear)

    # ---- Front sight post + open hood ----------------------------------
    front_base = box("front_sight_base", (0.018, 0.02, 0.016), loc=(0, 0.355, 0.035))
    front_base.data.materials.append(metal)
    parts.append(front_base)

    post = box("front_post", (0.0035, 0.0035, 0.018), loc=(0, 0.355, 0.055))
    post.data.materials.append(dark)
    parts.append(post)

    for side, sx in (("L", 0.012), ("R", -0.012)):
        wing = box(f"front_wing_{side}", (0.003, 0.012, 0.022), loc=(sx, 0.355, 0.05))
        wing.data.materials.append(dark)
        parts.append(wing)

    # ---- Join into single mesh for clean import ------------------------
    for p in parts:
        apply_object_transforms(p)
        p.parent = root

    # Keep as separate meshes under root for material variety — finish each.
    for p in parts:
        finish(p, bw=0.35 * MM)
        p.parent = root

    # ---- Anchors (exported as empties parented to root) -----------------
    # Optical axis through ghost ring center and front post tip.
    sight_y = -0.008
    sight_z = 0.086
    sight = empty("SightAlign", (0, sight_y, sight_z), parent=root)
    muzzle = empty("Muzzle", (0, 0.60, 0.012), parent=root)
    fire = empty("FirePoint", (0, 0.60, 0.012), parent=root)
    eject = empty("EjectionPort", (0.03, 0.06, 0.03), parent=root)

    # Orient empties: local +Y is barrel forward in Blender; Unity FBX remap → +Z.
    for e in (sight, muzzle, fire, eject):
        e.rotation_euler = (0, 0, 0)

    os.makedirs(OUT_DIR, exist_ok=True)
    fbx_path = os.path.join(OUT_DIR, "M4_Viewmodel.fbx")

    # Export meshes + empties. hardsurface export_fbx only selects MESH by default —
    # temporarily widen object_types via a local export.
    bpy.ops.object.select_all(action="DESELECT")
    root.select_set(True)
    for o in root.children:
        o.select_set(True)
    bpy.context.view_layer.objects.active = root

    bpy.ops.export_scene.fbx(
        filepath=fbx_path,
        use_selection=True,
        apply_scale_options="FBX_SCALE_NONE",
        global_scale=1.0,
        axis_forward="-Z",
        axis_up="Y",
        object_types={"EMPTY", "MESH"},
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        bake_space_transform=True,
        path_mode="COPY",
        embed_textures=False,
        add_leaf_bones=False,
    )
    print(f"[proc_m4] exported {fbx_path}")
    print(f"[proc_m4] SightAlign at local (0, {sight_y}, {sight_z}) Blender / expect Unity (0, {sight_z}, {-sight_y}) after remap check")


if __name__ == "__main__":
    build()
