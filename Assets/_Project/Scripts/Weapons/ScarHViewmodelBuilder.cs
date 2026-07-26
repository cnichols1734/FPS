using ArenaFps.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaFps.Weapons
{
    /// <summary>
    /// Builds the SCAR-H first-person viewmodel: rigged hands, rifle and the clips sliced out of
    /// the Sketchfab showcase take.
    ///
    /// Nothing about this asset's placement can be taken at face value. The Blender export puts
    /// ~121x scale on the mesh nodes, ~406x on the armature, and repeats a ~147-unit authoring
    /// offset on every node, so no importer scale factor or hand-tuned offset is ever "right".
    ///
    /// Instead the rifle measures itself. Its meshes are rigidly bound to the static "Weapon" bone
    /// (this pack keys only the arms, magazine, trigger and slide), so composing that bone's matrix
    /// with the mesh bind pose gives exact geometry during Awake, when renderer bounds are still
    /// garbage. From that we read the barrel axis off the mesh bounds, fix its sign from the
    /// trigger-to-slide direction, and solve one wrapper transform that aims the muzzle down
    /// camera-forward at a known length and seats the weapon in a hip pocket.
    ///
    /// Bone transforms are never written to — zeroing them is what turned the arms to spaghetti.
    /// </summary>
    public static class ScarHViewmodelBuilder
    {
        public const string RootName = "ScarH_Viewmodel";
        const string ResourcePath = "Weapons/FP_ScarH";
        const string TextureFolder = "Weapons/ScarH";

        /// <summary>
        /// Visible rifle length in metres. A real SCAR-H is ~1.0m; viewmodels are trimmed so the
        /// buttplate clears the 0.05m near plane instead of being sliced in half by it.
        /// </summary>
        const float TargetGunLength = 0.82f;

        /// <summary>Rifle bounding-box centre in WeaponRoot space: right of, and below, the eye.</summary>
        static readonly Vector2 HipCentre = new(0.105f, -0.145f);

        /// <summary>How far ahead of the eye the buttplate sits.</summary>
        const float HipButtDistance = 0.06f;

        /// <summary>Rigid carrier bone for the rifle meshes in this pack.</summary>
        const string GunBoneName = "Weapon";

        public static Transform Ensure(Transform weaponRoot)
        {
            if (weaponRoot == null)
                return null;

            DestroyStale(weaponRoot);

            var prefab = Resources.Load<GameObject>(ResourcePath);
            if (prefab == null)
            {
                Debug.LogError("[ScarH] Missing Resources/Weapons/FP_ScarH — reimport the FBX.");
                return null;
            }

            var wrapper = new GameObject(RootName).transform;
            wrapper.SetParent(weaponRoot, false);

            var instance = Object.Instantiate(prefab, wrapper, false);
            instance.name = "FP_ScarH";
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            ApplyMaterials(wrapper);

            var driver = EnsureAnimator(wrapper.gameObject, instance);
            // The graph does not evaluate until later in the frame, so stamp the idle pose now and
            // the first rendered frame shows a shooter instead of the spread-armed rest pose.
            driver?.SampleIdleImmediate(instance);

            if (Solve(wrapper, instance.transform, out var frame))
                PlaceAnchors(wrapper, frame);
            else
                FallbackAnchors(wrapper);

            FinishSetup(wrapper);
            return wrapper;
        }

        static void DestroyStale(Transform weaponRoot)
        {
            foreach (var name in new[] { "PlaceholderAR", "M4_Viewmodel", RootName })
            {
                var stale = weaponRoot.Find(name);
                if (stale == null)
                    continue;
                // Destroy is deferred to end of frame; rename so the rebuild cannot find it.
                stale.name = "__stale";
                Object.Destroy(stale.gameObject);
            }
        }

        /// <summary>
        /// The rifle's world-space frame plus the extents of every rifle mesh projected onto it.
        /// </summary>
        struct GunFrame
        {
            public Transform Bone;
            public Vector3 Forward;
            public Vector3 Up;
            public Vector3 Right;
            public float MinF, MaxF;
            public float MinU, MaxU;
            public float MinR, MaxR;

            public float Length => MaxF - MinF;
            public float CentreU => (MinU + MaxU) * 0.5f;
            public float CentreR => (MinR + MaxR) * 0.5f;

            /// <summary>Rebuilds a world point from oriented-box coordinates.</summary>
            public Vector3 Point(float f, float u, float r) => Forward * f + Up * u + Right * r;
        }

        /// <summary>
        /// Aims, scales and seats the pack. Every measurement is retaken after each step because
        /// moving the wrapper moves the bones the rifle is measured through.
        /// </summary>
        static bool Solve(Transform wrapper, Transform instance, out GunFrame frame)
        {
            frame = default;

            var gunSkins = CollectGunSkins(instance);
            if (gunSkins.Length == 0)
            {
                Debug.LogWarning("[ScarH] No rifle meshes found — leaving the viewmodel unposed.");
                return false;
            }

            if (!TryAxes(gunSkins, instance, out var bone, out var localFwd, out var localUp, out int boneIndex))
                return false;

            wrapper.localRotation = Quaternion.identity;
            wrapper.localScale = Vector3.one;
            wrapper.localPosition = Vector3.zero;

            var parent = wrapper.parent;

            // 1. Aim: rotate so the barrel runs down WeaponRoot's +Z and the rail faces +Y.
            var primary = gunSkins[0];
            var meshToWorld = RigidMatrix(primary, boneIndex);
            Vector3 worldFwd = meshToWorld.MultiplyVector(localFwd).normalized;
            Vector3 worldUp = meshToWorld.MultiplyVector(localUp).normalized;

            Vector3 fwdInParent = parent != null ? parent.InverseTransformDirection(worldFwd) : worldFwd;
            Vector3 upInParent = parent != null ? parent.InverseTransformDirection(worldUp) : worldUp;
            wrapper.localRotation = Quaternion.Inverse(Quaternion.LookRotation(fwdInParent, upInParent));

            // 2. Size: uniform scale so the barrel axis spans TargetGunLength.
            frame = Measure(gunSkins, boneIndex, bone, localFwd, localUp);
            if (frame.Length > 1e-5f)
                wrapper.localScale = Vector3.one * Mathf.Clamp(TargetGunLength / frame.Length, 1e-5f, 5000f);

            // 3. Seat: drop the rifle into the hip pocket, measured in WeaponRoot space.
            var box = ParentSpaceBounds(gunSkins, boneIndex, parent);
            wrapper.localPosition += new Vector3(
                HipCentre.x - box.center.x,
                HipCentre.y - box.center.y,
                HipButtDistance - box.min.z);

            frame = Measure(gunSkins, boneIndex, bone, localFwd, localUp);

            Debug.Log($"[ScarH] solved: length={frame.Length:0.###}m scale={wrapper.localScale.x:0.#####} " +
                      $"pos={wrapper.localPosition} carrier={(bone != null ? bone.name : "none")}");
            return true;
        }

        static SkinnedMeshRenderer[] CollectGunSkins(Transform instance)
        {
            var all = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (!IsHands(all[i].name) && all[i].sharedMesh != null)
                    count++;
            }

            var result = new SkinnedMeshRenderer[count];
            int w = 0;
            // Longest mesh first: it owns the barrel axis and therefore the frame.
            float bestSpan = -1f;
            int bestIndex = -1;
            for (int i = 0; i < all.Length; i++)
            {
                if (IsHands(all[i].name) || all[i].sharedMesh == null)
                    continue;
                var size = all[i].sharedMesh.bounds.size;
                float span = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                if (span > bestSpan)
                {
                    bestSpan = span;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
                result[w++] = all[bestIndex];
            for (int i = 0; i < all.Length && w < count; i++)
            {
                if (i == bestIndex || IsHands(all[i].name) || all[i].sharedMesh == null)
                    continue;
                result[w++] = all[i];
            }

            return result;
        }

        static bool IsHands(string name) =>
            name.IndexOf("hand", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("arm", System.StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Resolves the rifle's own axes in mesh-local space from its bounds, then fixes the signs
        /// from rig landmarks: the trigger sits at the rear, and the shoulders sit above the hands.
        /// </summary>
        static bool TryAxes(SkinnedMeshRenderer[] gunSkins, Transform instance, out Transform bone,
                            out Vector3 localFwd, out Vector3 localUp, out int boneIndex)
        {
            bone = null;
            localFwd = Vector3.forward;
            localUp = Vector3.up;
            boneIndex = -1;

            var primary = gunSkins[0];
            var mesh = primary.sharedMesh;
            var bones = primary.bones;
            if (mesh == null || bones == null || bones.Length == 0 || mesh.bindposes == null || mesh.bindposes.Length == 0)
            {
                Debug.LogWarning("[ScarH] Rifle mesh has no usable skin — cannot solve a pose.");
                return false;
            }

            boneIndex = IndexOfBone(bones, GunBoneName);
            if (boneIndex < 0 || boneIndex >= mesh.bindposes.Length)
                boneIndex = 0;
            bone = bones[boneIndex];

            // Longest bounds axis is the barrel, second longest is the rail-to-magazine height.
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

            localFwd = Axis(fwdAxis);
            localUp = Axis(upAxis);

            var meshToWorld = RigidMatrix(primary, boneIndex);
            var worldToMesh = meshToWorld.inverse;

            // Downrange is trigger-to-slide; both bones exist in this pack and the gap between them
            // is a wider margin than measuring either against the bounds centre.
            var trigger = FindDeep(instance, "Trigger");
            var slide = FindDeep(instance, "Slide");
            Vector3 downrange = Vector3.zero;
            if (trigger != null && slide != null)
                downrange = worldToMesh.MultiplyVector(slide.position - trigger.position);
            else if (trigger != null)
                downrange = mesh.bounds.center - worldToMesh.MultiplyPoint3x4(trigger.position);

            if (downrange.sqrMagnitude > 1e-9f && Vector3.Dot(downrange, localFwd) < 0f)
                localFwd = -localFwd;

            // Rail points away from the shoulders-to-hands drop.
            Vector3 upReference = UpReference(instance);
            if (upReference.sqrMagnitude > 1e-6f)
            {
                Vector3 refLocal = worldToMesh.MultiplyVector(upReference);
                if (Vector3.Dot(refLocal, localUp) < 0f)
                    localUp = -localUp;
            }

            return true;
        }

        /// <summary>Shoulders sit above hands, which makes their difference a reliable "up".</summary>
        static Vector3 UpReference(Transform instance)
        {
            var shoulderL = FindDeep(instance, "shoulder.L");
            var shoulderR = FindDeep(instance, "shoulder.R");
            var handL = FindDeep(instance, "hand.L");
            var handR = FindDeep(instance, "hand.R");
            if (shoulderL == null || shoulderR == null || handL == null || handR == null)
                return Vector3.up;

            Vector3 shoulders = (shoulderL.position + shoulderR.position) * 0.5f;
            Vector3 hands = (handL.position + handR.position) * 0.5f;
            return shoulders - hands;
        }

        static Vector3 Axis(int index) =>
            index == 0 ? Vector3.right : index == 1 ? Vector3.up : Vector3.forward;

        static int IndexOfBone(Transform[] bones, string name)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null && bones[i].name == name)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Mesh-local to world for a rigidly bound mesh: the carrier bone's matrix composed with
        /// its bind pose. Works during Awake, unlike renderer bounds.
        /// </summary>
        static Matrix4x4 RigidMatrix(SkinnedMeshRenderer skin, int boneIndex)
        {
            var mesh = skin.sharedMesh;
            var bones = skin.bones;
            if (mesh == null || bones == null)
                return skin.transform.localToWorldMatrix;

            int usable = Mathf.Min(bones.Length, mesh.bindposes != null ? mesh.bindposes.Length : 0);
            if (usable <= 0)
                return skin.transform.localToWorldMatrix;

            int index = Mathf.Clamp(boneIndex, 0, usable - 1);
            var bone = bones[index];
            return bone != null
                ? bone.localToWorldMatrix * mesh.bindposes[index]
                : skin.transform.localToWorldMatrix;
        }

        /// <summary>Same carrier bone for every rifle mesh so they share one measurement frame.</summary>
        static Matrix4x4 CarrierMatrix(SkinnedMeshRenderer skin, int fallbackIndex)
        {
            int index = IndexOfBone(skin.bones, GunBoneName);
            return RigidMatrix(skin, index >= 0 ? index : fallbackIndex);
        }

        static GunFrame Measure(SkinnedMeshRenderer[] gunSkins, int boneIndex, Transform bone,
                                Vector3 localFwd, Vector3 localUp)
        {
            var primary = gunSkins[0];
            var meshToWorld = RigidMatrix(primary, boneIndex);

            var frame = new GunFrame
            {
                Bone = bone,
                Forward = meshToWorld.MultiplyVector(localFwd).normalized,
                Up = meshToWorld.MultiplyVector(localUp).normalized,
                MinF = float.MaxValue, MaxF = float.MinValue,
                MinU = float.MaxValue, MaxU = float.MinValue,
                MinR = float.MaxValue, MaxR = float.MinValue,
            };
            frame.Up = (frame.Up - frame.Forward * Vector3.Dot(frame.Up, frame.Forward)).normalized;
            frame.Right = Vector3.Cross(frame.Up, frame.Forward).normalized;

            for (int i = 0; i < gunSkins.Length; i++)
            {
                var skin = gunSkins[i];
                var matrix = CarrierMatrix(skin, boneIndex);
                var bounds = skin.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 world = matrix.MultiplyPoint3x4(Corner(bounds, c));
                    float f = Vector3.Dot(world, frame.Forward);
                    float u = Vector3.Dot(world, frame.Up);
                    float r = Vector3.Dot(world, frame.Right);
                    if (f < frame.MinF) frame.MinF = f;
                    if (f > frame.MaxF) frame.MaxF = f;
                    if (u < frame.MinU) frame.MinU = u;
                    if (u > frame.MaxU) frame.MaxU = u;
                    if (r < frame.MinR) frame.MinR = r;
                    if (r > frame.MaxR) frame.MaxR = r;
                }
            }

            return frame;
        }

        static Bounds ParentSpaceBounds(SkinnedMeshRenderer[] gunSkins, int boneIndex, Transform parent)
        {
            var bounds = new Bounds();
            bool any = false;

            for (int i = 0; i < gunSkins.Length; i++)
            {
                var skin = gunSkins[i];
                var matrix = CarrierMatrix(skin, boneIndex);
                var local = skin.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 world = matrix.MultiplyPoint3x4(Corner(local, c));
                    Vector3 p = parent != null ? parent.InverseTransformPoint(world) : world;
                    if (!any)
                    {
                        bounds = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(p);
                    }
                }
            }

            return bounds;
        }

        static Vector3 Corner(Bounds b, int index)
        {
            var e = b.extents;
            return b.center + new Vector3(
                (index & 1) == 0 ? -e.x : e.x,
                (index & 2) == 0 ? -e.y : e.y,
                (index & 4) == 0 ? -e.z : e.z);
        }

        /// <summary>
        /// Anchors hang off the wrapper rather than the rifle bone: in this pack the rifle is static
        /// relative to the armature root (only arms, magazine, trigger and slide are keyed), so
        /// there is nothing to track, and the wrapper keeps them at unit world scale.
        /// </summary>
        static void PlaceAnchors(Transform wrapper, GunFrame frame)
        {
            float length = frame.Length;
            var aim = Quaternion.LookRotation(frame.Forward, frame.Up);
            Vector3 muzzle = frame.Point(frame.MaxF, frame.CentreU, frame.CentreR);

            Anchor(wrapper, "Muzzle", muzzle, aim);
            Anchor(wrapper, "FirePoint", muzzle, aim);
            // Rear sight: on top of the rail, a little behind the midpoint.
            Anchor(wrapper, "SightAlign",
                frame.Point(frame.MinF + length * 0.42f, frame.MaxU, frame.CentreR), aim);
            Anchor(wrapper, "EjectionPort",
                frame.Point(frame.MinF + length * 0.52f,
                            frame.CentreU + (frame.MaxU - frame.CentreU) * 0.4f,
                            frame.MaxR), aim);
        }

        static void Anchor(Transform wrapper, string name, Vector3 worldPos, Quaternion worldRot)
        {
            var existing = FindDeep(wrapper, name);
            Transform t = existing != null ? existing : new GameObject(name).transform;
            t.SetParent(wrapper, false);
            // The wrapper carries the solved uniform scale; undo it so anchors stay unit sized.
            float s = wrapper.localScale.x;
            t.localScale = Vector3.one * (Mathf.Abs(s) > 1e-6f ? 1f / s : 1f);
            t.position = worldPos;
            t.rotation = worldRot;
        }

        static void FallbackAnchors(Transform wrapper)
        {
            SetLocal(wrapper, "Muzzle", new Vector3(0f, 0f, TargetGunLength));
            SetLocal(wrapper, "FirePoint", new Vector3(0f, 0f, TargetGunLength));
            SetLocal(wrapper, "SightAlign", new Vector3(0f, 0.06f, TargetGunLength * 0.45f));
            SetLocal(wrapper, "EjectionPort", new Vector3(0.04f, 0.03f, TargetGunLength * 0.5f));
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

        /// <summary>Name lookup at any depth, since callers do not know the imported hierarchy.</summary>
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

        static void ApplyMaterials(Transform root)
        {
            var body = Load("ScarH_Body") ?? BuildRuntimeMaterial("ScarH_Body_Runtime", "ScarH_Body", false);
            var stock = Load("ScarH_Buttock") ?? BuildRuntimeMaterial("ScarH_Buttock_Runtime", "ScarH_Buttock", false);
            var hands = Load("ScarH_Hands") ?? BuildRuntimeMaterial("ScarH_Hands_Runtime", "FPS_Hands", true);

            foreach (var rend in root.GetComponentsInChildren<Renderer>(true))
            {
                var n = rend.name;
                Material chosen;
                if (IsHands(n))
                    chosen = hands;
                else if (n.IndexOf("BUTT", System.StringComparison.OrdinalIgnoreCase) >= 0
                         || n.IndexOf("BARREL", System.StringComparison.OrdinalIgnoreCase) >= 0
                         || n.IndexOf("STOCK", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    chosen = stock;
                else
                    chosen = body;

                if (chosen != null)
                    rend.sharedMaterial = chosen;
                rend.shadowCastingMode = ShadowCastingMode.Off;
                rend.receiveShadows = false;
            }
        }

        /// <summary>Editor-authored material wins — it carries the packed metallic/smoothness map.</summary>
        static Material Load(string name) => Resources.Load<Material>($"{TextureFolder}/{name}");

        /// <summary>Last resort when the editor pass has not authored the materials yet.</summary>
        static Material BuildRuntimeMaterial(string name, string stem, bool hasAo)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                         ?? Shader.Find("Standard");
            if (shader == null)
                return null;

            var mat = new Material(shader) { name = name };

            var albedo = Resources.Load<Texture2D>($"{TextureFolder}/{stem}_D");
            var normal = Resources.Load<Texture2D>($"{TextureFolder}/{stem}_N");
            var packed = Resources.Load<Texture2D>($"{TextureFolder}/{stem}_MG");
            var ao = hasAo ? Resources.Load<Texture2D>($"{TextureFolder}/{stem}_AO") : null;

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
                if (mat.HasProperty("_BumpScale"))
                    mat.SetFloat("_BumpScale", 1f);
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
            else
            {
                if (mat.HasProperty("_Metallic"))
                    mat.SetFloat("_Metallic", hasAo ? 0.02f : 0.55f);
                if (mat.HasProperty("_Smoothness"))
                    mat.SetFloat("_Smoothness", hasAo ? 0.28f : 0.38f);
            }

            if (ao != null && mat.HasProperty("_OcclusionMap"))
            {
                mat.SetTexture("_OcclusionMap", ao);
                if (mat.HasProperty("_OcclusionStrength"))
                    mat.SetFloat("_OcclusionStrength", 1f);
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
                // Skinned viewmodels are always on screen; recomputed bounds stop the arms
                // vanishing when the animated pose leaves the imported bind AABB.
                if (rend is SkinnedMeshRenderer skin)
                    skin.updateWhenOffscreen = true;
            }
        }
    }
}
