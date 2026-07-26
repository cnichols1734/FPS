"""Reusable headless critique renderer: frames all geometry, renders N views to PNG.

Usage:
  blender -b scene.blend -P render_turntable.py -- /tmp/out 4 BLENDER_EEVEE
"""
import bpy
import sys
import math
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out_prefix = argv[0] if argv else "/tmp/turn"
n_views = int(argv[1]) if len(argv) > 1 else 4
engine = argv[2] if len(argv) > 2 else "BLENDER_EEVEE"

scene = bpy.context.scene
meshes = [o for o in bpy.data.objects if o.type == "MESH" and not o.hide_render]

pts = []
for o in meshes:
    for c in o.bound_box:
        pts.append(o.matrix_world @ Vector(c))
mn = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
mx = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
center = (mn + mx) / 2
radius = (mx - mn).length / 2
deps = bpy.context.evaluated_depsgraph_get()
raw = sum(len(o.data.polygons) for o in meshes)
evald = 0
for o in meshes:
    m = o.evaluated_get(deps).to_mesh()
    m.calc_loop_triangles()
    evald += len(m.loop_triangles)
    o.evaluated_get(deps).to_mesh_clear()
print(f"SCENE meshes={len(meshes)} raw_polys={raw} evaluated_tris={evald} "
      f"bbox={tuple(round(v,3) for v in (mx-mn))} radius={radius:.3f}")

# 3-point studio lighting using sun lamps (distance independent)
for rot, e in (((math.radians(55), 0, math.radians(40)), 5.0),
               ((math.radians(65), 0, math.radians(215)), 2.0),
               ((math.radians(110), 0, math.radians(300)), 1.2)):
    ld = bpy.data.lights.new("Sun", "SUN")
    ld.energy = e
    ld.angle = math.radians(9)
    lo = bpy.data.objects.new("Sun", ld)
    scene.collection.objects.link(lo)
    lo.rotation_euler = rot

w = bpy.data.worlds.new("W")
w.use_nodes = True
bg = w.node_tree.nodes["Background"]
bg.inputs[0].default_value = (0.19, 0.20, 0.23, 1)
bg.inputs[1].default_value = 0.75
scene.world = w

cam_data = bpy.data.cameras.new("Cam")
cam_data.lens = 50
cam = bpy.data.objects.new("Cam", cam_data)
scene.collection.objects.link(cam)
scene.camera = cam

scene.view_settings.view_transform = "AgX"
scene.view_settings.look = "AgX - Base Contrast"
scene.render.engine = engine
scene.render.resolution_x = 1100
scene.render.resolution_y = 700
scene.render.film_transparent = False
scene.render.image_settings.file_format = "PNG"
if engine == "CYCLES":
    scene.cycles.samples = 96
    scene.cycles.device = "GPU"
else:
    scene.eevee.taa_render_samples = 64

# angles: 3/4 front, side, 3/4 rear, top-down muzzle
angles = [(0.72, -1.0, 0.42), (0.0, -1.0, 0.10), (-0.85, -1.0, 0.38), (0.55, -0.55, 1.0)]
dist_mul = [4.2, 3.9, 4.2, 4.2]

for i in range(min(n_views, len(angles))):
    d = Vector(angles[i]).normalized()
    cam.location = center + d * radius * dist_mul[i]
    cam.rotation_euler = (center - cam.location).normalized().to_track_quat("-Z", "Y").to_euler()
    scene.render.filepath = f"{out_prefix}_{i}.png"
    bpy.ops.render.render(write_still=True)
    print(f"VIEW {i} -> {out_prefix}_{i}.png")

print("TURNTABLE_OK")
