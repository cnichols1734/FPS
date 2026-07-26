"""
Headless turntable renders for weapon/character critic loop.
Usage:
  blender --background model.blend --python ArtSrc/pipeline/turntable.py -- /tmp/out 8 CYCLES
"""
from __future__ import annotations

import math
import os
import sys
from pathlib import Path

import bpy


def argv_after_double_dash() -> list[str]:
    if "--" in sys.argv:
        return sys.argv[sys.argv.index("--") + 1 :]
    return []


def setup_camera_light() -> None:
    # Camera
    bpy.ops.object.camera_add(location=(0.45, -0.7, 0.25))
    cam = bpy.context.active_object
    cam.name = "TurntableCamera"
    bpy.context.scene.camera = cam
    # Point at origin
    direction = -cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()

    bpy.ops.object.light_add(type="AREA", location=(0.6, -0.4, 0.8))
    light = bpy.context.active_object
    light.data.energy = 80
    light.data.size = 0.6

    bpy.ops.object.light_add(type="AREA", location=(-0.5, 0.3, 0.4))
    fill = bpy.context.active_object
    fill.data.energy = 25
    fill.data.size = 0.8


def main() -> None:
    args = argv_after_double_dash()
    out_dir = Path(args[0]) if args else Path("/tmp/arena_turntable")
    frames = int(args[1]) if len(args) > 1 else 8
    engine = args[2] if len(args) > 2 else "BLENDER_EEVEE"

    out_dir.mkdir(parents=True, exist_ok=True)
    setup_camera_light()

    scene = bpy.context.scene
    scene.render.engine = engine
    scene.render.resolution_x = 1024
    scene.render.resolution_y = 1024
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False

    # Find a root mesh to orbit
    targets = [o for o in scene.objects if o.type == "MESH"]
    root = targets[0] if targets else None

    for i in range(frames):
        angle = (2.0 * math.pi * i) / frames
        if root:
            root.rotation_euler[2] = angle
        scene.render.filepath = str(out_dir / f"frame_{i:02d}.png")
        bpy.ops.render.render(write_still=True)
        print(f"[turntable] wrote {scene.render.filepath}")

    print(f"[turntable] done — {frames} frames in {out_dir}")


if __name__ == "__main__":
    main()
