#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using ArenaFps.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Additive COD-readability pass: denser modular facades, storytelling props, derived normal maps,
    /// softer lighting, and corrected screenshot camera composition. Does not touch gameplay systems.
    /// </summary>
    public static class AaaEnvironmentPass2
    {
        const string Gen = "Assets/_Project/Art/Textures/Generated";
        const string MatDir = "Assets/_Project/Art/Materials/Map";

        static readonly Dictionary<string, Material> Mats = new();

        [MenuItem("Arena FPS/AAA Environment Pass 2 (Additive Densify)")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[AAA Pass2] Exiting play mode; run again once Unity returns to edit mode.");
                return;
            }

            var root = GameObject.Find("ThreeLaneMap");
            if (root == null)
            {
                Debug.LogError("[AAA Pass2] ThreeLaneMap missing. Not rebuilding to avoid touching gameplay state.");
                return;
            }

            EnsureFolders();
            ClearPrevious(root.transform);
            BuildDerivedNormalTextures();
            BuildMaterials();
            AaaMaterialPass.Run();
            RebindGeneratedMaterials(root.transform);

            AddModularFacadeDepth(root.transform);
            AddStoryProps(root.transform);
            AddMidLaneBeautySet(root.transform);
            RepairCriticalCovers(root.transform);
            EnsureCompositeCoverColliders(root.transform);
            TuneSoftLighting(root.transform);
            ReframeCaptureCameras();

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            Debug.Log("[AAA Pass2] Additive densify complete: modular trims, props, material normals, lighting, cameras.");
        }

        static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder(Gen))
                Directory.CreateDirectory(Path.GetFullPath(Gen));
            if (!AssetDatabase.IsValidFolder(MatDir))
                Directory.CreateDirectory(Path.GetFullPath(MatDir));
        }

        static void ClearPrevious(Transform root)
        {
            var doomed = new List<GameObject>();
            foreach (Transform child in root)
            {
                if (child.name.StartsWith("P2_"))
                    doomed.Add(child.gameObject);
            }
            foreach (var go in doomed)
                Object.DestroyImmediate(go);
        }

        static void BuildDerivedNormalTextures()
        {
            CreateNormal("P2_Asphalt_Normal.png", 13, 0.22f, TextureKind.Asphalt);
            CreateNormal("P2_Concrete_Normal.png", 27, 0.16f, TextureKind.Concrete);
            CreateNormal("P2_Brick_Normal.png", 41, 0.28f, TextureKind.Brick);
            CreateNormal("P2_Metal_Normal.png", 59, 0.20f, TextureKind.Metal);
        }

        enum TextureKind { Asphalt, Concrete, Brick, Metal }

        static void CreateNormal(string fileName, int seed, float strength, TextureKind kind)
        {
            var path = $"{Gen}/{fileName}";
            var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false, true);
            for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                float h = Height(x, y, seed, kind);
                float hx = Height(x + 1, y, seed, kind) - h;
                float hy = Height(x, y + 1, seed, kind) - h;
                var n = new Vector3(-hx * strength, -hy * strength, 1f).normalized;
                tex.SetPixel(x, y, new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f));
            }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }
        }

        static float Height(int x, int y, int seed, TextureKind kind)
        {
            float n = Mathf.PerlinNoise((x + seed) * 0.045f, (y - seed) * 0.045f) * 0.65f + Hash01(x, y, seed) * 0.35f;
            if (kind == TextureKind.Brick)
            {
                int row = y / 24;
                int off = (row & 1) * 32;
                if ((x + off) % 64 < 4 || y % 24 < 4) n *= 0.22f;
            }
            else if (kind == TextureKind.Metal)
            {
                n += ((x / 7) % 2) * 0.18f;
            }
            else if (kind == TextureKind.Concrete && (x % 72 < 2 || y % 72 < 2))
            {
                n *= 0.45f;
            }
            return n;
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

        static void BuildMaterials()
        {
            Mats.Clear();
            Mats["asphaltWet"] = Mat("P2_AsphaltWet", "Asphalt_Color.png", "P2_Asphalt_Normal.png", new Color(0.42f, 0.43f, 0.43f), 0f, 0.42f, 24f);
            Mats["concrete"] = Mat("P2_ConcreteVaried", "Concrete_Color.png", "P2_Concrete_Normal.png", new Color(0.78f, 0.77f, 0.72f), 0f, 0.24f, 5f);
            Mats["brickDark"] = Mat("P2_BrickDark", "BrickWall_Color.png", "P2_Brick_Normal.png", new Color(0.72f, 0.55f, 0.48f), 0f, 0.18f, 3.2f);
            Mats["plasterWarm"] = Mat("P2_PlasterWarm", "Plaster_Color.png", null, new Color(0.86f, 0.78f, 0.64f), 0f, 0.22f, 3.4f);
            Mats["metal"] = Mat("P2_MetalRough", "Metal_Color.png", "P2_Metal_Normal.png", new Color(0.68f, 0.70f, 0.68f), 0.65f, 0.30f, 2.2f);
            Mats["poster"] = Mat("P2_PosterMilitary", "Poster_Military_01.png", null, Color.white, 0f, 0.18f, 1f);
            Mats["glass"] = Solid("P2_SoftGlass", new Color(0.035f, 0.06f, 0.075f, 0.85f), 0f, 0.68f);
            Mats["trim"] = Solid("P2_DarkTrim", new Color(0.075f, 0.065f, 0.055f), 0f, 0.28f);
            Mats["clothBlue"] = Solid("P2_BlueCloth", new Color(0.06f, 0.22f, 0.78f), 0f, 0.18f);
            Mats["clothRed"] = Solid("P2_RedCloth", new Color(0.76f, 0.08f, 0.055f), 0f, 0.18f);
            Mats["warmLight"] = Solid("P2_WarmLightPanel", new Color(1f, 0.78f, 0.42f), 0f, 0.52f);
        }

        static Material Mat(string name, string colorTex, string normalTex, Color tint, float metallic, float smoothness, float tiling)
        {
            var mat = Solid(name, tint, metallic, smoothness);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Gen}/{colorTex}");
            if (tex != null)
            {
                mat.SetTexture("_BaseMap", tex);
                mat.mainTextureScale = new Vector2(tiling, tiling);
            }
            if (!string.IsNullOrEmpty(normalTex))
            {
                var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Gen}/{normalTex}");
                if (normal != null && mat.HasProperty("_BumpMap"))
                {
                    mat.SetTexture("_BumpMap", normal);
                    mat.SetFloat("_BumpScale", 0.75f);
                    mat.EnableKeyword("_NORMALMAP");
                }
            }
            EditorUtility.SetDirty(mat);
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
            return mat;
        }

        static void RebindGeneratedMaterials(Transform root)
        {
            foreach (var tag in root.GetComponentsInChildren<MapMaterialTag>(true))
            {
                Material mat = null;
                if (tag.materialKey == "Mat_Asphalt") mat = Mats["asphaltWet"];
                else if (tag.materialKey == "Mat_Brick") mat = Mats["brickDark"];
                else if (tag.materialKey == "Mat_Concrete") mat = Mats["concrete"];
                else if (tag.materialKey == "Mat_Plaster") mat = Mats["plasterWarm"];
                else if (tag.materialKey == "Mat_Metal") mat = Mats["metal"];
                var r = tag.GetComponent<MeshRenderer>();
                if (r != null && mat != null) r.sharedMaterial = mat;
            }
        }

        static void AddModularFacadeDepth(Transform root)
        {
            Facade(root, "P2_WestSouthFacade", -10.05f, -29f, -15f, true, Mats["brickDark"]);
            Facade(root, "P2_WestMidFacade", -11.5f, -9f, 8f, true, Mats["plasterWarm"]);
            Facade(root, "P2_WestNorthFacade", -9.3f, 13f, 30f, true, Mats["brickDark"]);
            Facade(root, "P2_EastSouthFacade", 10.1f, -31f, -13f, false, Mats["plasterWarm"]);
            Facade(root, "P2_EastMidFacade", 11.25f, -8f, 12f, false, Mats["brickDark"]);
            Facade(root, "P2_EastNorthFacade", 10.2f, 18f, 32f, false, Mats["plasterWarm"]);

            FireEscape(root, "P2_FireEscape_W", new Vector3(-10.25f, 4.1f, 2f), true);
            FireEscape(root, "P2_FireEscape_E", new Vector3(10.25f, 4.1f, -2.5f), false);
            RooftopSilhouette(root, "P2_Rooftop_W", new Vector3(-15f, 8.8f, -3f));
            RooftopSilhouette(root, "P2_Rooftop_E", new Vector3(15.5f, 9.8f, 3f));
        }

        static void Facade(Transform root, string prefix, float x, float z0, float z1, bool faceEast, Material body)
        {
            float normal = faceEast ? 1f : -1f;
            float zMid = (z0 + z1) * 0.5f;
            float len = Mathf.Abs(z1 - z0);
            Box(root, prefix + "_Panel", new Vector3(x, 3.2f, zMid), new Vector3(0.18f, 5.8f, len), body, false);
            for (int i = 0; i < Mathf.Max(3, Mathf.FloorToInt(len / 4f)); i++)
            {
                float z = z0 + 2f + i * 4f;
                Window(root, prefix + "_WinA_" + i, new Vector3(x + normal * 0.12f, 3.0f, z), faceEast);
                Window(root, prefix + "_WinB_" + i, new Vector3(x + normal * 0.12f, 5.1f, z + 0.5f), faceEast);
                Box(root, prefix + "_Sill_" + i, new Vector3(x + normal * 0.19f, 2.16f, z), new Vector3(0.28f, 0.12f, 1.65f), Mats["concrete"], false);
            }
            Box(root, prefix + "_CorniceLow", new Vector3(x + normal * 0.16f, 1.35f, zMid), new Vector3(0.28f, 0.22f, len + 0.5f), Mats["trim"], false);
            Box(root, prefix + "_CorniceTop", new Vector3(x + normal * 0.16f, 6.45f, zMid), new Vector3(0.32f, 0.32f, len + 0.5f), Mats["concrete"], false);
            Awning(root, prefix + "_Awning", new Vector3(x + normal * 0.55f, 2.55f, zMid - len * 0.25f), faceEast, faceEast ? Mats["clothBlue"] : Mats["clothRed"]);
            Poster(root, prefix + "_Poster", new Vector3(x + normal * 0.21f, 2.95f, zMid + len * 0.25f), faceEast);
        }

        static void Window(Transform root, string name, Vector3 pos, bool faceEast)
        {
            Box(root, name + "_Recess", pos - Vector3.right * (faceEast ? 0.04f : -0.04f), new Vector3(0.10f, 1.42f, 1.38f), Mats["trim"], false);
            Box(root, name + "_Glass", pos + Vector3.right * (faceEast ? 0.02f : -0.02f), new Vector3(0.06f, 1.15f, 1.08f), Mats["glass"], false);
            Box(root, name + "_Lintel", pos + new Vector3(faceEast ? 0.06f : -0.06f, 0.73f, 0f), new Vector3(0.16f, 0.16f, 1.48f), Mats["concrete"], false);
        }

        static void Awning(Transform root, string name, Vector3 pos, bool faceEast, Material mat)
        {
            var go = Box(root, name, pos, new Vector3(1.1f, 0.16f, 4.2f), mat, false);
            go.transform.rotation = Quaternion.Euler(0f, 0f, faceEast ? -8f : 8f);
        }

        static void Poster(Transform root, string name, Vector3 pos, bool faceEast)
        {
            Box(root, name, pos, new Vector3(0.055f, 1.65f, 1.18f), Mats["poster"], false);
        }

        static void FireEscape(Transform root, string name, Vector3 pos, bool faceEast)
        {
            float n = faceEast ? 1f : -1f;
            for (int level = 0; level < 3; level++)
            {
                float y = pos.y + level * 1.55f;
                Box(root, name + "_Deck_" + level, new Vector3(pos.x + n * 0.55f, y, pos.z), new Vector3(1.1f, 0.10f, 3.4f), Mats["metal"], false);
                Box(root, name + "_Rail_" + level, new Vector3(pos.x + n * 1.08f, y + 0.52f, pos.z), new Vector3(0.08f, 0.9f, 3.4f), Mats["metal"], false);
            }
            Box(root, name + "_Ladder", new Vector3(pos.x + n * 1.1f, pos.y + 1.5f, pos.z - 1.7f), new Vector3(0.08f, 4.3f, 0.12f), Mats["metal"], false);
        }

        static void RooftopSilhouette(Transform root, string name, Vector3 pos)
        {
            Box(root, name + "_HVAC", pos, new Vector3(2.2f, 0.8f, 1.4f), Mats["metal"], true);
            Box(root, name + "_Duct", pos + new Vector3(-1.6f, -0.15f, 1.2f), new Vector3(3.2f, 0.35f, 0.42f), Mats["metal"], false);
            Cylinder(root, name + "_Tank", pos + new Vector3(2f, 0.8f, -1.2f), 1.2f, 1.6f, Mats["metal"]);
        }

        static void AddStoryProps(Transform root)
        {
            MarketStall(root, "P2_MarketStall_East", new Vector3(22f, 0f, -7f), -12f, Mats["clothRed"]);
            MarketStall(root, "P2_MarketStall_West", new Vector3(-22f, 0f, 9f), 18f, Mats["clothBlue"]);
            TrashCluster(root, "P2_Trash_West", new Vector3(-25f, 0f, -15f));
            TrashCluster(root, "P2_Trash_East", new Vector3(25f, 0f, 16f));
            PipeRack(root, "P2_PipeRack_Mid", new Vector3(-5.5f, 0f, 6.5f));
            BarricadeRun(root, "P2_Barricades_BluePush", new Vector3(-3.5f, 0f, -15f), 20f);
            BarricadeRun(root, "P2_Barricades_RedPush", new Vector3(3.5f, 0f, 15f), -160f);
        }

        static void AddMidLaneBeautySet(Transform root)
        {
            Box(root, "P2_Mid_OverheadSign", new Vector3(0f, 5.2f, -1.8f), new Vector3(8.2f, 1.3f, 0.16f), Mats["poster"], false);
            Box(root, "P2_Mid_SignFrame", new Vector3(0f, 5.2f, -1.88f), new Vector3(8.55f, 1.62f, 0.12f), Mats["trim"], false);
            for (int i = -3; i <= 3; i++)
                Box(root, "P2_Mid_StringLight_" + i, new Vector3(i * 1.35f, 4.3f + Mathf.Abs(i) * 0.04f, -3.2f), new Vector3(0.22f, 0.22f, 0.22f), Mats["warmLight"], false);
            Box(root, "P2_Mid_ForegroundWetPatch", new Vector3(3f, 0.075f, -10f), new Vector3(5.5f, 0.025f, 3.2f), Mats["asphaltWet"], false);
            Cylinder(root, "P2_Mid_DrainCover", new Vector3(-2.2f, 0.13f, -8.4f), 1.1f, 0.05f, Mats["metal"]);
        }

        static void MarketStall(Transform root, string name, Vector3 pos, float yaw, Material cloth)
        {
            var parent = Empty(root, name, pos, yaw);
            Box(parent, "Counter", new Vector3(0f, 0.65f, 0f), new Vector3(3.4f, 1.0f, 1.2f), Mats["concrete"], true);
            Box(parent, "Canopy", new Vector3(0f, 2.1f, 0f), new Vector3(4.1f, 0.18f, 2.2f), cloth, false);
            Box(parent, "BackSign", new Vector3(0f, 1.5f, 0.72f), new Vector3(3.2f, 1.3f, 0.08f), Mats["poster"], false);
        }

        static void TrashCluster(Transform root, string name, Vector3 pos)
        {
            var p = Empty(root, name, pos, 0f);
            for (int i = 0; i < 7; i++)
            {
                var offset = new Vector3((Hash01(i, 0, 3) - 0.5f) * 2.4f, 0.25f, (Hash01(i, 1, 5) - 0.5f) * 1.8f);
                Box(p, "Bag_" + i, offset, new Vector3(0.55f, 0.5f, 0.55f), Mats["trim"], true);
            }
            Box(p, "PosterPile", new Vector3(0.4f, 0.08f, -0.8f), new Vector3(1.2f, 0.06f, 0.8f), Mats["poster"], false);
        }

        static void PipeRack(Transform root, string name, Vector3 pos)
        {
            var p = Empty(root, name, pos, 28f);
            for (int i = 0; i < 5; i++)
                Cylinder(p, "Pipe_" + i, new Vector3(0f, 0.28f + i * 0.22f, (i % 2) * 0.35f), 0.18f, 3.8f, Mats["metal"], Axis.Z);
            Box(p, "RackCollider", new Vector3(0f, 0.7f, 0.2f), new Vector3(4f, 1.2f, 0.8f), Mats["metal"], true);
        }

        static void BarricadeRun(Transform root, string name, Vector3 pos, float yaw)
        {
            var p = Empty(root, name, pos, yaw);
            for (int i = 0; i < 4; i++)
            {
                Box(p, "Barrier_" + i, new Vector3((i - 1.5f) * 1.35f, 0.45f, 0f), new Vector3(1.2f, 0.9f, 0.42f), Mats["concrete"], true);
                Box(p, "Stripe_" + i, new Vector3((i - 1.5f) * 1.35f, 0.65f, -0.24f), new Vector3(0.82f, 0.20f, 0.04f), Mats["warmLight"], false);
            }
        }

        static Transform Empty(Transform root, string name, Vector3 pos, float yaw)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            return go.transform;
        }

        static void EnsureCompositeCoverColliders(Transform root)
        {
            foreach (Transform child in root)
            {
                if (!child.name.StartsWith("Cover_"))
                    continue;
                var renderers = child.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0)
                    continue;
                var bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

                foreach (var existing in child.GetComponents<Collider>())
                    Object.DestroyImmediate(existing);

                var c = child.gameObject.AddComponent<BoxCollider>();
                c.center = child.InverseTransformPoint(bounds.center);
                var localMin = child.InverseTransformPoint(bounds.min);
                var localMax = child.InverseTransformPoint(bounds.max);
                c.size = new Vector3(Mathf.Abs(localMax.x - localMin.x), Mathf.Abs(localMax.y - localMin.y), Mathf.Abs(localMax.z - localMin.z));
            }
        }

        static void RepairCriticalCovers(Transform root)
        {
            RepairCover(root, "Cover_A", new Vector3(-12.2f, 0f, -18f), CoverKind.Crates);
            RepairCover(root, "Cover_B", new Vector3(12.2f, 0f, -16f), CoverKind.Dumpster);
            RepairCover(root, "Cover_C", new Vector3(0f, 0f, 9.2f), CoverKind.Barriers);
        }

        enum CoverKind { Crates, Dumpster, Barriers }

        static void RepairCover(Transform root, string name, Vector3 pos, CoverKind kind)
        {
            var t = root.Find(name);
            GameObject go;
            if (t == null)
            {
                go = new GameObject(name);
                go.transform.SetParent(root, true);
            }
            else
            {
                go = t.gameObject;
            }

            go.transform.position = pos;
            go.transform.rotation = Quaternion.identity;

            if (go.transform.childCount == 0)
            {
                if (kind == CoverKind.Crates)
                {
                    Box(go.transform, "RepairCrate_A", new Vector3(-0.75f, 0.55f, 0f), new Vector3(1.05f, 1.05f, 1.05f), Mats["concrete"], true);
                    Box(go.transform, "RepairCrate_B", new Vector3(0.45f, 0.55f, 0.08f), new Vector3(1.05f, 1.05f, 1.05f), Mats["metal"], true);
                    Box(go.transform, "RepairCrate_C", new Vector3(-0.15f, 1.55f, -0.12f), new Vector3(1.0f, 0.9f, 1.0f), Mats["trim"], true);
                }
                else if (kind == CoverKind.Dumpster)
                {
                    Box(go.transform, "RepairDumpster_Body", new Vector3(0f, 0.85f, 0f), new Vector3(2.65f, 1.5f, 1.45f), Mats["metal"], true);
                    Box(go.transform, "RepairDumpster_Lid", new Vector3(0f, 1.67f, 0.08f), new Vector3(2.75f, 0.18f, 1.35f), Mats["trim"], false);
                }
                else
                {
                    for (int i = 0; i < 5; i++)
                    {
                        Box(go.transform, "RepairBarrier_" + i, new Vector3((i - 2f) * 1.35f, 0.45f, 0f), new Vector3(1.2f, 0.9f, 0.42f), Mats["concrete"], true);
                    }
                }
            }
        }

        static void TuneSoftLighting(Transform root)
        {
            var light = Object.FindAnyObjectByType<Light>();
            if (light != null && light.type == LightType.Directional)
            {
                light.intensity = 1.08f;
                light.shadowStrength = 0.58f;
                light.color = new Color(0.92f, 0.96f, 1f);
                light.transform.rotation = Quaternion.Euler(50f, -38f, 0f);
            }
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.42f, 0.48f, 0.58f);
            RenderSettings.ambientEquatorColor = new Color(0.32f, 0.30f, 0.27f);
            RenderSettings.ambientGroundColor = new Color(0.18f, 0.16f, 0.14f);
            RenderSettings.fog = true;
            RenderSettings.fogDensity = 0.0055f;
            RenderSettings.fogColor = new Color(0.50f, 0.54f, 0.58f);

            Practical(root, "P2_Practical_KeyMid", new Vector3(-4.5f, 4f, -2f), new Color(1f, 0.70f, 0.42f), 2.2f, 13f);
            Practical(root, "P2_Practical_FillBlue", new Vector3(-12f, 3.2f, -25f), new Color(0.42f, 0.58f, 1f), 1.2f, 16f);
            Practical(root, "P2_Practical_FillRed", new Vector3(12f, 3.2f, 25f), new Color(1f, 0.38f, 0.32f), 1.2f, 16f);
        }

        static void Practical(Transform root, string name, Vector3 pos, Color color, float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, true);
            go.transform.position = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.shadows = LightShadows.None;
        }

        static void ReframeCaptureCameras()
        {
            CameraRig("AAA_Aerial_Camera", new Vector3(0f, 46f, -30f), new Vector3(0f, 1.1f, 0f), 50f);
            CameraRig("AAA_EyeLevel_Camera", new Vector3(-24f, 1.75f, -27f), new Vector3(-13f, 1.55f, -4f), 67f);
            CameraRig("AAA_MidLane_Camera", new Vector3(7.2f, 1.75f, -18.5f), new Vector3(0f, 1.55f, 2.2f), 62f);
        }

        static void CameraRig(string name, Vector3 pos, Vector3 lookAt, float fov)
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

        enum Axis { Y, X, Z }

        static GameObject Box(Transform root, string name, Vector3 pos, Vector3 scale, Material mat, bool collider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(root, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.identity;
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

        static GameObject Cylinder(Transform root, string name, Vector3 pos, float diameter, float length, Material mat, Axis axis = Axis.Y)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(root, true);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(diameter, length * 0.5f, diameter);
            if (axis == Axis.X) go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            else if (axis == Axis.Z) go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = mat;
            go.isStatic = true;
            return go;
        }
    }
}
#endif
