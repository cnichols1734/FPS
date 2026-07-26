"""Probe the SCAR-H FBX bind pose so the viewmodel is placed from measured numbers.

Skinned meshes ignore their own node transform at runtime — placement comes from the
bones and the bind matrices stored on each skin cluster. This prints the authoritative
bind-pose geometry so ScarHViewmodelBuilder can hard-code a calibrated pose instead of
guessing from SkinnedMeshRenderer.bounds (which is stale during Awake).
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from fbx_dump import parse  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FBX = os.path.join(REPO, "Assets/_Project/Resources/Weapons/FP_ScarH.fbx")


def mat_mul(a, b):
    out = [0.0] * 16
    for i in range(4):
        for j in range(4):
            s = 0.0
            for k in range(4):
                s += a[i * 4 + k] * b[k * 4 + j]
            out[i * 4 + j] = s
    return out


def xform(m, p):
    x, y, z = p
    return (
        x * m[0] + y * m[4] + z * m[8] + m[12],
        x * m[1] + y * m[5] + z * m[9] + m[13],
        x * m[2] + y * m[6] + z * m[10] + m[14],
    )


def name_of(node):
    raw = node.props[1]
    return raw.split("\x00")[0] if isinstance(raw, str) else str(raw)


def kind_of(node):
    return node.props[2] if len(node.props) > 2 else ""


def child_props(node, key):
    c = node.find(key)
    if c is None or not c.props:
        return None
    return c.props[0]


def aabb(points):
    lo = [min(p[i] for p in points) for i in range(3)]
    hi = [max(p[i] for p in points) for i in range(3)]
    return lo, hi


def fmt(v):
    return "(" + ", ".join(f"{x:+.4f}" for x in v) + ")"


def report(label, lo, hi):
    size = [hi[i] - lo[i] for i in range(3)]
    center = [(hi[i] + lo[i]) / 2 for i in range(3)]
    print(f"    {label}")
    print(f"      min={fmt(lo)} max={fmt(hi)}")
    print(f"      size={fmt(size)} center={fmt(center)}")


def main():
    _, root = parse(FBX)
    objects = root.find("Objects")
    conns = root.find("Connections")

    models = {}
    for m in objects.find_all("Model"):
        models[m.props[0]] = m

    geoms = {}
    for g in objects.find_all("Geometry"):
        geoms[g.props[0]] = g

    clusters, skins = {}, {}
    for d in objects.find_all("Deformer"):
        if kind_of(d) == "Cluster":
            clusters[d.props[0]] = d
        elif kind_of(d) == "Skin":
            skins[d.props[0]] = d

    geom_to_model = {}
    skin_to_geom = {}
    cluster_to_skin = {}
    bone_of_cluster = {}

    for c in conns.find_all("C"):
        if not c.props or c.props[0] != "OO":
            continue
        src, dst = c.props[1], c.props[2]
        if src in geoms and dst in models:
            geom_to_model[src] = dst
        elif src in skins and dst in geoms:
            skin_to_geom[src] = dst
        elif src in clusters and dst in skins:
            cluster_to_skin[src] = dst
        elif src in models and dst in clusters:
            bone_of_cluster[dst] = src

    # geometry uid -> list of clusters
    geom_clusters = {}
    for cu, sk in cluster_to_skin.items():
        gid = skin_to_geom.get(sk)
        if gid is not None:
            geom_clusters.setdefault(gid, []).append(cu)

    print(f"FBX: {FBX}")
    print(f"models={len(models)} geoms={len(geoms)} clusters={len(clusters)} skins={len(skins)}")

    print("\n================ GEOMETRY (bind pose) ================")
    all_world = []
    per_mesh = {}

    for gid, g in geoms.items():
        mid = geom_to_model.get(gid)
        mname = name_of(models[mid]) if mid in models else f"<geom {gid}>"
        verts = child_props(g, "Vertices")
        if not verts:
            continue
        pts = [(verts[i], verts[i + 1], verts[i + 2]) for i in range(0, len(verts), 3)]

        print(f"\n  {mname}  verts={len(pts)}")
        lo, hi = aabb(pts)
        report("raw local", lo, hi)

        cl = geom_clusters.get(gid, [])
        if not cl:
            print("    (no skin cluster)")
            continue

        transform = child_props(clusters[cl[0]], "Transform")
        link = child_props(clusters[cl[0]], "TransformLink")

        if transform:
            world = [xform(transform, p) for p in pts]
            lo, hi = aabb(world)
            report("Transform (mesh bind global)", lo, hi)
            all_world.extend(world)
            per_mesh[mname] = (lo, hi)

        if transform and link:
            combo = mat_mul(transform, link)
            world2 = [xform(combo, p) for p in pts]
            lo2, hi2 = aabb(world2)
            report("Transform*TransformLink", lo2, hi2)

    if all_world:
        print("\n================ COMBINED (Transform space) ================")
        lo, hi = aabb(all_world)
        report("ALL MESHES", lo, hi)
        size = [hi[i] - lo[i] for i in range(3)]
        longest = max(range(3), key=lambda i: size[i])
        print(f"      longest axis = {'XYZ'[longest]} ({size[longest]:.4f} units)")
        for target in (0.55, 0.9, 1.0):
            print(f"      scale for {target}m along that axis = {target / size[longest]:.6f}")

    print("\n================ BONE BIND POSITIONS (TransformLink) ================")
    want = (
        "ROOT", "Weapon", "Magazine", "Trigger", "Slide",
        "hand.L", "hand.R", "forearm.L", "forearm.R",
        "shoulder.L", "shoulder.R", "upper_arm.L", "upper_arm.R",
    )
    found = {}
    for cu, cnode in clusters.items():
        bone = bone_of_cluster.get(cu)
        if bone is None:
            continue
        bname = name_of(models[bone])
        link = child_props(cnode, "TransformLink")
        if link:
            found[bname] = (link[12], link[13], link[14])

    for w in want:
        if w in found:
            print(f"  {w:14} {fmt(found[w])}")
        else:
            print(f"  {w:14} <not skinned>")

    print("\n  all bone bind positions AABB:")
    if found:
        lo, hi = aabb(list(found.values()))
        report("bones", lo, hi)

    print("\n================ NODE TRS (for reference) ================")
    for uid, m in models.items():
        nm = name_of(m)
        if nm in ("Dragunov_FP_Rig", "BUTTSTOCK_BARREL", "BODY_MAG", "FPS_Hands", "ROOT", "Weapon"):
            from fbx_dump import prop70
            print(f"  {nm}: T={prop70(m,'Lcl Translation')} R={prop70(m,'Lcl Rotation')} S={prop70(m,'Lcl Scaling')}")


if __name__ == "__main__":
    main()
