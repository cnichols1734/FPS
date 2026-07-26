using ArenaFps.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaFps.Weapons
{
    /// <summary>
    /// Installs the Quaternius CC0 assault-rifle viewmodel (Resources/Weapons/M4_Viewmodel)
    /// with open ghost-ring optics.
    ///
    /// The imported pivot and unit scale are treated as untrusted: an FBX re-export or a
    /// changed importer setting can silently move the gun kilometres off or blow it up 100x.
    /// <see cref="Normalize"/> therefore re-derives the pose from the geometry every time so
    /// the weapon lands in the same place regardless of what the asset claims.
    /// </summary>
    public static class M4ViewmodelBuilder
    {
        public const string RootName = "M4_Viewmodel";
        const string ResourcePath = "Weapons/M4_Viewmodel";

        /// <summary>
        /// Pose contract, expressed in WeaponRoot local space once normalized:
        ///   * the sight's optical axis runs along x = 0, y = 0
        ///   * the buttplate sits on z = 0 and the muzzle on z = <see cref="TargetLength"/>
        /// ViewmodelMotion's hip and ADS offsets are authored against exactly this space.
        /// </summary>
        public const float TargetLength = 0.52f;

        /// <summary>Anything outside this is a unit-scale accident, not a design choice.</summary>
        const float MinPlausibleLength = 0.02f;
        const float MaxPlausibleLength = 200f;

        public static Transform Ensure(Transform weaponRoot)
        {
            if (weaponRoot == null)
                return null;

            DestroyStale(weaponRoot);

            var existing = weaponRoot.Find(RootName);
            if (existing != null && IsAuthoredGun(existing))
            {
                Normalize(existing);
                Finalize(existing);
                return existing;
            }

            if (existing != null)
                Object.Destroy(existing.gameObject);

            var prefab = Resources.Load<GameObject>(ResourcePath);
            if (prefab == null)
            {
                Debug.LogError("[M4ViewmodelBuilder] Missing Resources/Weapons/M4_Viewmodel — reimport the FBX.");
                return null;
            }

            var instance = Object.Instantiate(prefab, weaponRoot, false);
            instance.name = RootName;

            EnsureAnchors(instance.transform);
            Normalize(instance.transform);
            ApplyRuntimeMaterials(instance.transform);
            Finalize(instance.transform);
            return instance.transform;
        }

        /// <summary>
        /// Scales the gun to <see cref="TargetLength"/> and re-origins it onto the pose
        /// contract. Idempotent: the transform is reset before measuring, so running this
        /// twice lands on the same result.
        /// </summary>
        static void Normalize(Transform gun)
        {
            gun.localPosition = Vector3.zero;
            gun.localRotation = Quaternion.identity;
            gun.localScale = Vector3.one;

            if (!TryMeasure(gun, out var bounds))
            {
                Debug.LogWarning("[M4ViewmodelBuilder] Viewmodel has no meshes to measure — leaving pose untouched.");
                return;
            }

            float length = bounds.size.z;
            if (length <= 1e-5f)
            {
                Debug.LogWarning("[M4ViewmodelBuilder] Viewmodel has no depth along its barrel axis — leaving pose untouched.");
                return;
            }

            float scale = TargetLength / length;
            if (length < MinPlausibleLength || length > MaxPlausibleLength)
            {
                Debug.LogWarning(
                    $"[M4ViewmodelBuilder] Imported gun is {length:0.###}m along its barrel axis, " +
                    $"which is not a viewmodel — check 'Convert Units' on the FBX importer. " +
                    $"Rescaling by {scale:0.####} to recover.");
            }

            gun.localScale = Vector3.one * scale;

            // Sight anchor drives x/y so the optical axis passes through the pivot; the
            // buttplate drives z so the whole weapon sits ahead of it.
            var sight = gun.Find("SightAlign");
            var axis = sight != null
                ? sight.localPosition
                : new Vector3(bounds.center.x, bounds.max.y, 0f);

            gun.localPosition = new Vector3(
                -axis.x * scale,
                -axis.y * scale,
                -bounds.min.z * scale);
        }

        /// <summary>Exact local-space bounds of every mesh under <paramref name="root"/>.</summary>
        static bool TryMeasure(Transform root, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            var toLocal = root.worldToLocalMatrix;

            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;

                var local = mesh.bounds;
                var matrix = toLocal * filter.transform.localToWorldMatrix;
                var center = local.center;
                var extents = local.extents;

                for (int corner = 0; corner < 8; corner++)
                {
                    var offset = new Vector3(
                        (corner & 1) == 0 ? -extents.x : extents.x,
                        (corner & 2) == 0 ? -extents.y : extents.y,
                        (corner & 4) == 0 ? -extents.z : extents.z);
                    var point = matrix.MultiplyPoint3x4(center + offset);

                    if (!any)
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(point);
                    }
                }
            }

            return any;
        }

        static void DestroyStale(Transform weaponRoot)
        {
            var placeholder = weaponRoot.Find("PlaceholderAR");
            if (placeholder != null)
                Object.Destroy(placeholder.gameObject);

            // Old procedural cube-stack used these names.
            var old = weaponRoot.Find(RootName);
            if (old != null && !IsAuthoredGun(old))
                Object.Destroy(old.gameObject);
        }

        static bool IsAuthoredGun(Transform root)
        {
            if (root.Find("RifleMesh") != null)
                return true;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null && mf.sharedMesh.vertexCount > 200)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Rebuilds any anchor the FBX did not ship. Runs before <see cref="Normalize"/>, so
        /// everything here is in the raw imported space and gets moved into the contract with
        /// the rest of the gun.
        /// </summary>
        static void EnsureAnchors(Transform root)
        {
            if (!TryMeasure(root, out var bounds))
                return;

            if (root.Find("SightAlign") == null)
                Anchor(root, "SightAlign", new Vector3(0f, bounds.max.y + bounds.size.y * 0.05f, bounds.center.z));

            if (root.Find("Muzzle") == null)
                Anchor(root, "Muzzle", new Vector3(0f, bounds.center.y, bounds.max.z + bounds.size.z * 0.02f));

            if (root.Find("FirePoint") == null)
            {
                var muzzle = root.Find("Muzzle");
                Anchor(root, "FirePoint", muzzle != null ? muzzle.localPosition : new Vector3(0f, bounds.center.y, bounds.max.z));
            }

            if (root.Find("EjectionPort") == null)
                Anchor(root, "EjectionPort", new Vector3(bounds.max.x, bounds.center.y, bounds.center.z + bounds.size.z * 0.1f));
        }

        static Transform Anchor(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            return go.transform;
        }

        static void ApplyRuntimeMaterials(Transform root)
        {
            var metal = MakeMat("AR_Metal_Runtime", new Color(0.09f, 0.095f, 0.1f), 0.88f, 0.42f);
            var dark = MakeMat("AR_Dark_Runtime", new Color(0.04f, 0.042f, 0.045f), 0.7f, 0.5f);

            foreach (var rend in root.GetComponentsInChildren<Renderer>(true))
            {
                bool optic = rend.name.IndexOf("Ghost", System.StringComparison.OrdinalIgnoreCase) >= 0
                             || rend.name.IndexOf("Post", System.StringComparison.OrdinalIgnoreCase) >= 0
                             || rend.name.IndexOf("Ring", System.StringComparison.OrdinalIgnoreCase) >= 0;
                rend.sharedMaterial = optic ? dark : metal;
                rend.shadowCastingMode = ShadowCastingMode.Off;
                rend.receiveShadows = false;
            }
        }

        static void Finalize(Transform root)
        {
            GameLayers.ApplyRecursive(root.gameObject, GameLayers.Viewmodel);
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
                col.enabled = false;

            // Kill z-fighting from imported duplicate surfaces.
            foreach (var rend in root.GetComponentsInChildren<Renderer>(true))
            {
                rend.shadowCastingMode = ShadowCastingMode.Off;
                rend.receiveShadows = false;
            }
        }

        static Material MakeMat(string name, Color color, float metallic, float smoothness)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                         ?? Shader.Find("Standard");
            var mat = new Material(shader) { name = name };
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            else
                mat.color = color;
            if (mat.HasProperty("_Metallic"))
                mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness"))
                mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Cull"))
                mat.SetFloat("_Cull", 0f);
            mat.enableInstancing = true;
            return mat;
        }
    }
}
