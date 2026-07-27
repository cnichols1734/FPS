#!/usr/bin/env python3
"""Recursively extract the zzz asset drop into a categorised staging tree.

Staging lives outside Assets/ so Unity does not auto-import 1.2GB before an
editor pass has decided what is actually wanted.
"""
import json
import os
import shutil
import zipfile

SRC = "/Users/christophernichols/FPS-Shooter/zzz"
DST = "/Users/christophernichols/FPS-Shooter/_incoming/zzz"

# Category is driven by filename because the vendors use inconsistent folder layouts.
CATEGORY = {
    "vehicles": ["abandoned-junk-car", "burned-out-cars", "red_car_wreck", "zis42", "source_gltf"],
    "ground": ["concrete_pavement", "cracks-in-asfalt", "damaged_asphalt", "rough_asphalt",
               "wet_destroyed_asphalt", "road_debris", "military_trenches_ground_patch"],
    "decals": ["graffiti"],
    "cloth": ["tarp", "canopy-cloth", "awning"],
    "props": ["cc0-antenna", "electrical-transformer", "electrical_box", "power-transformer",
              "metal_water_tank", "modular_building_balcony", "urban_trash", "tarped_crate",
              "industrial_junkyard", "military_trenches_debris_pile", "rock_sandstone",
              "weathered_concrete_barriers"],
}

MESH_EXT = {".fbx", ".gltf", ".glb", ".obj", ".blend", ".bin"}
TEX_EXT = {".png", ".jpg", ".jpeg", ".tga", ".exr", ".tif", ".tiff"}


def categorise(name):
    low = name.lower()
    for cat, keys in CATEGORY.items():
        for k in keys:
            if k in low:
                return cat
    return "misc"


def safe_extract(archive, dest):
    """Extract without allowing paths to escape dest."""
    try:
        with zipfile.ZipFile(archive) as z:
            for member in z.namelist():
                target = os.path.realpath(os.path.join(dest, member))
                if not target.startswith(os.path.realpath(dest)):
                    continue
                z.extract(member, dest)
        return True
    except Exception as e:
        print("  FAILED %s: %s" % (os.path.basename(archive), e))
        return False


def main():
    if os.path.isdir(DST):
        shutil.rmtree(DST)

    report = {}
    unhandled = []

    for entry in sorted(os.listdir(SRC)):
        path = os.path.join(SRC, entry)
        if not os.path.isfile(path):
            continue

        stem, ext = os.path.splitext(entry)
        cat = categorise(entry)
        dest = os.path.join(DST, cat, stem)
        os.makedirs(dest, exist_ok=True)

        if ext.lower() == ".zip":
            if not safe_extract(path, dest):
                continue
        elif ext.lower() in MESH_EXT:
            shutil.copy2(path, dest)
        else:
            unhandled.append(entry)
            continue

        # Nested archives: vendors ship zip-in-zip and rar-in-zip.
        for _ in range(3):
            nested = []
            for root, _dirs, files in os.walk(dest):
                for f in files:
                    if f.lower().endswith(".zip"):
                        nested.append(os.path.join(root, f))
            if not nested:
                break
            for n in nested:
                inner = os.path.join(os.path.dirname(n), os.path.splitext(os.path.basename(n))[0])
                os.makedirs(inner, exist_ok=True)
                if safe_extract(n, inner):
                    os.remove(n)

        meshes, texes, rars, others = [], [], [], []
        for root, _dirs, files in os.walk(dest):
            for f in files:
                e = os.path.splitext(f)[1].lower()
                rel = os.path.relpath(os.path.join(root, f), dest)
                if e in MESH_EXT:
                    meshes.append(rel)
                elif e in TEX_EXT:
                    texes.append(rel)
                elif e == ".rar":
                    rars.append(rel)
                else:
                    others.append(rel)

        report[entry] = {
            "category": cat,
            "dest": os.path.relpath(dest, DST),
            "meshes": sorted(meshes),
            "textureCount": len(texes),
            "rarsNeedingManualExtract": sorted(rars),
        }
        print("%-58s -> %-9s meshes=%-3d tex=%-4d rar=%d"
              % (entry, cat, len(meshes), len(texes), len(rars)))

    with open(os.path.join(DST, "INVENTORY.json"), "w") as f:
        json.dump(report, f, indent=2)

    print("\n=== TOTALS ===")
    for cat in sorted(set(v["category"] for v in report.values())):
        items = [k for k, v in report.items() if v["category"] == cat]
        nm = sum(len(report[k]["meshes"]) for k in items)
        nt = sum(report[k]["textureCount"] for k in items)
        print("%-9s archives=%-3d meshes=%-4d textures=%d" % (cat, len(items), nm, nt))

    stuck = {k: v["rarsNeedingManualExtract"] for k, v in report.items()
             if v["rarsNeedingManualExtract"]}
    if stuck:
        print("\nRAR (no extractor installed):")
        for k, v in stuck.items():
            print("  %s -> %s" % (k, v))
    if unhandled:
        print("\nUnhandled files: %s" % unhandled)


if __name__ == "__main__":
    main()
