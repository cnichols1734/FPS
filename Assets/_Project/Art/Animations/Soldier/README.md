# Enemy soldier mocap

Drop Mixamo animation FBX files in this folder, then run **Arena FPS → Import Soldier Animations**.

Roles are resolved from the filename, so a new download is integrated by copying it here — there is
no clip list to maintain and no Animator asset to edit.

## Downloading from Mixamo

1. Go to [mixamo.com](https://www.mixamo.com) and sign in (free Adobe account).
2. Any Mixamo character works as the preview rig — the importer retargets onto our avatar either way.
3. For each animation, choose **Download** with:
   - Format: **FBX Binary (.fbx)**
   - Skin: **Without Skin**
   - Frames per Second: **30**
   - Keyframe Reduction: **none**
4. Save the file here under a name containing the keywords for its role.

## Roles

| Role | Keywords in the filename | Currently installed |
|------|--------------------------|---------------------|
| Idle | `idle` / `aiming` | `Idle.fbx` |
| Walk forward | `walk` | `Walk_Forward.fbx` |
| Walk backward | `walk` + `back` | `Walk_Back.fbx` |
| Strafe left | `strafe`/`walk`/`run` + `left` | `Strafe_Left.fbx` |
| Strafe right | `strafe`/`walk`/`run` + `right` | `Strafe_Right.fbx` |
| Run forward | `run` | `Run_Forward.fbx` |
| Run backward | `run` + `back` | `Run_Back.fbx` |
| Start walking | `start` + `walk` | `Start_Walk_Forward.fbx` |
| Start backpedal | `start` + `walk` + `back` | `Start_Walk_Back.fbx` |
| Stop walking | `stop` + `walk` | `Stop_Walk_Forward.fbx` |
| Stop backpedal | `stop` + `walk` + `back` | `Stop_Walk_Back.fbx` |
| Jump forward | `jump` | `Jump_Forward.fbx` |
| Jump backward | `jump` + `back` | `Jump_Back.fbx` |
| Fire | `fire` / `firing` / `shoot` | `Fire.fbx` |
| Reload | `reload` | **missing** |
| Death, standing | `death` / `dying` | `Death_Standing.fbx` |
| Death, mid-stride | `death`/`dying` + `walk`/`run` | `Death_Moving.fbx` |

Matching is on whole words, tested most-specific first, so raw Mixamo names such as
`standing still Falling Back Death.fbx` resolve correctly without renaming. A partial set is fine:
any missing role degrades (a missing backward run uses the backward walk; a missing transition just
blends), and a file matching nothing is logged and ignored.

**Reload is the one gap.** Without it a reloading enemy keeps its idle upper body. Grab Mixamo's
*Reloading* clip and save it here as `Reload.fbx` to close it.

## How the clips are used

`SoldierClipPlayer` builds a three-layer Playables graph at runtime:

- **Locomotion** — a seven-way blend across idle, walk/run forward and back, and the two strafes,
  all sharing one distance-driven clock so the feet do not skate or fall out of stride with each other.
- **Action** — full-body one-shots that outrank the blend: start and stop on the movement edge,
  jumps on `NavMeshAgent` off-mesh links, and death.
- **Upper body** — fire and reload, masked from the waist up so the legs keep walking underneath.

Aim, foot planting and impact flinch are layered on top in `SoldierAnimator`, so clips authored
facing straight ahead still track the player.

## Notes

- **Do not** rename files after importing — the role is resolved from the filename at import time.
- Every clip is imported as **Humanoid** with **Create From This Model**, which is what lets it
  retarget onto `MaleWarrior`. Leaving a clip on Generic is the usual cause of sunken, skating or
  splay-legged Mixamo imports. **Copy From Other Avatar** looks like the tidier option but cannot be
  used here: it requires an identical transform hierarchy, and Mixamo's skinless downloads parent
  `mixamorig:Hips` to the file root while the character has an `Armature` node in between. Unity
  reports a *"Copied Avatar Rig Configuration mis-match"* and imports no clip at all.
- Root motion is baked out on purpose: the `NavMeshAgent` owns movement, so clips animate in place.
- Only locomotion clips are set to loop. A one-shot that loops re-fires forever.
- Death plays as a lead-in and then hands the body to the ragdoll partway through, so the corpse
  still settles against real geometry. A heavy hit skips the clip entirely — see `RagdollDriver`.
