import bpy
import sys
import time

argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []
engine = argv[0] if argv else "BLENDER_EEVEE"
out = argv[1] if len(argv) > 1 else "/tmp/headless_test.png"

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

# Simple hard-surface-ish test object: beveled cube + cylinder (barrel proxy)
bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0, 0, 0))
body = bpy.context.active_object
body.scale = (1.6, 0.25, 0.4)
bev = body.modifiers.new("Bevel", 'BEVEL')
bev.width = 0.02
bev.segments = 3
bev.harden_normals = True

bpy.ops.mesh.primitive_cylinder_add(radius=0.06, depth=1.2, location=(1.4, 0, 0.1),
                                    rotation=(0, 1.5708, 0), vertices=32)
barrel = bpy.context.active_object

mat = bpy.data.materials.new("GunMetal")
mat.use_nodes = True
bsdf = mat.node_tree.nodes["Principled BSDF"]
bsdf.inputs["Base Color"].default_value = (0.12, 0.12, 0.13, 1.0)
bsdf.inputs["Metallic"].default_value = 1.0
bsdf.inputs["Roughness"].default_value = 0.35
for o in (body, barrel):
    o.data.materials.append(mat)

cam_data = bpy.data.cameras.new("Cam")
cam = bpy.data.objects.new("Cam", cam_data)
scene.collection.objects.link(cam)
cam.location = (2.6, -3.4, 1.9)
cam.rotation_euler = (1.15, 0.0, 0.62)
scene.camera = cam

light_data = bpy.data.lights.new("Key", 'AREA')
light_data.energy = 900
light_data.size = 4
light = bpy.data.objects.new("Key", light_data)
scene.collection.objects.link(light)
light.location = (3, -4, 5)
light.rotation_euler = (0.5, 0.3, 0.6)

world = bpy.data.worlds.new("W")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.05, 0.05, 0.06, 1)
scene.world = world

scene.render.engine = engine
scene.render.resolution_x = 640
scene.render.resolution_y = 400
scene.render.image_settings.file_format = 'PNG'
scene.render.filepath = out
if engine == "CYCLES":
    scene.cycles.samples = 32

t0 = time.time()
bpy.ops.render.render(write_still=True)
print(f"RESULT_OK engine={engine} seconds={time.time() - t0:.2f} out={out}")
