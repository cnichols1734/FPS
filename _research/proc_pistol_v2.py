"""Pistol v2 - iteration pass after render critique.

Critique of v1:
  - grip finger-grooves punched clean through the grip (cutter radius too large)
  - frame read as a floating slab; no accessory rail
  - material washed out to flat grey: pointiness wear buried under noise,
    base albedo far too high for parkerised steel
  - no grip texture, no slide chamfers -> silhouette fine, surfaces dead
"""
import bpy
import bmesh
import math
from mathutils import Vector

MM = 0.001


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
    bpy.ops.object.transform_apply(scale=True)
    return o


def cyl(name, r, d, loc=(0, 0, 0), rot=(0, 0, 0), verts=32):
    bpy.ops.mesh.primitive_cylinder_add(radius=r, depth=d, location=loc,
                                        rotation=rot, vertices=verts)
    o = bpy.context.active_object
    o.name = name
    return o


def boolean(target, cutter, op="DIFFERENCE"):
    m = target.modifiers.new(f"b_{cutter.name}", "BOOLEAN")
    m.object = cutter
    m.operation = op
    m.solver = "EXACT"
    cutter.hide_render = True


def apply_all(obj):
    bpy.context.view_layer.objects.active = obj
    for m in list(obj.modifiers):
        try:
            bpy.ops.object.modifier_apply(modifier=m.name)
        except RuntimeError as e:
            print(f"  APPLY_FAIL {obj.name}/{m.name}: {e}")


def finish(obj, bw=0.35 * MM, seg=2):
    m = obj.modifiers.new("Bevel", "BEVEL")
    m.width = bw
    m.segments = seg
    m.limit_method = "ANGLE"
    m.angle_limit = math.radians(45)
    m.harden_normals = True
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(32))


reset()
cutters = []
SL, SW, SH = 186 * MM, 25.5 * MM, 26 * MM
GRIP_ANG = math.radians(17)

# ---------------------------------------------------------------- SLIDE -----
slide = box("slide", (SL, SW, SH))
bm = bmesh.new(); bm.from_mesh(slide.data)
top = [f for f in bm.faces if f.normal.z > 0.9]
bmesh.ops.inset_region(bm, faces=top, thickness=5.0 * MM)
for f in bm.faces:
    if f.normal.z > 0.9:
        for v in f.verts:
            v.co.z += 2.2 * MM
# muzzle taper + rear radius
for v in bm.verts:
    if v.co.x > SL * 0.44:
        v.co.z *= 0.94
    if v.co.x < -SL * 0.46:
        v.co.z *= 0.95
        v.co.y *= 0.95
bm.to_mesh(slide.data); bm.free()

port = box("c_port", (44 * MM, SW * 1.4, 14 * MM), loc=(20 * MM, 4 * MM, 6 * MM))
cutters.append(port); boolean(slide, port)

# cocking serrations: rear 7 + front 4, both sides, angled
for tag, n, x0, step in (("r", 7, -SL * 0.5 + 13 * MM, 7.0 * MM),
                         ("f", 4, SL * 0.5 - 17 * MM, -7.0 * MM)):
    for i in range(n):
        for side in (1, -1):
            s = box(f"c_serr{tag}{i}{side}", (2.3 * MM, 5 * MM, 19 * MM),
                    loc=(x0 + i * step, side * SW * 0.5, 1.5 * MM),
                    rot=(0, math.radians(-13), 0))
            cutters.append(s); boolean(slide, s)

barrel = cyl("barrel", 5.6 * MM, 26 * MM, loc=(SL * 0.5 - 4 * MM, 0, -1 * MM),
             rot=(0, math.radians(90), 0), verts=48)
bore = cyl("c_bore", 4.55 * MM, 44 * MM, loc=(SL * 0.5, 0, -1 * MM),
           rot=(0, math.radians(90), 0), verts=48)
cutters.append(bore); boolean(barrel, bore); boolean(slide, bore)

# ---------------------------------------------------------------- SIGHTS ----
fsight = box("fsight", (3 * MM, 2.6 * MM, 6.5 * MM),
             loc=(SL * 0.5 - 15 * MM, 0, SH * 0.5 + 3.6 * MM))
rsight = box("rsight", (5.5 * MM, 15 * MM, 6.5 * MM),
             loc=(-SL * 0.5 + 15 * MM, 0, SH * 0.5 + 3.6 * MM))
notch = box("c_notch", (9 * MM, 3.6 * MM, 4.5 * MM),
            loc=(-SL * 0.5 + 15 * MM, 0, SH * 0.5 + 5.6 * MM))
cutters.append(notch); boolean(rsight, notch)

# ---------------------------------------------------------------- FRAME -----
frame = box("frame", (SL * 0.80, SW - 2.5 * MM, 19 * MM),
            loc=(-8 * MM, 0, -(SH * 0.5 + 8.5 * MM)))
# dust-cover step: thin the forward half
bm = bmesh.new(); bm.from_mesh(frame.data)
for v in bm.verts:
    if v.co.x > 10 * MM:
        v.co.z = v.co.z * 0.62 - 2.0 * MM
bm.to_mesh(frame.data); bm.free()

# accessory rail: 3 cross-slots under the dust cover
for i in range(3):
    r = box(f"c_rail{i}", (3.0 * MM, SW, 4 * MM),
            loc=(24 * MM + i * 10 * MM, 0, -(SH * 0.5 + 17 * MM)))
    cutters.append(r); boolean(frame, r)

# ---------------------------------------------------------------- GRIP ------
GRIP_L = 112 * MM
grip_z = -(SH * 0.5 + 8 * MM + GRIP_L * 0.46)
grip = box("grip", (47 * MM, 30 * MM, GRIP_L), loc=(-54 * MM, 0, grip_z),
           rot=(0, GRIP_ANG, 0))
bm = bmesh.new(); bm.from_mesh(grip.data)
for v in bm.verts:
    t = (v.co.z + GRIP_L * 0.5) / GRIP_L
    v.co.y *= 0.84 + 0.16 * t
    v.co.x *= 0.88 + 0.12 * t
bmesh.ops.bevel(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces),
                offset=5.0 * MM, segments=4, affect="EDGES", profile=0.5)
bm.to_mesh(grip.data); bm.free()

# FIX v1: grooves were r=7mm cylinders spanning the whole grip -> punched through.
# Now: shallow scallops on the FRONTSTRAP only, offset outward so they only graze.
for i in range(3):
    zc = -(SH * 0.5 + 30 * MM + i * 22 * MM)
    xc = -54 * MM + 22 * MM + math.tan(GRIP_ANG) * (grip_z - zc)
    g = cyl(f"c_groove{i}", 9 * MM, 26 * MM, loc=(xc + 7.0 * MM, 0, zc),
            rot=(math.radians(90), 0, 0), verts=28)
    cutters.append(g); boolean(grip, g)

# stippling: shallow dimple grid on both side panels (real geometry, cheap)
for side in (1, -1):
    for row in range(9):
        for col in range(4):
            zc = -(SH * 0.5 + 26 * MM + row * 8 * MM)
            xc = -54 * MM - 12 * MM + col * 8 * MM + math.tan(GRIP_ANG) * (grip_z - zc)
            d = cyl(f"c_dim{side}{row}{col}", 2.6 * MM, 4 * MM,
                    loc=(xc, side * 14.2 * MM, zc),
                    rot=(math.radians(90), 0, 0), verts=6)
            cutters.append(d); boolean(grip, d)

# --------------------------------------------------------- TRIGGER GROUP ----
bpy.ops.mesh.primitive_torus_add(major_radius=21 * MM, minor_radius=3.0 * MM,
                                 major_segments=40, minor_segments=14,
                                 location=(-16 * MM, 0, -(SH * 0.5 + 30 * MM)),
                                 rotation=(math.radians(90), 0, 0))
guard = bpy.context.active_object
guard.name = "guard"
guard.scale = (1.0, 1.18, 1.0)
bpy.ops.object.transform_apply(scale=True)
gc = box("c_guardtop", (70 * MM, 40 * MM, 30 * MM),
         loc=(-16 * MM, 0, -(SH * 0.5 + 30 * MM) + 23 * MM))
cutters.append(gc); boolean(guard, gc)

trigger = box("trigger", (3.2 * MM, 5.5 * MM, 15 * MM),
              loc=(-18 * MM, 0, -(SH * 0.5 + 31 * MM)))
tsafe = box("c_tsafe", (1.4 * MM, 2.2 * MM, 12 * MM),
            loc=(-17.4 * MM, 0, -(SH * 0.5 + 31 * MM)))
cutters.append(tsafe); boolean(trigger, tsafe)

mag = box("mag", (44 * MM, 31 * MM, 7 * MM),
          loc=(-54 * MM - math.tan(GRIP_ANG) * GRIP_L * 0.5, 0,
               -(SH * 0.5 + 8 * MM + GRIP_L * 0.96)),
          rot=(0, GRIP_ANG, 0))

slide_stop = box("slide_stop", (30 * MM, 3 * MM, 6 * MM),
                 loc=(-18 * MM, -(SW * 0.5 - 0.5 * MM), -(SH * 0.5 + 4 * MM)))

parts = [slide, barrel, fsight, rsight, frame, grip, guard, trigger, mag, slide_stop]
for o in parts:
    apply_all(o)
for c in cutters:
    if c.name in bpy.data.objects:
        bpy.data.objects.remove(c, do_unlink=True)
for o in parts:
    finish(o)


# ------------------------------------------------------------- MATERIAL -----
def gunmetal(name, base, rough, metallic=1.0, wear_amt=1.0, wear_col=(0.42, 0.41, 0.40)):
    """Parkerised steel: dark albedo, pointiness-driven edge wear (Cycles only),
    fine machining noise in roughness. v1 mistake: wear factor was averaged with
    broadband noise, which flattened everything to mid-grey."""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt, links = m.node_tree, m.node_tree.links
    bsdf = nt.nodes["Principled BSDF"]

    geo = nt.nodes.new("ShaderNodeNewGeometry")
    edge = nt.nodes.new("ShaderNodeValToRGB")          # isolate convex edges only
    edge.color_ramp.elements[0].position = 0.515
    edge.color_ramp.elements[1].position = 0.560
    links.new(geo.outputs["Pointiness"], edge.inputs["Fac"])

    grime = nt.nodes.new("ShaderNodeTexNoise")          # break up the wear line
    grime.inputs["Scale"].default_value = 90.0
    grime.inputs["Detail"].default_value = 6.0
    mask = nt.nodes.new("ShaderNodeMath")               # edge * grime, scaled
    mask.operation = "MULTIPLY"
    links.new(edge.outputs["Color"], mask.inputs[0])
    links.new(grime.outputs["Fac"], mask.inputs[1])
    gain = nt.nodes.new("ShaderNodeMath")
    gain.operation = "MULTIPLY"
    gain.inputs[1].default_value = 2.4 * wear_amt
    links.new(mask.outputs[0], gain.inputs[0])

    col = nt.nodes.new("ShaderNodeMix")
    col.data_type = "RGBA"
    col.clamp_factor = True
    col.inputs[6].default_value = (*base, 1)
    col.inputs[7].default_value = (*wear_col, 1)
    links.new(gain.outputs[0], col.inputs[0])
    links.new(col.outputs[2], bsdf.inputs["Base Color"])

    micro = nt.nodes.new("ShaderNodeTexNoise")
    micro.inputs["Scale"].default_value = 600.0
    micro.inputs["Detail"].default_value = 4.0
    rmap = nt.nodes.new("ShaderNodeMapRange")
    rmap.inputs["To Min"].default_value = rough - 0.07
    rmap.inputs["To Max"].default_value = rough + 0.07
    links.new(micro.outputs["Fac"], rmap.inputs["Value"])
    links.new(rmap.outputs["Result"], bsdf.inputs["Roughness"])

    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.06
    links.new(micro.outputs["Fac"], bump.inputs["Height"])
    links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])

    bsdf.inputs["Metallic"].default_value = metallic
    return m


steel = gunmetal("Steel", (0.017, 0.017, 0.019), 0.30)
poly = gunmetal("Polymer", (0.013, 0.013, 0.015), 0.55, metallic=0.0,
                wear_amt=0.35, wear_col=(0.10, 0.10, 0.10))

def assign(obj, mat):
    """Applying a Boolean merges the cutter's (empty) material slots into the
    target, so faces keep pointing at a null slot 0. Wipe slots and force
    every polygon onto index 0."""
    obj.data.materials.clear()
    obj.data.materials.append(mat)
    for p in obj.data.polygons:
        p.material_index = 0


for o in (slide, barrel, fsight, rsight, trigger, slide_stop):
    assign(o, steel)
for o in (frame, grip, guard, mag):
    assign(o, poly)

deps = bpy.context.evaluated_depsgraph_get()
tris = 0
for o in parts:
    me = o.evaluated_get(deps).to_mesh()
    me.calc_loop_triangles()
    tris += len(me.loop_triangles)
    o.evaluated_get(deps).to_mesh_clear()
print(f"PISTOL_V2 parts={len(parts)} tris={tris}")

bpy.ops.wm.save_as_mainfile(filepath="/tmp/proc_pistol_v2.blend")
print("PISTOL_V2_OK")
