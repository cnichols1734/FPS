"""
Import Quaternius CC0 AssaultRifle2 and prepare it as an Arena FPS viewmodel.

Blender working axes: +Y is barrel-forward, +Z is up. The FBX export remaps those to
Unity +Z forward / +Y up.

Pivot contract (relied on by M4ViewmodelBuilder):
  * X: weapon centreline sits on x = 0
  * Y: stock butt sits on y = 0, so the whole gun extends forward of the pivot
  * Z: the ghost-ring optical axis sits on z = 0, so ADS is a near-pure forward push

Anything downstream that needs a different pose should move WeaponRoot, not the mesh.
"""
from __future__ import annotations

import math
import os
import sys

import bpy
from mathutils import Vector

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, ROOT)

from pipeline.hardsurface import assert_blender_version, reset_scene  # noqa: E402

CACHE = os.path.abspath(
    os.path.join(
        ROOT,
        "..",
        "Tools",
        "AssetFetch",
        "cache",
        "weapons",
        "extracted",
        "Ultimate Gun Pack - July 2019",
        "FBX",
    )
)
OUT_DIR = os.path.abspath(
    os.path.join(ROOT, "..", "Assets", "_Project", "Resources", "Weapons")
)
ART_DIR = os.path.abspath(
    os.path.join(ROOT, "..", "Assets", "_Project", "Art", "Models", "Weapons", "M4")
)

# Muzzle-to-buttplate length of the finished viewmodel, in metres. Real carbines are
# ~0.84 m; viewmodels are shortened so the stock does not fill the lower screen.
TARGET_LENGTH = 0.52

# Fraction along the barrel axis for the irons. The rear aperture wants to be near the
# eye and the post near the muzzle, which is what gives the sights a usable baseline.
REAR_SIGHT_ALONG = 0.42
FRONT_SIGHT_ALONG = 0.88


def mat(name, color, rough=0.4, metal=0.85):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = metal
    return m


def import_fbx(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    return [o for o in bpy.data.objects if o not in before]


def join_meshes(objects, name):
    meshes = [o for o in objects if o.type == "MESH"]
    if not meshes:
        raise RuntimeError(f"No meshes in {name}")
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    meshes[0].name = name
    return meshes[0]


def world_bounds(obj):
    corners = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
    mn = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
    mx = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
    return mn, mx, (mn + mx) * 0.5, mx - mn


def apply_transforms(obj):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    obj.select_set(False)


def empty(name, loc, parent):
    bpy.ops.object.empty_add(type="PLAIN_AXES", location=loc)
    o = bpy.context.active_object
    o.name = name
    o.empty_display_size = 0.02
    o.parent = parent
    return o


def muzzle_points_forward(obj) -> bool:
    """True when the thin barrel end is toward +Y.

    Quaternius guns arrive facing either way. Sniff the silhouette instead of trusting
    the source: slice along Y and compare how tall the mesh is at each end. The receiver,
    grip and magazine make the stock end far deeper than the barrel end.
    """
    verts = [obj.matrix_world @ v.co for v in obj.data.vertices]
    ys = [v.y for v in verts]
    lo, hi = min(ys), max(ys)
    span = hi - lo
    if span <= 1e-6:
        return True

    def depth(a, b):
        zs = [v.z for v in verts if a <= v.y <= b]
        return (max(zs) - min(zs)) if zs else 0.0

    back = depth(lo, lo + span * 0.25)
    front = depth(hi - span * 0.25, hi)
    return front < back


def build():
    assert_blender_version()
    reset_scene()

    rifle_path = os.path.join(CACHE, "AssaultRifle2_1.fbx")
    if not os.path.isfile(rifle_path):
        raise FileNotFoundError(rifle_path)

    rifle = join_meshes(import_fbx(rifle_path), "RifleMesh")
    apply_transforms(rifle)

    mn, mx, center, size = world_bounds(rifle)
    axes = sorted([("x", size.x), ("y", size.y), ("z", size.z)], key=lambda a: a[1], reverse=True)
    long_axis = axes[0][0]
    print(f"[prepare_m4] raw size={tuple(round(v, 4) for v in size)} long={long_axis}")

    rifle.location -= center
    apply_transforms(rifle)

    # Put the long axis on +Y and keep up on +Z.
    if long_axis == "x":
        rifle.rotation_euler = (0, 0, math.radians(90))
    elif long_axis == "z":
        rifle.rotation_euler = (math.radians(-90), 0, 0)
    apply_transforms(rifle)

    if not muzzle_points_forward(rifle):
        print("[prepare_m4] barrel pointed backwards — flipping 180 about Z")
        rifle.rotation_euler = (0, 0, math.radians(180))
        apply_transforms(rifle)

    # Uniform scale to the target length. The old build clamped height here, which
    # silently shrank the whole weapon to a 26 cm stub; proportions are the model's to
    # decide, so only the length is driven.
    mn, mx, center, size = world_bounds(rifle)
    scale = TARGET_LENGTH / max(size.y, 1e-6)
    rifle.scale = (scale, scale, scale)
    apply_transforms(rifle)

    mn, mx, center, size = world_bounds(rifle)
    print(f"[prepare_m4] scaled size={tuple(round(v, 4) for v in size)}")

    metal = mat("AR_Metal", (0.08, 0.082, 0.09), rough=0.4, metal=0.9)
    dark = mat("AR_Dark", (0.035, 0.036, 0.04), rough=0.55, metal=0.75)
    rifle.data.materials.clear()
    rifle.data.materials.append(metal)

    bpy.ops.object.empty_add(type="PLAIN_AXES", location=(0, 0, 0))
    root = bpy.context.active_object
    root.name = "M4_Viewmodel"
    root.empty_display_size = 0.04
    rifle.parent = root

    # Open ghost-ring optic on the rail: large aperture, thin rim, so ADS stays
    # see-through once depth of field softens it.
    ring_radius = 0.014
    optic_y = mn.y + size.y * REAR_SIGHT_ALONG
    optic_z = mx.z + ring_radius * 0.5
    bpy.ops.mesh.primitive_torus_add(
        major_radius=ring_radius,
        minor_radius=0.0018,
        major_segments=28,
        minor_segments=8,
        location=(0.0, optic_y, optic_z),
        # A torus is born in the XY plane with its hole along +Z. The hole has to look
        # down the barrel, so swing Z onto Y. The old build rotated about Y instead,
        # which left the ring lying flat like a washer — impossible to aim through.
        rotation=(math.radians(90), 0, 0),
    )
    ring = bpy.context.active_object
    ring.name = "GhostRing"
    ring.data.materials.append(dark)
    ring.parent = root

    bpy.ops.mesh.primitive_cube_add(
        size=1, location=(0.0, mn.y + size.y * FRONT_SIGHT_ALONG, optic_z)
    )
    post = bpy.context.active_object
    post.name = "FrontPost"
    post.scale = Vector((0.0025, 0.0025, 0.012))
    bpy.ops.object.transform_apply(scale=True)
    post.data.materials.append(dark)
    post.parent = root

    # Re-origin to the pivot contract: centreline on x=0, stock butt on y=0, optical
    # axis on z=0. Done by moving the children so the root empty stays clean at origin.
    shift = Vector((-center.x, -mn.y, -optic_z))
    for child in (rifle, ring, post):
        child.location += shift
        apply_transforms(child)

    mn, mx, center, size = world_bounds(rifle)
    optic_y += shift.y
    optic_z += shift.z

    sight_loc = Vector((0.0, optic_y, optic_z))
    muzzle_loc = Vector((0.0, mx.y + 0.01, 0.0))
    empty("SightAlign", sight_loc, root)
    empty("Muzzle", muzzle_loc, root)
    empty("FirePoint", muzzle_loc, root)
    empty("EjectionPort", Vector((0.022, center.y + size.y * 0.1, mx.z * 0.5)), root)

    os.makedirs(OUT_DIR, exist_ok=True)
    os.makedirs(ART_DIR, exist_ok=True)

    for out in (
        os.path.join(OUT_DIR, "M4_Viewmodel.fbx"),
        os.path.join(ART_DIR, "M4_Viewmodel.fbx"),
    ):
        bpy.ops.object.select_all(action="DESELECT")
        root.select_set(True)
        for o in root.children_recursive:
            o.select_set(True)
        bpy.context.view_layer.objects.active = root
        bpy.ops.export_scene.fbx(
            filepath=out,
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
        print(f"[prepare_m4] exported {out}")

    print(
        f"[prepare_m4] final size={tuple(round(v, 4) for v in size)} "
        f"bounds y[{mn.y:+.4f},{mx.y:+.4f}] z[{mn.z:+.4f},{mx.z:+.4f}]"
    )
    print(f"[prepare_m4] sight={tuple(round(v, 4) for v in sight_loc)} (Blender m)")
    print("[prepare_m4] FBX is written in centimetres; Unity converts file units on import.")


if __name__ == "__main__":
    build()
