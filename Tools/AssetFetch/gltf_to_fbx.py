"""Convert the two glTF assets Unity could not import into FBX.

Unity has no glTF importer in this project, so `scene.gltf` files fall back to
DefaultImporter and expose no mesh. Blender round-trips them to FBX.

Run headless:
  /Applications/Blender.app/Contents/MacOS/Blender -b -P Tools/AssetFetch/gltf_to_fbx.py
"""
import bpy
import os
import sys
from mathutils import Vector

ROOT = "/Users/christophernichols/FPS-Shooter"

# These were mis-filed under "vehicles" in the original drop. They are in fact a
# rooftop antenna and a wall-mounted AC unit, so they belong with the props.
JOBS = [
    {
        "src": f"{ROOT}/Assets/_Project/Art/Imported/Zzz/vehicles/source_gltf/scene.gltf",
        "out": f"{ROOT}/Assets/_Project/Art/Imported/Zzz/props/gltf_antenna/rooftop_antenna.fbx",
        "label": "rooftop_antenna",
    },
    {
        "src": f"{ROOT}/Assets/_Project/Art/Imported/Zzz/vehicles/source_gltf_1/scene.gltf",
        "out": f"{ROOT}/Assets/_Project/Art/Imported/Zzz/props/gltf_wall_ac/wall_ac_unit.fbx",
        "label": "wall_ac_unit",
    },
]


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.images):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def mesh_objects():
    return [o for o in bpy.context.scene.objects if o.type == "MESH"]


def total_tris():
    n = 0
    for o in mesh_objects():
        o.data.calc_loop_triangles()
        n += len(o.data.loop_triangles)
    return n


def combined_bounds():
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for o in mesh_objects():
        for corner in o.bound_box:
            w = o.matrix_world @ Vector(corner)
            for i in range(3):
                lo[i] = min(lo[i], w[i])
                hi[i] = max(hi[i], w[i])
    return lo, hi


def main():
    report = []
    for job in JOBS:
        clear_scene()

        if not os.path.exists(job["src"]):
            report.append(f"{job['label']}: MISSING SOURCE {job['src']}")
            continue

        bpy.ops.import_scene.gltf(filepath=job["src"])

        meshes = mesh_objects()
        if not meshes:
            report.append(f"{job['label']}: imported but contains no mesh")
            continue

        tris = total_tris()
        lo, hi = combined_bounds()
        size = hi - lo

        # Drop the origin to the base of the mesh so a ground/wall raycast hit can
        # be used directly as the placement position without a half-height offset.
        for o in meshes:
            o.select_set(True)
        bpy.context.view_layer.objects.active = meshes[0]
        if len(meshes) > 1:
            bpy.ops.object.join()
        obj = bpy.context.view_layer.objects.active

        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
        lo, hi = combined_bounds()
        obj.data.transform(
            __import__("mathutils").Matrix.Translation(
                Vector((-(lo.x + hi.x) / 2.0, -(lo.y + hi.y) / 2.0, -lo.z))
            )
        )
        obj.data.update()

        os.makedirs(os.path.dirname(job["out"]), exist_ok=True)
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.ops.export_scene.fbx(
            filepath=job["out"],
            use_selection=True,
            apply_scale_options="FBX_SCALE_ALL",
            path_mode="COPY",
            embed_textures=True,
            mesh_smooth_type="FACE",
        )

        report.append(
            f"{job['label']}: OK tris={tris} "
            f"size={size.x:.2f} x {size.y:.2f} x {size.z:.2f} m (blender XYZ, Z up) "
            f"origin=base -> {job['out']}"
        )

    text = "\n".join(report)
    with open(f"{ROOT}/_research/GLTF_CONVERT.txt", "w") as fh:
        fh.write(text + "\n")
    print("\n=== GLTF CONVERT REPORT ===")
    print(text)


main()
