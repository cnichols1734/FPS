"""Minimal binary-FBX reader that prints Model node transforms and mesh extents.

Used to verify what Unity will actually import for the viewmodel: node hierarchy,
Lcl Translation/Rotation/Scaling, PreRotation, GeometricTranslation and the raw
vertex bounds of every mesh.
"""
from __future__ import annotations

import struct
import sys
import zlib


class Reader:
    def __init__(self, data: bytes):
        self.d = data
        self.p = 0

    def u8(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def u32(self):
        v = struct.unpack_from("<I", self.d, self.p)[0]
        self.p += 4
        return v

    def u64(self):
        v = struct.unpack_from("<Q", self.d, self.p)[0]
        self.p += 8
        return v


class Node:
    def __init__(self, name):
        self.name = name
        self.props = []
        self.children = []

    def find(self, name):
        for c in self.children:
            if c.name == name:
                return c
        return None

    def find_all(self, name):
        return [c for c in self.children if c.name == name]


def read_array(r: Reader, kind: str):
    length = r.u32()
    encoding = r.u32()
    comp_len = r.u32()
    raw = r.d[r.p:r.p + comp_len]
    r.p += comp_len
    if encoding == 1:
        raw = zlib.decompress(raw)
    fmt = {"f": "<%df", "d": "<%dd", "l": "<%dq", "i": "<%di", "b": "<%db"}[kind]
    return list(struct.unpack(fmt % length, raw[:struct.calcsize(fmt % length)]))


def read_prop(r: Reader):
    t = chr(r.u8())
    if t == "Y":
        v = struct.unpack_from("<h", r.d, r.p)[0]
        r.p += 2
        return v
    if t == "C":
        return bool(r.u8())
    if t == "I":
        v = struct.unpack_from("<i", r.d, r.p)[0]
        r.p += 4
        return v
    if t == "F":
        v = struct.unpack_from("<f", r.d, r.p)[0]
        r.p += 4
        return v
    if t == "D":
        v = struct.unpack_from("<d", r.d, r.p)[0]
        r.p += 8
        return v
    if t == "L":
        v = struct.unpack_from("<q", r.d, r.p)[0]
        r.p += 8
        return v
    if t in "fdlib":
        return read_array(r, t)
    if t in "SR":
        n = r.u32()
        v = r.d[r.p:r.p + n]
        r.p += n
        return v.decode("utf-8", "replace") if t == "S" else v
    raise ValueError(f"unknown property type {t!r} at {r.p}")


def read_node(r: Reader, wide: bool):
    end = r.u64() if wide else r.u32()
    nprops = r.u64() if wide else r.u32()
    r.u64() if wide else r.u32()  # property list length
    namelen = r.u8()
    name = r.d[r.p:r.p + namelen].decode("utf-8", "replace")
    r.p += namelen
    if end == 0:
        return None
    node = Node(name)
    for _ in range(nprops):
        node.props.append(read_prop(r))
    sentinel = 25 if wide else 13
    while r.p < end - sentinel:
        child = read_node(r, wide)
        if child is None:
            break
        node.children.append(child)
    r.p = end
    return node


def parse(path: str):
    data = open(path, "rb").read()
    version = struct.unpack_from("<I", data, 23)[0]
    wide = version >= 7500
    r = Reader(data)
    r.p = 27
    root = Node("Root")
    while r.p < len(data) - (25 if wide else 13):
        n = read_node(r, wide)
        if n is None:
            break
        root.children.append(n)
    return version, root


def prop70(node: Node, key: str):
    p70 = node.find("Properties70")
    if p70 is None:
        return None
    for p in p70.find_all("P"):
        if p.props and p.props[0] == key:
            return [x for x in p.props[4:] if isinstance(x, (int, float))]
    return None


def main(path: str):
    version, root = parse(path)
    print(f"FBX version {version}  file={path}")

    gs = root.find("GlobalSettings")
    if gs:
        for k in ("UpAxis", "UpAxisSign", "FrontAxis", "FrontAxisSign",
                  "CoordAxis", "CoordAxisSign", "UnitScaleFactor",
                  "OriginalUpAxis", "OriginalUnitScaleFactor"):
            print(f"  global {k:26} = {prop70(gs, k)}")

    objects = root.find("Objects")
    if objects is None:
        print("no Objects node")
        return

    models = {}
    for m in objects.find_all("Model"):
        uid = m.props[0]
        raw = m.props[1]
        name = raw.split("\x00")[0] if isinstance(raw, str) else str(raw)
        models[uid] = (name, m)

    geoms = {}
    for g in objects.find_all("Geometry"):
        geoms[g.props[0]] = g

    # Connections: child_uid -> parent_uid
    parents = {}
    geom_of = {}
    conns = root.find("Connections")
    if conns:
        for c in conns.find_all("C"):
            if c.props and c.props[0] == "OO":
                child, parent = c.props[1], c.props[2]
                if child in geoms:
                    geom_of[parent] = child
                else:
                    parents[child] = parent

    print("\n--- Model nodes -------------------------------------------------")
    for uid, (name, m) in models.items():
        parent = parents.get(uid, 0)
        pname = models.get(parent, ("<scene root>", None))[0]
        t = prop70(m, "Lcl Translation") or [0, 0, 0]
        rot = prop70(m, "Lcl Rotation") or [0, 0, 0]
        s = prop70(m, "Lcl Scaling") or [1, 1, 1]
        pre = prop70(m, "PreRotation")
        gt = prop70(m, "GeometricTranslation")
        kind = m.props[2] if len(m.props) > 2 else "?"
        print(f"\n  {name}   (type={kind}, parent={pname})")
        print(f"     Lcl Translation = {fmt(t)}")
        print(f"     Lcl Rotation    = {fmt(rot)}")
        print(f"     Lcl Scaling     = {fmt(s)}")
        if pre:
            print(f"     PreRotation     = {fmt(pre)}   <-- baked axis rotation")
        if gt:
            print(f"     GeometricTranslation = {fmt(gt)}")

        gid = geom_of.get(uid)
        if gid:
            verts = geoms[gid].find("Vertices")
            if verts and verts.props:
                v = verts.props[0]
                xs, ys, zs = v[0::3], v[1::3], v[2::3]
                print(f"     verts={len(xs)}  "
                      f"x[{min(xs):+.4f},{max(xs):+.4f}] "
                      f"y[{min(ys):+.4f},{max(ys):+.4f}] "
                      f"z[{min(zs):+.4f},{max(zs):+.4f}]")


def fmt(v):
    return "(" + ", ".join(f"{x:+.5f}" for x in v) + ")"


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else
         "Assets/_Project/Resources/Weapons/M4_Viewmodel.fbx")
