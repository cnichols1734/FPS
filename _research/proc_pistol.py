"""Procedural hard-surface pistol, authored purely in bpy.

Test case for: can an AI agent write Blender Python that produces a
credible, close-up-viewable modern service pistol?

Technique stack under test:
  - parametric primitives + profile extrusion
  - boolean cutters (ejection port, slide serrations, mag well)
  - bevel + weighted normals for edge highlights
  - mirror for symmetric detail
Run:  blender -b --factory-startup -P proc_pistol.py
"""
import bpy
import bmesh
import math
from mathutils import Vector

# ----------------------------------------------------------------------------
# Parameters (millimetres, converted to metres) -- roughly Glock 17 proportions
# ----------------------------------------------------------------------------
MM = 0.001
P = dict(
    slide_len=186 * MM,
    slide_h=26 * MM,
    slide_w=25.5 * MM,
    frame_h=34 * MM,
    grip_angle=math.radians(18),
    grip_len=118 * MM,
    grip_w=30 * MM,
    barrel_d=11 * MM,
    trigger_guard_r=22 * MM,
    serration_count=7,
)


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    for c in (bpy.data.meshes, bpy.data.objects, bpy.data.materials):
        for b in list(c):
            c.remove(b)


def box(name, size, loc=(0, 0, 0), rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.active_object
    o.name = name
    o.scale = Vector(size)
    bpy.ops.object.transform_apply(scale=True, rotation=False, location=False)
    return o


def cyl(name, r, d, loc=(0, 0, 0), rot=(0, 0, 0), verts=32):
    bpy.ops.mesh.primitive_cylinder_add(radius=r, depth=d, location=loc,
                                        rotation=rot, vertices=verts)
    o = bpy.context.active_object
    o.name = name
    return o


def boolean(target, cutter, op="DIFFERENCE"):
    m = target.modifiers.new(f"bool_{cutter.name}", "BOOLEAN")
    m.object = cutter
    m.operation = op
    m.solver = "EXACT"
    cutter.display_type = "WIRE"
    cutter.hide_render = True
    return m


def apply_all(obj):
    bpy.context.view_layer.objects.active = obj
    for m in list(obj.modifiers):
        try:
            bpy.ops.object.modifier_apply(modifier=m.name)
        except RuntimeError as e:
            print(f"  apply failed {m.name}: {e}")


def bevel(obj, width=0.4 * MM, segments=2, angle=50):
    m = obj.modifiers.new("Bevel", "BEVEL")
    m.width = width
    m.segments = segments
    m.limit_method = "ANGLE"
    m.angle_limit = math.radians(angle)
    m.harden_normals = True
    m.miter_outer = "MITER_ARC"
    return m


def shade_smooth(obj):
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35))


# ----------------------------------------------------------------------------
reset()
cutters = []

# --- SLIDE ------------------------------------------------------------------
slide = box("slide", (P["slide_len"], P["slide_w"], P["slide_h"]),
            loc=(0, 0, 0))

# chamfer the top of the slide into a rounded/faceted profile
bm = bmesh.new()
bm.from_mesh(slide.data)
top = [f for f in bm.faces if f.normal.z > 0.9]
bmesh.ops.inset_region(bm, faces=top, thickness=5.5 * MM, depth=0)
for f in bm.faces:
    if f.normal.z > 0.9:
        for v in f.verts:
            v.co.z += 2.0 * MM
bm.to_mesh(slide.data)
bm.free()

# muzzle end taper
bm = bmesh.new(); bm.from_mesh(slide.data)
front = [v for v in bm.verts if v.co.x > P["slide_len"] * 0.46]
for v in front:
    v.co.z *= 0.93
bm.to_mesh(slide.data); bm.free()

# ejection port
port = box("cut_port", (46 * MM, P["slide_w"] * 1.4, 15 * MM),
           loc=(18 * MM, 3 * MM, 5 * MM))
cutters.append(port)
boolean(slide, port)

# slide serrations (rear cocking grooves), mirrored L/R
for i in range(P["serration_count"]):
    x = -P["slide_len"] * 0.5 + 12 * MM + i * 7 * MM
    for side in (1, -1):
        s = box(f"cut_serr_{i}_{side}", (2.4 * MM, 6 * MM, 20 * MM),
                loc=(x, side * (P["slide_w"] * 0.5), 2 * MM),
                rot=(0, 0, 0))
        s.rotation_euler = (0, math.radians(-14), 0)
        cutters.append(s)
        boolean(slide, s)

# front slide serrations
for i in range(4):
    x = P["slide_len"] * 0.5 - 16 * MM - i * 7 * MM
    for side in (1, -1):
        s = box(f"cut_fserr_{i}_{side}", (2.2 * MM, 6 * MM, 18 * MM),
                loc=(x, side * (P["slide_w"] * 0.5), 2 * MM))
        s.rotation_euler = (0, math.radians(-14), 0)
        cutters.append(s)
        boolean(slide, s)

# --- BARREL / MUZZLE --------------------------------------------------------
barrel = cyl("barrel", P["barrel_d"] * 0.5, 30 * MM,
             loc=(P["slide_len"] * 0.5 - 6 * MM, 0, -1 * MM),
             rot=(0, math.radians(90), 0), verts=48)
bore = cyl("cut_bore", 4.6 * MM, 40 * MM,
           loc=(P["slide_len"] * 0.5 + 2 * MM, 0, -1 * MM),
           rot=(0, math.radians(90), 0), verts=48)
cutters.append(bore)
boolean(barrel, bore)
boolean(slide, bore)

# --- SIGHTS -----------------------------------------------------------------
front_sight = box("front_sight", (3 * MM, 3 * MM, 6 * MM),
                  loc=(P["slide_len"] * 0.5 - 14 * MM, 0, P["slide_h"] * 0.5 + 3.4 * MM))
rear_sight = box("rear_sight", (5 * MM, 16 * MM, 6 * MM),
                 loc=(-P["slide_len"] * 0.5 + 14 * MM, 0, P["slide_h"] * 0.5 + 3.4 * MM))
notch = box("cut_notch", (8 * MM, 4 * MM, 4 * MM),
            loc=(-P["slide_len"] * 0.5 + 14 * MM, 0, P["slide_h"] * 0.5 + 5.5 * MM))
cutters.append(notch)
boolean(rear_sight, notch)

# --- FRAME + GRIP -----------------------------------------------------------
frame = box("frame", (P["slide_len"] * 0.82, P["slide_w"] - 2 * MM, 20 * MM),
            loc=(-6 * MM, 0, -(P["slide_h"] * 0.5 + 9 * MM)))

grip = box("grip", (P["grip_len"] * 0.42, P["grip_w"], P["grip_len"]),
           loc=(-52 * MM, 0, -(P["slide_h"] * 0.5 + 8 * MM + P["grip_len"] * 0.46)),
           rot=(0, P["grip_angle"], 0))

# taper grip toward the base and round the backstrap
bm = bmesh.new(); bm.from_mesh(grip.data)
for v in bm.verts:
    t = (v.co.z + P["grip_len"] * 0.5) / P["grip_len"]      # 0 bottom -> 1 top
    v.co.y *= 0.86 + 0.14 * t
    v.co.x *= 0.90 + 0.10 * t
bmesh.ops.bevel(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces),
                offset=4.0 * MM, segments=3, affect="EDGES", profile=0.5)
bm.to_mesh(grip.data); bm.free()

# grip texturing: stippled panels via array of small boolean bumps is expensive,
# so use a displacement-free approach - shallow finger grooves on the frontstrap
for i in range(3):
    g = cyl(f"cut_groove_{i}", 7 * MM, P["grip_w"] * 1.3,
            loc=(-30 * MM - i * 1.5 * MM,
                 0,
                 -(P["slide_h"] * 0.5 + 26 * MM + i * 24 * MM)),
            rot=(math.radians(90), 0, 0), verts=24)
    g.location.x += math.tan(P["grip_angle"]) * (i * 24 * MM)
    cutters.append(g)
    boolean(grip, g)

# --- TRIGGER GUARD ----------------------------------------------------------
bpy.ops.mesh.primitive_torus_add(major_radius=P["trigger_guard_r"],
                                 minor_radius=3.2 * MM,
                                 major_segments=32, minor_segments=12,
                                 location=(-14 * MM, 0,
                                           -(P["slide_h"] * 0.5 + 30 * MM)),
                                 rotation=(math.radians(90), 0, 0))
guard = bpy.context.active_object
guard.name = "trigger_guard"
guard.scale = (1.0, 1.15, 1.0)
bpy.ops.object.transform_apply(scale=True)
guard_cut = box("cut_guardtop", (60 * MM, 40 * MM, 30 * MM),
                loc=(-14 * MM, 0, -(P["slide_h"] * 0.5 + 30 * MM) + 24 * MM))
cutters.append(guard_cut)
boolean(guard, guard_cut)

trigger = box("trigger", (3 * MM, 6 * MM, 15 * MM),
              loc=(-16 * MM, 0, -(P["slide_h"] * 0.5 + 30 * MM)))
bm = bmesh.new(); bm.from_mesh(trigger.data)
bmesh.ops.bevel(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces),
                offset=1.2 * MM, segments=2, affect="EDGES")
bm.to_mesh(trigger.data); bm.free()

# --- MAGAZINE BASEPLATE -----------------------------------------------------
mag = box("mag_base", (P["grip_len"] * 0.44, P["grip_w"] * 1.05, 6 * MM),
          loc=(-52 * MM - math.tan(P["grip_angle"]) * P["grip_len"] * 0.5,
               0,
               -(P["slide_h"] * 0.5 + 8 * MM + P["grip_len"] * 0.95)),
          rot=(0, P["grip_angle"], 0))

# --- APPLY / FINALISE -------------------------------------------------------
parts = [slide, barrel, front_sight, rear_sight, frame, grip, guard, trigger, mag]
for o in parts:
    apply_all(o)
for c in cutters:
    bpy.data.objects.remove(c, do_unlink=True)
for o in parts:
    bevel(o)
    shade_smooth(o)

print(f"PARTS={len(parts)} "
      f"TRIS={sum(len(o.data.loop_triangles) for o in parts if o.data.calc_loop_triangles() is None)}")

# --- MATERIAL: procedural worn parkerised steel -----------------------------
def worn_metal(name, base=(0.035, 0.035, 0.038), rough=0.42, wear=0.55):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.48
    ramp.color_ramp.elements[1].position = 0.53
    n1 = nt.nodes.new("ShaderNodeTexNoise")
    n1.inputs["Scale"].default_value = 320.0
    n1.inputs["Detail"].default_value = 8.0
    mixfac = nt.nodes.new("ShaderNodeMix")
    mixfac.data_type = "FLOAT"
    mixfac.inputs[0].default_value = wear
    mixcol = nt.nodes.new("ShaderNodeMix")
    mixcol.data_type = "RGBA"
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.12

    nt.links.new(geo.outputs["Pointiness"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], mixfac.inputs[2])
    nt.links.new(n1.outputs["Fac"], mixfac.inputs[3])
    nt.links.new(mixfac.outputs[0], mixcol.inputs["Factor"])
    mixcol.inputs[6].default_value = (*base, 1)
    mixcol.inputs[7].default_value = (0.55, 0.54, 0.52, 1)
    nt.links.new(mixcol.outputs[2], bsdf.inputs["Base Color"])
    nt.links.new(n1.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    bsdf.inputs["Metallic"].default_value = 1.0
    bsdf.inputs["Roughness"].default_value = rough
    return m


steel = worn_metal("Steel")
polymer = worn_metal("Polymer", base=(0.018, 0.018, 0.02), rough=0.62, wear=0.25)
polymer.node_tree.nodes["Principled BSDF"].inputs["Metallic"].default_value = 0.0

for o in (slide, barrel, front_sight, rear_sight, trigger):
    o.data.materials.append(steel)
for o in (frame, grip, guard, mag):
    o.data.materials.append(polymer)

bpy.ops.wm.save_as_mainfile(filepath="/tmp/proc_pistol.blend")
print("PISTOL_BUILD_OK /tmp/proc_pistol.blend")
