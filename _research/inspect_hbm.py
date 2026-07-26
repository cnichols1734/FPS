import bpy

print("=== ARMATURES IN FILE ===")
arms = [o for o in bpy.data.objects if o.type == "ARMATURE"]
print(f"  count={len(arms)} names={[a.name for a in arms]}")
print(f"  bpy.data.armatures count={len(bpy.data.armatures)}")

print("=== OBJECTS ===")
for o in sorted(bpy.data.objects, key=lambda x: x.name):
    verts = len(o.data.vertices) if o.type == "MESH" else "-"
    polys = len(o.data.polygons) if o.type == "MESH" else "-"
    vgroups = len(o.vertex_groups)
    mods = [m.type for m in o.modifiers]
    print(f"  {o.name:<40} type={o.type:<10} verts={verts:<8} polys={polys:<8} vgroups={vgroups} mods={mods}")

print("=== HAND / ARM ASSETS DETAIL ===")
for o in bpy.data.objects:
    if any(k in o.name.lower() for k in ("hand", "arm", "body")):
        if o.type != "MESH":
            continue
        me = o.data
        print(f"  {o.name}: verts={len(me.vertices)} polys={len(me.polygons)} "
              f"uv_layers={[u.name for u in me.uv_layers]} "
              f"shape_keys={'yes' if me.shape_keys else 'no'} "
              f"multires={[m.total_levels for m in o.modifiers if m.type=='MULTIRES']}")
