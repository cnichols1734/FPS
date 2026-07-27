#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Focused additive pass for the AAA_EyeLevel_Camera failure case: close-range clutter,
    /// wall breakup, ground grime, cables, vents, posters, and softer practical lighting.
    /// </summary>
    public static class AaaEyeLevelDensify
    {
        const string Gen = "Assets/_Project/Art/Textures/Generated";
        const string MatDir = "Assets/_Project/Art/Materials/Map";
        static readonly Dictionary<string, Material> Mats = new();

        [MenuItem("Arena FPS/AAA EyeLevel Densify")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[AAA EyeLevel] Exiting play mode; run again in edit mode.");
                return;
            }

            var map = GameObject.Find("ThreeLaneMap");
            if (map == null)
            {
                Debug.LogError("[AAA EyeLevel] ThreeLaneMap missing; additive pass aborted.");
                return;
            }

            EnsureFolders();
            ClearPrevious(map.transform);
            BuildMaterials();
            BuildEyeLaneWallBreakup(map.transform);
            BuildCloseRangeStoryProps(map.transform);
            BuildGroundGrime(map.transform);
            BuildCablesAndPracticals(map.transform);
            TuneEyeLighting();
            ReframeCameras();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            Debug.Log("[AAA EyeLevel] Densify complete: close props, wall breakup, grime, lighting, cameras.");
        }

        static void EnsureFolders()
        {
            if (!Directory.Exists(Path.GetFullPath(MatDir)))
                Directory.CreateDirectory(Path.GetFullPath(MatDir));
        }

        static void ClearPrevious(Transform map)
        {
            var doomed = new List<GameObject>();
            foreach (Transform child in map)
            {
                if (child.name.StartsWith("EL_"))
                    doomed.Add(child.gameObject);
            }
            foreach (var go in doomed)
                Object.DestroyImmediate(go);
        }

        static void BuildMaterials()
        {
            Mats.Clear();
            Mats["brick"] = Mat("EL_BrickRelief", "BrickWall_Color.png", new Color(0.72f, 0.55f, 0.48f), 0f, 0.20f, 3.4f);
            Mats["concrete"] = Mat("EL_ConcreteEdge", "Concrete_Color.png", new Color(0.72f, 0.71f, 0.66f), 0f, 0.24f, 4.5f);
            Mats["metal"] = Mat("EL_RoughMetal", "Metal_Color.png", new Color(0.62f, 0.64f, 0.62f), 0.6f, 0.26f, 2f);
            Mats["plaster"] = Mat("EL_DirtyPlaster", "Plaster_Color.png", new Color(0.82f, 0.74f, 0.60f), 0f, 0.22f, 3f);
            Mats["poster"] = Mat("EL_Poster", "Poster_Military_01.png", Color.white, 0f, 0.16f, 1f);
            Mats["trash"] = Solid("EL_TrashBlack", new Color(0.025f, 0.024f, 0.022f), 0f, 0.12f);
            Mats["cardboard"] = Solid("EL_Cardboard", new Color(0.50f, 0.34f, 0.16f), 0f, 0.18f);
            Mats["sand"] = Solid("EL_Sandbag", new Color(0.58f, 0.49f, 0.34f), 0f, 0.16f);
            Mats["dark"] = Solid("EL_DarkTrim", new Color(0.055f, 0.050f, 0.045f), 0f, 0.28f);
            Mats["warm"] = Solid("EL_WarmPractical", new Color(1.0f, 0.72f, 0.38f), 0f, 0.58f);
            Mats["oil"] = Solid("EL_OilGrime", new Color(0.018f, 0.017f, 0.014f, 0.88f), 0f, 0.62f);
            Mats["paper"] = Solid("EL_StreetPaper", new Color(0.72f, 0.68f, 0.56f), 0f, 0.20f);
            Mats["blue"] = Solid("EL_BlueAccent", new Color(0.06f, 0.22f, 0.86f), 0f, 0.22f);
        }

        static Material Mat(string name, string texName, Color tint, float metallic, float smoothness, float tiling)
        {
            var mat = Solid(name, tint, metallic, smoothness);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Gen}/{texName}");
            if (tex != null)
            {
                mat.SetTexture("_BaseMap", tex);
                mat.mainTextureScale = new Vector2(tiling, tiling);
            }
            return mat;
        }

        static Material Solid(string name, Color color, float metallic, float smoothness)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void BuildEyeLaneWallBreakup(Transform map)
        {
            // Right-side close brick wall: make it stop reading as one flat plane.
            for (int i = 0; i < 7; i++)
            {
                float z = -27f + i * 3.1f;
                Box(map, $"EL_RightWall_Panel_{i}", new Vector3(-13.15f, 2.5f, z), new Vector3(0.10f, 1.75f, 1.8f), Mats["dark"], false);
                Box(map, $"EL_RightWall_Glass_{i}", new Vector3(-13.08f, 2.55f, z), new Vector3(0.055f, 1.34f, 1.34f), Mats["metal"], false);
                Box(map, $"EL_RightWall_Lintel_{i}", new Vector3(-12.98f, 3.32f, z), new Vector3(0.16f, 0.18f, 1.95f), Mats["concrete"], false);
                Box(map, $"EL_RightWall_Sill_{i}", new Vector3(-12.98f, 1.62f, z), new Vector3(0.16f, 0.16f, 1.85f), Mats["concrete"], false);
            }

            Box(map, "EL_RightWall_LowerTrim", new Vector3(-12.93f, 1.25f, -16.5f), new Vector3(0.18f, 0.22f, 23.5f), Mats["concrete"], false);
            Box(map, "EL_RightWall_UpperPipe", new Vector3(-12.86f, 4.35f, -16.5f), new Vector3(0.13f, 0.13f, 24f), Mats["metal"], false);
            Cylinder(map, "EL_RightWall_VertPipe_A", new Vector3(-12.82f, 2.5f, -23.2f), 0.10f, 4.0f, Mats["metal"]);
            Cylinder(map, "EL_RightWall_VertPipe_B", new Vector3(-12.82f, 2.6f, -11.0f), 0.10f, 4.3f, Mats["metal"]);
            Vent(map, "EL_RightWall_Vent_A", new Vector3(-12.75f, 3.65f, -19.5f), true);
            Vent(map, "EL_RightWall_Vent_B", new Vector3(-12.75f, 3.25f, -8.5f), true);
            Poster(map, "EL_RightWall_Poster", new Vector3(-12.74f, 2.4f, -14.2f), true);

            // Left perimeter wall: panels, cable boxes, conduit, and light fixtures visible in eye view.
            for (int i = 0; i < 6; i++)
            {
                float z = -27f + i * 3.8f;
                Box(map, $"EL_LeftWall_ServicePanel_{i}", new Vector3(-30.8f, 2.1f, z), new Vector3(0.10f, 1.2f, 1.6f), Mats["metal"], false);
                Box(map, $"EL_LeftWall_Frame_{i}", new Vector3(-30.72f, 2.1f, z), new Vector3(0.08f, 1.45f, 1.85f), Mats["dark"], false);
            }
            Box(map, "EL_LeftWall_ConduitLow", new Vector3(-30.68f, 1.3f, -17f), new Vector3(0.10f, 0.10f, 24f), Mats["metal"], false);
            Box(map, "EL_LeftWall_ConduitHigh", new Vector3(-30.68f, 3.55f, -17f), new Vector3(0.10f, 0.10f, 24f), Mats["metal"], false);
        }

        static void BuildCloseRangeStoryProps(Transform map)
        {
            SandbagPile(map, "EL_Foreground_Sandbags", new Vector3(-24.4f, 0f, -21.0f), 20f);
            CratePile(map, "EL_Foreground_Crates", new Vector3(-25.6f, 0f, -18.0f), -12f);
            TrashPile(map, "EL_Left_TrashPile", new Vector3(-28.7f, 0f, -23.0f));
            TrashPile(map, "EL_Right_TrashPile", new Vector3(-14.2f, 0f, -20.0f));
            PalletStack(map, "EL_Left_Pallets", new Vector3(-27.2f, 0f, -13.8f), 8f);
            PipeBundle(map, "EL_Right_PipeBundle", new Vector3(-15.0f, 0f, -10.5f), -18f);
            ConeLine(map, "EL_ConeLine", new Vector3(-22.0f, 0f, -15.5f), 5);
            NewspaperScatter(map, new Vector3(-23f, 0.095f, -19.0f));
        }

        static void BuildGroundGrime(Transform map)
        {
            Decal(map, "EL_OilPatch_Foreground", new Vector3(-22.2f, 0.08f, -21.8f), new Vector3(3.1f, 0.025f, 1.6f), Mats["oil"], 8f);
            Decal(map, "EL_GrimeTrail_Left", new Vector3(-25.6f, 0.082f, -16.4f), new Vector3(2.4f, 0.025f, 5.2f), Mats["oil"], -4f);
            Decal(map, "EL_PaperSheet_A", new Vector3(-20.7f, 0.10f, -18.9f), new Vector3(0.65f, 0.022f, 0.42f), Mats["paper"], 23f);
            Decal(map, "EL_PaperSheet_B", new Vector3(-18.6f, 0.10f, -13.6f), new Vector3(0.75f, 0.022f, 0.48f), Mats["paper"], -18f);
            Decal(map, "EL_PaperSheet_C", new Vector3(-24.2f, 0.10f, -11.4f), new Vector3(0.55f, 0.022f, 0.40f), Mats["paper"], 51f);
            for (int i = 0; i < 8; i++)
            {
                float z = -26f + i * 2.4f;
                Decal(map, $"EL_TireScuff_{i}", new Vector3(-21.6f + (i % 2) * 1.6f, 0.085f, z), new Vector3(0.42f, 0.018f, 1.55f), Mats["oil"], i * 7f);
            }
        }

        static void BuildCablesAndPracticals(Transform map)
        {
            Beam(map, "EL_OverheadCable_A", new Vector3(-30.4f, 4.1f, -25.5f), new Vector3(-13.2f, 3.7f, -22.4f), 0.035f, Mats["dark"]);
            Beam(map, "EL_OverheadCable_B", new Vector3(-30.3f, 3.9f, -17.5f), new Vector3(-13.1f, 3.4f, -15.1f), 0.03f, Mats["dark"]);
            Beam(map, "EL_OverheadCable_C", new Vector3(-30.2f, 4.2f, -10.5f), new Vector3(-13.0f, 3.6f, -8.5f), 0.03f, Mats["dark"]);

            LightFixture(map, "EL_Left_Practical_A", new Vector3(-30.58f, 3.0f, -23.0f), new Vector3(1f, 0f, 0f));
            LightFixture(map, "EL_Left_Practical_B", new Vector3(-30.58f, 3.0f, -14.0f), new Vector3(1f, 0f, 0f));
            LightFixture(map, "EL_Right_Practical", new Vector3(-12.75f, 3.15f, -18.4f), new Vector3(-1f, 0f, 0f));
        }

        static void TuneEyeLighting()
        {
            var sun = Object.FindAnyObjectByType<Light>();
            if (sun != null && sun.type == LightType.Directional)
            {
                sun.intensity = 1.12f;
                sun.shadowStrength = 0.48f;
                sun.color = new Color(0.95f, 0.98f, 1f);
            }
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.48f, 0.54f, 0.64f);
            RenderSettings.ambientEquatorColor = new Color(0.36f, 0.33f, 0.29f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.20f, 0.17f);
            RenderSettings.fogDensity = 0.0048f;
            RenderSettings.fogColor = new Color(0.52f, 0.56f, 0.60f);
        }

        static void ReframeCameras()
        {
            SetCamera("AAA_EyeLevel_Camera", new Vector3(-24.2f, 1.68f, -24.4f), new Vector3(-17.2f, 1.45f, -8.2f), 70f);
            SetCamera("AAA_MidLane_Camera", new Vector3(0f, 2.05f, -21.5f), new Vector3(0f, 1.45f, 1.5f), 64f);
        }

        static void SandbagPile(Transform map, string name, Vector3 pos, float yaw)
        {
            var root = Empty(map, name, pos, yaw);
            for (int row = 0; row < 3; row++)
            for (int i = 0; i < 5; i++)
                Box(root, $"Bag_{row}_{i}", new Vector3((i - 2f) * 0.65f + row * 0.18f, 0.22f + row * 0.24f, 0f), new Vector3(0.62f, 0.24f, 0.45f), Mats["sand"], true, 0f);
        }

        static void CratePile(Transform map, string name, Vector3 pos, float yaw)
        {
            var root = Empty(map, name, pos, yaw);
            for (int i = 0; i < 5; i++)
                Box(root, $"Crate_{i}", new Vector3((i % 3) * 0.82f, 0.43f + (i / 3) * 0.78f, (i / 3) * 0.65f), new Vector3(0.78f, 0.78f, 0.78f), i % 2 == 0 ? Mats["cardboard"] : Mats["concrete"], true, i * 8f);
        }

        static void TrashPile(Transform map, string name, Vector3 pos)
        {
            var root = Empty(map, name, pos, 0f);
            for (int i = 0; i < 9; i++)
            {
                float x = (Hash01(i, 2, 7) - 0.5f) * 2.1f;
                float z = (Hash01(i, 3, 9) - 0.5f) * 1.7f;
                Box(root, $"Bag_{i}", new Vector3(x, 0.24f, z), new Vector3(0.50f, 0.46f, 0.50f), Mats["trash"], true, i * 13f);
            }
            Box(root, "BrokenPoster", new Vector3(0.2f, 0.08f, -0.8f), new Vector3(1.0f, 0.04f, 0.75f), Mats["poster"], false, -18f);
        }

        static void PalletStack(Transform map, string name, Vector3 pos, float yaw)
        {
            var root = Empty(map, name, pos, yaw);
            for (int level = 0; level < 3; level++)
            {
                Box(root, $"PalletBase_{level}", new Vector3(0f, 0.16f + level * 0.22f, 0f), new Vector3(1.9f, 0.08f, 1.1f), Mats["cardboard"], true, 0f);
                for (int slat = -1; slat <= 1; slat++)
                    Box(root, $"PalletSlat_{level}_{slat}", new Vector3(slat * 0.55f, 0.23f + level * 0.22f, 0f), new Vector3(0.16f, 0.08f, 1.2f), Mats["dark"], true, 0f);
            }
        }

        static void PipeBundle(Transform map, string name, Vector3 pos, float yaw)
        {
            var root = Empty(map, name, pos, yaw);
            for (int i = 0; i < 5; i++)
                Cylinder(root, $"Pipe_{i}", new Vector3(0f, 0.20f + i * 0.20f, (i % 2) * 0.30f), 0.16f, 2.8f, Mats["metal"], Axis.Z);
        }

        static void ConeLine(Transform map, string name, Vector3 pos, int count)
        {
            var root = Empty(map, name, pos, -8f);
            for (int i = 0; i < count; i++)
            {
                Box(root, $"ConeBase_{i}", new Vector3(i * 0.95f, 0.08f, 0f), new Vector3(0.44f, 0.16f, 0.44f), Mats["dark"], true, 0f);
                Box(root, $"ConeBody_{i}", new Vector3(i * 0.95f, 0.42f, 0f), new Vector3(0.28f, 0.60f, 0.28f), Mats["warm"], true, 0f);
            }
        }

        static void NewspaperScatter(Transform map, Vector3 origin)
        {
            for (int i = 0; i < 10; i++)
            {
                var p = origin + new Vector3((Hash01(i, 4, 3) - 0.5f) * 6f, 0f, (Hash01(i, 5, 5) - 0.5f) * 7f);
                Decal(map, $"EL_Newsprint_{i}", p, new Vector3(0.45f + Hash01(i, 1, 2) * 0.35f, 0.018f, 0.32f + Hash01(i, 2, 2) * 0.25f), Mats["paper"], Hash01(i, 3, 2) * 180f);
            }
        }

        static void Vent(Transform map, string name, Vector3 pos, bool faceEast)
        {
            Box(map, name + "_Back", pos, new Vector3(0.16f, 0.85f, 1.05f), Mats["metal"], false, 0f);
            for (int i = -2; i <= 2; i++)
                Box(map, name + "_Slat_" + i, pos + new Vector3(faceEast ? 0.08f : -0.08f, i * 0.13f, 0f), new Vector3(0.08f, 0.035f, 0.96f), Mats["dark"], false, 0f);
        }

        static void Poster(Transform map, string name, Vector3 pos, bool faceEast)
        {
            Box(map, name, pos, new Vector3(0.055f, 1.55f, 1.12f), Mats["poster"], false, 0f);
        }

        static void LightFixture(Transform map, string name, Vector3 pos, Vector3 throwDir)
        {
            Box(map, name + "_Box", pos, new Vector3(0.28f, 0.18f, 0.52f), Mats["metal"], false, 0f);
            Box(map, name + "_Glow", pos + throwDir * 0.12f + Vector3.down * 0.03f, new Vector3(0.08f, 0.12f, 0.42f), Mats["warm"], false, 0f);
            var go = new GameObject(name + "_Light");
            go.transform.SetParent(map, true);
            go.transform.position = pos + throwDir * 0.35f;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.72f, 0.42f);
            l.intensity = 2.2f;
            l.range = 8.5f;
            l.shadows = LightShadows.None;
        }

        static void Decal(Transform map, string name, Vector3 pos, Vector3 scale, Material mat, float yaw)
        {
            Box(map, name, pos, scale, mat, false, yaw);
        }

        static Transform Empty(Transform parent, string name, Vector3 pos, float yaw)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            return go.transform;
        }

        static GameObject Box(Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat, bool collider)
            => Box(parent, name, localPos, scale, mat, collider, 0f);

        static GameObject Box(Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat, bool collider, float yaw)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = scale;
            go.isStatic = true;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = mat;
            if (!collider)
            {
                var c = go.GetComponent<Collider>();
                if (c != null) Object.DestroyImmediate(c);
            }
            return go;
        }

        enum Axis { Y, X, Z }

        static GameObject Cylinder(Transform parent, string name, Vector3 localPos, float diameter, float length, Material mat, Axis axis = Axis.Y)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(diameter, length * 0.5f, diameter);
            if (axis == Axis.X) go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            else if (axis == Axis.Z) go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = mat;
            go.isStatic = true;
            return go;
        }

        static void Beam(Transform parent, string name, Vector3 a, Vector3 b, float thickness, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, true);
            var mid = (a + b) * 0.5f;
            var dir = b - a;
            go.transform.position = mid;
            go.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            go.transform.localScale = new Vector3(thickness, thickness, dir.magnitude);
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = mat;
            Object.DestroyImmediate(go.GetComponent<Collider>());
        }

        static void SetCamera(string name, Vector3 pos, Vector3 lookAt, float fov)
        {
            var go = GameObject.Find(name);
            if (go == null)
            {
                var rig = GameObject.Find("__AaaCaptureRig") ?? new GameObject("__AaaCaptureRig");
                go = new GameObject(name);
                go.transform.SetParent(rig.transform, true);
                go.AddComponent<Camera>();
            }
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((lookAt - pos).normalized, Vector3.up);
            var cam = go.GetComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 180f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.enabled = false;
        }

        static float Hash01(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 1442695041;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0x7fffffff) / (float)int.MaxValue;
            }
        }
    }
}
#endif
