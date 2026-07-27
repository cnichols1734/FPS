#!/usr/bin/env python3
"""Rescue the red Renault wreck for in-game use.

The catalogue flagged it SKIP for three reasons, all of which are fixable rather
than fatal: 1.16M triangles, a raw ~35m bounding box, and a centre pivot that
would make it float when ground-snapped. Vehicle variety matters here (only 3
usable car meshes for 33 placeholder slots), so decimate rather than discard.

Run via: blender --background --python rescue_car.py
Outputs into _incoming/zzz/_derived/ so the original staging tree is untouched
while another agent is importing from it.
"""
import os
import sys

import bpy

SRC = ("/Users/christophernichols/FPS-Shooter/_incoming/zzz/vehicles/"
       "red_car_wreck/source/red-renault-carwreck/red-renault-carwreck.fbx")
OUT_DIR = "/Users/christophernichols/FPS-Shooter/_incoming/zzz/_derived/red_car_wreck"

# A real Renault 5 / Clio-class hatchback is ~3.7m long. The raw file measures
# ~35m, so it was authored in the wrong unit scale.
TARGET_LENGTH_M = 3.8

# Two LODs: LOD0 for hero placement, LOD1 for background fill.
LODS = [("LOD0", 40000), ("LOD1", 12000)]


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.objects):
        for item in list(block):
            try:
                block.remove(item)
            except Exception:
                pass


def mesh_objects():
    return [o for o in bpy.context.scene.objects if o.type == "MESH"]


def total_tris():
    n = 0
    for o in mesh_objects():
        o.data.calc_loop_triangles()
        n += len(o.data.loop_triangles)
    return n


def combined_bounds():
    import mathutils
    mn = mathutils.Vector((1e18, 1e18, 1e18))
    mx = mathutils.Vector((-1e18, -1e18, -1e18))
    for o in mesh_objects():
        for corner in o.bound_box:
            w = o.matrix_world @ mathutils.Vector(corner)
            for i in range(3):
                mn[i] = min(mn[i], w[i])
                mx[i] = max(mx[i], w[i])
    return mn, mx


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    log = []

    clear_scene()
    bpy.ops.import_scene.fbx(filepath=SRC)

    meshes = mesh_objects()
    if not meshes:
        print("RESCUE FAIL: no meshes imported")
        return

    log.append("imported objects: %d" % len(meshes))
    log.append("raw triangles: %d" % total_tris())

    # Join into a single object so decimation and pivot work as one unit.
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    obj.name = "RedRenaultWreck"

    mn, mx = combined_bounds()
    raw_len = max(mx[0] - mn[0], mx[1] - mn[1])
    log.append("raw bounds: %.2f x %.2f x %.2f (longest %.2f m)"
               % (mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2], raw_len))

    # Normalise scale to real-world metres.
    scale = TARGET_LENGTH_M / raw_len if raw_len > 0 else 1.0
    obj.scale = (scale, scale, scale)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    log.append("applied scale factor %.5f -> target %.2f m" % (scale, TARGET_LENGTH_M))

    # Move the origin to the base centre so the car sits ON the ground when
    # placed at a ground-raycast hit, instead of sinking half-way in.
    mn, mx = combined_bounds()
    cx = (mn[0] + mx[0]) / 2.0
    cy = (mn[1] + mx[1]) / 2.0
    bz = mn[2]
    for v in obj.data.vertices:
        v.co.x -= cx
        v.co.y -= cy
        v.co.z -= bz
    obj.location = (0, 0, 0)
    obj.data.update()
    mn, mx = combined_bounds()
    log.append("origin moved to base centre; bounds now min=(%.2f,%.2f,%.2f) max=(%.2f,%.2f,%.2f)"
               % (mn[0], mn[1], mn[2], mx[0], mx[1], mx[2]))

    base_tris = total_tris()
    log.append("triangles before decimate: %d" % base_tris)

    for name, target in LODS:
        # Rebuild from the pristine joined state for each LOD.
        for m in list(obj.modifiers):
            obj.modifiers.remove(m)

        ratio = min(1.0, float(target) / float(base_tris))
        mod = obj.modifiers.new(name="dec", type="DECIMATE")
        mod.decimate_type = "COLLAPSE"
        mod.ratio = ratio

        bpy.context.view_layer.objects.active = obj
        eval_obj = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
        eval_mesh = eval_obj.to_mesh()
        eval_mesh.calc_loop_triangles()
        got = len(eval_mesh.loop_triangles)
        eval_obj.to_mesh_clear()

        out = os.path.join(OUT_DIR, "red_renault_wreck_%s.fbx" % name)
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.export_scene.fbx(
            filepath=out,
            use_selection=True,
            apply_scale_options="FBX_SCALE_ALL",
            global_scale=1.0,
            apply_unit_scale=True,
            use_mesh_modifiers=True,
            mesh_smooth_type="FACE",
            path_mode="COPY",
            embed_textures=False,
        )
        log.append("%s: ratio %.4f -> %d tris -> %s" % (name, ratio, got, os.path.basename(out)))

    for m in list(obj.modifiers):
        obj.modifiers.remove(m)

    report = "\n".join(log)
    with open(os.path.join(OUT_DIR, "RESCUE_REPORT.txt"), "w") as f:
        f.write(report + "\n")
    print("=== RESCUE REPORT ===")
    print(report)


if __name__ == "__main__":
    main()
