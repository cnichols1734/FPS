#!/usr/bin/env python3
"""
Procedural Overflow-style street props → Wavefront OBJ (+ MTL).

Generates satellite dishes, window AC units, sandbags, corrugated awnings,
overhead cables, utility-pole crossarms, jersey barriers, market stall frames,
and rubble piles for a Unity 6 FPS map. Units are metres. Pivot conventions:
  - ground props: base centre (y=0)
  - wall-mounted: mount point at origin (back face / bracket)
"""

from __future__ import annotations

import argparse
import math
import random
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, List, Optional, Sequence, Tuple

import numpy as np

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

ROOT = Path(__file__).resolve().parents[2]
OUT_DIR = ROOT / "_incoming" / "models" / "generated"
CONTACT_SHEET = OUT_DIR / "_contactsheet.png"
MANIFEST = OUT_DIR / "MANIFEST.md"

Vec3 = Tuple[float, float, float]
Vec2 = Tuple[float, float]


# ---------------------------------------------------------------------------
# Mesh core
# ---------------------------------------------------------------------------

@dataclass
class Mesh:
    name: str
    vertices: List[Vec3] = field(default_factory=list)
    normals: List[Vec3] = field(default_factory=list)
    uvs: List[Vec2] = field(default_factory=list)
    # faces as list of (v_idx, vt_idx, vn_idx) triples — 0-based
    faces: List[Tuple[Tuple[int, int, int], Tuple[int, int, int], Tuple[int, int, int]]] = field(
        default_factory=list
    )
    material: str = "PropDefault"

    def add_vertex(self, p: Vec3, n: Vec3, uv: Vec2) -> int:
        i = len(self.vertices)
        self.vertices.append(p)
        self.normals.append(n)
        self.uvs.append(uv)
        return i

    def add_tri(self, a: int, b: int, c: int) -> None:
        self.faces.append(((a, a, a), (b, b, b), (c, c, c)))

    def add_tri_vn(self, ia: int, ib: int, ic: int) -> None:
        """Add triangle using existing vertex/normal/uv indices (same index)."""
        self.add_tri(ia, ib, ic)

    def transform(self, m: np.ndarray) -> None:
        """Apply 4x4 transform to positions; rotate normals by 3x3 upper."""
        r = m[:3, :3]
        t = m[:3, 3]
        # For normals, use inverse-transpose of rotation (pure rotation → r itself)
        rn = r
        new_v, new_n = [], []
        for v, n in zip(self.vertices, self.normals):
            p = r @ np.array(v) + t
            nn = rn @ np.array(n)
            ln = np.linalg.norm(nn)
            if ln > 1e-12:
                nn = nn / ln
            new_v.append((float(p[0]), float(p[1]), float(p[2])))
            new_n.append((float(nn[0]), float(nn[1]), float(nn[2])))
        self.vertices = new_v
        self.normals = new_n

    def translate(self, dx: float, dy: float, dz: float) -> None:
        self.vertices = [(x + dx, y + dy, z + dz) for x, y, z in self.vertices]

    def merge(self, other: "Mesh") -> None:
        off = len(self.vertices)
        self.vertices.extend(other.vertices)
        self.normals.extend(other.normals)
        self.uvs.extend(other.uvs)
        for f in other.faces:
            self.faces.append(
                tuple((a + off, b + off, c + off) for a, b, c in f)  # type: ignore
            )

    def tri_count(self) -> int:
        return len(self.faces)

    def bounds(self) -> Tuple[Vec3, Vec3]:
        if not self.vertices:
            z = (0.0, 0.0, 0.0)
            return z, z
        xs = [v[0] for v in self.vertices]
        ys = [v[1] for v in self.vertices]
        zs = [v[2] for v in self.vertices]
        return (min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs))

    def size(self) -> Vec3:
        a, b = self.bounds()
        return (b[0] - a[0], b[1] - a[1], b[2] - a[2])


def mat_trs(t: Vec3 = (0, 0, 0), euler_deg: Vec3 = (0, 0, 0), s: Vec3 = (1, 1, 1)) -> np.ndarray:
    rx, ry, rz = [math.radians(a) for a in euler_deg]
    cx, sx = math.cos(rx), math.sin(rx)
    cy, sy = math.cos(ry), math.sin(ry)
    cz, sz = math.cos(rz), math.sin(rz)
    Rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]])
    Ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    Rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    R = Rz @ Ry @ Rx
    S = np.diag(s)
    M = np.eye(4)
    M[:3, :3] = R @ S
    M[:3, 3] = t
    return M


def _n(v: np.ndarray) -> np.ndarray:
    l = np.linalg.norm(v)
    return v / l if l > 1e-12 else np.array([0.0, 1.0, 0.0])


def _lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


# ---------------------------------------------------------------------------
# Primitive builders (bevelled, UV'd, normal'd)
# ---------------------------------------------------------------------------

def add_box(
    mesh: Mesh,
    center: Vec3,
    size: Vec3,
    *,
    bevel: float = 0.002,
    uv_scale: float = 1.0,
    deform: Optional[Callable] = None,
) -> None:
    """Axis-aligned box with chamfered edges. Origin-relative centre."""
    hx, hy, hz = size[0] * 0.5, size[1] * 0.5, size[2] * 0.5
    b = min(bevel, hx * 0.35, hy * 0.35, hz * 0.35)
    cx, cy, cz = center

    # Face rectangles (inset by b on each edge)
    faces_spec = [
        # +Z (front)
        ((0, 0, 1), [( -hx + b, -hy + b,  hz), ( hx - b, -hy + b,  hz),
                     ( hx - b,  hy - b,  hz), (-hx + b,  hy - b,  hz)]),
        # -Z (back)
        ((0, 0, -1), [( hx - b, -hy + b, -hz), (-hx + b, -hy + b, -hz),
                      (-hx + b,  hy - b, -hz), ( hx - b,  hy - b, -hz)]),
        # +Y (top)
        ((0, 1, 0), [(-hx + b,  hy, -hz + b), ( hx - b,  hy, -hz + b),
                     ( hx - b,  hy,  hz - b), (-hx + b,  hy,  hz - b)]),
        # -Y (bottom)
        ((0, -1, 0), [(-hx + b, -hy,  hz - b), ( hx - b, -hy,  hz - b),
                      ( hx - b, -hy, -hz + b), (-hx + b, -hy, -hz + b)]),
        # +X
        ((1, 0, 0), [( hx, -hy + b, -hz + b), ( hx, -hy + b,  hz - b),
                     ( hx,  hy - b,  hz - b), ( hx,  hy - b, -hz + b)]),
        # -X
        ((-1, 0, 0), [(-hx, -hy + b,  hz - b), (-hx, -hy + b, -hz + b),
                      (-hx,  hy - b, -hz + b), (-hx,  hy - b,  hz - b)]),
    ]

    def V(x, y, z):
        p = np.array([x + cx, y + cy, z + cz], dtype=float)
        if deform:
            p = deform(p)
        return (float(p[0]), float(p[1]), float(p[2]))

    def face_quad(nrm, pts):
        nx, ny, nz = nrm
        # UVs from plane projection
        idxs = []
        for i, (x, y, z) in enumerate(pts):
            if abs(nz) > 0.5:
                u, v = x * uv_scale, y * uv_scale
            elif abs(ny) > 0.5:
                u, v = x * uv_scale, z * uv_scale
            else:
                u, v = z * uv_scale, y * uv_scale
            idxs.append(mesh.add_vertex(V(x, y, z), (float(nx), float(ny), float(nz)), (u, v)))
        mesh.add_tri(idxs[0], idxs[1], idxs[2])
        mesh.add_tri(idxs[0], idxs[2], idxs[3])

    for nrm, pts in faces_spec:
        face_quad(nrm, pts)

    # Edge chamfers (12 edges) — each is a strip between two face edges
    # Edge: (axis of edge, face A normal, face B normal, coords)
    edges = [
        # along X at y=±hy, z=±hz
        ("x", (0, 1, 0), (0, 0, 1),  hy,  hz),
        ("x", (0, 1, 0), (0, 0, -1), hy, -hz),
        ("x", (0, -1, 0), (0, 0, 1), -hy,  hz),
        ("x", (0, -1, 0), (0, 0, -1), -hy, -hz),
        # along Y at x=±hx, z=±hz
        ("y", (1, 0, 0), (0, 0, 1),  hx,  hz),
        ("y", (1, 0, 0), (0, 0, -1), hx, -hz),
        ("y", (-1, 0, 0), (0, 0, 1), -hx,  hz),
        ("y", (-1, 0, 0), (0, 0, -1), -hx, -hz),
        # along Z at x=±hx, y=±hy
        ("z", (1, 0, 0), (0, 1, 0),  hx,  hy),
        ("z", (1, 0, 0), (0, -1, 0), hx, -hy),
        ("z", (-1, 0, 0), (0, 1, 0), -hx,  hy),
        ("z", (-1, 0, 0), (0, -1, 0), -hx, -hy),
    ]

    for axis, na, nb, c0, c1 in edges:
        n_avg = _n(np.array(na, dtype=float) + np.array(nb, dtype=float))
        # Four corners of chamfer quad (two along each face, inset)
        if axis == "x":
            # Face A at y=±hy, face B at z=±hz — chamfer strip between them.
            sy = 1 if c0 > 0 else -1
            sz = 1 if c1 > 0 else -1
            pA0 = (-hx + b, c0, c1 - sz * b)
            pA1 = ( hx - b, c0, c1 - sz * b)
            pB0 = (-hx + b, c0 - sy * b, c1)
            pB1 = ( hx - b, c0 - sy * b, c1)
            pts = [pA0, pA1, pB1, pB0]
        elif axis == "y":
            sx = 1 if c0 > 0 else -1
            sz = 1 if c1 > 0 else -1
            pA0 = (c0, -hy + b, c1 - sz * b)
            pA1 = (c0,  hy - b, c1 - sz * b)
            pB0 = (c0 - sx * b, -hy + b, c1)
            pB1 = (c0 - sx * b,  hy - b, c1)
            pts = [pA0, pA1, pB1, pB0]
        else:  # z
            sx = 1 if c0 > 0 else -1
            sy = 1 if c1 > 0 else -1
            pA0 = (c0, c1 - sy * b, -hz + b)
            pA1 = (c0, c1 - sy * b,  hz - b)
            pB0 = (c0 - sx * b, c1, -hz + b)
            pB1 = (c0 - sx * b, c1,  hz - b)
            pts = [pA0, pA1, pB1, pB0]

        idxs = []
        for x, y, z in pts:
            idxs.append(
                mesh.add_vertex(
                    V(x, y, z),
                    (float(n_avg[0]), float(n_avg[1]), float(n_avg[2])),
                    (x * uv_scale, y * uv_scale),
                )
            )
        # Ensure winding roughly outward
        mesh.add_tri(idxs[0], idxs[1], idxs[2])
        mesh.add_tri(idxs[0], idxs[2], idxs[3])

    # Corner chamfers (8 corners) as triangles / small fans
    for sx in (-1, 1):
        for sy in (-1, 1):
            for sz in (-1, 1):
                # Three points on the three adjacent face corners
                p_xy = (sx * (hx - b), sy * (hy - b), sz * hz)
                p_xz = (sx * (hx - b), sy * hy, sz * (hz - b))
                p_yz = (sx * hx, sy * (hy - b), sz * (hz - b))
                nrm = _n(np.array([sx, sy, sz], dtype=float))
                ia = mesh.add_vertex(V(*p_xy), tuple(nrm), (p_xy[0] * uv_scale, p_xy[1] * uv_scale))
                ib = mesh.add_vertex(V(*p_xz), tuple(nrm), (p_xz[0] * uv_scale, p_xz[1] * uv_scale))
                ic = mesh.add_vertex(V(*p_yz), tuple(nrm), (p_yz[0] * uv_scale, p_yz[1] * uv_scale))
                # Winding: outward
                if sx * sy * sz > 0:
                    mesh.add_tri(ia, ib, ic)
                else:
                    mesh.add_tri(ia, ic, ib)


def add_cylinder(
    mesh: Mesh,
    p0: Vec3,
    p1: Vec3,
    radius: float,
    *,
    segments: int = 10,
    uv_scale: float = 1.0,
    capped: bool = True,
    bevel_caps: bool = True,
) -> None:
    """Tube/cylinder between p0 and p1."""
    a = np.array(p0, dtype=float)
    b = np.array(p1, dtype=float)
    axis = b - a
    length = np.linalg.norm(axis)
    if length < 1e-9:
        return
    axis = axis / length
    # Orthonormal basis
    tmp = np.array([0.0, 1.0, 0.0]) if abs(axis[1]) < 0.9 else np.array([1.0, 0.0, 0.0])
    u = _n(np.cross(axis, tmp))
    v = _n(np.cross(axis, u))

    r_bevel = radius * 0.92 if bevel_caps else radius
    rings = []
    # Slight end taper for "bevel"
    for t, rad in ((0.0, r_bevel), (0.0, radius), (1.0, radius), (1.0, r_bevel)):
        centre = a + axis * (t * length)
        ring = []
        for i in range(segments):
            ang = 2 * math.pi * i / segments
            offset = (math.cos(ang) * u + math.sin(ang) * v) * rad
            pos = centre + offset
            nrm = _n(offset) if rad > 1e-9 else axis
            uv = (i / segments * uv_scale, t * length * uv_scale)
            ring.append(mesh.add_vertex(tuple(pos), tuple(nrm), uv))
        rings.append(ring)

    # Side quads between ring 1 and 2 (full radius)
    for ri in (0, 1, 2):
        rA, rB = rings[ri], rings[ri + 1]
        for i in range(segments):
            j = (i + 1) % segments
            mesh.add_tri(rA[i], rB[i], rB[j])
            mesh.add_tri(rA[i], rB[j], rA[j])

    if capped:
        # Cap centres
        for end, centre_pt, outward, ring in (
            (0, a, -axis, rings[0]),
            (1, b, axis, rings[-1]),
        ):
            ci = mesh.add_vertex(
                tuple(centre_pt),
                tuple(outward),
                (0.5, 0.5),
            )
            for i in range(segments):
                j = (i + 1) % segments
                if end == 0:
                    mesh.add_tri(ci, ring[j], ring[i])
                else:
                    mesh.add_tri(ci, ring[i], ring[j])


def add_tube_path(
    mesh: Mesh,
    points: Sequence[Vec3],
    radius: float,
    *,
    segments: int = 8,
    uv_scale: float = 2.0,
) -> None:
    """Sweep a circular cross-section along a polyline."""
    pts = [np.array(p, dtype=float) for p in points]
    if len(pts) < 2:
        return
    # Tangents
    tangents = []
    for i in range(len(pts)):
        if i == 0:
            t = pts[1] - pts[0]
        elif i == len(pts) - 1:
            t = pts[-1] - pts[-2]
        else:
            t = pts[i + 1] - pts[i - 1]
        tangents.append(_n(t))

    # Parallel transport frames
    tmp = np.array([0.0, 1.0, 0.0]) if abs(tangents[0][1]) < 0.9 else np.array([1.0, 0.0, 0.0])
    normal = _n(np.cross(tangents[0], tmp))
    binormal = _n(np.cross(tangents[0], normal))

    rings = []
    v_accum = 0.0
    for i, p in enumerate(pts):
        if i > 0:
            # Rotate frame
            axis = np.cross(tangents[i - 1], tangents[i])
            alen = np.linalg.norm(axis)
            if alen > 1e-8:
                axis = axis / alen
                ang = math.acos(max(-1, min(1, float(np.dot(tangents[i - 1], tangents[i])))))
                # Rodrigues on normal
                k = axis
                n = normal
                normal = _n(
                    n * math.cos(ang)
                    + np.cross(k, n) * math.sin(ang)
                    + k * np.dot(k, n) * (1 - math.cos(ang))
                )
            binormal = _n(np.cross(tangents[i], normal))
            v_accum += float(np.linalg.norm(pts[i] - pts[i - 1]))

        ring = []
        for s in range(segments):
            ang = 2 * math.pi * s / segments
            offset = (math.cos(ang) * normal + math.sin(ang) * binormal) * radius
            pos = p + offset
            nrm = _n(offset)
            uv = (s / segments, v_accum * uv_scale)
            ring.append(mesh.add_vertex(tuple(pos), tuple(nrm), uv))
        rings.append(ring)

    for ri in range(len(rings) - 1):
        for s in range(segments):
            t = (s + 1) % segments
            a, b, c, d = rings[ri][s], rings[ri + 1][s], rings[ri + 1][t], rings[ri][t]
            mesh.add_tri(a, b, c)
            mesh.add_tri(a, c, d)


def add_sphere(
    mesh: Mesh,
    center: Vec3,
    radius: float,
    *,
    stacks: int = 8,
    slices: int = 12,
    uv_scale: float = 1.0,
    deform: Optional[Callable] = None,
) -> None:
    cx, cy, cz = center
    grid = []
    for i in range(stacks + 1):
        v = i / stacks
        phi = v * math.pi
        row = []
        for j in range(slices + 1):
            u = j / slices
            th = u * 2 * math.pi
            nrm = np.array(
                [math.sin(phi) * math.cos(th), math.cos(phi), math.sin(phi) * math.sin(th)]
            )
            pos = np.array([cx, cy, cz]) + nrm * radius
            if deform:
                pos = deform(pos)
                nrm = _n(pos - np.array([cx, cy, cz]))
            row.append(
                mesh.add_vertex(
                    tuple(pos),
                    tuple(nrm),
                    (u * uv_scale, v * uv_scale),
                )
            )
        grid.append(row)
    for i in range(stacks):
        for j in range(slices):
            a, b = grid[i][j], grid[i][j + 1]
            c, d = grid[i + 1][j + 1], grid[i + 1][j]
            if i == 0:
                mesh.add_tri(a, c, d)
                mesh.add_tri(a, b, c)
            elif i == stacks - 1:
                mesh.add_tri(a, b, c)
                mesh.add_tri(a, c, d)
            else:
                mesh.add_tri(a, b, c)
                mesh.add_tri(a, c, d)


def add_paraboloid_dish(
    mesh: Mesh,
    *,
    radius: float,
    depth: float,
    rim: float = 0.015,
    segments: int = 24,
    rings: int = 6,
) -> None:
    """Open parabolic dish facing +Z, rim at z=0, dish curves toward -Z."""
    # z(r) = -depth * (r/radius)^2. Rings start at i=1 to avoid a zero-radius ring.
    k = depth / (radius * radius) if radius > 1e-9 else 0.0
    centre_in = mesh.add_vertex((0.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.5, 0.0))
    grid = [None]  # index 0 unused; rings 1..rings
    for i in range(1, rings + 1):
        t = i / rings
        r = radius * t
        z = -depth * (t * t)
        row = []
        for j in range(segments):
            ang = 2 * math.pi * j / segments
            x = r * math.cos(ang)
            y = r * math.sin(ang)
            n_in = _n(np.array([-2 * k * x, -2 * k * y, 1.0]))
            row.append(mesh.add_vertex((x, y, z), tuple(n_in), (ang / (2 * math.pi), t)))
        grid.append(row)

    for j in range(segments):
        j2 = (j + 1) % segments
        mesh.add_tri(centre_in, grid[1][j], grid[1][j2])
    for i in range(1, rings):
        for j in range(segments):
            j2 = (j + 1) % segments
            a, b = grid[i][j], grid[i][j2]
            c, d_ = grid[i + 1][j2], grid[i + 1][j]
            mesh.add_tri(a, b, c)
            mesh.add_tri(a, c, d_)

    # Outer back shell (slightly offset) for thickness silhouette
    thick = 0.008
    centre_out = mesh.add_vertex((0.0, 0.0, -thick), (0.0, 0.0, -1.0), (0.5, 0.0))
    grid_b = [None]
    for i in range(1, rings + 1):
        t = i / rings
        r = radius * t
        z = -depth * (t * t) - thick
        row = []
        for j in range(segments):
            ang = 2 * math.pi * j / segments
            x = r * math.cos(ang)
            y = r * math.sin(ang)
            n_out = _n(np.array([2 * k * x, 2 * k * y, -1.0]))
            row.append(mesh.add_vertex((x, y, z), tuple(n_out), (ang / (2 * math.pi), t)))
        grid_b.append(row)
    for j in range(segments):
        j2 = (j + 1) % segments
        mesh.add_tri(centre_out, grid_b[1][j2], grid_b[1][j])
    for i in range(1, rings):
        for j in range(segments):
            j2 = (j + 1) % segments
            a, b = grid_b[i][j], grid_b[i][j2]
            c, d_ = grid_b[i + 1][j2], grid_b[i + 1][j]
            mesh.add_tri(a, d_, c)
            mesh.add_tri(a, c, b)

    # Rim ring (bevelled torus-ish)
    rim_r = rim
    for j in range(segments):
        ang0 = 2 * math.pi * j / segments
        ang1 = 2 * math.pi * (j + 1) / segments
        for (r0, z0), (r1, z1) in [
            ((radius, 0.0), (radius + rim_r, 0.0)),
            ((radius + rim_r, 0.0), (radius + rim_r * 0.6, -rim_r)),
            ((radius + rim_r * 0.6, -rim_r), (radius, -thick)),
        ]:
            def pt(r, z, ang):
                return (r * math.cos(ang), r * math.sin(ang), z)

            p00 = pt(r0, z0, ang0)
            p01 = pt(r0, z0, ang1)
            p10 = pt(r1, z1, ang0)
            p11 = pt(r1, z1, ang1)
            nrm = _n(np.array([math.cos((ang0 + ang1) * 0.5), math.sin((ang0 + ang1) * 0.5), 0.3]))
            ia = mesh.add_vertex(p00, tuple(nrm), (j / segments, 0))
            ib = mesh.add_vertex(p01, tuple(nrm), ((j + 1) / segments, 0))
            ic = mesh.add_vertex(p11, tuple(nrm), ((j + 1) / segments, 1))
            id_ = mesh.add_vertex(p10, tuple(nrm), (j / segments, 1))
            mesh.add_tri(ia, ib, ic)
            mesh.add_tri(ia, ic, id_)


# ---------------------------------------------------------------------------
# OBJ / MTL I/O
# ---------------------------------------------------------------------------

def write_obj(mesh: Mesh, path: Path, mtl_name: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = [
        f"# {mesh.name}",
        f"mtllib {mtl_name}",
        f"o {mesh.name}",
        f"usemtl {mesh.material}",
    ]
    for v in mesh.vertices:
        lines.append(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}")
    for vt in mesh.uvs:
        lines.append(f"vt {vt[0]:.6f} {vt[1]:.6f}")
    for vn in mesh.normals:
        lines.append(f"vn {vn[0]:.6f} {vn[1]:.6f} {vn[2]:.6f}")
    for f in mesh.faces:
        # f v/vt/vn — 1-based
        parts = []
        for vi, vti, vni in f:
            parts.append(f"{vi + 1}/{vti + 1}/{vni + 1}")
        lines.append("f " + " ".join(parts))
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def write_mtl(path: Path, materials: Sequence[Tuple[str, Vec3]]) -> None:
    lines = ["# Generated prop materials"]
    for name, kd in materials:
        lines += [
            f"newmtl {name}",
            "Ka 0.2 0.2 0.2",
            f"Kd {kd[0]:.3f} {kd[1]:.3f} {kd[2]:.3f}",
            "Ks 0.15 0.15 0.15",
            "Ns 20",
            "d 1.0",
            "illum 2",
            "",
        ]
    path.write_text("\n".join(lines), encoding="utf-8")


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------

@dataclass
class ValidationResult:
    ok: bool
    name: str
    tris: int
    verts: int
    size: Vec3
    errors: List[str]


def validate_mesh(mesh: Mesh, *, expected_size: Optional[Vec3] = None, tol: float = 0.35) -> ValidationResult:
    errors: List[str] = []
    if not mesh.vertices:
        errors.append("no vertices")
    if not mesh.normals:
        errors.append("no normals")
    if not mesh.uvs:
        errors.append("no uvs")
    if not mesh.faces:
        errors.append("no faces")
    if len(mesh.vertices) != len(mesh.normals) or len(mesh.vertices) != len(mesh.uvs):
        errors.append(
            f"count mismatch v={len(mesh.vertices)} n={len(mesh.normals)} uv={len(mesh.uvs)}"
        )

    for i, v in enumerate(mesh.vertices):
        if any(math.isnan(c) or math.isinf(c) for c in v):
            errors.append(f"NaN/Inf vertex {i}")
            break
    for i, n in enumerate(mesh.normals):
        if any(math.isnan(c) or math.isinf(c) for c in n):
            errors.append(f"NaN/Inf normal {i}")
            break
        if abs(math.sqrt(n[0] ** 2 + n[1] ** 2 + n[2] ** 2) - 1.0) > 0.15:
            # soft warn — don't fail, but note
            pass

    degenerates = 0
    for f in mesh.faces:
        ia, ib, ic = f[0][0], f[1][0], f[2][0]
        if max(ia, ib, ic) >= len(mesh.vertices):
            errors.append("face index out of range")
            break
        a = np.array(mesh.vertices[ia])
        b = np.array(mesh.vertices[ib])
        c = np.array(mesh.vertices[ic])
        area = 0.5 * np.linalg.norm(np.cross(b - a, c - a))
        if area < 1e-10:
            degenerates += 1
    if degenerates > max(3, len(mesh.faces) * 0.02):
        errors.append(f"too many degenerate tris: {degenerates}")

    sz = mesh.size()
    if expected_size:
        for dim, got, exp in zip("xyz", sz, expected_size):
            if exp <= 0:
                continue
            if abs(got - exp) / exp > tol and abs(got - exp) > 0.08:
                # soft: report but only fail if wildly off
                if abs(got - exp) / max(exp, 1e-6) > 1.5:
                    errors.append(f"size {dim}={got:.3f} vs expected ~{exp:.3f}")

    if mesh.tri_count() < 10:
        errors.append(f"too few tris: {mesh.tri_count()}")
    if mesh.tri_count() > 5000:
        errors.append(f"too many tris: {mesh.tri_count()}")

    return ValidationResult(
        ok=len(errors) == 0,
        name=mesh.name,
        tris=mesh.tri_count(),
        verts=len(mesh.vertices),
        size=sz,
        errors=errors,
    )


def parse_obj_check(path: Path) -> List[str]:
    """Re-parse written OBJ and verify consistency."""
    errors = []
    vs = vts = vns = faces = 0
    try:
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.startswith("v "):
                vs += 1
                parts = line.split()
                vals = [float(parts[i]) for i in range(1, 4)]
                if any(math.isnan(x) or math.isinf(x) for x in vals):
                    errors.append("NaN in file vertices")
            elif line.startswith("vt "):
                vts += 1
            elif line.startswith("vn "):
                vns += 1
            elif line.startswith("f "):
                faces += 1
    except Exception as e:
        errors.append(f"parse error: {e}")
        return errors
    if vs == 0 or vts == 0 or vns == 0 or faces == 0:
        errors.append(f"empty channels v={vs} vt={vts} vn={vns} f={faces}")
    return errors


# ---------------------------------------------------------------------------
# Prop generators
# ---------------------------------------------------------------------------

def gen_satellite_dish(rng: random.Random, variant: int) -> Mesh:
    """Wall/pole-mounted parabolic dish. Pivot at mount point (origin)."""
    m = Mesh(name=f"sat_dish_{variant:02d}", material="MetalGrey")
    diameter = rng.uniform(0.65, 1.15)
    radius = diameter * 0.5
    depth = radius * rng.uniform(0.22, 0.32)
    # Dish in XY, open toward +Z; mount at -Z
    add_paraboloid_dish(m, radius=radius, depth=depth, rim=0.012 + rng.uniform(0, 0.006),
                        segments=20 + variant % 4, rings=5)

    # LNB arm — from lower rim toward focus (reads clearly in silhouette)
    focus_z = depth * 0.15
    arm_start = (0.0, -radius * 0.85, -depth * 0.15)
    arm_mid = (0.0, -radius * 0.25, focus_z + depth * 0.35)
    arm_end = (rng.uniform(-0.015, 0.015), 0.0, focus_z + depth * 0.55)
    add_cylinder(m, arm_start, arm_mid, 0.011, segments=8)
    add_cylinder(m, arm_mid, arm_end, 0.011, segments=8)
    # LNB feed horn / converter box
    add_cylinder(m, arm_end, (arm_end[0], arm_end[1], arm_end[2] + 0.05), 0.03, segments=10)
    add_box(m, (arm_end[0], arm_end[1] - 0.01, arm_end[2] + 0.07), (0.07, 0.05, 0.055), bevel=0.003)

    # Rooftop tilt: dish faces somewhat upward/outward
    tilt = rng.uniform(-35, -15)
    yaw = rng.uniform(-20, 20)
    m.transform(mat_trs(euler_deg=(tilt, yaw, rng.uniform(-4, 4))))

    # Push assembly forward so wall plate is at origin
    mn, mx = m.bounds()
    m.translate(0, 0, -mn[2] + 0.06)

    # Wall / roof mount plate + stub arm
    add_box(m, (0, 0, 0.012), (0.14, 0.2, 0.024), bevel=0.003)
    add_cylinder(m, (0, 0, 0.02), (0, 0.02, 0.14), 0.018, segments=8)
    add_box(m, (0, 0.02, 0.14), (0.08, 0.06, 0.05), bevel=0.003)

    if variant % 2 == 0:
        # Tripod roof mount
        for ang in (0, 120, 240):
            rad = math.radians(ang + rng.uniform(-8, 8))
            ex = math.sin(rad) * 0.18
            ey = -0.12 + math.cos(rad) * 0.05
            add_cylinder(m, (0, 0, 0.04), (ex, ey, 0.02), 0.009, segments=6)

    if variant >= 2:
        # Bent rim ding
        dent_ang = rng.uniform(0.5, 2.0)
        dx = math.cos(dent_ang) * radius * 0.9
        dy = math.sin(dent_ang) * radius * 0.9
        add_box(m, (dx, dy, 0.12), (0.05, 0.04, 0.025), bevel=0.002)

    # Recentre X/Y on mount; keep z mount at ~0
    mn, mx = m.bounds()
    m.translate(-(mn[0] + mx[0]) * 0.5, -(mn[1] + mx[1]) * 0.5, -mn[2])
    m.material = "MetalGrey"
    return m


def gen_window_ac(rng: random.Random, variant: int) -> Mesh:
    """Window AC unit. Pivot at back-face mount (origin)."""
    m = Mesh(name=f"window_ac_{variant:02d}", material="MetalWhite")
    w = rng.uniform(0.64, 0.78)
    h = rng.uniform(0.40, 0.50)
    d = rng.uniform(0.45, 0.56)

    # Main case — slight non-uniform scale for manufacturing irregularity
    case = Mesh(name="case")
    add_box(case, (0, 0, d * 0.5), (w, h, d), bevel=0.005, uv_scale=1.2)
    # Case denting: push a few verts inward on top/side
    dent_x = rng.uniform(-w * 0.25, w * 0.25)
    new_v = []
    for x, y, z in case.vertices:
        dx, dy, dz = x, y, z
        # Top-panel dent
        if y > h * 0.35 and abs(x - dent_x) < 0.12 and 0.2 * d < z < 0.75 * d:
            dy -= 0.012 * (1.0 - abs(x - dent_x) / 0.12)
        # Side ding
        if variant >= 1 and x > w * 0.4 and abs(y) < h * 0.2:
            dx -= 0.01
        new_v.append((dx, dy, dz))
    case.vertices = new_v
    m.merge(case)

    # Front flange / lip framing the grille opening
    front_z = d
    flange_t = 0.018
    gw, gh = w * 0.78, h * 0.68
    # Outer flange ring as four strips
    add_box(m, (0, gh * 0.5 + 0.02, front_z - 0.008), (w * 0.92, flange_t, 0.02), bevel=0.002)
    add_box(m, (0, -gh * 0.5 - 0.02, front_z - 0.008), (w * 0.92, flange_t, 0.02), bevel=0.002)
    add_box(m, (gw * 0.5 + 0.025, 0, front_z - 0.008), (flange_t, gh + 0.05, 0.02), bevel=0.002)
    add_box(m, (-gw * 0.5 - 0.025, 0, front_z - 0.008), (flange_t, gh + 0.05, 0.02), bevel=0.002)

    # Recessed dark well behind louvres
    add_box(m, (0, 0, front_z - 0.055), (gw * 0.98, gh * 0.98, 0.04), bevel=0.002)

    # Actual angled louvre slats (recessed into well)
    louvre_count = 8 + variant
    for i in range(louvre_count):
        t = (i + 0.5) / louvre_count
        y = -gh * 0.5 + t * gh
        slat = Mesh(name="slat")
        add_box(slat, (0, 0, 0), (gw * 0.94, 0.011, 0.032), bevel=0.001)
        # Pitch so edges catch light; stagger depth slightly
        pitch = 22 + rng.uniform(-3, 3)
        slat.transform(
            mat_trs(
                t=(rng.uniform(-0.005, 0.005), y, front_z - 0.028 + rng.uniform(-0.004, 0.002)),
                euler_deg=(pitch, 0, rng.uniform(-1.5, 1.5)),
            )
        )
        m.merge(slat)

    # Fan hub / motor cylinder behind grille
    add_cylinder(
        m,
        (0, 0.015, front_z - 0.07),
        (0, 0.015, front_z - 0.035),
        min(gw, gh) * 0.22,
        segments=12,
    )

    # Side intake louvres
    for sx in (-1.0, 1.0):
        for k in range(5):
            yy = -h * 0.28 + k * (h * 0.11)
            slat = Mesh(name="side")
            add_box(slat, (0, 0, 0), (0.014, 0.016, d * 0.32), bevel=0.001)
            slat.transform(
                mat_trs(
                    t=(sx * (w * 0.5 - 0.006), yy, d * 0.42),
                    euler_deg=(0, 0, sx * 12),
                )
            )
            m.merge(slat)

    # Bottom drip / condensate lip (protrudes, catches stain)
    add_box(m, (0, -h * 0.5 + 0.008, d * 0.78), (w * 0.88, 0.016, 0.06), bevel=0.002)
    add_box(m, (0, -h * 0.5 - 0.006, d * 0.88), (w * 0.7, 0.01, 0.035), bevel=0.001)

    # Visible L-bracket mounts underneath
    by = -h * 0.5
    for x in (-w * 0.32, w * 0.32):
        # Horizontal shelf under unit
        add_box(m, (x, by - 0.012, d * 0.35), (0.035, 0.014, d * 0.55), bevel=0.002)
        # Vertical wall plate
        add_box(m, (x, by + 0.04, 0.012), (0.04, 0.1, 0.016), bevel=0.002)
        # Diagonal brace
        add_cylinder(m, (x, by, 0.04), (x, by - 0.11, d * 0.7), 0.009, segments=6)
        # Bolt nubs
        add_cylinder(m, (x, by + 0.02, 0.02), (x, by + 0.02, 0.035), 0.006, segments=5)

    # Control panel / badge bump on side
    add_box(
        m,
        (w * 0.5 - 0.02, h * 0.15, d * 0.25),
        (0.02, 0.08, 0.1),
        bevel=0.002,
    )

    if variant >= 2:
        # Corner crush
        add_box(m, (-w * 0.42, -h * 0.35, d * 0.92), (0.08, 0.07, 0.05), bevel=0.003)

    m.transform(mat_trs(euler_deg=(0, rng.uniform(-2, 2), rng.uniform(-1.2, 1.2))))
    mn, _ = m.bounds()
    m.translate(0, 0, -mn[2])
    mn, mx = m.bounds()
    m.translate(0, -(mn[1] + mx[1]) * 0.5, 0)
    return m


def _sandbag_blob(
    mesh: Mesh,
    centre: Vec3,
    size: Vec3,
    rng: random.Random,
    seed_off: int = 0,
    *,
    stacks: int = 6,
    slices: int = 10,
    settle: float = 1.0,
) -> None:
    """Soft slumped sandbag from deformed ellipsoid. size = (length, height, depth)."""
    sx, sy, sz = size

    def deform(p: np.ndarray) -> np.ndarray:
        local = p - np.array(centre)
        # Flatten / settle bottom under weight
        if local[1] < 0:
            local[1] *= 0.55 + 0.2 * (1.0 - settle)
        else:
            # Squash top slightly when loaded from above
            local[1] *= 1.0 - 0.08 * settle
        # Lateral bulge (press against neighbours / gravity)
        bulge = 1.0 + (0.10 + 0.08 * settle) * math.cos(local[1] / max(sy * 0.5, 1e-6) * math.pi)
        local[2] *= bulge
        local[0] *= 1.0 + 0.06 * math.sin(local[2] * 6 + seed_off * 0.7)
        # Tied-end cinch
        end = abs(local[0]) / max(sx * 0.5, 1e-6)
        if end > 0.65:
            pinch = 1.0 - 0.4 * ((end - 0.65) / 0.35)
            local[1] *= pinch
            local[2] *= pinch
        local += np.array(
            [rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1)]
        ) * (0.01 + 0.006 * settle)
        return np.array(centre) + local

    cx, cy, cz = centre
    grid = []
    for i in range(stacks + 1):
        v = i / stacks
        phi = v * math.pi
        row = []
        for j in range(slices):
            u = j / slices
            th = u * 2 * math.pi
            nrm = np.array(
                [math.sin(phi) * math.cos(th), math.cos(phi), math.sin(phi) * math.sin(th)]
            )
            pos = np.array([cx, cy, cz]) + nrm * np.array([sx * 0.5, sy * 0.5, sz * 0.5])
            pos = deform(pos)
            row.append((pos, u, v))
        grid.append(row)

    idx_grid = []
    for i in range(stacks + 1):
        row_i = []
        for j in range(slices):
            pos = grid[i][j][0]
            jn = (j + 1) % slices
            jp = (j - 1) % slices
            if i == 0:
                nrm = np.array([0.0, 1.0, 0.0])
            elif i == stacks:
                nrm = np.array([0.0, -1.0, 0.0])
            else:
                e1 = grid[i][jn][0] - grid[i][jp][0]
                e2 = grid[min(i + 1, stacks)][j][0] - grid[max(i - 1, 0)][j][0]
                nrm = _n(np.cross(e1, e2))
                if np.dot(nrm, pos - np.array(centre)) < 0:
                    nrm = -nrm
            u, v = grid[i][j][1], grid[i][j][2]
            row_i.append(mesh.add_vertex(tuple(pos), tuple(nrm), (u * 1.5, v)))
        idx_grid.append(row_i)

    for i in range(stacks):
        for j in range(slices):
            j2 = (j + 1) % slices
            a, b = idx_grid[i][j], idx_grid[i][j2]
            c, d_idx = idx_grid[i + 1][j2], idx_grid[i + 1][j]
            mesh.add_tri(a, b, c)
            mesh.add_tri(a, c, d_idx)


def _place_sandbag(
    mesh: Mesh,
    centre: Vec3,
    size: Vec3,
    rng: random.Random,
    seed_off: int,
    *,
    yaw_deg: float = 0.0,
    settle: float = 1.0,
    detail: str = "wall",
) -> None:
    """Build one bag in a temp mesh, rotate slightly, merge."""
    tmp = Mesh(name="bag")
    stacks, slices = (7, 12) if detail == "hero" else (5, 9)
    _sandbag_blob(
        tmp,
        (0.0, 0.0, 0.0),
        size,
        rng,
        seed_off,
        stacks=stacks,
        slices=slices,
        settle=settle,
    )
    eul = (
        rng.uniform(-6, 6),
        yaw_deg + rng.uniform(-8, 8),
        rng.uniform(-5, 5),
    )
    tmp.transform(mat_trs(t=centre, euler_deg=eul))
    mesh.merge(tmp)


def _sandbag_straight_wall(
    mesh: Mesh,
    rng: random.Random,
    *,
    length: float,
    courses: int,
    bag_l: float = 0.50,
    bag_h: float = 0.24,
    bag_d: float = 0.34,
    origin: Vec3 = (0.0, 0.0, 0.0),
    yaw_deg: float = 0.0,
    damaged: bool = False,
    seed_base: int = 0,
) -> None:
    """
    Tileable straight sandbag wall along local +X, running-bond courses.
    Course rise ≈ bag_h * 0.85 so N courses ≈ N * 0.20 m (+ top bulge).
    """
    pitch = bag_l * 0.92  # centre-to-centre along wall
    n_bags = max(3, int(round(length / pitch)))
    # Snap length so ends tile: segment width = n_bags * pitch
    span = n_bags * pitch
    course_rise = bag_h * 0.82

    yaw = math.radians(yaw_deg)
    cos_y, sin_y = math.cos(yaw), math.sin(yaw)
    ox, oy, oz = origin

    def world(lx: float, ly: float, lz: float) -> Vec3:
        # Rotate local (x along wall, z through wall) into world XZ
        wx = ox + lx * cos_y - lz * sin_y
        wz = oz + lx * sin_y + lz * cos_y
        return (wx, oy + ly, wz)

    for row in range(courses):
        staggered = row % 2 == 1
        # Running bond: odd courses offset by half pitch; still span full width
        # by using n_bags bags starting at -span/2 + half_pitch/2 ... 
        if staggered:
            count = n_bags
            x0 = -span * 0.5 + pitch * 0.5
        else:
            count = n_bags
            x0 = -span * 0.5 + pitch * 0.5

        # Extra half-offset for stagger — shift bag centres, keep ends filled
        x_shift = (pitch * 0.5) if staggered else 0.0

        for i in range(count):
            # Skip some top-course bags for damaged / collapsed look
            if damaged and row >= courses - 2:
                # Collapse right side of top courses
                t = i / max(count - 1, 1)
                if t > 0.45 and rng.random() < (0.55 if row == courses - 1 else 0.3):
                    # Fallen bag near base instead
                    if rng.random() < 0.5:
                        lx = x0 + i * pitch + x_shift + rng.uniform(-0.1, 0.1)
                        ly = bag_h * 0.35
                        lz = bag_d * rng.uniform(0.6, 1.2)
                        _place_sandbag(
                            mesh,
                            world(lx, ly, lz),
                            (
                                bag_l * rng.uniform(0.9, 1.05),
                                bag_h * rng.uniform(0.85, 1.0),
                                bag_d * rng.uniform(0.9, 1.1),
                            ),
                            rng,
                            seed_base + row * 40 + i,
                            yaw_deg=yaw_deg + rng.uniform(40, 100),
                            settle=0.4,
                        )
                    continue

            lx = x0 + i * pitch + x_shift
            # Keep bags within tileable span (clamp ends)
            lx = max(-span * 0.5 + bag_l * 0.35, min(span * 0.5 - bag_l * 0.35, lx))
            ly = bag_h * 0.42 + row * course_rise
            # Lower bags bulge forward slightly under load of courses above
            load = (courses - 1 - row) / max(courses - 1, 1)
            lz = rng.uniform(-0.025, 0.025) + load * 0.015
            sz = (
                bag_l * rng.uniform(0.94, 1.06),
                bag_h * rng.uniform(0.92, 1.06),
                bag_d * rng.uniform(0.94, 1.08) * (1.0 + 0.06 * load),
            )
            _place_sandbag(
                mesh,
                world(lx, ly, lz),
                sz,
                rng,
                seed_base + row * 40 + i,
                yaw_deg=yaw_deg,
                settle=0.5 + 0.5 * load,
            )


def gen_sandbag(rng: random.Random, variant: int) -> Mesh:
    """
    Sandbag props. Pivot base-centre.
      0–1: single loose bags
      2–3: crouch-cover straight walls (≈1.0 m, tileable)
      4:   standing-cover straight wall (≈1.25 m, tileable)
      5–6: L-shaped corner segments
      7:   partially collapsed / damaged wall
    """
    m = Mesh(name=f"sandbag_{variant:02d}", material="Burlap")

    if variant <= 1:
        bag_l = rng.uniform(0.48, 0.55)
        bag_h = rng.uniform(0.22, 0.26)
        bag_d = rng.uniform(0.32, 0.38)
        _place_sandbag(
            m,
            (0.0, bag_h * 0.42, 0.0),
            (bag_l, bag_h, bag_d),
            rng,
            variant * 3,
            yaw_deg=rng.uniform(-15, 15),
            settle=0.35,
            detail="hero",
        )
        if variant == 1:
            # Pair of loose bags
            _place_sandbag(
                m,
                (bag_l * 0.55, bag_h * 0.4, rng.uniform(-0.05, 0.08)),
                (bag_l * rng.uniform(0.95, 1.05), bag_h * 0.95, bag_d * 1.02),
                rng,
                11,
                yaw_deg=rng.uniform(10, 40),
                settle=0.3,
                detail="hero",
            )
    elif variant in (2, 3):
        # Crouch cover: 5 courses → ~1.0 m
        length = rng.uniform(1.7, 2.3) if variant == 2 else rng.uniform(1.5, 2.0)
        _sandbag_straight_wall(
            m,
            rng,
            length=length,
            courses=5,
            bag_l=rng.uniform(0.48, 0.52),
            bag_h=rng.uniform(0.23, 0.255),
            bag_d=rng.uniform(0.33, 0.36),
            seed_base=variant * 100,
        )
    elif variant == 4:
        # Standing cover: 6 courses → ~1.25 m
        _sandbag_straight_wall(
            m,
            rng,
            length=rng.uniform(1.8, 2.5),
            courses=6,
            bag_l=0.50,
            bag_h=0.245,
            bag_d=0.35,
            seed_base=400,
        )
    elif variant in (5, 6):
        # L-corner: two arms, crouch height
        arm = rng.uniform(1.1, 1.5)
        courses = 5
        bag_h = 0.24
        _sandbag_straight_wall(
            m,
            rng,
            length=arm,
            courses=courses,
            bag_l=0.50,
            bag_h=bag_h,
            bag_d=0.34,
            origin=(0.0, 0.0, 0.0),
            yaw_deg=0.0,
            seed_base=variant * 100,
        )
        # Second arm along +Z; shift so corner bags overlap less
        _sandbag_straight_wall(
            m,
            rng,
            length=arm * rng.uniform(0.9, 1.1),
            courses=courses,
            bag_l=0.50,
            bag_h=bag_h,
            bag_d=0.34,
            origin=(0.0, 0.0, 0.0),
            yaw_deg=90.0 if variant == 5 else -90.0,
            seed_base=variant * 100 + 50,
        )
    else:
        # Damaged / partially collapsed crouch wall
        _sandbag_straight_wall(
            m,
            rng,
            length=rng.uniform(1.8, 2.4),
            courses=5,
            bag_l=0.50,
            bag_h=0.24,
            bag_d=0.35,
            damaged=True,
            seed_base=700,
        )

    mn, _ = m.bounds()
    m.translate(0, -mn[1], 0)
    # Centre XZ for loose bags / corners; for straight walls keep X span
    # centred so tiling pivot is segment centre.
    mn, mx = m.bounds()
    m.translate(-(mn[0] + mx[0]) * 0.5, 0, -(mn[2] + mx[2]) * 0.5)
    mn, _ = m.bounds()
    m.translate(0, -mn[1], 0)
    return m


def gen_awning(rng: random.Random, variant: int) -> Mesh:
    """Corrugated shop canopy. Pivot at wall attach (back edge centre, y at underside)."""
    m = Mesh(name=f"awning_{variant:02d}", material="CorrugatedMetal")
    span = rng.uniform(2.0, 3.0)  # X
    depth = rng.uniform(0.9, 1.4)  # Z out from wall
    drop = rng.uniform(0.25, 0.45)  # slope drop
    corrugation = rng.uniform(0.035, 0.05)
    wave_amp = rng.uniform(0.012, 0.02)
    sagging = variant == 3  # designated damaged/sagging

    # Sheet grid
    nu, nv = 28, 10
    grid_idx = []
    for j in range(nv + 1):
        v = j / nv
        row = []
        for i in range(nu + 1):
            u = i / nu
            x = (u - 0.5) * span
            z = v * depth
            y = -v * drop
            # Corrugation along X
            y += math.sin(x / corrugation * math.pi) * wave_amp
            if sagging:
                # Sag in middle + bent corner
                sag = -0.18 * math.sin(u * math.pi) * (v ** 1.2)
                y += sag
                if u > 0.85 and v > 0.7:
                    y -= 0.12
                    x += 0.05
            # Slight warp
            y += math.sin(u * 4 + variant) * 0.008 * v
            # Normal approx
            # Partial derivatives
            dy_dx = math.cos(x / corrugation * math.pi) * wave_amp * (math.pi / corrugation)
            nrm = _n(np.array([-dy_dx, 1.0, drop / max(depth, 1e-6)]))
            row.append(m.add_vertex((x, y, z), tuple(nrm), (u * span * 0.5, v * depth * 0.5)))
        grid_idx.append(row)

    for j in range(nv):
        for i in range(nu):
            a, b = grid_idx[j][i], grid_idx[j][i + 1]
            c, d = grid_idx[j + 1][i + 1], grid_idx[j + 1][i]
            m.add_tri(a, b, c)
            m.add_tri(a, c, d)
            # underside flip
            n_down = (0.0, -1.0, 0.0)
            # duplicate underside with slight offset — skip for budget; single sided OK for awning

    # Support frame tubes
    # Wall bar
    add_cylinder(m, (-span * 0.48, 0.02, 0.02), (span * 0.48, 0.02, 0.02), 0.018, segments=8)
    # Front bar
    front_y = -drop + ( -0.15 if sagging else 0.0)
    add_cylinder(
        m,
        (-span * 0.48, front_y, depth - 0.02),
        (span * 0.48, front_y * (0.7 if sagging else 1.0), depth - 0.02),
        0.016,
        segments=8,
    )
    # Struts
    for x in (-span * 0.4, 0.0, span * 0.4):
        fy = front_y if abs(x) < span * 0.2 else front_y
        if sagging and x > 0:
            fy -= 0.1
        add_cylinder(m, (x, 0.0, 0.02), (x, fy, depth - 0.02), 0.012, segments=6)

    # Wall mount plates
    for x in (-span * 0.45, span * 0.45):
        add_box(m, (x, 0.0, 0.01), (0.08, 0.08, 0.02), bevel=0.002)

    return m


def _catenary(p0: Vec3, p1: Vec3, sag: float, n: int = 16) -> List[Vec3]:
    """Points along a catenary-like curve with given sag depth."""
    a = np.array(p0, dtype=float)
    b = np.array(p1, dtype=float)
    pts = []
    for i in range(n + 1):
        t = i / n
        p = a * (1 - t) + b * t
        # sag as parabola in Y (down)
        p[1] -= sag * 4.0 * t * (1 - t)
        pts.append((float(p[0]), float(p[1]), float(p[2])))
    return pts


def gen_cable(rng: random.Random, variant: int) -> Mesh:
    """Overhead cable tube. Pivot at start endpoint. Radius 15–25 mm."""
    m = Mesh(name=f"cable_{variant:02d}", material="CableBlack")
    span = rng.uniform(4.0, 12.0)
    sag = rng.uniform(0.3, 1.8)
    height_delta = rng.uniform(-0.8, 0.5)
    lateral = rng.uniform(-1.5, 1.5)
    p0 = (0.0, 0.0, 0.0)
    p1 = (span, height_delta, lateral)

    tangled = variant >= 4  # multi-strand bundle

    if not tangled:
        pts = _catenary(p0, p1, sag, n=18)
        pts = [
            (x + 0.05 * math.sin(i * 0.7 + variant), y, z + 0.04 * math.cos(i * 0.5))
            for i, (x, y, z) in enumerate(pts)
        ]
        # 15–25 mm radius so cables read at street scale
        r = rng.uniform(0.015, 0.025)
        add_tube_path(m, pts, r, segments=8)
    else:
        strands = 3 + (variant % 3)
        for s in range(strands):
            off = rng.uniform(0.04, 0.12)
            phase = s * 2.1
            sag_s = sag * rng.uniform(0.85, 1.15)
            pts = _catenary(
                (0, rng.uniform(-0.05, 0.05), rng.uniform(-0.05, 0.05)),
                (
                    span + rng.uniform(-0.3, 0.3),
                    height_delta + rng.uniform(-0.2, 0.2),
                    lateral + rng.uniform(-0.4, 0.4),
                ),
                sag_s,
                n=20,
            )
            pts = [
                (
                    x + off * math.sin(i * 0.9 + phase),
                    y + off * 0.3 * math.cos(i * 0.6 + phase),
                    z + off * math.cos(i * 0.9 + phase),
                )
                for i, (x, y, z) in enumerate(pts)
            ]
            add_tube_path(m, pts, rng.uniform(0.015, 0.022), segments=7)

        for t in (0.3, 0.5, 0.7):
            cx = span * t
            cy = -sag * 4.0 * t * (1 - t)
            add_cylinder(
                m,
                (cx, cy - 0.05, lateral * t - 0.05),
                (cx, cy + 0.05, lateral * t + 0.05),
                0.035,
                segments=6,
                capped=True,
            )

    return m


def gen_crossarm(rng: random.Random, variant: int) -> Mesh:
    """Utility pole crossarm with ceramic insulators. Pivot at pole attach centre."""
    m = Mesh(name=f"crossarm_{variant:02d}", material="WoodBrown")
    length = rng.uniform(1.4, 2.0)
    thick = rng.uniform(0.07, 0.1)
    # Main beam along X, pole attach at origin
    add_box(m, (0, 0, 0), (length, thick, thick * 0.85), bevel=0.004, uv_scale=0.8)

    # Secondary crossarm on variant 1
    if variant >= 1:
        add_box(m, (0, -0.18, 0), (length * 0.75, thick * 0.85, thick * 0.75), bevel=0.003)

    # Insulators
    n_ins = 4 + variant
    for i in range(n_ins):
        x = (i / (n_ins - 1) - 0.5) * (length * 0.85)
        # Pin
        add_cylinder(m, (x, thick * 0.5, 0), (x, thick * 0.5 + 0.08, 0), 0.01, segments=6)
        # Ceramic discs
        for k, r in enumerate((0.04, 0.055, 0.04)):
            cy = thick * 0.5 + 0.09 + k * 0.035
            add_cylinder(m, (x, cy, 0), (x, cy + 0.02, 0), r, segments=10)
        # Cap
        add_cylinder(
            m,
            (x, thick * 0.5 + 0.09 + 3 * 0.035, 0),
            (x, thick * 0.5 + 0.22, 0),
            0.015,
            segments=6,
        )

    # Pole stub (short) for context
    add_cylinder(m, (0, -0.5, 0), (0, 0.15, 0), 0.09, segments=10)
    # Band clamp
    add_box(m, (0, 0, 0), (thick * 1.4, thick * 1.3, thick * 1.4), bevel=0.002)

    # Weathered chip
    if variant >= 1:
        add_box(
            m,
            (length * 0.4, thick * 0.3, thick * 0.3),
            (0.06, 0.03, 0.03),
            bevel=0.002,
        )

    # Pivot: shift so pole centre xz=0, crossarm mid height near y=0 already
    return m


def gen_jersey_barrier(rng: random.Random, variant: int) -> Mesh:
    """Concrete jersey barrier. Pivot base-centre."""
    m = Mesh(name=f"jersey_barrier_{variant:02d}", material="Concrete")
    length = rng.uniform(1.8, 2.4)
    # Classic profile in XZ cross-section, extruded in X
    # Profile points (z, y) — wide base tapering
    profile = [
        (-0.32, 0.00),
        (-0.32, 0.08),
        (-0.18, 0.25),
        (-0.12, 0.55),
        (-0.12, 0.95),
        (0.12, 0.95),
        (0.12, 0.55),
        (0.18, 0.25),
        (0.32, 0.08),
        (0.32, 0.00),
    ]
    # Bevel top corners in profile
    damaged = variant >= 1
    heavily = variant >= 2

    nx = 6
    # Build as extruded profile with end caps
    # rings along X
    rings = []
    for i in range(nx + 1):
        t = i / nx
        x = (t - 0.5) * length
        ring = []
        for pi, (z, y) in enumerate(profile):
            zz, yy = z, y
            if damaged and y > 0.85 and abs(z) > 0.05 and t > 0.7:
                # chipped top corner
                yy -= 0.08 * ((t - 0.7) / 0.3)
                zz *= 0.7
            if heavily and 0.3 < t < 0.45 and y < 0.15:
                yy += rng.uniform(0, 0.02)
                zz += rng.uniform(-0.02, 0.02)
            # slight irregularity
            zz += rng.uniform(-0.005, 0.005)
            nrm_2d = _n(np.array([zz, yy - 0.4]))  # rough
            # Better: outward from centreline
            nrm = _n(np.array([0.0, (1 if y > 0.5 else 0.2), math.copysign(1.0, z) if abs(z) > 0.05 else 0.0]))
            ring.append(m.add_vertex((x, yy, zz), tuple(nrm), (t * length * 0.5, y)))
        rings.append(ring)

    nprof = len(profile)
    for i in range(nx):
        for j in range(nprof):
            j2 = (j + 1) % nprof
            a, b = rings[i][j], rings[i][j2]
            c, d = rings[i + 1][j2], rings[i + 1][j]
            m.add_tri(a, b, c)
            m.add_tri(a, c, d)

    # Caps
    for end, ring, sign in ((0, rings[0], -1), (1, rings[-1], 1)):
        # fan from centroid
        cx = (-0.5 if end == 0 else 0.5) * length
        cy = 0.4
        cz = 0.0
        ci = m.add_vertex((cx, cy, cz), (float(sign), 0.0, 0.0), (0.5, 0.5))
        for j in range(nprof):
            j2 = (j + 1) % nprof
            if sign < 0:
                m.add_tri(ci, ring[j2], ring[j])
            else:
                m.add_tri(ci, ring[j], ring[j2])

    # Footing chamfer
    add_box(m, (0, 0.02, 0), (length * 0.98, 0.04, 0.62), bevel=0.006)

    if damaged:
        # Chipped top corner chunk
        chip = Mesh(name="chip")
        add_box(chip, (0, 0, 0), (0.18, 0.14, 0.16), bevel=0.01)
        chip.transform(
            mat_trs(
                t=(length * 0.42, 0.92, 0.08),
                euler_deg=(rng.uniform(-20, 10), rng.uniform(-30, 30), rng.uniform(-15, 15)),
                s=(1.0, 0.7, 0.85),
            )
        )
        m.merge(chip)
    if heavily:
        add_cylinder(
            m,
            (length * 0.38, 0.88, 0.06),
            (length * 0.38 + 0.1, 1.05, 0.12),
            0.01,
            segments=6,
        )
        # Crumbled base corner
        add_box(m, (-length * 0.4, 0.06, 0.28), (0.2, 0.1, 0.12), bevel=0.008)

    mn, _ = m.bounds()
    m.translate(0, -mn[1], 0)
    return m


def gen_stall_frame(rng: random.Random, variant: int) -> Mesh:
    """Market stall pipe frame with fabric/corrugated top. Pivot base-centre."""
    m = Mesh(name=f"stall_frame_{variant:02d}", material="MetalGrey")
    w = rng.uniform(1.8, 2.6)
    d = rng.uniform(1.2, 1.8)
    h = rng.uniform(2.0, 2.5)
    r_pipe = rng.uniform(0.018, 0.028)

    # Four uprights
    feet = [
        (-w * 0.5, 0, -d * 0.5),
        (w * 0.5, 0, -d * 0.5),
        (w * 0.5, 0, d * 0.5),
        (-w * 0.5, 0, d * 0.5),
    ]
    for fx, _, fz in feet:
        add_cylinder(m, (fx, 0, fz), (fx, h, fz), r_pipe, segments=8)

    # Top rectangle
    top_y = h
    corners = [(fx, top_y, fz) for fx, _, fz in feet]
    for a, b in ((0, 1), (1, 2), (2, 3), (3, 0)):
        add_cylinder(m, corners[a], corners[b], r_pipe * 0.9, segments=6)

    # Mid rails on long sides
    mid_y = h * 0.45
    add_cylinder(m, (-w * 0.5, mid_y, -d * 0.5), (w * 0.5, mid_y, -d * 0.5), r_pipe * 0.8, segments=6)
    add_cylinder(m, (-w * 0.5, mid_y, d * 0.5), (w * 0.5, mid_y, d * 0.5), r_pipe * 0.8, segments=6)

    # Cross braces (asymmetry)
    if variant >= 1:
        add_cylinder(
            m,
            (-w * 0.5, 0.1, -d * 0.5),
            (-w * 0.5, mid_y, d * 0.5),
            r_pipe * 0.7,
            segments=6,
        )

    # Roof: fabric or corrugated
    corrugated = variant % 2 == 0
    nu, nv = (16, 8) if corrugated else (8, 6)
    slope = rng.uniform(0.05, 0.15)
    grid = []
    for j in range(nv + 1):
        v = j / nv
        row = []
        for i in range(nu + 1):
            u = i / nu
            x = (u - 0.5) * w * 1.05
            z = (v - 0.5) * d * 1.05
            y = top_y + 0.02 - abs(z) * slope
            if corrugated:
                y += math.sin(x * 12) * 0.015
            if variant == 2:
                # sagging fabric
                y -= 0.12 * math.sin(u * math.pi) * math.sin(v * math.pi)
            nrm = _n(np.array([0.0, 1.0, slope * math.copysign(1, z) if abs(z) > 0.01 else 0.0]))
            row.append(m.add_vertex((x, y, z), tuple(nrm), (u * 2, v * 2)))
        grid.append(row)
    for j in range(nv):
        for i in range(nu):
            a, b = grid[j][i], grid[j][i + 1]
            c, d_idx = grid[j + 1][i + 1], grid[j + 1][i]
            m.add_tri(a, b, c)
            m.add_tri(a, c, d_idx)

    # Bent leg on variant 2
    if variant == 2:
        # already have sag; nudge one upright via extra bent pipe
        add_cylinder(
            m,
            (w * 0.5, 0, d * 0.5),
            (w * 0.5 + 0.08, h * 0.5, d * 0.5 + 0.05),
            r_pipe,
            segments=6,
        )

    mn, _ = m.bounds()
    m.translate(0, -mn[1], 0)
    return m


def _add_broken_slab(mesh: Mesh, centre: Vec3, size: Vec3, rng: random.Random) -> None:
    """Flat broken concrete slab with a tilted shard for a jagged break."""
    tmp = Mesh(name="slab")
    add_box(tmp, (0, 0, 0), size, bevel=0.008)
    shard = Mesh(name="shard")
    add_box(shard, (0, 0, 0), (size[0] * 0.45, size[1] * 0.7, size[2] * 0.25), bevel=0.006)
    shard.transform(
        mat_trs(
            t=(size[0] * 0.3, size[1] * 0.2, size[2] * 0.2),
            euler_deg=(rng.uniform(10, 35), rng.uniform(0, 90), rng.uniform(-20, 20)),
        )
    )
    tmp.merge(shard)
    tmp.transform(
        mat_trs(
            t=centre,
            euler_deg=(rng.uniform(-25, 25), rng.uniform(0, 360), rng.uniform(-15, 15)),
        )
    )
    new_v = []
    for x, y, z in tmp.vertices:
        new_v.append(
            (x + rng.uniform(-0.015, 0.015), y + rng.uniform(-0.01, 0.01), z + rng.uniform(-0.015, 0.015))
        )
    tmp.vertices = new_v
    mesh.merge(tmp)


def _heap_height(dist: float, spread: float, peak: float) -> float:
    """Broad-base heap profile: higher toward centre, falls off to edges."""
    t = min(1.0, dist / max(spread, 1e-6))
    return peak * max(0.0, (1.0 - t * t) ** 1.1)


def gen_rubble(rng: random.Random, variant: int) -> Mesh:
    """Dense debris heap. Pivot base-centre. Variants 2–3 include bent rebar."""
    m = Mesh(name=f"rubble_{variant:02d}", material="Concrete")
    spread = 0.65 + variant * 0.12
    peak = 0.35 + variant * 0.08

    # Large slabs (anchors)
    n_slabs = 2 + min(variant, 2)
    for _ in range(n_slabs):
        ang = rng.uniform(0, 6.28)
        rad = rng.uniform(0, spread * 0.55)
        cx, cz = math.cos(ang) * rad, math.sin(ang) * rad
        cy = 0.03 + _heap_height(rad, spread, peak) * 0.25
        _add_broken_slab(
            m,
            (cx, cy, cz),
            (rng.uniform(0.35, 0.7), rng.uniform(0.04, 0.1), rng.uniform(0.22, 0.45)),
            rng,
        )

    # Medium chunks
    n_med = 16 + variant * 4
    for i in range(n_med):
        ang = rng.uniform(0, 6.28)
        rad = rng.uniform(0, spread * 0.9)
        cx, cz = math.cos(ang) * rad, math.sin(ang) * rad
        surface = _heap_height(rad, spread, peak)
        cy = rng.uniform(0.02, max(0.04, surface * 0.85))
        is_brick = i % 4 == 0
        if is_brick:
            sx, sy, sz = rng.uniform(0.16, 0.24), rng.uniform(0.05, 0.08), rng.uniform(0.08, 0.12)
        else:
            sx = rng.uniform(0.08, 0.28)
            sy = rng.uniform(0.05, 0.14)
            sz = rng.uniform(0.08, 0.26)
        tmp = Mesh(name="chunk")
        add_box(tmp, (0, 0, 0), (sx, sy, sz), bevel=0.006)
        new_v = []
        for x, y, z in tmp.vertices:
            scale = 1.0 - 0.3 * max(0, x / max(sx * 0.5, 1e-6))
            new_v.append(
                (
                    x + rng.uniform(-0.01, 0.01),
                    y * scale + rng.uniform(-0.008, 0.008),
                    z * scale + rng.uniform(-0.01, 0.01),
                )
            )
        tmp.vertices = new_v
        tmp.transform(
            mat_trs(
                t=(cx, cy, cz),
                euler_deg=(rng.uniform(-55, 55), rng.uniform(0, 360), rng.uniform(-45, 45)),
            )
        )
        m.merge(tmp)

    # Small gravel / dust fill packed into heap (simpler unbevelled boxes)
    n_gravel = 28 + variant * 8
    for _ in range(n_gravel):
        ang = rng.uniform(0, 6.28)
        rad = rng.uniform(0, spread)
        cx, cz = math.cos(ang) * rad, math.sin(ang) * rad
        surface = _heap_height(rad, spread, peak)
        cy = rng.uniform(0.005, max(0.02, surface * 0.7))
        s = rng.uniform(0.02, 0.055)
        tmp = Mesh(name="g")
        add_box(tmp, (cx, cy, cz), (s, s * rng.uniform(0.5, 1.0), s * rng.uniform(0.7, 1.2)), bevel=0.0015)
        m.merge(tmp)

    # Protruding bent rebar on denser variants
    if variant >= 2:
        for _ in range(2 + variant - 2):
            ang = rng.uniform(0, 6.28)
            rad = rng.uniform(0.05, spread * 0.4)
            cx, cz = math.cos(ang) * rad, math.sin(ang) * rad
            y0 = _heap_height(rad, spread, peak) * 0.5
            # Bent polyline tube
            p0 = (cx, y0, cz)
            p1 = (
                cx + rng.uniform(-0.05, 0.05),
                y0 + rng.uniform(0.15, 0.35),
                cz + rng.uniform(-0.05, 0.05),
            )
            p2 = (
                p1[0] + rng.uniform(0.05, 0.18),
                p1[1] + rng.uniform(-0.05, 0.12),
                p1[2] + rng.uniform(-0.1, 0.1),
            )
            add_tube_path(m, [p0, p1, p2], 0.008, segments=5)
            # Second kink on some
            if rng.random() < 0.6:
                p3 = (
                    p2[0] + rng.uniform(-0.08, 0.08),
                    p2[1] - rng.uniform(0.02, 0.1),
                    p2[2] + rng.uniform(-0.08, 0.08),
                )
                add_tube_path(m, [p2, p3], 0.007, segments=5)

    mn, _ = m.bounds()
    m.translate(0, -mn[1], 0)
    mn, mx = m.bounds()
    m.translate(-(mn[0] + mx[0]) * 0.5, 0, -(mn[2] + mx[2]) * 0.5)
    mn, _ = m.bounds()
    m.translate(0, -mn[1], 0)
    return m


# ---------------------------------------------------------------------------
# Contact sheet rendering (matplotlib)
# ---------------------------------------------------------------------------

def _mesh_preview_color(name: str) -> np.ndarray:
    if name.startswith("cable"):
        return np.array([0.85, 0.75, 0.35])  # bright so thin tubes read on dark bg
    if name.startswith("sandbag"):
        return np.array([0.72, 0.62, 0.38])
    if name.startswith("window_ac"):
        return np.array([0.82, 0.84, 0.80])
    if name.startswith("rubble"):
        return np.array([0.62, 0.60, 0.56])
    return np.array([0.75, 0.70, 0.60])


def render_contact_sheet(meshes: List[Mesh], path: Path, cols: int = 6) -> None:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from mpl_toolkits.mplot3d.art3d import Poly3DCollection

    n = len(meshes)
    cols = min(cols, max(n, 1))
    rows = math.ceil(n / cols)
    fig = plt.figure(figsize=(cols * 2.8, rows * 2.8), facecolor="#2a2a2a")

    for idx, mesh in enumerate(meshes):
        ax = fig.add_subplot(rows, cols, idx + 1, projection="3d", facecolor="#333333")
        faces = mesh.faces
        # Keep enough faces that bag courses / louvres still read
        step = max(1, len(faces) // 1600)
        mn, mx = mesh.bounds()
        sx, sy, sz = mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2]
        cx = (mn[0] + mx[0]) * 0.5
        cy = (mn[1] + mx[1]) * 0.5
        cz = (mn[2] + mx[2]) * 0.5

        # Long thin cables: crop to a mid-span window so tube radius is visible
        is_cable = mesh.name.startswith("cable")
        if is_cable and sx > 3.0:
            x0, x1 = cx - 1.2, cx + 1.2
        else:
            x0, x1 = mn[0] - 0.05, mx[0] + 0.05

        base = _mesh_preview_color(mesh.name)
        tris_u = []
        shaded = []
        for f in faces[::step]:
            pts_u = []
            skip = False
            for k in range(3):
                x, y, z = mesh.vertices[f[k][0]]
                if is_cable and sx > 3.0 and (x < x0 or x > x1):
                    skip = True
                    break
                pts_u.append([x, z, y])  # Unity Y-up → mpl Z-up
            if skip:
                continue
            tris_u.append(pts_u)
            a, b, c = np.array(pts_u[0]), np.array(pts_u[1]), np.array(pts_u[2])
            nrm = np.cross(b - a, c - a)
            ln = np.linalg.norm(nrm)
            nrm = nrm / ln if ln > 1e-12 else np.array([0.0, 0.0, 1.0])
            light = max(0.15, float(np.dot(nrm, _n(np.array([0.35, -0.45, 0.85])))))
            shaded.append(base * (0.35 + 0.65 * light))

        if tris_u:
            coll = Poly3DCollection(tris_u, linewidths=0.03, edgecolors="#222222")
            coll.set_facecolor(shaded)
            ax.add_collection3d(coll)

        # Framing: prefer height readability for walls; crop cables
        if is_cable and sx > 3.0:
            # Mid-span crop — exaggerate vertical framing slightly
            extent_x = 1.3
            extent_z = max(sy, sz, 0.35) * 0.7
            ax.set_xlim(cx - extent_x, cx + extent_x)
            ax.set_ylim(cz - extent_z, cz + extent_z)
            ax.set_zlim(cy - extent_z, cy + extent_z)
            ax.view_init(elev=18, azim=-70)
        elif mesh.name.startswith("sandbag") and sy >= 0.8:
            # Preserve real proportions so walls don't squash into pillars
            pad = 0.08
            ax.set_xlim(mn[0] - pad, mx[0] + pad)
            ax.set_ylim(mn[2] - pad, mx[2] + pad)
            ax.set_zlim(mn[1], mx[1] + pad)
            ax.view_init(elev=16, azim=-25)
        elif mesh.name.startswith("window_ac"):
            extent = max(sx, sy, sz, 0.1) * 0.55
            ax.set_xlim(cx - extent, cx + extent)
            ax.set_ylim(cz - extent, cz + extent)
            ax.set_zlim(cy - extent, cy + extent)
            ax.view_init(elev=12, azim=160)  # show grille face
        else:
            extent = max(sx, sy, sz, 0.1) * 0.55
            ax.set_xlim(cx - extent, cx + extent)
            ax.set_ylim(cz - extent, cz + extent)
            ax.set_zlim(cy - extent, cy + extent)
            ax.view_init(elev=22, azim=-55)

        h_label = f"{mesh.name}\n{sy:.2f}m H" if mesh.name.startswith("sandbag") else mesh.name
        ax.set_title(h_label, color="white", fontsize=6.5, pad=2)
        ax.set_xticks([])
        ax.set_yticks([])
        ax.set_zticks([])
        try:
            if mesh.name.startswith("sandbag") and sy >= 0.8:
                ax.set_box_aspect([max(sx, 0.2), max(sz, 0.2), max(sy, 0.2)])
            else:
                ax.set_box_aspect([1, 1, 1])
        except Exception:
            pass
        ax.xaxis.pane.fill = False
        ax.yaxis.pane.fill = False
        ax.zaxis.pane.fill = False

    fig.suptitle("Overflow procedural props — silhouette check", color="white", fontsize=12)
    fig.tight_layout()
    path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(path, dpi=150, facecolor=fig.get_facecolor())
    plt.close(fig)


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

MATERIALS = [
    ("PropDefault", (0.6, 0.6, 0.6)),
    ("MetalGrey", (0.55, 0.56, 0.58)),
    ("MetalWhite", (0.75, 0.76, 0.74)),
    ("Burlap", (0.55, 0.48, 0.32)),
    ("CorrugatedMetal", (0.45, 0.42, 0.38)),
    ("CableBlack", (0.12, 0.12, 0.12)),
    ("WoodBrown", (0.35, 0.25, 0.16)),
    ("Concrete", (0.62, 0.62, 0.58)),
]


def build_all(seed: int = 42) -> List[Tuple[Mesh, Optional[Vec3]]]:
    """Return (mesh, expected_size_hint) pairs."""
    rng = random.Random(seed)
    items: List[Tuple[Mesh, Optional[Vec3]]] = []

    for i in range(4):
        items.append((gen_satellite_dish(rng, i), (1.2, 1.2, 1.0)))
    for i in range(3):
        items.append((gen_window_ac(rng, i), (0.8, 0.5, 0.6)))
    for i in range(8):
        # Walls: expect ~1.0–1.3 m height; loose bags smaller
        hint = (2.2, 1.1, 0.8) if i >= 2 else None
        items.append((gen_sandbag(rng, i), hint))
    for i in range(4):
        items.append((gen_awning(rng, i), (3.0, 0.6, 1.5)))
    for i in range(6):
        items.append((gen_cable(rng, i), None))
    for i in range(2):
        items.append((gen_crossarm(rng, i), (2.0, 0.8, 0.4)))
    for i in range(3):
        items.append((gen_jersey_barrier(rng, i), (2.2, 1.0, 0.7)))
    for i in range(3):
        items.append((gen_stall_frame(rng, i), (2.6, 2.5, 1.8)))
    for i in range(4):
        items.append((gen_rubble(rng, i), None))

    return items


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Generate Overflow procedural prop OBJs")
    parser.add_argument("--out", type=Path, default=OUT_DIR)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--skip-render", action="store_true")
    args = parser.parse_args(argv)

    out: Path = args.out
    out.mkdir(parents=True, exist_ok=True)

    write_mtl(out / "props.mtl", MATERIALS)

    items = build_all(args.seed)
    results: List[ValidationResult] = []
    meshes: List[Mesh] = []
    manifest_rows: List[str] = []

    print(f"Generating {len(items)} props → {out}")

    for mesh, expected in items:
        obj_path = out / f"{mesh.name}.obj"
        write_obj(mesh, obj_path, "props.mtl")

        vr = validate_mesh(mesh, expected_size=expected)
        parse_errs = parse_obj_check(obj_path)
        vr.errors.extend(parse_errs)
        vr.ok = len(vr.errors) == 0
        results.append(vr)
        meshes.append(mesh)

        sx, sy, sz = vr.size
        status = "OK" if vr.ok else "FAIL"
        print(f"  [{status}] {mesh.name:22s}  tris={vr.tris:4d}  "
              f"bbox=({sx:.2f} × {sy:.2f} × {sz:.2f} m)")
        if vr.errors:
            for e in vr.errors:
                print(f"         ! {e}")

        manifest_rows.append(
            f"| `{mesh.name}.obj` | {vr.tris} | {sx:.3f} × {sy:.3f} × {sz:.3f} m | "
            f"{'pass' if vr.ok else 'FAIL: ' + '; '.join(vr.errors)} |"
        )

    if not args.skip_render:
        print(f"Rendering contact sheet → {CONTACT_SHEET}")
        render_contact_sheet(meshes, out / "_contactsheet.png")

    # MANIFEST
    by_cat: dict[str, int] = {}
    for mesh in meshes:
        cat = mesh.name.rsplit("_", 1)[0]
        by_cat[cat] = by_cat.get(cat, 0) + 1

    lines = [
        "# Generated Overflow Props",
        "",
        "Procedural Wavefront OBJ meshes for a Peshawar-style market street "
        "(BO2 Overflow reference). Units: metres. Companion MTL: `props.mtl`.",
        "",
        "## Counts by category",
        "",
    ]
    for cat, n in sorted(by_cat.items()):
        lines.append(f"- **{cat}**: {n}")
    lines += [
        "",
        f"**Total meshes:** {len(meshes)}",
        "",
        "## Mesh list",
        "",
        "| File | Tris | Bounding box (W×H×D) | Validation |",
        "|---|---:|---|---|",
    ]
    lines.extend(manifest_rows)
    lines += [
        "",
        "## Pivot conventions",
        "",
        "- Ground props (sandbag, jersey, stall, rubble): base centre, Y-up.",
        "- Wall-mounted (sat dish, window AC, awning): mount point at origin.",
        "- Cables: start endpoint at origin.",
        "- Crossarm: pole centre attach at origin.",
        "",
        "## Contact sheet",
        "",
        "See `_contactsheet.png` for silhouette preview of every variant.",
        "",
    ]
    (out / "MANIFEST.md").write_text("\n".join(lines) + "\n", encoding="utf-8")

    failed = [r for r in results if not r.ok]
    print(f"\nDone. {len(results) - len(failed)}/{len(results)} passed validation.")
    if failed:
        print("Failures:")
        for r in failed:
            print(f"  - {r.name}: {r.errors}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
