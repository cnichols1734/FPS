import bpy
import math
from mathutils import Vector

target = bpy.data.objects.get("Hand  - Realistic")
scene = bpy.context.scene

for o in list(bpy.data.objects):
    if o is not target:
        bpy.data.objects.remove(o, do_unlink=True)
if target.name not in scene.collection.objects:
    scene.collection.objects.link(target)

# Show multires at full level so we judge the real sculpt density
for m in target.modifiers:
    if m.type == "MULTIRES":
        m.levels = m.total_levels
        m.render_levels = m.total_levels
        print(f"MULTIRES total={m.total_levels}")

target.location = (0, 0, 0)
target.rotation_euler = (0, 0, 0)
bpy.context.view_layer.update()

co = [target.matrix_world @ v.co for v in target.data.vertices]
mn = Vector((min(c.x for c in co), min(c.y for c in co), min(c.z for c in co)))
mx = Vector((max(c.x for c in co), max(c.y for c in co), max(c.z for c in co)))
center = (mn + mx) / 2
size = max(mx - mn)
print(f"BBOX size={size:.4f}m center={tuple(round(v,3) for v in center)}")

mat = bpy.data.materials.new("Skin")
mat.use_nodes = True
b = mat.node_tree.nodes["Principled BSDF"]
b.inputs["Base Color"].default_value = (0.58, 0.40, 0.33, 1)
b.inputs["Roughness"].default_value = 0.5
target.data.materials.clear()
target.data.materials.append(mat)

cam_data = bpy.data.cameras.new("Cam")
cam_data.lens = 55
cam = bpy.data.objects.new("Cam", cam_data)
scene.collection.objects.link(cam)
dist = size * 2.2
direction = Vector((0.75, -1.0, 0.55)).normalized()
cam.location = center + direction * dist
look = (center - cam.location).normalized()
cam.rotation_euler = look.to_track_quat('-Z', 'Y').to_euler()
scene.camera = cam

# Sun lamps: intensity is distance-independent, so no blowout
for rot, energy in (((math.radians(50), 0, math.radians(35)), 4.0),
                    ((math.radians(70), 0, math.radians(200)), 1.5)):
    ld = bpy.data.lights.new("Sun", 'SUN')
    ld.energy = energy
    ld.angle = math.radians(12)
    lo = bpy.data.objects.new("Sun", ld)
    scene.collection.objects.link(lo)
    lo.rotation_euler = rot

w = bpy.data.worlds.new("W")
w.use_nodes = True
w.node_tree.nodes["Background"].inputs[0].default_value = (0.15, 0.16, 0.18, 1)
w.node_tree.nodes["Background"].inputs[1].default_value = 0.6
scene.world = w

scene.view_settings.view_transform = 'AgX'
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = 900
scene.render.resolution_y = 700
scene.render.filepath = "/tmp/hbm_hand.png"
scene.render.image_settings.file_format = 'PNG'
bpy.ops.render.render(write_still=True)

deps = bpy.context.evaluated_depsgraph_get()
ev = target.evaluated_get(deps)
print(f"EVALUATED polys={len(ev.data.polygons)}")
print("HAND_RENDER_OK /tmp/hbm_hand.png")
