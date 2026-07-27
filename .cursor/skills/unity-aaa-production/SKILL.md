---
name: unity-aaa-production
description: Master guide to the ai-game-developer Unity MCP toolchain (191 live Editor tools) for building and shipping AAA-quality games in this project. Covers the capability map, the get-inspect-modify-verify loop, production phases from blockout to polish, and per-domain playbooks. Use when doing any Unity work here — scenes, prefabs, scripts, level geometry, animation, AI, cameras, input, VFX, terrain, timeline, profiling, or screenshots.
---

# Unity AAA Production

This project is wired to a live Unity Editor through the `ai-game-developer` MCP server: **191 tools** that read and write the actual Editor, not files on disk. Scene graph, prefabs, assets, C# compilation, play mode, profiler, and the render output are all directly addressable.

Treat the Editor as the source of truth. Never hand-edit `.unity`, `.prefab`, `.controller`, `.asset`, or `.meta` YAML — use the tools, or the Editor's own state will diverge and the AssetDatabase will fight you.

Project: `Urban Arena FPS` — Unity 6.5, URP Forward+, main scene `Assets/_Project/Scenes/Arena.unity`, scripts under `Assets/_Project/Scripts/{AI,Ballistics,Combat,Core,Feedback,Input,Player,UI,Weapons,World,Audio}`.

## The loop

Every non-trivial change follows the same four beats. Skipping the read or the verify is how sessions go sideways.

1. **Orient** — `scene-list-opened`, `scene-get-data`, `gameobject-find`, `assets-find`. Know what exists before creating anything.
2. **Inspect** — `*-get` / `*-get-data` on the exact object you're about to change. Never write a diff blind; field names and nesting are rarely what you'd guess.
3. **Modify** — the matching `*-modify` / `*-add` / `*-create` tool.
4. **Verify** — `console-get-logs` for errors, `screenshot-game-view` / `screenshot-scene-view` / `screenshot-isolated` to actually *look* at the result, `tests-run` for logic. Then `scene-save` or `assets-prefab-save`.

You have eyes. Use them. After any visual change, take a screenshot and judge it like an art director — silhouette, contrast, framing, scale, lighting. That feedback loop is the single biggest lever on final quality.

## Golden rules

- **Prefab-first.** Anything that appears more than once is a prefab. Edit via `assets-prefab-open` → change → `assets-prefab-save` → `assets-prefab-close`, so every instance inherits. Editing instances in-scene creates override drift.
- **Get before modify.** `gameobject-component-get`, `assets-get-data`, `animator-get-data`, etc. return the exact serialized shape. Use `paths` or `viewQuery` to read a subtree instead of dumping the whole object — most of these serializations are enormous.
- **Prefer `jsonPatch` / `pathPatches` over full diffs.** Path syntax: `field`, `nested/field`, `array/[0]`, `dict/[key]`. Cheaper, atomic, and far less likely to clobber unrelated fields than a whole-object `content` override.
- **Fields vs props are separate channels.** ReflectorNet does not fall back between them. A C# field goes through `fields`, a property through `props`. Wrong channel = silent no-op.
- **Scripts compile.** `script-update-or-create` validates with Roslyn and waits for the domain reload. After it returns, check `console-get-logs` for compile errors before touching anything that depends on the new type.
- **Save scenes before `tests-run`.** A dirty scene aborts the run.
- **`script-execute` is the escape hatch.** Anything the 191 tools don't cover — batch operations, lightmap settings, custom importer tweaks, bulk asset surgery — write it as C# and run it. Body-only mode (`isMethodBody=true`) skips the boilerplate. This is what makes the toolchain complete rather than merely broad.
- **`reflection-method-find` + `reflection-method-call`** reach private Editor internals when even that isn't enough.

## Capability map

Full catalog with one-line descriptions: [tool-index.md](tool-index.md). Per-tool schemas live in the 191 sibling skills under `.cursor/skills/<tool-name>/SKILL.md` — read one when you need exact argument shapes.

| Domain | Core tools | What it unlocks |
|---|---|---|
| Scene & hierarchy | `scene-*`, `gameobject-*`, `editor-selection-*` | Level assembly, streaming setup, additive scenes |
| Prefabs & assets | `assets-*` (find, create-folder, copy, move, delete, get-data, modify, material-create, prefab-*) | The content pipeline, prefab variants, materials |
| Code | `script-read`, `script-update-or-create`, `script-delete`, `script-execute`, `reflection-*`, `tests-run` | Gameplay systems, editor tooling, arbitrary automation |
| Level geometry | `probuilder-*` (create-shape, create-poly-shape, extrude, bevel, bridge, subdivide, connect-edges, delete-faces, flip-normals, set-face-material, set-pivot, merge-objects) | Full greybox → finished blockout without leaving the agent |
| Terrain | `terrain-*` (create, set-heights, sample-heights, add-layer, paint-layer, place-trees, detail-prototypes, neighbors) | Outdoor environments, heightmap sculpting, foliage |
| Animation | `animation-*`, `animator-*` | Clips, curves, events, state machines, parameters, transitions, blend logic |
| Cameras | `cinemachine-*` (camera-create, set-body, set-aim, set-lens, set-noise, set-priority, set-targets, add-extension, brain-ensure, set-default-blend) | Game feel: recoil shake, ADS blends, deocclusion, cinematic cuts |
| Cinematics | `timeline-*` (create, track-add, clip-add, clip-set-timing, marker-add, director-bind, track-bind) | Cutscenes, scripted set pieces, signal-driven events |
| Input | `inputsystem-*` (asset-create, actionmap-add, action-add, binding-add, binding-composite-add, binding-set, controlscheme-add) | Rebindable KBM + gamepad schemes |
| AI & navigation | `navigation-*` (surface-add, surface-bake, agent-add, agent-set-destination, link-add, modifier-add, modifier-volume-add, set-bake-settings) | Enemy pathing, cover volumes, traversal links |
| VFX | `particle-system-get/modify` (~24 modules), `assets-material-create`, `assets-shader-*` | Muzzle flashes, impacts, blood, smoke, environmental atmosphere |
| Splines | `splines-*` | Patrol routes, camera rails, procedural placement paths |
| 2D | `tilemap-*` | UI maps, 2D minigames, rule tiles |
| Packages | `package-search`, `package-add`, `package-list`, `package-remove` | Pull in new capability mid-session (domain reload aware) |
| Verification | `screenshot-camera`, `screenshot-game-view`, `screenshot-scene-view`, `screenshot-isolated`, `console-get-logs`, `console-clear-logs`, `tests-run` | The feedback loop |
| Runtime | `editor-application-get-state`, `editor-application-set-state` | Enter/exit play mode to test behavior for real |
| Performance | `profiler-*` (start, capture-frame, memory-stats, rendering-stats, script-stats, save-data) | Frame budget enforcement |

## Production phases

Work in this order. Each phase ends with a verification gate; do not carry unverified work forward.

**1. Blockout.** `probuilder-create-poly-shape` for floor plans, `probuilder-extrude` for walls and volumes, greybox material via `assets-material-create`. Play the space before any art goes in. Gate: `screenshot-scene-view` from three angles + walk it in play mode.

**2. Systems.** `script-update-or-create` for gameplay code, `inputsystem-*` for controls, `navigation-*` for AI traversal. Keep systems in the existing `Assets/_Project/Scripts/*` folders. Gate: `tests-run` (EditMode for speed) + clean `console-get-logs`.

**3. Content.** Prefabs for weapons, enemies, props, pickups. `animator-*` for state machines, `animation-*` for clips and events (footsteps, shell ejection, hit frames). Gate: `screenshot-isolated` on each prefab, composite mode for a turnaround.

**4. Feel.** This is where AAA separates from asset-flip. `cinemachine-set-noise` for recoil and impact shake, `cinemachine-set-body/aim` for ADS and hip-fire camera states with a blend between them, `particle-system-modify` for muzzle flash and impact response, animation events driving audio. Gate: play mode + screenshots mid-action.

**5. Set pieces.** `timeline-*` for scripted moments, `splines-*` for rails and patrols, `timeline-marker-add` signals to fire gameplay events from the timeline.

**6. Polish & perf.** Lighting and post via `object-modify` on the URP volume profile, then `profiler-start` → play → `profiler-get-rendering-stats` / `profiler-get-memory-stats`. Target 60fps minimum on the M1 Pro this project is tuned for; investigate anything over 16ms frame time. Gate: profiler numbers recorded, screenshots at final quality.

## Token discipline

These tools can return enormous payloads. Serializing a whole scene or a deep component tree will eat the context window fast.

- Use `paths` / `viewQuery` on every `*-get*` call that supports it.
- `gameobject-find` with `includeData` off unless you need the serialization.
- `probuilder-get-mesh-info` with `detail="summary"`, or skip it entirely — `faceDirection="up"` semantic selection usually removes the need.
- `gameobject-component-list-all` is paginated; filter rather than paging through everything.
- `unity-tool-list` accepts a `regexSearch` filter.

## Common pitfalls

- Modifying a prefab **instance** when you meant the **asset** — check whether a prefab stage is open; `gameobject-find` prefers the open prefab over the scene.
- Writing to a component before reading it, then wondering why the diff silently did nothing (usually fields-vs-props).
- Forgetting `assets-refresh` after touching files outside the Unity API.
- Running `tests-run` with a dirty scene.
- Assuming a script change took effect before the domain reload finished.
- Baking a NavMesh before the geometry is final, or with a `NavMeshSurface` whose collect volume doesn't cover the level.
- Leaving a prefab stage open — `assets-prefab-close` when done, or later scene operations target the wrong context.

## Playbooks

Concrete step-by-step recipes for the recurring jobs — weapon prefab, enemy with NavMesh AI, ProBuilder level blockout, animator state machine, input scheme, camera feel, cutscene, VFX, performance pass: [playbooks.md](playbooks.md).

## CLI fallback

Every tool is also reachable outside MCP:

```bash
unity-mcp-cli run-tool <tool-name> --input '{"key": "value"}'
```

For multi-line payloads (C# source, JSON blobs) pipe via stdin: `--input-file -` with a heredoc. Install with `npm install -g unity-mcp-cli` or prefix `npx`.
