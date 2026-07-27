# Tool index — ai-game-developer (191 tools)

Grouped catalog of every tool registered in the connected Unity Editor. Per-tool argument schemas live in `.cursor/skills/<tool-name>/SKILL.md`.

Regenerate/verify with `unity-tool-list` (`regexSearch` filters across names, descriptions, and arguments).

## Scene

| Tool | Purpose |
|---|---|
| `scene-create` | Create and save a new `.unity` scene asset |
| `scene-open` | Open a scene (Single or Additive) |
| `scene-list-opened` | Shallow list of opened scenes |
| `scene-get-data` | Root GameObjects of a scene (supports `paths` / `viewQuery`) |
| `scene-set-active` | Mark an opened scene active |
| `scene-save` | Save an opened scene (optionally to a new path) |
| `scene-unload` | Unload an opened scene |

## GameObjects & components

| Tool | Purpose |
|---|---|
| `gameobject-create` | Create empty or primitive GameObject, optionally parented/positioned |
| `gameobject-find` | Find GameObject in open prefab or active scene; children + component preview |
| `gameobject-modify` | Modify GameObject fields/props (diff, pathPatches, jsonPatch), batchable |
| `gameobject-duplicate` | Duplicate a batch of GameObjects |
| `gameobject-set-parent` | Reparent a batch |
| `gameobject-destroy` | Destroy GameObject and children |
| `gameobject-component-add` | Add components by type name |
| `gameobject-component-get` | Inspect one component's serialized state |
| `gameobject-component-modify` | Modify a component (diff, pathPatches, jsonPatch) |
| `gameobject-component-destroy` | Remove components |
| `gameobject-component-list-all` | Paginated list of all concrete `Component` subclasses |
| `object-get-data` / `object-modify` | Generic read/write for any `UnityEngine.Object` |
| `editor-selection-get` / `editor-selection-set` | Editor Selection |

## Assets & prefabs

| Tool | Purpose |
|---|---|
| `assets-find` | Search AssetDatabase (`t:`, `l:`, `b:`, `a:`, `glob:`) |
| `assets-find-built-in` | Search Unity built-in resources |
| `assets-get-data` | Full or path-scoped serialization of an asset |
| `assets-modify` | Modify an asset (content / pathPatches / jsonPatch) |
| `assets-create-folder` / `assets-copy` / `assets-move` / `assets-delete` | Project file operations |
| `assets-refresh` | AssetDatabase refresh; forces script recompile |
| `assets-material-create` | New material from a shader name |
| `assets-shader-list-all` / `assets-shader-get-data` | Shader discovery and introspection |
| `assets-prefab-create` | Prefab or prefab variant from a scene object or existing prefab |
| `assets-prefab-instantiate` | Instantiate prefab into active scene |
| `assets-prefab-open` / `assets-prefab-save` / `assets-prefab-close` | Prefab edit stage |

## Scripting & reflection

| Tool | Purpose |
|---|---|
| `script-read` | Read a `.cs` file (supports line ranges) |
| `script-update-or-create` | Write C# with Roslyn validation; waits for compilation |
| `script-delete` | Delete scripts, refresh, wait for compile |
| `script-execute` | Compile and run arbitrary C# (full-class or `isMethodBody` mode) |
| `reflection-method-find` | Locate any method (incl. private) across loaded assemblies |
| `reflection-method-call` | Invoke a found method with parameters |
| `type-get-json-schema` | JSON Schema for any C# type |
| `tests-run` | Run EditMode/PlayMode tests with filters |

## ProBuilder (level geometry)

| Tool | Purpose |
|---|---|
| `probuilder-create-shape` | Primitive editable mesh (Cube, Cylinder, Stair, Prism, …) |
| `probuilder-create-poly-shape` | Extrude a 2D outline into 3D — floor plans, walls, platforms |
| `probuilder-extrude` | Extrude faces by index or `faceDirection` |
| `probuilder-bevel` | Chamfer edges |
| `probuilder-bridge` | New face connecting two edges |
| `probuilder-connect-edges` | Insert edge loops |
| `probuilder-subdivide-edges` | Add vertices along edges |
| `probuilder-delete-faces` | Remove faces / cut openings |
| `probuilder-flip-normals` | Invert faces (interior spaces) |
| `probuilder-set-face-material` | Multi-material meshes |
| `probuilder-set-pivot` | Move origin without moving geometry |
| `probuilder-merge-objects` | Combine meshes (draw-call optimization) |
| `probuilder-get-mesh-info` | Face/vertex/edge data (`summary` or `full`) |

## Terrain

`terrain-create`, `terrain-list`, `terrain-get`, `terrain-set-size`, `terrain-set-heightmap-resolution`, `terrain-set-heights`, `terrain-sample-heights`, `terrain-add-layer`, `terrain-remove-layer`, `terrain-paint-layer`, `terrain-set-tree-prototypes`, `terrain-place-trees`, `terrain-set-detail-prototypes`, `terrain-set-neighbors`, `terrain-get-component`, `terrain-modify-component`

## Animation

| Tool | Purpose |
|---|---|
| `animation-create` | New empty `.anim` clips |
| `animation-get-data` | Clip length, frame rate, curves, events, bindings |
| `animation-modify` | Set/remove curves, frame rate, wrap mode, animation events |
| `animator-create` | New `.controller` assets |
| `animator-get-data` | Parameters, layers, states, transitions |
| `animator-modify` | Add/remove parameters, layers, states, transitions; set default state, motion, speed |

## Cinemachine

`cinemachine-brain-ensure`, `cinemachine-camera-create`, `cinemachine-camera-list`, `cinemachine-camera-get`, `cinemachine-set-body`, `cinemachine-set-aim`, `cinemachine-set-lens`, `cinemachine-set-noise`, `cinemachine-set-priority`, `cinemachine-set-targets`, `cinemachine-set-default-blend`, `cinemachine-add-extension`, `cinemachine-get`, `cinemachine-modify`

## Timeline

`timeline-create`, `timeline-list`, `timeline-track-add`, `timeline-track-list`, `timeline-track-remove`, `timeline-track-bind`, `timeline-clip-add`, `timeline-clip-move`, `timeline-clip-set-timing`, `timeline-marker-add`, `timeline-director-bind`, `timeline-get`, `timeline-modify`

## Input System

`inputsystem-asset-create`, `inputsystem-get`, `inputsystem-actionmap-add`, `inputsystem-actionmap-remove`, `inputsystem-action-add`, `inputsystem-action-remove`, `inputsystem-binding-add`, `inputsystem-binding-composite-add`, `inputsystem-binding-set`, `inputsystem-binding-remove`, `inputsystem-controlscheme-add`, `inputsystem-modify`, `inputsystem-save`

## Navigation (AI)

`navigation-surface-add`, `navigation-surface-bake`, `navigation-set-bake-settings`, `navigation-agent-add`, `navigation-agent-set-destination`, `navigation-link-add`, `navigation-modifier-add`, `navigation-modifier-volume-add`, `navigation-list`, `navigation-get`, `navigation-modify`

## Particles

`particle-system-get`, `particle-system-modify` — opt-in access to ~24 modules (Main, Emission, Shape, Velocity, Noise, Collision, Trails, Renderer, Sub-Emitters, Lights, …).

## Splines

`splines-container-create`, `splines-add-spline`, `splines-list`, `splines-add-knot`, `splines-insert-knot`, `splines-set-knot`, `splines-remove-knot`, `splines-get-knots`, `splines-set-tangent-mode`, `splines-set-closed`, `splines-evaluate`, `splines-get`, `splines-modify`

## Tilemap (2D)

`tilemap-create`, `tilemap-list`, `tilemap-create-tile-asset`, `tilemap-create-rule-tile`, `tilemap-set-tile`, `tilemap-get-tile`, `tilemap-box-fill`, `tilemap-clear`, `tilemap-set-tile-flags`, `tilemap-set-collider-type`, `tilemap-set-orientation`, `tilemap-get`, `tilemap-modify`

## Packages

`package-search`, `package-add`, `package-list`, `package-remove` — installs/removals trigger a domain reload; the result arrives after the reload.

## Editor state, logs, screenshots

| Tool | Purpose |
|---|---|
| `editor-application-get-state` | Play mode, pause, compilation state |
| `editor-application-set-state` | Enter/exit/pause play mode (blocked while compile errors exist) |
| `console-get-logs` | Editor logs, filterable by type and time window |
| `console-clear-logs` | Clear log cache + Console window to isolate an action |
| `screenshot-game-view` | Game View render texture, upright, at current resolution |
| `screenshot-camera` | Render from any Camera (defaults to `Camera.main`) |
| `screenshot-scene-view` | Scene View at a requested size |
| `screenshot-isolated` | Isolate a GameObject: layer culling, custom background, lighting JSON, 2x2 composite turnaround |

## Profiler

`profiler-start`, `profiler-stop`, `profiler-get-status`, `profiler-capture-frame`, `profiler-get-rendering-stats`, `profiler-get-memory-stats`, `profiler-get-script-stats`, `profiler-list-modules`, `profiler-enable-module`, `profiler-save-data`, `profiler-load-data`, `profiler-clear-data`

## Meta

`unity-tool-list` (list/filter registered tools), `tool-set-enabled-state` (enable/disable tools in batch).
