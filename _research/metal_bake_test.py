import bpy
import time

prefs = bpy.context.preferences.addons["cycles"].preferences
print("=== CYCLES DEVICE ENUMERATION (background mode) ===")
for dev_type in ("METAL", "CPU"):
    try:
        prefs.compute_device_type = dev_type
    except TypeError:
        print(f"  compute_device_type '{dev_type}' NOT AVAILABLE")
        continue
    prefs.get_devices()
    devs = [(d.name, d.type, d.use) for d in prefs.devices]
    print(f"  compute_device_type={dev_type} -> {devs}")

prefs.compute_device_type = "METAL"
prefs.get_devices()
for d in prefs.devices:
    d.use = True

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.engine = "CYCLES"
scene.cycles.device = "GPU"
scene.cycles.samples = 16

bpy.ops.mesh.primitive_uv_sphere_add(radius=1.0)
obj = bpy.context.active_object
bpy.ops.object.shade_smooth()

# UV unwrap (needed for baking)
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.uv.smart_project()
bpy.ops.object.mode_set(mode="OBJECT")

# Procedural "worn metal" material: noise-driven roughness + pointiness edge wear
mat = bpy.data.materials.new("WornMetal")
mat.use_nodes = True
nt = mat.node_tree
bsdf = nt.nodes["Principled BSDF"]

geo = nt.nodes.new("ShaderNodeNewGeometry")
ramp = nt.nodes.new("ShaderNodeValToRGB")
ramp.color_ramp.elements[0].position = 0.45
ramp.color_ramp.elements[1].position = 0.58
noise = nt.nodes.new("ShaderNodeTexNoise")
noise.inputs["Scale"].default_value = 24.0
mixc = nt.nodes.new("ShaderNodeMix")
mixc.data_type = "RGBA"

nt.links.new(geo.outputs["Pointiness"], ramp.inputs["Fac"])
nt.links.new(ramp.outputs["Color"], mixc.inputs["Factor"])
mixc.inputs[6].default_value = (0.05, 0.05, 0.055, 1.0)   # dark parkerized base
mixc.inputs[7].default_value = (0.62, 0.60, 0.57, 1.0)    # bare steel edge wear
nt.links.new(mixc.outputs[2], bsdf.inputs["Base Color"])
nt.links.new(noise.outputs["Fac"], bsdf.inputs["Roughness"])
bsdf.inputs["Metallic"].default_value = 1.0
obj.data.materials.append(mat)

# Bake target
img = bpy.data.images.new("BakedBaseColor", 1024, 1024)
tex_node = nt.nodes.new("ShaderNodeTexImage")
tex_node.image = img
nt.nodes.active = tex_node
tex_node.select = True

bpy.context.view_layer.objects.active = obj
obj.select_set(True)

print("=== BAKE TEST (headless) ===")
t0 = time.time()
try:
    bpy.ops.object.bake(type="DIFFUSE", pass_filter={"COLOR"}, save_mode="INTERNAL")
    img.filepath_raw = "/tmp/hl_bake_basecolor.png"
    img.file_format = "PNG"
    img.save()
    print(f"BAKE_OK seconds={time.time() - t0:.2f} -> /tmp/hl_bake_basecolor.png")
except Exception as e:
    print(f"BAKE_FAIL {type(e).__name__}: {e}")

print("=== GPU RENDER TEST ===")
scene.render.filepath = "/tmp/hl_cycles_gpu.png"
scene.render.resolution_x = 400
scene.render.resolution_y = 400
t0 = time.time()
bpy.ops.render.render(write_still=True)
print(f"GPU_RENDER_OK seconds={time.time() - t0:.2f} device={scene.cycles.device}")
