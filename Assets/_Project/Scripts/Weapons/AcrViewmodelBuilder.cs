using ArenaFps.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaFps.Weapons
{
    /// <summary>
    /// Builds the ACR first-person viewmodel (hands + carbine) from the multi-take FPS pack.
    ///
    /// Unlike the SCAR showcase export, this pack is authored around a Head_Cam bone. Pose is
    /// solved by sampling idle, then aligning that bone to WeaponRoot so the clips play exactly
    /// as the animator intended — no geometry-axis guesswork on the barrel.
    /// </summary>
    public static class AcrViewmodelBuilder
    {
        public const string RootName = "ACR_Viewmodel";
        const string ResourcePath = "Weapons/FP_ACR";
        const string TextureFolder = "Weapons/ACR";

        const string CamBoneName = "Head_Cam";
        const string GunBoneName = "ACRRifle";

        /// <summary>Visible rifle length used only for muzzle/sight anchors when bone measure fails.</summary>
        const float FallbackGunLength = 0.78f;

        /// <summary>
        /// Hip-only zoom. Applied via <see cref="ViewmodelMotion"/> so ADS still solves against
        /// the pure Head_Cam pose (scale 1).
        /// </summary>
        public const float HipZoomScale = 1.10f;

        /// <summary>
        /// Hip-only pocket. Applied via <see cref="ViewmodelMotion"/> so full ADS returns to the
        /// pre-framing Head_Cam seating.
        /// </summary>
        public static readonly Vector3 HipPocket = new(0f, -0.11f, 0f);

        public static Transform Ensure(Transform weaponRoot)
        {
            if (weaponRoot == null)
                return null;

            DestroyStale(weaponRoot);

            var prefab = Resources.Load<GameObject>(ResourcePath);
            if (prefab == null)
            {
                Debug.LogError("[ACR] Missing Resources/Weapons/FP_ACR — run Arena FPS → Import ACR Viewmodel.");
                return null;
            }

            var wrapper = new GameObject(RootName).transform;
            wrapper.SetParent(weaponRoot, false);

            var instance = Object.Instantiate(prefab, wrapper, false);
            instance.name = "FP_ACR";
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            ApplyMaterials(wrapper);

            var driver = EnsureAnimator(wrapper.gameObject, instance);
            driver?.SampleIdleImmediate(instance);

            AlignToHeadCam(wrapper, instance.transform);
            PlaceAnchors(wrapper, instance.transform);
            FinishSetup(wrapper);
            return wrapper;
        }

        static void DestroyStale(Transform weaponRoot)
        {
            // Collect first — Find after rename would miss siblings created in the same swap.
            var doomed = new System.Collections.Generic.List<GameObject>();
            for (int i = 0; i < weaponRoot.childCount; i++)
            {
                var child = weaponRoot.GetChild(i);
                var n = child.name;
                if (n == "PlaceholderAR" || n == "M4_Viewmodel" || n == ScarHViewmodelBuilder.RootName
                    || n == RootName || n == "__stale" || n.StartsWith("__stale", System.StringComparison.Ordinal))
                    doomed.Add(child.gameObject);
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                // Immediate: deferred Destroy left the previous rifle on screen for a frame and
                // made ACR look like a broken SCAR swap.
                doomed[i].name = "__stale";
                Object.DestroyImmediate(doomed[i]);
            }
        }

        /// <summary>
        /// Puts Head_Cam on WeaponRoot's origin. This pack authors the rifle along the camera bone's
        /// back axis (Blender → Unity axis conversion), so after matching Head_Cam we yaw 180° when
        /// the gun still sits behind the eye, then re-zero the bone on the lens.
        /// </summary>
        static void AlignToHeadCam(Transform wrapper, Transform instance)
        {
            var cam = FindDeep(instance, CamBoneName);
            if (cam == null)
            {
                Debug.LogWarning("[ACR] Head_Cam missing — leaving identity pose.");
                wrapper.localPosition = Vector3.zero;
                wrapper.localRotation = Quaternion.identity;
                wrapper.localScale = Vector3.one;
                return;
            }

            var parent = wrapper.parent;
            if (parent == null)
                return;

            wrapper.localPosition = Vector3.zero;
            wrapper.localRotation = Quaternion.identity;
            wrapper.localScale = Vector3.one;

            // Position-only snap first so the 180° yaw pivots about the eye.
            SnapHeadCamPosition(wrapper, cam, parent);

            // This pack authors the view along Head_Cam's back axis after Unity's axis conversion.
            // Matching Head_Cam.forward to +Z puts the idle rifle behind the lens; yaw 180 fixes it.
            // Bind-pose depth checks lie here — the idle clip is what matters, and it sits on -Z.
            wrapper.Rotate(0f, 180f, 0f, Space.Self);
            SnapHeadCamPosition(wrapper, cam, parent);

            // Hip zoom/pocket stay off the wrapper — ViewmodelMotion owns them so ADS calibrates
            // against this Head_Cam identity and blends framing out as you aim.
            Debug.Log("[ACR] Head_Cam aligned: wrapper pos=" + wrapper.localPosition
                      + " rot=" + wrapper.localRotation.eulerAngles
                      + " handDepth=" + HandDepthInParent(instance, parent).ToString("0.###"));
        }

        static void SnapHeadCamPosition(Transform wrapper, Transform cam, Transform parent)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                Vector3 camPos = parent.InverseTransformPoint(cam.position);
                wrapper.localPosition -= camPos;
            }
        }

        static float HandDepthInParent(Transform instance, Transform parent)
        {
            var hand = FindDeep(instance, "Hand_R") ?? FindDeep(instance, "IK_Hand_Cntrl_R");
            if (hand == null || parent == null)
                return 0f;
            return parent.InverseTransformPoint(hand.position).z;
        }

        static void SnapHeadCamToParent(Transform wrapper, Transform cam, Transform parent)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                Vector3 camPos = parent.InverseTransformPoint(cam.position);
                Quaternion camRot = Quaternion.Inverse(parent.rotation) * cam.rotation;
                wrapper.localRotation = Quaternion.Inverse(camRot) * wrapper.localRotation;
                wrapper.localPosition = wrapper.localPosition - wrapper.localRotation * camPos;
            }
        }

        static void PlaceAnchors(Transform wrapper, Transform instance)
        {
            var gunBone = FindDeep(instance, GunBoneName) ?? FindDeep(instance, "Rif");
            var trigger = FindDeep(instance, "Trigger");
            var pmag = FindDeep(instance, "Pmag");

            if (TryMeasureGun(instance, out var muzzleWorld, out var forward, out var up, out var length))
            {
                var aim = Quaternion.LookRotation(forward, up);
                Anchor(wrapper, "Muzzle", muzzleWorld, aim);
                Anchor(wrapper, "FirePoint", muzzleWorld, aim);

                // Prefer the optic glass plane (scope submesh). Fallback: rail estimate.
                Vector3 sight = TryScopeSight(instance, forward, up, out var optic)
                    ? optic
                    : muzzleWorld - forward * (length * 0.55f) + up * 0.04f;
                Anchor(wrapper, "SightAlign", sight, aim);

                Vector3 eject = muzzleWorld - forward * (length * 0.45f) + up * 0.02f
                                + Vector3.Cross(up, forward).normalized * 0.03f;
                Anchor(wrapper, "EjectionPort", eject, aim);
                return;
            }

            // Fallback: gun bone forward if measure failed.
            if (gunBone != null)
            {
                var aim = gunBone.rotation;
                Vector3 muzzle = gunBone.TransformPoint(new Vector3(0f, 0.02f, FallbackGunLength));
                if (trigger != null)
                {
                    Vector3 dir = (gunBone.position - trigger.position).normalized;
                    if (dir.sqrMagnitude > 1e-6f)
                    {
                        // Prefer along the longest gun extent from trigger toward muzzle tip.
                        muzzle = trigger.position + gunBone.forward * FallbackGunLength;
                        aim = Quaternion.LookRotation(gunBone.forward, gunBone.up);
                    }
                }

                Anchor(wrapper, "Muzzle", muzzle, aim);
                Anchor(wrapper, "FirePoint", muzzle, aim);
                Anchor(wrapper, "SightAlign", muzzle - aim * Vector3.forward * 0.35f + aim * Vector3.up * 0.04f, aim);
                Anchor(wrapper, "EjectionPort",
                    muzzle - aim * Vector3.forward * 0.3f + aim * Vector3.up * 0.02f + aim * Vector3.right * 0.03f, aim);
                return;
            }

            SetLocal(wrapper, "Muzzle", new Vector3(0f, -0.05f, FallbackGunLength));
            SetLocal(wrapper, "FirePoint", new Vector3(0f, -0.05f, FallbackGunLength));
            SetLocal(wrapper, "SightAlign", new Vector3(0f, -0.01f, FallbackGunLength * 0.45f));
            SetLocal(wrapper, "EjectionPort", new Vector3(0.03f, -0.03f, FallbackGunLength * 0.5f));
            _ = pmag;
        }

        // ACR_Scope_E.png red reticle island (2048 sheet). SightAlign must sit on this, not the
        // housing centroid, or the hologram drifts off the HUD crosshair at ADS.
        const float ReticleUMin = 1783f / 2048f;
        const float ReticleUMax = 2043f / 2048f;
        const float ReticleVMin = 1265f / 2048f;
        const float ReticleVMax = 1495f / 2048f;
        // Brightness-weighted centre of the emissive reticle (not the UV island midpoint).
        const float ReticleAimU = 1899.1f / 2048f;
        const float ReticleAimV = 1382.5f / 2048f;

        /// <summary>
        /// Place SightAlign on the emissive reticle geometry (preferred) or the scope submesh
        /// centre. ACRRifle packs Rifle/Silencer/Scope/… as submeshes on one skin.
        /// </summary>
        static bool TryScopeSight(Transform instance, Vector3 forward, Vector3 up, out Vector3 sightWorld)
        {
            _ = forward;
            _ = up;
            return TryMeasureReticleWorld(instance, out sightWorld);
        }

        /// <summary>
        /// World-space centre of the ACR hologram reticle in the current skinned pose.
        /// Used for ADS seating and for calibrating onto the HUD crosshair.
        /// </summary>
        public static bool TryMeasureReticleWorld(Transform instance, out Vector3 sightWorld)
        {
            sightWorld = default;
            if (instance == null)
                return false;

            foreach (var skin in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!TryAverageScopePoint(skin, preferReticle: true, out sightWorld))
                    continue;
                return true;
            }

            return false;
        }

        static bool TryAverageScopePoint(SkinnedMeshRenderer skin, bool preferReticle, out Vector3 sightWorld)
        {
            sightWorld = default;
            var mesh = skin != null ? skin.sharedMesh : null;
            if (mesh == null || mesh.subMeshCount < 2 || !mesh.isReadable)
                return false;

            int scopeSub = -1;
            var mats = skin.sharedMaterials;
            for (int i = 0; i < mats.Length && i < mesh.subMeshCount; i++)
            {
                string slot = mats[i] != null ? mats[i].name : string.Empty;
                if (Contains(slot, "scope") || (Contains(skin.name, "ACRRifle") && i == 2))
                {
                    scopeSub = i;
                    break;
                }
            }

            if (scopeSub < 0)
                return false;

            var indices = mesh.GetIndices(scopeSub);
            if (indices == null || indices.Length == 0)
                return false;

            var baked = new Mesh();
            skin.BakeMesh(baked);
            var verts = baked.vertices;
            var uvs = mesh.uv;
            if (verts == null || verts.Length == 0)
            {
                Object.Destroy(baked);
                return false;
            }

            // Weight toward the emissive aim UV so the glowing circle — not the mesh trim — is what
            // lands on the HUD crosshair.
            float vAimFlip = 1f - ReticleAimV;

            Vector3 sumReticle = Vector3.zero;
            float weightReticle = 0f;
            Vector3 sumScope = Vector3.zero;
            int scopeCount = 0;
            int step = Mathf.Max(1, indices.Length / 800);
            for (int i = 0; i < indices.Length; i += step)
            {
                int vi = indices[i];
                if (vi < 0 || vi >= verts.Length)
                    continue;

                sumScope += verts[vi];
                scopeCount++;

                if (uvs == null || vi >= uvs.Length)
                    continue;

                var uv = uvs[vi];
                if (!IsReticleUv(uv))
                    continue;

                float dv = Mathf.Min(Mathf.Abs(uv.y - ReticleAimV), Mathf.Abs(uv.y - vAimFlip));
                float du = Mathf.Abs(uv.x - ReticleAimU);
                float w = 1f / (0.00005f + du * du + dv * dv);
                sumReticle += verts[vi] * w;
                weightReticle += w;
            }

            Object.Destroy(baked);
            if (preferReticle && weightReticle > 0f)
            {
                sightWorld = skin.transform.TransformPoint(sumReticle / weightReticle);
                return true;
            }

            if (scopeCount == 0)
                return false;

            sightWorld = skin.transform.TransformPoint(sumScope / scopeCount);
            return true;
        }

        static bool IsReticleUv(Vector2 uv)
        {
            if (uv.x < ReticleUMin || uv.x > ReticleUMax)
                return false;
            // Texture V may be flipped depending on FBX import.
            return (uv.y >= ReticleVMin && uv.y <= ReticleVMax)
                   || (uv.y >= 1f - ReticleVMax && uv.y <= 1f - ReticleVMin);
        }

        static bool TryMeasureGun(Transform instance, out Vector3 muzzleWorld, out Vector3 forward,
                                  out Vector3 up, out float length)
        {
            muzzleWorld = default;
            forward = Vector3.forward;
            up = Vector3.up;
            length = 0f;

            SkinnedMeshRenderer rifle = null;
            float best = -1f;
            foreach (var skin in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin.sharedMesh == null)
                    continue;
                if (IsHands(skin.name))
                    continue;
                float span = skin.sharedMesh.bounds.size.magnitude;
                if (span > best)
                {
                    best = span;
                    rifle = skin;
                }
            }

            if (rifle == null)
                return false;

            var mesh = rifle.sharedMesh;
            var bones = rifle.bones;
            int boneIndex = IndexOfBone(bones, GunBoneName);
            if (boneIndex < 0)
                boneIndex = 0;
            if (bones == null || boneIndex >= bones.Length || mesh.bindposes == null
                || boneIndex >= mesh.bindposes.Length)
                return false;

            var bone = bones[boneIndex];
            var meshToWorld = bone.localToWorldMatrix * mesh.bindposes[boneIndex];

            var size = mesh.bounds.size;
            int fwdAxis = size.x > size.y ? (size.x > size.z ? 0 : 2) : (size.y > size.z ? 1 : 2);
            int upAxis = -1;
            for (int i = 0; i < 3; i++)
            {
                if (i == fwdAxis)
                    continue;
                if (upAxis < 0 || size[i] > size[upAxis])
                    upAxis = i;
            }

            Vector3 localFwd = Axis(fwdAxis);
            Vector3 localUp = Axis(upAxis);

            var trigger = FindDeep(instance, "Trigger");
            var worldToMesh = meshToWorld.inverse;
            if (trigger != null)
            {
                Vector3 toTip = mesh.bounds.center - worldToMesh.MultiplyPoint3x4(trigger.position);
                // Tip should be away from the trigger along the long axis.
                Vector3 extent = Vector3.zero;
                extent[fwdAxis] = size[fwdAxis] * 0.5f;
                // Prefer the bounds corner farthest from the trigger projected on fwd.
                float bestDot = float.MinValue;
                Vector3 bestCorner = mesh.bounds.max;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = Corner(mesh.bounds, c);
                    float d = Vector3.Distance(corner, worldToMesh.MultiplyPoint3x4(trigger.position));
                    if (d > bestDot)
                    {
                        bestDot = d;
                        bestCorner = corner;
                    }
                }

                Vector3 away = bestCorner - worldToMesh.MultiplyPoint3x4(trigger.position);
                if (Vector3.Dot(away, localFwd) < 0f)
                    localFwd = -localFwd;
                _ = toTip;
            }

            forward = meshToWorld.MultiplyVector(localFwd).normalized;
            up = meshToWorld.MultiplyVector(localUp).normalized;
            up = (up - forward * Vector3.Dot(up, forward)).normalized;

            float minF = float.MaxValue, maxF = float.MinValue;
            float centreU = 0f, centreR = 0f;
            int samples = 0;
            Vector3 right = Vector3.Cross(up, forward).normalized;
            for (int c = 0; c < 8; c++)
            {
                Vector3 world = meshToWorld.MultiplyPoint3x4(Corner(mesh.bounds, c));
                float f = Vector3.Dot(world, forward);
                if (f < minF) minF = f;
                if (f > maxF) maxF = f;
                centreU += Vector3.Dot(world, up);
                centreR += Vector3.Dot(world, right);
                samples++;
            }

            centreU /= samples;
            centreR /= samples;
            length = maxF - minF;
            muzzleWorld = forward * maxF + up * centreU + right * centreR;
            return length > 1e-4f;
        }

        static Vector3 Axis(int index) =>
            index == 0 ? Vector3.right : index == 1 ? Vector3.up : Vector3.forward;

        static Vector3 Corner(Bounds b, int index)
        {
            var e = b.extents;
            return b.center + new Vector3(
                (index & 1) == 0 ? -e.x : e.x,
                (index & 2) == 0 ? -e.y : e.y,
                (index & 4) == 0 ? -e.z : e.z);
        }

        static int IndexOfBone(Transform[] bones, string name)
        {
            if (bones == null)
                return -1;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null && bones[i].name == name)
                    return i;
            }

            return -1;
        }

        static void Anchor(Transform wrapper, string name, Vector3 worldPos, Quaternion worldRot)
        {
            var existing = FindDeep(wrapper, name);
            Transform t = existing != null ? existing : new GameObject(name).transform;
            t.SetParent(wrapper, false);
            float s = wrapper.localScale.x;
            t.localScale = Vector3.one * (Mathf.Abs(s) > 1e-6f ? 1f / s : 1f);
            t.position = worldPos;
            t.rotation = worldRot;
        }

        static void SetLocal(Transform parent, string name, Vector3 localPos)
        {
            var t = parent.Find(name);
            if (t == null)
            {
                t = new GameObject(name).transform;
                t.SetParent(parent, false);
            }

            t.localPosition = localPos;
            t.localRotation = Quaternion.identity;
        }

        public static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
                return null;
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        static bool IsHands(string name) =>
            name.IndexOf("hand", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("arm", System.StringComparison.OrdinalIgnoreCase) >= 0;

        static void ApplyMaterials(Transform root)
        {
            var rifle = Load("ACR_Rifle") ?? BuildRuntime("ACR_Rifle_Runtime", "ACR_Rifle", true, false);
            var pmag = Load("ACR_Pmag") ?? BuildRuntime("ACR_Pmag_Runtime", "ACR_Pmag", false, false);
            var scope = Load("ACR_Scope") ?? BuildRuntime("ACR_Scope_Runtime", "ACR_Scope", false, true);
            var silencer = Load("ACR_Silencer") ?? BuildRuntime("ACR_Silencer_Runtime", "ACR_Silencer", false, false);
            var foregrip = Load("ACR_Foregrip") ?? rifle;
            var arms = Load("ACR_Arms") ?? BuildRuntime("ACR_Arms_Runtime", "ACR_Arms", true, false);

            foreach (var rend in root.GetComponentsInChildren<Renderer>(true))
            {
                var n = rend.name;
                Material chosen;
                if (IsHands(n))
                    chosen = arms;
                else if (Contains(n, "scope"))
                    chosen = scope;
                else if (Contains(n, "silencer") || Contains(n, "suppressor"))
                    chosen = silencer;
                else if (Contains(n, "pmag") || Contains(n, "mag"))
                    chosen = pmag;
                else if (Contains(n, "foregrip") || Contains(n, "grip"))
                    chosen = foregrip;
                else
                    chosen = rifle;

                // Multi-material meshes (ACRRifle): assign by slot name when possible.
                if (rend.sharedMaterials != null && rend.sharedMaterials.Length > 1)
                {
                    var mats = rend.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        string slot = mats[i] != null ? mats[i].name : string.Empty;
                        // After import with MaterialImportMode.None slots may be null — use mesh
                        // material names from the FBX via renderer material names if present.
                        mats[i] = PickBySlot(slot, n, i, rifle, pmag, scope, silencer, foregrip, arms);
                    }

                    rend.sharedMaterials = mats;
                }
                else if (chosen != null)
                {
                    rend.sharedMaterial = chosen;
                }

                rend.shadowCastingMode = ShadowCastingMode.Off;
                rend.receiveShadows = false;
            }
        }

        static Material PickBySlot(string slot, string rendererName, int index,
                                   Material rifle, Material pmag, Material scope,
                                   Material silencer, Material foregrip, Material arms)
        {
            string key = (slot + " " + rendererName).ToLowerInvariant();
            if (key.Contains("arm")) return arms;
            if (key.Contains("scope")) return scope;
            if (key.Contains("silencer") || key.Contains("suppressor")) return silencer;
            if (key.Contains("pmag") || key.Contains("mag")) return pmag;
            if (key.Contains("foregrip") || key.Contains("grip")) return foregrip;
            // ACRRifle material order from Blender: Rifle, Silencer, Scope, Foregrip, Pmag
            if (Contains(rendererName, "ACRRifle") || Contains(rendererName, "Rifle"))
            {
                return index switch
                {
                    0 => rifle,
                    1 => silencer,
                    2 => scope,
                    3 => foregrip,
                    4 => pmag,
                    _ => rifle,
                };
            }

            return rifle;
        }

        static bool Contains(string name, string token) =>
            name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;

        static Material Load(string name) => Resources.Load<Material>($"{TextureFolder}/{name}");

        static Material BuildRuntime(string name, string stem, bool hasAo, bool glassCutout)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                         ?? Shader.Find("Standard");
            if (shader == null)
                return null;

            var mat = new Material(shader) { name = name };
            var albedo = glassCutout
                ? Resources.Load<Texture2D>($"{TextureFolder}/{stem}_Base")
                  ?? Resources.Load<Texture2D>($"{TextureFolder}/{stem}_D")
                : Resources.Load<Texture2D>($"{TextureFolder}/{stem}_D");
            var normal = Resources.Load<Texture2D>($"{TextureFolder}/{stem}_N");
            var packed = Resources.Load<Texture2D>($"{TextureFolder}/{stem}_MG");
            var ao = hasAo ? Resources.Load<Texture2D>($"{TextureFolder}/{stem}_AO") : null;
            var emit = glassCutout ? Resources.Load<Texture2D>($"{TextureFolder}/{stem}_E") : null;

            if (albedo != null)
            {
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", albedo);
                else if (mat.HasProperty("_MainTex"))
                    mat.SetTexture("_MainTex", albedo);
            }

            if (normal != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
            }

            if (packed != null && mat.HasProperty("_MetallicGlossMap"))
            {
                mat.SetTexture("_MetallicGlossMap", packed);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                if (mat.HasProperty("_Metallic"))
                    mat.SetFloat("_Metallic", 1f);
                if (mat.HasProperty("_Smoothness"))
                    mat.SetFloat("_Smoothness", 1f);
            }

            if (ao != null && mat.HasProperty("_OcclusionMap"))
            {
                mat.SetTexture("_OcclusionMap", ao);
                if (mat.HasProperty("_OcclusionStrength"))
                    mat.SetFloat("_OcclusionStrength", 1f);
            }

            if (emit != null && mat.HasProperty("_EmissionMap"))
            {
                mat.SetTexture("_EmissionMap", emit);
                mat.EnableKeyword("_EMISSION");
                if (mat.HasProperty("_EmissionColor"))
                    mat.SetColor("_EmissionColor", Color.white);
            }

            if (glassCutout)
            {
                if (mat.HasProperty("_AlphaClip"))
                    mat.SetFloat("_AlphaClip", 1f);
                if (mat.HasProperty("_Cutoff"))
                    mat.SetFloat("_Cutoff", 0.5f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.renderQueue = (int)RenderQueue.AlphaTest;
            }

            mat.enableInstancing = true;
            return mat;
        }

        static ViewmodelAnimator EnsureAnimator(GameObject host, GameObject instance)
        {
            var animator = instance.GetComponentInChildren<Animator>() ?? instance.AddComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.updateMode = AnimatorUpdateMode.Normal;
            animator.runtimeAnimatorController = null;

            var driver = host.GetComponent<ViewmodelAnimator>() ?? host.AddComponent<ViewmodelAnimator>();
            driver.Bind(animator, ResourcePath);
            return driver;
        }

        static void FinishSetup(Transform root)
        {
            GameLayers.ApplyRecursive(root.gameObject, GameLayers.Viewmodel);
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
                col.enabled = false;

            foreach (var rend in root.GetComponentsInChildren<Renderer>(true))
            {
                rend.shadowCastingMode = ShadowCastingMode.Off;
                rend.receiveShadows = false;
                if (rend is SkinnedMeshRenderer skin)
                    skin.updateWhenOffscreen = true;
            }
        }
    }
}
