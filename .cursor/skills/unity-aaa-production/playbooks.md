# Playbooks

Step-by-step recipes for the recurring jobs in this project. Each one assumes the loop from `SKILL.md`: orient, inspect, modify, verify.

## Level blockout (ProBuilder)

1. `probuilder-create-poly-shape` with the room outline as `[[x,z], …]` and a `height` — one call per room or corridor. Floor plans beat stacking cubes.
2. Carve openings: `probuilder-delete-faces` with `faceDirection`, then `probuilder-bridge` to close gaps.
3. Detail passes: `probuilder-extrude` for pilasters and ledges, `probuilder-bevel` on silhouette edges, `probuilder-connect-edges` where you need geometry to push around.
4. Interiors from a solid: `probuilder-flip-normals` on the shell.
5. Greybox material: `assets-material-create` (Universal Render Pipeline/Lit), then `probuilder-set-face-material` — different values for floor / wall / ceiling so you can read the space.
6. `probuilder-set-pivot` to `Center` before making prefabs; `probuilder-merge-objects` for static clusters once the layout is locked.
7. Verify: `screenshot-scene-view` from three angles, then play mode for traversal and sightlines.

Semantic `faceDirection` ("up", "forward", …) usually removes the need to call `probuilder-get-mesh-info` at all.

## Weapon prefab

1. `gameobject-create` root → `gameobject-component-add` for the weapon script, `AudioSource`, and a muzzle child transform.
2. Parent the FBX model and the muzzle VFX under the root (`gameobject-set-parent`).
3. `animator-create` a viewmodel controller; `animator-modify` to add states (Idle, Fire, Reload, ADS_In, ADS_Out) and the parameters that drive them.
4. `animation-modify` to add animation events on the clips — shell eject, mag drop, audio cues. Events are what make the weapon feel mechanical rather than floaty.
5. `particle-system-modify` on the muzzle flash: short Main duration, burst Emission, small Shape cone, and Lights for the flash pop.
6. `assets-prefab-create` at `Assets/_Project/Prefabs/Weapons/…`.
7. Verify: `screenshot-isolated` with composite mode for a turnaround, then play mode and fire it.

## Enemy with NavMesh AI

1. `navigation-surface-add` on a level root; set collection mode and the box volume to cover the playable area.
2. `navigation-set-bake-settings` for the agent radius/height/step/slope that match the character.
3. `navigation-surface-bake` — after geometry is final, not before.
4. `navigation-modifier-volume-add` to mark no-go or high-cost zones; `navigation-link-add` for vaults, drops, and ladders.
5. Enemy prefab: `navigation-agent-add` + the AI script from `Assets/_Project/Scripts/AI` + an `Animator` driven by agent velocity.
6. `navigation-list` to confirm agents are on the mesh; `navigation-agent-set-destination` to smoke-test pathing in play mode.

## Animator state machine

1. `animator-get-data` first — always. State and parameter names must match exactly.
2. `animator-modify` batches: add parameters, then states with their motions, then transitions, then set the default state.
3. Locomotion: drive a blend via float parameters (Speed, Strafe) rather than a web of bool transitions.
4. Upper-body actions (fire, reload) belong on a second layer with an avatar mask, so they compose with locomotion instead of interrupting it.
5. Verify in play mode; `console-get-logs` catches missing-parameter warnings.

## Input scheme

1. `inputsystem-get` on the existing `.inputactions` asset before adding anything.
2. `inputsystem-action-add` for the action (type + expectedControlType), then `inputsystem-binding-add` for simple bindings.
3. `inputsystem-binding-composite-add` for WASD (`2DVector`) and any 1D axes.
4. `inputsystem-controlscheme-add` for Keyboard&Mouse and Gamepad, with device requirements; tag bindings with matching `groups`.
5. `inputsystem-save`, then verify in play mode with both devices.

## Camera feel (Cinemachine)

1. `cinemachine-brain-ensure` on the main camera.
2. One `cinemachine-camera-create` per state: hip-fire, ADS, sprint, death. Switch by `cinemachine-set-priority` at runtime.
3. `cinemachine-set-lens` — narrower FOV for ADS is the single most-read cue that aiming engaged.
4. `cinemachine-set-noise` (`CinemachineBasicMultiChannelPerlin`) for recoil and impact shake; drive `AmplitudeGain` from code and decay it.
5. `cinemachine-set-default-blend` short (0.1–0.2s EaseInOut) for weapon states, longer for cinematic cuts.
6. `cinemachine-add-extension` `CinemachineDeoccluder` for third-person or death cams.

## Cutscene / set piece

1. `timeline-create` the `.playable`, `timeline-director-bind` it to a scene object.
2. `timeline-track-add` per element: Animation for characters, Activation for props, Audio for the mix, Signal for gameplay hooks.
3. `timeline-track-bind` each output track to its Animator / GameObject.
4. `timeline-clip-add` then `timeline-clip-set-timing` for blends and eases — blend-in/out durations are what stop the cut from snapping.
5. `timeline-marker-add` signals to trigger gameplay at exact frames.
6. `splines-container-create` + knots for camera rails; `splines-evaluate` to sanity-check the path.

## VFX pass

1. `particle-system-get` with only the module flags you need — the full dump is huge.
2. `particle-system-modify` with just the changed modules. Impacts read best as three stacked systems: a fast bright spark burst, a slower smoke puff, and a debris system with Collision enabled.
3. Materials via `assets-material-create`; check available shaders with `assets-shader-list-all`.
4. Verify with `screenshot-game-view` mid-play — particles are a motion effect, so a static Scene View shot lies to you.

## Performance pass

1. `profiler-start`, enter play mode, run representative gameplay.
2. `profiler-get-rendering-stats` (frame time, FPS, device), `profiler-get-memory-stats`, `profiler-get-script-stats`.
3. Over 16ms on the M1 Pro target: check draw calls first (`probuilder-merge-objects` for static clusters, shared materials), then script cost, then GC allocation in the script stats.
4. `profiler-save-data` to snapshot a baseline before optimizing, so you can prove the delta.

## Bulk / uncovered operations

When no tool fits, `script-execute` with `isMethodBody=true`:

```csharp
// e.g. retarget every material on every prop prefab in a folder
var guids = UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[]{"Assets/_Project/Prefabs/Props"});
foreach (var g in guids) { /* … */ }
UnityEditor.AssetDatabase.SaveAssets();
return guids.Length;
```

Unity API calls must run on the main thread — `script-execute` handles that. For private Editor internals, `reflection-method-find` then `reflection-method-call`.
