"""Predict where the M4 viewmodel ends up on screen, straight from the FBX on disk.

Unity cannot be driven headlessly while the editor holds the project lock, so this
replays the same arithmetic the runtime does — FBX import, M4ViewmodelBuilder.Normalize,
then the hip and ADS poses from ViewmodelMotion — and checks the result against the
camera frustum. Run it after any change to the gun asset or the pose offsets.
"""
from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from fbx_dump import parse, prop70  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FBX = os.path.join(REPO, "Assets/_Project/Resources/Weapons/M4_Viewmodel.fbx")

# Must mirror M4ViewmodelBuilder / ViewmodelMotion.
TARGET_LENGTH = 0.52
HIP_OFFSET = (0.145, -0.105, 0.06)
ADS_SIGHT_DISTANCE = 0.18
ADS_VERTICAL_BIAS = 0.004

# Must mirror CreatePlayerPrefab.
NEAR_CLIP = 0.05
FOV_VERTICAL = 75.0
ASPECT = 16.0 / 9.0

# Unity's "Convert Units" on a Blender-exported FBX: the file is centimetres.
UNIT_SCALE = 0.01


def load():
    """Mesh bounds and anchors in Unity local space, metres."""
    _, root = parse(FBX)
    objects = root.find("Objects")

    models, geoms = {}, {}
    for m in objects.find_all("Model"):
        raw = m.props[1]
        models[m.props[0]] = (raw.split("\x00")[0] if isinstance(raw, str) else str(raw), m)
    for g in objects.find_all("Geometry"):
        geoms[g.props[0]] = g

    geom_of = {}
    for c in root.find("Connections").find_all("C"):
        if c.props and c.props[0] == "OO" and c.props[1] in geoms:
            geom_of[c.props[2]] = c.props[1]

    lo = [math.inf] * 3
    hi = [-math.inf] * 3
    anchors = {}

    for uid, (name, m) in models.items():
        t = prop70(m, "Lcl Translation") or [0.0, 0.0, 0.0]
        # FBX is right-handed with -Z forward; Unity flips Z to go left-handed.
        anchors[name] = (t[0] * UNIT_SCALE, t[1] * UNIT_SCALE, -t[2] * UNIT_SCALE)

        gid = geom_of.get(uid)
        if not gid:
            continue
        verts = geoms[gid].find("Vertices").props[0]
        for i in range(0, len(verts), 3):
            p = (
                (verts[i] + t[0]) * UNIT_SCALE,
                (verts[i + 1] + t[1]) * UNIT_SCALE,
                -(verts[i + 2] + t[2]) * UNIT_SCALE,
            )
            for a in range(3):
                lo[a] = min(lo[a], p[a])
                hi[a] = max(hi[a], p[a])

    return tuple(lo), tuple(hi), anchors


def normalize(lo, hi, anchors):
    """M4ViewmodelBuilder.Normalize: uniform scale to length, re-origin onto the contract."""
    length = hi[2] - lo[2]
    scale = TARGET_LENGTH / length
    axis = anchors["SightAlign"]
    offset = (-axis[0] * scale, -axis[1] * scale, -lo[2] * scale)
    return scale, offset, length


def place(point, scale, offset, pose):
    return tuple(pose[a] + offset[a] + point[a] * scale for a in range(3))


def visible(point, label, problems):
    """Is the point inside the camera frustum? Camera sits at the CameraPivot origin."""
    x, y, z = point
    if z < NEAR_CLIP:
        return f"{label:12} ({x:+.3f},{y:+.3f},{z:+.3f})  CLIPPED (z < near {NEAR_CLIP})"
    half_h = z * math.tan(math.radians(FOV_VERTICAL / 2))
    half_w = half_h * ASPECT
    inside = abs(y) <= half_h and abs(x) <= half_w
    if not inside:
        problems.append(f"{label} is outside the frustum at {point}")
    sx = x / half_w
    sy = y / half_h
    return (f"{label:12} ({x:+.3f},{y:+.3f},{z:+.3f})  "
            f"screen ({sx:+.2f},{sy:+.2f})  {'on screen' if inside else 'OFF SCREEN'}")


def report(name, pose, scale, offset, lo, hi, anchors, problems):
    print(f"\n--- {name}  WeaponRoot.localPosition = "
          f"({pose[0]:+.3f}, {pose[1]:+.3f}, {pose[2]:+.3f}) ---")

    body_lo = place(lo, scale, offset, pose)
    body_hi = place(hi, scale, offset, pose)
    print(f"  gun body     x[{body_lo[0]:+.3f},{body_hi[0]:+.3f}] "
          f"y[{body_lo[1]:+.3f},{body_hi[1]:+.3f}] "
          f"z[{body_lo[2]:+.3f},{body_hi[2]:+.3f}]")

    for anchor in ("SightAlign", "Muzzle"):
        print("  " + visible(place(anchors[anchor], scale, offset, pose), anchor, problems))

    if body_hi[2] < NEAR_CLIP:
        problems.append(f"{name}: the entire gun is behind the near plane — nothing renders")
    if body_lo[1] > 0.0:
        problems.append(f"{name}: the whole gun sits above the eye line")


def main():
    lo, hi, anchors = load()
    scale, offset, raw_length = normalize(lo, hi, anchors)

    print(f"imported gun length {raw_length:.4f} m  ->  normalize scale {scale:.4f}  "
          f"(target {TARGET_LENGTH} m)")
    print(f"post-normalize offset ({offset[0]:+.4f}, {offset[1]:+.4f}, {offset[2]:+.4f})")

    sight = place(anchors["SightAlign"], scale, offset, (0, 0, 0))
    print(f"sight in WeaponRoot space ({sight[0]:+.4f}, {sight[1]:+.4f}, {sight[2]:+.4f})  "
          f"— x and y must be ~0 for the pose contract")

    problems = []
    if abs(sight[0]) > 1e-4 or abs(sight[1]) > 1e-4:
        problems.append("sight optical axis does not pass through the pivot")

    report("HIP", HIP_OFFSET, scale, offset, lo, hi, anchors, problems)

    # ViewmodelMotion.ConfigureIronSightAds, with the camera at the CameraPivot origin.
    ads_pose = (
        -sight[0],
        -ADS_VERTICAL_BIAS - sight[1],
        ADS_SIGHT_DISTANCE - sight[2],
    )
    report("ADS", ads_pose, scale, offset, lo, hi, anchors, problems)

    ring_radius = (hi[0] - lo[0]) * 0.5 * scale
    subtend = math.degrees(math.atan2(ring_radius, ADS_SIGHT_DISTANCE)) * 2
    print(f"\naperture subtends ~{subtend:.1f} deg of a {FOV_VERTICAL:.0f} deg view "
          f"({subtend / FOV_VERTICAL * 100:.0f}% of screen height)")

    if problems:
        print("\nPROBLEMS:")
        for p in problems:
            print("  - " + p)
        return 1
    print("\nAll checks passed: gun is on screen at the hip and centred at ADS.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
