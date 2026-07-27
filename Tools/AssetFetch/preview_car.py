#!/usr/bin/env python3
"""Render the rescued wreck so we can confirm it is actually a car-shaped car.

The raw bounding box was near-cubic, which usually means stray geometry sits far
from the body. Verify visually instead of trusting the numbers.
"""
import math
import os

import bpy
import mathutils

SRC = ("/Users/christophernichols/FPS-Shooter/_incoming/zzz/_derived/"
       "red_car_wreck/red_renault_wreck_LOD0.fbx")
OUT = "/Users/christophernichols/FPS-Shooter/_research/car_preview.png"


def clear():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)


def main():
    clear()
    bpy.ops.import_scene.fbx(filepath=SRC)
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    if not meshes:
        print("PREVIEW FAIL: nothing imported")
        return

    # Per-object bounds tell us whether one element is an outlier.
    print("=== PER-OBJECT WORLD BOUNDS ===")
    for o in meshes:
        mn = mathutils.Vector((1e18,) * 3)
        mx = mathutils.Vector((-1e18,) * 3)
        for c in o.bound_box:
            w = o.matrix_world @ mathutils.Vector(c)
            for i in range(3):
                mn[i] = min(mn[i], w[i])
                mx[i] = max(mx[i], w[i])
        o.data.calc_loop_triangles()
        print("%-28s tris=%-7d size=(%.2f, %.2f, %.2f)"
              % (o.name, len(o.data.loop_triangles),
                 mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2]))

    # Histogram of vertex distance from the median centre: reveals outliers.
    obj = meshes[0]
    pts = [obj.matrix_world @ v.co for v in obj.data.vertices]
    cx = sorted(p.x for p in pts)[len(pts) // 2]
    cy = sorted(p.y for p in pts)[len(pts) // 2]
    cz = sorted(p.z for p in pts)[len(pts) // 2]
    centre = mathutils.Vector((cx, cy, cz))
    dists = sorted((p - centre).length for p in pts)
    print("=== VERTEX DISTANCE FROM MEDIAN CENTRE ===")
    for pct in (50, 90, 99, 99.9, 100):
        idx = min(len(dists) - 1, int(len(dists) * pct / 100.0))
        print("  p%-5s = %.3f m" % (pct, dists[idx]))
    print("  verts beyond 3m: %d / %d" % (sum(1 for d in dists if d > 3.0), len(dists)))

    # Frame the bulk of the geometry (p99) rather than the outliers.
    radius = dists[int(len(dists) * 0.99)]
    span = max(radius * 2.2, 1.0)

    bpy.ops.object.camera_add(location=(centre.x + span, centre.y - span, centre.z + span * 0.6))
    cam = bpy.context.object
    direction = centre - cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.camera = cam

    bpy.ops.object.light_add(type="SUN", location=(centre.x + 5, centre.y - 5, centre.z + 10))
    bpy.context.object.data.energy = 4.0

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = 900
    scene.render.resolution_y = 600
    scene.render.filepath = OUT
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    bpy.ops.render.render(write_still=True)
    print("wrote " + OUT)


if __name__ == "__main__":
    main()
