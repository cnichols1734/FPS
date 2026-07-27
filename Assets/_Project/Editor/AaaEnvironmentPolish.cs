#if UNITY_EDITOR
using ArenaFps.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Additive densification pass — windows, glass, clutter, posters — without nuking the map.
    /// </summary>
    public static class AaaEnvironmentPolish
    {
        [MenuItem("Arena FPS/AAA Environment Polish (Additive)")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[AAA Polish] Exit play mode first.");
                return;
            }

            var root = GameObject.Find("ThreeLaneMap");
            if (root == null)
            {
                Debug.LogError("[AAA Polish] ThreeLaneMap missing — run AAA Environment Pass first.");
                return;
            }

            var parent = root.transform;
            var glass = EnsureMat("Mat_Glass", new Color(0.05f, 0.08f, 0.1f, 0.65f), 0.05f, 0.0f, null, 1f, transparent: true);
            var darkMetal = EnsureMat("Mat_DarkMetal", new Color(0.12f, 0.13f, 0.14f), 0.35f, 0.9f,
                "Assets/_Project/Art/Textures/Generated/Metal_Color.png", 2f);
            var brick = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/Map/Mat_Brick.mat");
            var concrete = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/Map/Mat_Concrete.mat");
            var wood = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/Map/Mat_Wood.mat");
            var metal = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/Map/Mat_Metal.mat");

            // Apply generated albedos if present.
            AaaMaterialPass.Run();

            AddWindowBank(parent, "WinBank_W1", new Vector3(-14.2f, 3.2f, -12f), 4, Vector3.right, glass, darkMetal);
            AddWindowBank(parent, "WinBank_W2", new Vector3(-16.8f, 4.5f, 4f), 3, Vector3.right, glass, darkMetal);
            AddWindowBank(parent, "WinBank_E1", new Vector3(14.2f, 3.2f, 10f), 4, Vector3.left, glass, darkMetal);
            AddWindowBank(parent, "WinBank_E2", new Vector3(16.5f, 5f, -2f), 3, Vector3.left, glass, darkMetal);
            AddWindowBank(parent, "WinBank_MidS", new Vector3(-6f, 2.8f, -8f), 2, Vector3.right, glass, darkMetal);
            AddWindowBank(parent, "WinBank_MidN", new Vector3(6f, 2.8f, 8f), 2, Vector3.left, glass, darkMetal);

            // Clutter piles
            ScatterCrates(parent, wood ?? concrete, new Vector3(-10f, 0f, -14f), 5);
            ScatterCrates(parent, wood ?? concrete, new Vector3(10f, 0f, 14f), 5);
            ScatterCrates(parent, wood ?? concrete, new Vector3(-2f, 0f, 2f), 4);
            ScatterBarriers(parent, metal ?? darkMetal, new Vector3(0f, 0f, -6f), 3);
            ScatterBarriers(parent, metal ?? darkMetal, new Vector3(0f, 0f, 6f), 3);

            // Roof AC units
            for (int i = 0; i < 8; i++)
            {
                float x = (i % 2 == 0 ? -1 : 1) * (12f + (i % 3));
                float z = -18f + i * 5f;
                Box(parent, $"AC_{i}", new Vector3(x, 7.2f, z), new Vector3(1.6f, 1.1f, 1.2f), darkMetal);
            }

            // Soften key light + ambient bounce so night streets don't crush to black.
            var light = Object.FindAnyObjectByType<Light>();
            if (light != null && light.type == LightType.Directional)
            {
                light.intensity = 1.25f;
                light.shadowStrength = 0.72f;
                light.color = new Color(0.75f, 0.82f, 1f);
                light.transform.rotation = Quaternion.Euler(55f, -40f, 0f);
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.25f, 0.3f, 0.4f);
            RenderSettings.ambientEquatorColor = new Color(0.22f, 0.2f, 0.18f);
            RenderSettings.ambientGroundColor = new Color(0.08f, 0.07f, 0.06f);
            RenderSettings.fogDensity = 0.009f;

            EnsureWarmPractical(parent, "Practical_Mid_A", new Vector3(-4f, 3.5f, 0f), new Color(1f, 0.65f, 0.35f), 4.5f, 12f);
            EnsureWarmPractical(parent, "Practical_Mid_B", new Vector3(5f, 3.2f, 3f), new Color(1f, 0.7f, 0.4f), 3.8f, 10f);
            EnsureWarmPractical(parent, "Practical_BlueSpawn", new Vector3(0f, 4f, -24f), new Color(0.55f, 0.7f, 1f), 3.5f, 14f);
            EnsureWarmPractical(parent, "Practical_RedSpawn", new Vector3(0f, 4f, 24f), new Color(1f, 0.45f, 0.35f), 3.5f, 14f);

            if (brick != null)
                ApplyMatIfExists("Wall_West", brick);

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("[AAA Polish] Windows, clutter, practicals applied.");
        }

        static void AddWindowBank(Transform parent, string prefix, Vector3 start, int count, Vector3 outward, Material glass, Material frame)
        {
            for (int i = 0; i < count; i++)
            {
                var along = Vector3.Cross(Vector3.up, outward).normalized;
                var center = start + along * (i * 2.1f);
                var frameGo = Box(parent, $"{prefix}_F{i}", center, new Vector3(
                    Mathf.Abs(outward.x) > 0.5f ? 0.18f : 1.5f,
                    1.7f,
                    Mathf.Abs(outward.z) > 0.5f ? 0.18f : 1.5f), frame);
                var glassGo = Box(parent, $"{prefix}_G{i}", center + outward * 0.06f, new Vector3(
                    Mathf.Abs(outward.x) > 0.5f ? 0.05f : 1.25f,
                    1.35f,
                    Mathf.Abs(outward.z) > 0.5f ? 0.05f : 1.25f), glass);
                StripCollider(frameGo);
                StripCollider(glassGo);
            }
        }

        static void ScatterCrates(Transform parent, Material mat, Vector3 origin, int count)
        {
            var rng = new System.Random(origin.GetHashCode());
            for (int i = 0; i < count; i++)
            {
                var p = origin + new Vector3((float)(rng.NextDouble() * 3f - 1.5f), 0.45f + (i % 2) * 0.5f,
                    (float)(rng.NextDouble() * 3f - 1.5f));
                var s = new Vector3(0.7f + (float)rng.NextDouble() * 0.5f, 0.7f + (float)rng.NextDouble() * 0.4f,
                    0.7f + (float)rng.NextDouble() * 0.5f);
                Box(parent, $"Crate_{origin.x:0}_{i}", p, s, mat);
            }
        }

        static void ScatterBarriers(Transform parent, Material mat, Vector3 origin, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Box(parent, $"Barrier_{origin.z:0}_{i}",
                    origin + new Vector3(-2f + i * 2f, 0.55f, 0f),
                    new Vector3(1.8f, 1.1f, 0.35f), mat);
            }
        }

        static void EnsureWarmPractical(Transform parent, string name, Vector3 pos, Color color, float intensity, float range)
        {
            var t = parent.Find(name);
            GameObject go;
            if (t != null) go = t.gameObject;
            else
            {
                go = new GameObject(name);
                go.transform.SetParent(parent, true);
            }

            go.transform.position = pos;
            var light = go.GetComponent<Light>();
            if (light == null)
                light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.Soft;
        }

        static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var existing = parent.Find(name);
            GameObject go;
            if (existing != null) go = existing.gameObject;
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                go.transform.SetParent(parent, true);
                go.isStatic = true;
                var tag = go.AddComponent<MapMaterialTag>();
                tag.materialKey = "Mat_Metal";
            }

            go.transform.position = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null && mat != null) r.sharedMaterial = mat;
            return go;
        }

        static void StripCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c != null) Object.DestroyImmediate(c);
        }

        static void ApplyMatIfExists(string name, Material mat)
        {
            var go = GameObject.Find(name);
            var r = go != null ? go.GetComponent<MeshRenderer>() : null;
            if (r != null) r.sharedMaterial = mat;
        }

        static Material EnsureMat(string name, Color color, float roughness, float metallic, string texPath, float tiling, bool transparent = false)
        {
            const string folder = "Assets/_Project/Art/Materials/Map";
            var path = $"{folder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f - roughness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);

            if (!string.IsNullOrEmpty(texPath))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                if (tex != null)
                {
                    mat.SetTexture("_BaseMap", tex);
                    mat.mainTextureScale = new Vector2(tiling, tiling);
                }
            }

            if (transparent)
            {
                mat.SetFloat("_Surface", 1f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = 3000;
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
#endif
