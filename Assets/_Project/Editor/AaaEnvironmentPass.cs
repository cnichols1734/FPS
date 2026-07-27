#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using ArenaFps.Core;
using ArenaFps.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ArenaFps.Editor
{
    /// <summary>
    /// AAA Environment Pass — Overflow-derived 6v6 arena geometry (source of truth).
    /// Target footprint: 118 x 154 m (~3.03x live 5,995 m2). Spec positions scaled by 1.22;
    /// human-scale dimensions (lane widths, storeys, doors, cover heights) stay unscaled.
    /// Rule: every collider must have a visible mesh; decorative trim above 2.2 m has no collider.
    /// </summary>
    public static class AaaEnvironmentPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string TextureDir = "Assets/_Project/Art/Textures/AaaGenerated";
        const string MaterialDir = "Assets/_Project/Art/Materials/Map";

        const float PosScale = 1.22f;
        const float MapWidth = 118f;
        const float MapLength = 154f;
        const float WallHalfX = 60f;
        const float WallHalfZ = 78f;
        const float WallHeight = 8f;
        const float HeadHeight = 2.2f;

        static readonly HashSet<string> PreserveRoots = new()
        {
            "Directional Light", "Global Volume", "Player", "PlayerSpawn",
            "Spawn_Blue_1", "Spawn_Blue_2", "Spawn_Blue_3",
            "Spawn_Blue_4", "Spawn_Blue_5", "Spawn_Blue_6",
            "Spawn_Red_1", "Spawn_Red_2", "Spawn_Red_3",
            "Spawn_Red_4", "Spawn_Red_5", "Spawn_Red_6",
        };

        static readonly Dictionary<string, Material> Mats = new();

        static float SX(float x) => x * PosScale;
        static float SZ(float z) => z * PosScale;
        static Vector3 P(float x, float z) => new(x * PosScale, 0f, z * PosScale);
        static Vector3 P(float x, float y, float z) => new(x * PosScale, y, z * PosScale);

        [MenuItem("Arena FPS/AAA Environment Pass")]
        public static void Run()
        {
            var scene = EditorSceneManager.GetActiveScene().path.EndsWith("Arena.unity")
                ? EditorSceneManager.GetActiveScene()
                : EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            ClearPreviousEnvironment();
            EnsureFolders();
            BuildProceduralTextures();
            BuildMaterials();

            var root = new GameObject("ThreeLaneMap");
            root.isStatic = true;
            SetStatic(root);

            BuildGroundAndShell(root.transform);
            BuildOverflowStructures(root.transform);
            BuildDoglegLanes(root.transform);
            BuildVerticality(root.transform);
            BuildCoverAndDensity(root.transform);
            BuildSilhouetteDressing(root.transform);
            BuildCaptureRig();
            PlaceSpawnsAndPlayer();
            TuneLighting();
            StripIllegalColliders(root.transform);

            try { SpawnArenaCombat.Run(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ArenaFps] SpawnArenaCombat skipped after environment rebuild: {ex.Message}");
                BakeNavMeshFallback();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ArenaFps] AAA Environment Pass complete: Overflow layout {MapWidth}x{MapLength} m (~3.03x baseline area).");
        }

        static void ClearPreviousEnvironment()
        {
            var doomed = new List<GameObject>();
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (PreserveRoots.Contains(go.name)) continue;
                string n = go.name;
                if (n == "ThreeLaneMap" || n == "__NavMeshSurface" || n == "__AaaCaptureRig"
                    || n == "__NavMeshLinks" || n == "CK_CityKitRoot" || n == "__AAA_COD_LaneDressingFix"
                    || n == "AAA_Lighting_Rig" || n == "AAA_ReflectionProbe_Mid")
                { doomed.Add(go); continue; }

                if (n.StartsWith("PB_") || n.StartsWith("AAA_") || n.StartsWith("Cover_")
                    || n.StartsWith("Prop_") || n.StartsWith("Bldg_") || n.StartsWith("P2_")
                    || n.StartsWith("FD_") || n.StartsWith("EL_") || n.StartsWith("PH_")
                    || n.StartsWith("KP_") || n.StartsWith("LD_") || n.StartsWith("CK_")
                    || n.StartsWith("Wall_") || n.StartsWith("Road_") || n.StartsWith("Col_"))
                    doomed.Add(go);
            }
            foreach (var go in doomed) Object.DestroyImmediate(go);
        }

        static void EnsureFolders()
        {
            EnsureFolder("Assets/_Project/Art");
            EnsureFolder("Assets/_Project/Art/Textures");
            EnsureFolder(TextureDir);
            EnsureFolder("Assets/_Project/Art/Materials");
            EnsureFolder(MaterialDir);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var normalized = path.Replace("\\", "/");
            var parts = normalized.Split('/');
            var current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        #region Textures / Materials
        static void BuildProceduralTextures()
        {
            CreateTexture("AAA_AsphaltCracked.png", new Color(0.09f, 0.095f, 0.1f), new Color(0.24f, 0.24f, 0.23f), 11, TexturePattern.Asphalt);
            CreateTexture("AAA_ConcretePanel.png", new Color(0.36f, 0.35f, 0.33f), new Color(0.68f, 0.66f, 0.61f), 23, TexturePattern.Concrete);
            CreateTexture("AAA_RedBrickMixed.png", new Color(0.23f, 0.11f, 0.08f), new Color(0.62f, 0.31f, 0.21f), 37, TexturePattern.Brick);
            CreateTexture("AAA_PlasterAged.png", new Color(0.45f, 0.42f, 0.36f), new Color(0.82f, 0.78f, 0.68f), 41, TexturePattern.Plaster);
            CreateTexture("AAA_CorrugatedMetal.png", new Color(0.18f, 0.20f, 0.21f), new Color(0.58f, 0.62f, 0.64f), 53, TexturePattern.Corrugated);
            CreateTexture("AAA_WoodPallet.png", new Color(0.20f, 0.12f, 0.06f), new Color(0.57f, 0.36f, 0.18f), 67, TexturePattern.Wood);
            CreateTexture("AAA_SandbagBurlap.png", new Color(0.35f, 0.29f, 0.19f), new Color(0.68f, 0.58f, 0.39f), 71, TexturePattern.Fabric);
            CreateTexture("AAA_ChainLink.png", new Color(0.05f, 0.06f, 0.06f), new Color(0.55f, 0.61f, 0.62f), 83, TexturePattern.ChainLink);
            CreateTexture("AAA_GraffitiPoster.png", new Color(0.10f, 0.10f, 0.12f), new Color(0.88f, 0.66f, 0.23f), 97, TexturePattern.Graffiti);
            CreateTexture("AAA_HazardStripe.png", new Color(0.06f, 0.055f, 0.045f), new Color(1.0f, 0.72f, 0.08f), 101, TexturePattern.Hazard);
            CreateTexture("AAA_DirtPacked.png", new Color(0.28f, 0.22f, 0.14f), new Color(0.52f, 0.42f, 0.28f), 109, TexturePattern.Concrete);
        }

        enum TexturePattern { Asphalt, Concrete, Brick, Plaster, Corrugated, Wood, Fabric, ChainLink, Graffiti, Hazard }

        static void CreateTexture(string fileName, Color low, Color high, int seed, TexturePattern pattern)
        {
            var path = $"{TextureDir}/{fileName}";
            var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false, false);
            for (int y = 0; y < tex.height; y++)
            for (int x = 0; x < tex.width; x++)
            {
                float n = Hash01(x, y, seed) * 0.55f + Mathf.PerlinNoise((x + seed) * 0.055f, (y - seed) * 0.055f) * 0.45f;
                var c = Color.Lerp(low, high, n);
                switch (pattern)
                {
                    case TexturePattern.Asphalt:
                        if ((x + seed * 7 + y / 9) % 97 < 2) c *= 0.46f;
                        break;
                    case TexturePattern.Concrete:
                        if (x % 64 < 2 || y % 64 < 2) c *= 0.58f;
                        break;
                    case TexturePattern.Brick:
                        int row = y / 22;
                        int offset = (row & 1) == 0 ? 0 : 32;
                        if ((x + offset) % 64 < 3 || y % 22 < 3) c *= 0.38f;
                        break;
                    case TexturePattern.Plaster:
                        if (Hash01(x / 6, y / 6, seed) > 0.82f) c = Color.Lerp(c, new Color(0.24f, 0.22f, 0.19f), 0.38f);
                        break;
                    case TexturePattern.Corrugated:
                        c *= 0.72f + ((x / 6) % 2) * 0.22f;
                        break;
                    case TexturePattern.Wood:
                        c *= 0.74f + Mathf.Sin((x + seed) * 0.12f) * 0.16f;
                        break;
                    case TexturePattern.Fabric:
                        if (x % 11 == 0 || y % 9 == 0) c *= 0.78f;
                        break;
                    case TexturePattern.ChainLink:
                        bool wire = Mathf.Abs(((x + y) % 32) - 16) < 2 || Mathf.Abs(((x - y + 256) % 32) - 16) < 2;
                        c = wire ? Color.Lerp(high, Color.white, 0.18f) : new Color(0.03f, 0.035f, 0.035f, 0.52f);
                        break;
                    case TexturePattern.Graffiti:
                        c *= 0.45f;
                        if (y > 72 && y < 165 && x > 24 && x < 230 && Mathf.Sin((x + seed) * 0.09f) > 0.25f)
                            c = Color.Lerp(new Color(0.94f, 0.2f, 0.18f), high, Hash01(x, y, seed + 4));
                        break;
                    case TexturePattern.Hazard:
                        c = ((x + y) / 28) % 2 == 0 ? high : low;
                        break;
                }
                c.a = 1f;
                tex.SetPixel(x, y, c);
            }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }
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
            Mats["Mat_Asphalt"] = MakeMat("Mat_Asphalt_AAA", new Color(0.18f, 0.18f, 0.17f), "AAA_AsphaltCracked.png", new Vector2(26f, 34f), 0f, 0.18f);
            Mats["Mat_Concrete"] = MakeMat("Mat_Concrete_AAA", new Color(0.55f, 0.53f, 0.49f), "AAA_ConcretePanel.png", new Vector2(7f, 7f), 0f, 0.22f);
            Mats["Mat_Brick"] = MakeMat("Mat_RedBrick_AAA", new Color(0.62f, 0.38f, 0.28f), "AAA_RedBrickMixed.png", new Vector2(5f, 5f), 0f, 0.20f);
            Mats["Mat_Plaster"] = MakeMat("Mat_Plaster_AAA", new Color(0.78f, 0.73f, 0.64f), "AAA_PlasterAged.png", new Vector2(4.5f, 4.5f), 0f, 0.24f);
            Mats["Mat_Metal"] = MakeMat("Mat_CorrugatedMetal_AAA", new Color(0.42f, 0.45f, 0.46f), "AAA_CorrugatedMetal.png", new Vector2(3.5f, 3.5f), 0.55f, 0.34f);
            Mats["Mat_Wood"] = MakeMat("Mat_PalletWood_AAA", new Color(0.47f, 0.29f, 0.14f), "AAA_WoodPallet.png", new Vector2(3f, 3f), 0f, 0.18f);
            Mats["Mat_Sandbag"] = MakeMat("Mat_Sandbag_AAA", new Color(0.60f, 0.50f, 0.34f), "AAA_SandbagBurlap.png", new Vector2(2f, 2f), 0f, 0.12f);
            Mats["Mat_Glass"] = MakeMat("Mat_DarkGlass_AAA", new Color(0.025f, 0.045f, 0.055f), null, Vector2.one, 0f, 0.72f);
            Mats["Mat_Blue"] = MakeMat("Mat_BlueTeamPaint_AAA", new Color(0.05f, 0.22f, 0.85f), null, Vector2.one, 0f, 0.32f);
            Mats["Mat_Red"] = MakeMat("Mat_RedTeamPaint_AAA", new Color(0.82f, 0.08f, 0.055f), null, Vector2.one, 0f, 0.32f);
            Mats["Mat_Trim"] = MakeMat("Mat_DarkTrim_AAA", new Color(0.085f, 0.08f, 0.07f), null, Vector2.one, 0f, 0.28f);
            Mats["Mat_Hazard"] = MakeMat("Mat_HazardStripe_AAA", Color.white, "AAA_HazardStripe.png", new Vector2(2.5f, 1f), 0f, 0.2f);
            Mats["Mat_Graffiti"] = MakeMat("Mat_GraffitiPoster_AAA", Color.white, "AAA_GraffitiPoster.png", Vector2.one, 0f, 0.26f);
            Mats["Mat_ChainLink"] = MakeMat("Mat_ChainLink_AAA", Color.white, "AAA_ChainLink.png", new Vector2(3f, 3f), 0.2f, 0.25f);
            Mats["Mat_Rubber"] = MakeMat("Mat_Rubber_AAA", new Color(0.018f, 0.017f, 0.015f), null, Vector2.one, 0f, 0.08f);
            Mats["Mat_PaintWhite"] = MakeMat("Mat_RoadPaint_AAA", new Color(0.86f, 0.83f, 0.72f), null, Vector2.one, 0f, 0.18f);
            Mats["Mat_Dirt"] = MakeMat("Mat_DirtPacked_AAA", new Color(0.42f, 0.34f, 0.22f), "AAA_DirtPacked.png", new Vector2(8f, 8f), 0f, 0.12f);
        }

        static Material MakeMat(string name, Color color, string textureName, Vector2 tiling, float metallic, float smoothness)
        {
            var path = $"{MaterialDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color); else mat.color = color;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            Texture2D tex = null;
            if (!string.IsNullOrEmpty(textureName))
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/{textureName}");
            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex); else mat.mainTexture = tex;
                mat.mainTextureScale = tiling;
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }
        #endregion

        #region Ground + irregular shell
        static void BuildGroundAndShell(Transform parent)
        {
            Box(parent, "Ground", new Vector3(0f, -0.06f, 0f), new Vector3(MapWidth, 0.12f, MapLength), "Mat_Asphalt");
            Box(parent, "Beach_Dirt", new Vector3(SX(-18f), 0.02f, SZ(-48f)), new Vector3(52f, 0.05f, 28f), "Mat_Dirt", false);

            ShellSeg(parent, "Wall_West_S", new Vector3(-WallHalfX, WallHeight * 0.5f, SZ(-50f)), new Vector3(1.2f, WallHeight, 36f));
            ShellSeg(parent, "Wall_West_Mid", new Vector3(-WallHalfX + 2.5f, WallHeight * 0.5f, 0f), new Vector3(1.2f, WallHeight, 42f));
            ShellSeg(parent, "Wall_West_N", new Vector3(-WallHalfX, WallHeight * 0.5f, SZ(50f)), new Vector3(1.2f, WallHeight, 36f));
            ShellSeg(parent, "Wall_East_S", new Vector3(WallHalfX, WallHeight * 0.5f, SZ(-48f)), new Vector3(1.2f, WallHeight, 40f));
            ShellSeg(parent, "Wall_East_Mid", new Vector3(WallHalfX - 3f, WallHeight * 0.5f, SZ(8f)), new Vector3(1.2f, WallHeight, 44f));
            ShellSeg(parent, "Wall_East_N", new Vector3(WallHalfX, WallHeight * 0.5f, SZ(52f)), new Vector3(1.2f, WallHeight, 32f));
            ShellSeg(parent, "Wall_South_W", new Vector3(SX(-28f), WallHeight * 0.5f, -WallHalfZ), new Vector3(40f, WallHeight, 1.2f));
            ShellSeg(parent, "Wall_South_E", new Vector3(SX(28f), WallHeight * 0.5f, -WallHalfZ + 1.5f), new Vector3(40f, WallHeight, 1.2f));
            ShellSeg(parent, "Wall_South_Mid", new Vector3(0f, WallHeight * 0.5f, -WallHalfZ - 1.2f), new Vector3(24f, WallHeight, 1.2f));
            ShellSeg(parent, "Wall_North_W", new Vector3(SX(-28f), WallHeight * 0.5f, WallHalfZ), new Vector3(40f, WallHeight, 1.2f));
            ShellSeg(parent, "Wall_North_E", new Vector3(SX(28f), WallHeight * 0.5f, WallHalfZ - 1.5f), new Vector3(40f, WallHeight, 1.2f));
            ShellSeg(parent, "Wall_North_Mid", new Vector3(0f, WallHeight * 0.5f, WallHalfZ + 1.2f), new Vector3(24f, WallHeight, 1.2f));
            ShellSeg(parent, "Wall_Corner_SW", new Vector3(-WallHalfX + 1f, WallHeight * 0.5f, -WallHalfZ + 1f), new Vector3(4f, WallHeight, 4f));
            ShellSeg(parent, "Wall_Corner_SE", new Vector3(WallHalfX - 1f, WallHeight * 0.5f, -WallHalfZ + 2f), new Vector3(4f, WallHeight, 4f));
            ShellSeg(parent, "Wall_Corner_NW", new Vector3(-WallHalfX + 1f, WallHeight * 0.5f, WallHalfZ - 1f), new Vector3(4f, WallHeight, 4f));
            ShellSeg(parent, "Wall_Corner_NE", new Vector3(WallHalfX - 1f, WallHeight * 0.5f, WallHalfZ - 2f), new Vector3(4f, WallHeight, 4f));

            for (float z = -60f; z <= 60f; z += 14f)
            {
                Box(parent, $"WallPanel_W_{z:0}", new Vector3(-WallHalfX + 0.7f, 4f, SZ(z * 0.82f)), new Vector3(0.12f, 5.5f, 5f), "Mat_Plaster", false);
                Box(parent, $"WallPanel_E_{z:0}", new Vector3(WallHalfX - 0.7f, 4f, SZ(z * 0.82f)), new Vector3(0.12f, 5.5f, 5f), "Mat_Plaster", false);
            }
        }

        static void ShellSeg(Transform parent, string name, Vector3 center, Vector3 size)
        {
            Box(parent, name, center, size, "Mat_Brick");
            Box(parent, name + "_Cap", center + Vector3.up * (size.y * 0.5f + 0.18f),
                new Vector3(size.x + 0.35f, 0.36f, size.z + 0.35f), "Mat_Concrete", false);
        }
        #endregion

        #region Structures
        static void BuildOverflowStructures(Transform parent)
        {
            Building(parent, "Bldg_Bank", P(-6f, 2f), new Vector3(14f, 7.5f, 16f), "Mat_Concrete", FacadeSide.South, -28f, "Mat_Plaster");
            Building(parent, "Bldg_Shoes", P(8f, 6f), new Vector3(8f, 8f, 8f), "Mat_Brick", FacadeSide.West);
            Building(parent, "Bldg_Baskets", P(14f, 28f), new Vector3(10f, 7f, 10f), "Mat_Brick", FacadeSide.West);
            Building(parent, "Bldg_Electronics", P(22f, 46f), new Vector3(12f, 6.5f, 10f), "Mat_Plaster", FacadeSide.South);
            Building(parent, "Bldg_Spices", P(18f, 14f), new Vector3(8f, 4.5f, 8f), "Mat_Plaster", FacadeSide.West);
            Building(parent, "Bldg_Deli", P(16f, -2f), new Vector3(9f, 5.5f, 9f), "Mat_Brick", FacadeSide.West);
            Building(parent, "Bldg_Construction", P(-10f, -18f), new Vector3(16f, 9f, 14f), "Mat_Concrete", FacadeSide.East);
            Building(parent, "Bldg_FruitShed", P(-22f, -34f), new Vector3(8f, 3.5f, 6f), "Mat_Wood", FacadeSide.East);
            Building(parent, "Bldg_StallsWest", P(-30f, 8f), new Vector3(6f, 3f, 10f), "Mat_Wood", FacadeSide.East);
            Building(parent, "Bldg_GlassCurve", P(6f, -36f), new Vector3(12f, 10f, 8f), "Mat_Concrete", FacadeSide.North, 15f, "Mat_Glass");
            Building(parent, "Bldg_ShopRow_E1", P(38f, -12f), new Vector3(8f, 6f, 14f), "Mat_Brick", FacadeSide.West);
            Building(parent, "Bldg_ShopRow_E2", P(36f, 8f), new Vector3(8f, 7f, 12f), "Mat_Brick", FacadeSide.West);
            Building(parent, "Bldg_ShopRow_E3", P(34f, 30f), new Vector3(8f, 6.5f, 12f), "Mat_Plaster", FacadeSide.West);
            Building(parent, "Bldg_WestBlock_S", P(-40f, -40f), new Vector3(10f, 6f, 12f), "Mat_Concrete", FacadeSide.East);
            Building(parent, "Bldg_WestBlock_N", P(-40f, 40f), new Vector3(10f, 6.5f, 12f), "Mat_Concrete", FacadeSide.East);
            Building(parent, "Bldg_BlueSpawnHall", P(0f, -56f), new Vector3(20f, 5f, 8f), "Mat_Concrete", FacadeSide.North, 0f, "Mat_Blue");
            Building(parent, "Bldg_RedSpawnHall", P(0f, 56f), new Vector3(20f, 5f, 8f), "Mat_Concrete", FacadeSide.South, 0f, "Mat_Red");
            Building(parent, "Bldg_TopBottom", P(10f, -10f), new Vector3(6f, 7.5f, 6f), "Mat_Brick", FacadeSide.West);
            Building(parent, "Bldg_MarketAnnex_S", P(28f, -24f), new Vector3(7f, 5f, 8f), "Mat_Plaster", FacadeSide.West, 12f);
            Building(parent, "Bldg_MarketAnnex_N", P(24f, 38f), new Vector3(7f, 5.5f, 7f), "Mat_Brick", FacadeSide.West, -10f);
            Building(parent, "Bldg_WestAnnex_Mid", P(-38f, 0f), new Vector3(8f, 5.5f, 10f), "Mat_Plaster", FacadeSide.East, 8f);
            Building(parent, "Bldg_PlazaKiosk_N", P(-8f, 36f), new Vector3(5f, 3.5f, 5f), "Mat_Wood", FacadeSide.South);
            Building(parent, "Bldg_PlazaKiosk_S", P(8f, -28f), new Vector3(5f, 3.2f, 5f), "Mat_Wood", FacadeSide.North);

            Cylinder(parent, "Bldg_FountainRing", P(-34f, 0.6f, -6f), 8f, 1.2f, "Mat_Concrete");
            Cylinder(parent, "Fountain_Inner", P(-34f, 0.35f, -6f), 5.2f, 0.7f, "Mat_Dirt", Axis.Y, false);

            BuildBoat(parent, P(28f, -42f), -35f);

            BuildVehicle(parent, "Prop_MidVan", P(-1f, -4f), new Vector3(2.4f, 2.2f, 5.5f), 8f, "Mat_Metal");
            BuildVehicle(parent, "Prop_MidSUV", P(5f, 8f), new Vector3(2.2f, 2f, 4.8f), -15f, "Mat_PaintWhite");
            BuildVehicle(parent, "Prop_MidContainer", P(1f, 2f), new Vector3(2.5f, 2.6f, 6f), 5f, "Mat_Metal");
            BuildVehicle(parent, "Prop_BlueMainCar", P(-3f, -38f), new Vector3(2.3f, 1.8f, 4.6f), 70f, "Mat_Metal");
            BuildVehicle(parent, "Prop_BlueMainCar2", P(6f, -42f), new Vector3(2.2f, 1.7f, 4.4f), -20f, "Mat_Hazard");
            BuildVehicle(parent, "Prop_RedMainCar", P(2f, 38f), new Vector3(2.3f, 1.8f, 4.6f), 110f, "Mat_Metal");
            BuildVehicle(parent, "Prop_BoatCar", P(22f, -48f), new Vector3(2.2f, 1.6f, 4.2f), -50f, "Mat_Metal");
            BuildVehicle(parent, "Prop_BeachCar", P(-18f, -50f), new Vector3(2.4f, 1.7f, 4.5f), 25f, "Mat_Plaster");

            Cylinder(parent, "Prop_UtilityPole_Main", P(-2f, 4.5f, -10f), 0.6f, 9f, "Mat_Wood");
            foreach (var pole in new[] { P(-20f, 20f), P(18f, -24f), P(8f, 36f), P(-28f, -40f), P(30f, 12f), P(-12f, -8f), P(14f, 20f) })
                Cylinder(parent, $"Prop_UtilityPole_{pole.x:0}_{pole.z:0}", pole + Vector3.up * 4.5f, 0.5f, 9f, "Mat_Wood");
        }

        static void BuildBoat(Transform parent, Vector3 ground, float yaw)
        {
            var root = new GameObject("Prop_Boat");
            root.transform.SetParent(parent, true);
            root.transform.position = ground;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            Box(root.transform, "Hull", new Vector3(0f, 1.6f, 0f), new Vector3(18f, 3.2f, 7f), "Mat_Wood");
            Box(root.transform, "Deck", new Vector3(0f, 3.3f, 0f), new Vector3(16f, 0.25f, 5.5f), "Mat_Wood");
            Box(root.transform, "Cabin", new Vector3(-4f, 4.2f, 0f), new Vector3(5f, 2f, 4f), "Mat_Metal");
            Box(root.transform, "Ramp", new Vector3(7f, 1.2f, 2.5f), new Vector3(4f, 0.35f, 2f), "Mat_Wood");
        }

        static void BuildVehicle(Transform parent, string name, Vector3 ground, Vector3 size, float yaw, string mat)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = ground;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            Box(root.transform, "Body", new Vector3(0f, size.y * 0.5f, 0f), size, mat);
            Box(root.transform, "Cabin", new Vector3(0f, size.y * 0.75f, size.z * 0.05f),
                new Vector3(size.x * 0.9f, size.y * 0.55f, size.z * 0.45f), "Mat_Glass", false);
            Cylinder(root.transform, "Wheel_FL", new Vector3(-size.x * 0.4f, 0.35f, size.z * 0.3f), 0.7f, 0.28f, "Mat_Rubber", Axis.X);
            Cylinder(root.transform, "Wheel_FR", new Vector3(size.x * 0.4f, 0.35f, size.z * 0.3f), 0.7f, 0.28f, "Mat_Rubber", Axis.X);
            Cylinder(root.transform, "Wheel_RL", new Vector3(-size.x * 0.4f, 0.35f, -size.z * 0.3f), 0.7f, 0.28f, "Mat_Rubber", Axis.X);
            Cylinder(root.transform, "Wheel_RR", new Vector3(size.x * 0.4f, 0.35f, -size.z * 0.3f), 0.7f, 0.28f, "Mat_Rubber", Axis.X);
        }
        #endregion

        #region Dogleg lanes + connectors
        static void BuildDoglegLanes(Transform parent)
        {
            RoadSeg(parent, "Road_A1", P(-28f, -58f), P(-34f, -28f), 8f);
            RoadSeg(parent, "Road_A2", P(-34f, -28f), P(-36f, -2f), 7f);
            RoadSeg(parent, "Road_A3", P(-36f, -2f), P(-30f, 22f), 6f);
            RoadSeg(parent, "Road_A4", P(-30f, 22f), P(-26f, 56f), 8f);

            RoadSeg(parent, "Road_B1", P(0f, -58f), P(-4f, -30f), 10f);
            RoadSeg(parent, "Road_B2", P(-4f, -30f), P(2f, -8f), 9f);
            RoadSeg(parent, "Road_B3", P(2f, -8f), P(-2f, 10f), 9f);
            RoadSeg(parent, "Road_B4", P(-2f, 10f), P(4f, 32f), 9f);
            RoadSeg(parent, "Road_B5", P(4f, 32f), P(0f, 58f), 10f);

            RoadSeg(parent, "Road_C1", P(30f, -58f), P(34f, -36f), 5f);
            RoadSeg(parent, "Road_C2", P(34f, -36f), P(28f, -18f), 4.5f);
            RoadSeg(parent, "Road_C3", P(28f, -18f), P(32f, 2f), 4f);
            RoadSeg(parent, "Road_C4", P(32f, 2f), P(26f, 24f), 4f);
            RoadSeg(parent, "Road_C5", P(26f, 24f), P(30f, 56f), 5f);

            RoadSeg(parent, "Conn_X1_BlueHub", P(-30f, -50f), P(30f, -50f), 8f);
            RoadSeg(parent, "Conn_X2_BeachCut", P(-16f, -38f), P(-4f, -36f), 4f);
            RoadSeg(parent, "Conn_X3_BoatMain", P(14f, -36f), P(4f, -34f), 4.5f);
            RoadSeg(parent, "Conn_X4_Construction", P(-14f, -18f), P(-4f, -16f), 3.5f);
            RoadSeg(parent, "Conn_X6_BankThrough", P(-14f, 0f), P(2f, 2f), 3f);
            RoadSeg(parent, "Conn_X7_VaultAlley", P(-14f, 0f), P(-30f, 2f), 3f);
            RoadSeg(parent, "Conn_X9_MidSouth", P(2f, -8f), P(14f, -6f), 3.5f);
            RoadSeg(parent, "Conn_X10_Spices", P(4f, 14f), P(22f, 14f), 3f);
            RoadSeg(parent, "Conn_X11_Baskets", P(2f, 26f), P(14f, 26f), 3f);
            RoadSeg(parent, "Conn_X13_Plaza", P(-30f, 40f), P(30f, 40f), 10f);
            RoadSeg(parent, "Conn_X14_RedHub", P(-30f, 52f), P(30f, 52f), 8f);
            RoadSeg(parent, "Conn_X15_StallsMain", P(-18f, 10f), P(-4f, 10f), 3.5f);
            RoadSeg(parent, "Conn_X16_DeliSpices", P(24f, -2f), P(24f, 14f), 3f);
            RoadSeg(parent, "Conn_X17_FruitDirt", P(-16f, -28f), P(-12f, -18f), 4f);

            for (int i = -5; i <= 5; i++)
            {
                float z = i * 10f;
                float xBias = Mathf.Sin(i * 0.7f) * 3f;
                Box(parent, $"Sidewalk_Mid_W_{i}", P(-6f + xBias * 0.3f, 0.08f, z), new Vector3(2.2f, 0.1f, 4f), "Mat_Concrete", false);
                Box(parent, $"Sidewalk_Mid_E_{i}", P(6f + xBias * 0.3f, 0.08f, z), new Vector3(2.2f, 0.1f, 4f), "Mat_Concrete", false);
            }
        }

        static void RoadSeg(Transform parent, string name, Vector3 a, Vector3 b, float width)
        {
            var mid = (a + b) * 0.5f;
            mid.y = 0.02f;
            var dir = b - a; dir.y = 0f;
            float len = dir.magnitude;
            if (len < 0.5f) return;
            // Collider kept so ground queries / pale-pixel raycasts hit the road mesh.
            // No center stripe — later mat passes matched StartsWith("Road_"/"Conn_") and
            // turned paint stripes into pale sand cards (CRITIQUE_01 #1).
            var go = Box(parent, name, mid, new Vector3(width, 0.04f, len), "Mat_Asphalt", true);
            go.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }
        #endregion

        #region Verticality
        static void BuildVerticality(Transform parent)
        {
            StairStack(parent, "Stairs_Shoes_S1", P(11f, 0.1f, 4f), 8, 0.375f, 2.5f, 1f, 0f);
            StairStack(parent, "Stairs_Baskets_S2", P(12f, 0.1f, 26f), 8, 0.375f, 2.5f, 1f, 0f);
            StairStack(parent, "Stairs_Electronics_S3", P(20f, 0.1f, 48f), 8, 0.375f, 2.5f, -1f, 180f);
            StairStack(parent, "Stairs_Bank_S4", P(-4f, 0.1f, 0f), 8, 0.375f, 2.5f, 1f, 0f);
            StairStack(parent, "Stairs_TopBottom_S5", P(9f, 0.1f, -10f), 8, 0.375f, 2.2f, 1f, 0f);
            StairStack(parent, "Stairs_Construction_S6", P(-12f, 0.1f, -22f), 8, 0.375f, 2f, 1f, 0f);
            StairStack(parent, "Stairs_Construction_S7", P(-8f, 3.1f, -20f), 8, 0.375f, 2f, 1f, 0f);

            Box(parent, "Bal_Shoes_W", P(4f, 4.5f, 6f), new Vector3(1f, 0.25f, 4f), "Mat_Concrete");
            Box(parent, "Bal_Baskets_SW", P(10f, 4f, 24f), new Vector3(1f, 0.25f, 3f), "Mat_Concrete");
            Box(parent, "Bal_Electronics_S", P(22f, 3.8f, 42f), new Vector3(1f, 0.25f, 4f), "Mat_Concrete");
            Box(parent, "Bal_Deli_W", P(12f, 3.5f, -2f), new Vector3(1f, 0.25f, 3f), "Mat_Concrete");
            Box(parent, "Cat_Construction_E", P(-2f, 6f, -16f), new Vector3(6f, 0.25f, 3f), "Mat_Metal");
            Box(parent, "Overlook_Bank_2F", P(-6f, 4.2f, 4f), new Vector3(6f, 0.25f, 5f), "Mat_Concrete");
            Box(parent, "Overlook_Shoes_2F", P(8f, 4.5f, 6f), new Vector3(5f, 0.25f, 5f), "Mat_Concrete");
            Box(parent, "Overlook_Baskets_2F", P(14f, 4f, 28f), new Vector3(6f, 0.25f, 5f), "Mat_Concrete");

            Scaffold(parent, "Scaffold_Construction_A", P(-6f, -22f), 0f);
            Scaffold(parent, "Scaffold_Construction_B", P(-14f, -14f), 90f);
            Scaffold(parent, "Scaffold_Construction_C", P(-8f, -18f), 45f);
            Box(parent, "Ladder_Boat_L2", P(30f, 1.25f, -40f), new Vector3(1f, 2.5f, 0.35f), "Mat_Metal");
        }
        #endregion

        #region Cover density
        static void BuildCoverAndDensity(Transform parent)
        {
            CrateStack(parent, "Cover_A", P(-12f, -18f), 3, 2, "Mat_Wood");
            Dumpster(parent, "Cover_B", P(12f, -16f), 90f);
            ConcreteBarrier(parent, "Cover_C", P(0f, 9f), 0f, 4);

            float[] a3z = { -6f, -2f, 2f, 6f, 10f, 14f };
            for (int i = 0; i < a3z.Length; i++)
                CrateStack(parent, $"Cover_A3_Headglitch_{i}", new Vector3(SX(-33f + (i % 2) * 3f), 0f, SZ(a3z[i])), 1, 1, "Mat_Wood");

            Rubble(parent, "Cover_RubblePile_A", P(-8f, -8f), new Vector3(4f, 1.4f, 3f));
            Rubble(parent, "Cover_RubblePile_B", P(4f, 12f), new Vector3(3.5f, 1.3f, 3f));
            ConcreteBarrier(parent, "Cover_Jersey_A", P(-3f, 16f), 15f, 2);
            ConcreteBarrier(parent, "Cover_Jersey_B", P(3f, -20f), -10f, 2);
            CrateStack(parent, "Cover_Plaza_0", P(0f, 40f), 2, 1, "Mat_Wood");
            CrateStack(parent, "Cover_Plaza_W", P(-8f, 38f), 2, 1, "Mat_Wood");
            CrateStack(parent, "Cover_Plaza_E", P(8f, 42f), 2, 1, "Mat_Wood");

            SandbagWall(parent, "Cover_Blue_Sandbags", P(0f, -48f), 8, 0f);
            SandbagWall(parent, "Cover_Red_Sandbags", P(0f, 48f), 8, 180f);
            SandbagWall(parent, "Cover_West_Sandbags", P(-32f, 4f), 6, 90f);
            SandbagWall(parent, "Cover_East_Sandbags", P(30f, -4f), 6, -90f);

            var fill = new (float x, float z, int kind)[]
            {
                (-24f, -20f, 0), (-20f, -8f, 1), (-28f, 16f, 2), (-22f, 28f, 0),
                (-16f, 18f, 1), (-10f, 22f, 2), (10f, 18f, 0), (18f, 4f, 1),
                (20f, -14f, 2), (26f, -8f, 0), (28f, 16f, 1), (22f, 30f, 2),
                (-4f, -22f, 0), (4f, -14f, 1), (-6f, 8f, 2), (8f, -4f, 0),
                (0f, 22f, 1), (-14f, -44f, 2), (16f, -44f, 0), (-8f, 48f, 1),
                (12f, 50f, 2), (32f, -28f, 0), (-34f, -24f, 1), (-34f, 28f, 2),
                (4f, 28f, 0), (-2f, -32f, 1), (14f, 8f, 2), (-18f, 0f, 0),
                (36f, 20f, 1), (-40f, 16f, 2), (0f, -12f, 0), (0f, 14f, 1),
            };
            for (int i = 0; i < fill.Length; i++)
            {
                var f = fill[i];
                var pos = P(f.x, f.z);
                if (f.kind == 0) CrateStack(parent, $"Prop_CrateFill_{i}", pos, 2, 1 + (i % 2), "Mat_Wood");
                else if (f.kind == 1) BarrelCluster(parent, $"Prop_BarrelFill_{i}", pos);
                else ConcreteBarrier(parent, $"Prop_BarrierFill_{i}", pos, i * 17f, 1 + (i % 2));
            }

            for (int i = 0; i < 6; i++)
            {
                float z = -40f + i * 16f;
                float x = (i % 2 == 0) ? -2.5f : 3f;
                ConcreteBarrier(parent, $"Cover_Jersey_Main_{i}", P(x, z), i * 7f, 1);
            }

            CrateStack(parent, "Cover_Market_C2", P(31f, -27f), 1, 1, "Mat_Wood");
            CrateStack(parent, "Cover_Market_C3", P(30f, -8f), 1, 1, "Mat_Wood");
            CrateStack(parent, "Cover_Market_C4", P(29f, 12f), 1, 1, "Mat_Wood");
            Dumpster(parent, "Prop_Dumpster_West", P(-28f, 14f), 0f);
            Dumpster(parent, "Prop_Dumpster_East", P(28f, -12f), 180f);
            Dumpster(parent, "Prop_Dumpster_MidS", P(8f, -6f), 90f);
            Dumpster(parent, "Prop_Dumpster_MidN", P(-6f, 18f), -90f);
            Rubble(parent, "Cover_Rubble_Beach", P(-20f, -44f), new Vector3(3.5f, 1.3f, 3f));
            Rubble(parent, "Cover_Rubble_Construction", P(-16f, -24f), new Vector3(4f, 1.5f, 3.5f));
            Rubble(parent, "Cover_Rubble_Plaza", P(4f, 36f), new Vector3(3f, 1.2f, 3f));
        }

        static void Rubble(Transform parent, string name, Vector3 ground, Vector3 size)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = ground;
            SetStatic(root);
            Box(root.transform, "Chunk_A", new Vector3(0f, size.y * 0.35f, 0f), size * 0.7f, "Mat_Concrete");
            Box(root.transform, "Chunk_B", new Vector3(size.x * 0.25f, size.y * 0.45f, size.z * 0.15f), size * 0.5f, "Mat_Brick");
            Box(root.transform, "Chunk_C", new Vector3(-size.x * 0.2f, size.y * 0.25f, -size.z * 0.2f), size * 0.4f, "Mat_Concrete");
        }
        #endregion

        #region Silhouette / dressing
        static void BuildSilhouetteDressing(Transform parent)
        {
            RoofKit(parent, P(-10f, 9.2f, -18f), "Construction");
            RoofKit(parent, P(-6f, 7.7f, 2f), "Bank");
            RoofKit(parent, P(8f, 8.2f, 6f), "Shoes");
            RoofKit(parent, P(14f, 7.2f, 28f), "Baskets");
            RoofKit(parent, P(22f, 6.7f, 46f), "Electronics");
            RoofKit(parent, P(38f, 6.2f, -12f), "ShopE1");
            RoofKit(parent, P(36f, 7.2f, 8f), "ShopE2");

            Billboard(parent, "Billboard_Market", P(12f, 8f, -8f), new Vector3(6.4f, 2.2f, 0.24f), -90f);
            Billboard(parent, "Billboard_Fountain", P(-28f, 7f, 4f), new Vector3(5.5f, 2f, 0.24f), 90f);
            Billboard(parent, "Billboard_Plaza", P(4f, 7.5f, 36f), new Vector3(7f, 2.4f, 0.24f), 0f);

            Wire(parent, "Cable_Main_A", P(-9f, 6.5f, -8f), P(9f, 7f, 4f));
            Wire(parent, "Cable_Main_B", P(-8f, 6.8f, 6f), P(10f, 7.2f, 16f));
            Wire(parent, "Cable_Main_C", P(-6f, 7f, 20f), P(8f, 7.4f, 30f));
            Wire(parent, "Cable_Main_D", P(-4f, 6.6f, -20f), P(6f, 7f, -10f));
            Wire(parent, "Cable_Market_A", P(20f, 6f, -10f), P(32f, 6.5f, 4f));
            Wire(parent, "Cable_Market_B", P(22f, 6.2f, 10f), P(30f, 6.8f, 24f));
            Wire(parent, "Cable_Market_C", P(18f, 6.4f, 28f), P(28f, 7f, 40f));
            Wire(parent, "Cable_West_A", P(-36f, 6f, -20f), P(-28f, 6.5f, 0f));
            Wire(parent, "Cable_West_B", P(-34f, 6.2f, 8f), P(-26f, 6.8f, 28f));

            for (float z = -60f; z <= 60f; z += 12f)
            {
                LampPost(parent, P(-44f, z), 0f);
                LampPost(parent, P(44f, z + 4f), 180f);
            }

            Fence(parent, "Fence_WestBacklot", P(-46f, -8f), 22f, true);
            Fence(parent, "Fence_EastBacklot", P(48f, 10f), 22f, true);
            Fence(parent, "Fence_BlueSpawn", P(-12f, -58f), 14f, false);
            Fence(parent, "Fence_RedSpawn", P(12f, 58f), 14f, false);

            TeamBanner(parent, "Banner_Blue", P(0f, 6.5f, -56f), "BLUE", "Mat_Blue");
            TeamBanner(parent, "Banner_Red", P(0f, 6.5f, 56f), "RED", "Mat_Red");

            for (int i = 0; i < 14; i++)
            {
                float z = -40f + i * 6f;
                Box(parent, $"Sign_Market_{i}", P(34f, 3.2f, z * 0.9f), new Vector3(0.12f, 1.2f, 2.8f),
                    i % 3 == 0 ? "Mat_Hazard" : (i % 3 == 1 ? "Mat_Blue" : "Mat_Red"), false);
                Box(parent, $"Sign_Main_{i}", new Vector3(SX((i % 2 == 0) ? -8f : 8f), 3.4f, SZ(z * 0.85f)),
                    new Vector3(2.6f, 1.1f, 0.12f), i % 2 == 0 ? "Mat_Hazard" : "Mat_Blue", false);
            }

            for (int i = 0; i < 20; i++)
            {
                float x = (i % 2 == 0) ? -10f : 12f;
                float z = -36f + i * 3.6f;
                Box(parent, $"AC_Unit_{i}", P(x, 3.8f, z), new Vector3(0.9f, 0.7f, 0.55f), "Mat_Metal", false);
            }
        }
        #endregion

        #region Building helpers
        enum FacadeSide { East, West, North, South }

        static void Building(Transform parent, string name, Vector3 centerBottom, Vector3 size, string bodyMat,
            FacadeSide mainSide, float yaw = 0f, string accentMat = null)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = centerBottom;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);

            var center = Vector3.up * (size.y * 0.5f);
            Box(root.transform, name + "_Mass", center, size, bodyMat);

            Box(root.transform, name + "_RoofLedge", center + Vector3.up * (size.y * 0.5f + 0.18f),
                new Vector3(size.x + 0.58f, 0.36f, size.z + 0.58f), "Mat_Concrete", false);
            Box(root.transform, name + "_Parapet_N", center + new Vector3(0f, size.y * 0.5f + 0.68f, size.z * 0.5f),
                new Vector3(size.x + 0.65f, 0.72f, 0.34f), "Mat_Trim", false);
            Box(root.transform, name + "_Parapet_S", center + new Vector3(0f, size.y * 0.5f + 0.68f, -size.z * 0.5f),
                new Vector3(size.x + 0.65f, 0.72f, 0.34f), "Mat_Trim", false);
            Box(root.transform, name + "_Parapet_E", center + new Vector3(size.x * 0.5f, size.y * 0.5f + 0.68f, 0f),
                new Vector3(0.34f, 0.72f, size.z + 0.65f), "Mat_Trim", false);
            Box(root.transform, name + "_Parapet_W", center + new Vector3(-size.x * 0.5f, size.y * 0.5f + 0.68f, 0f),
                new Vector3(0.34f, 0.72f, size.z + 0.65f), "Mat_Trim", false);

            AddHorizontalTrim(root.transform, name + "_TrimLower", center, size, 1.15f, "Mat_Trim");
            AddHorizontalTrim(root.transform, name + "_TrimUpper", center, size, Mathf.Max(2.7f, size.y - 1.25f), "Mat_Concrete");
            AddCornerPillars(root.transform, name, center, size);
            BuildFacade(root.transform, name, center, size, mainSide, accentMat ?? "Mat_Trim");
            if (mainSide is FacadeSide.East or FacadeSide.West)
            {
                BuildFacade(root.transform, name + "_North", center, size, FacadeSide.North, accentMat ?? "Mat_Trim", false);
                BuildFacade(root.transform, name + "_South", center, size, FacadeSide.South, accentMat ?? "Mat_Trim", false);
            }
            else
            {
                BuildFacade(root.transform, name + "_East", center, size, FacadeSide.East, accentMat ?? "Mat_Trim", false);
                BuildFacade(root.transform, name + "_West", center, size, FacadeSide.West, accentMat ?? "Mat_Trim", false);
            }
        }

        static void AddHorizontalTrim(Transform parent, string name, Vector3 center, Vector3 size, float y, string mat)
        {
            bool collide = y <= HeadHeight;
            Box(parent, name + "_N", new Vector3(center.x, y, center.z + size.z * 0.5f + 0.055f), new Vector3(size.x + 0.15f, 0.18f, 0.16f), mat, collide);
            Box(parent, name + "_S", new Vector3(center.x, y, center.z - size.z * 0.5f - 0.055f), new Vector3(size.x + 0.15f, 0.18f, 0.16f), mat, collide);
            Box(parent, name + "_E", new Vector3(center.x + size.x * 0.5f + 0.055f, y, center.z), new Vector3(0.16f, 0.18f, size.z + 0.15f), mat, collide);
            Box(parent, name + "_W", new Vector3(center.x - size.x * 0.5f - 0.055f, y, center.z), new Vector3(0.16f, 0.18f, size.z + 0.15f), mat, collide);
        }

        static void AddCornerPillars(Transform parent, string name, Vector3 center, Vector3 size)
        {
            float y = size.y * 0.5f;
            var pillar = new Vector3(0.42f, size.y + 0.15f, 0.42f);
            Box(parent, name + "_Pillar_NE", new Vector3(center.x + size.x * 0.5f + 0.04f, y, center.z + size.z * 0.5f + 0.04f), pillar, "Mat_Concrete", false);
            Box(parent, name + "_Pillar_NW", new Vector3(center.x - size.x * 0.5f - 0.04f, y, center.z + size.z * 0.5f + 0.04f), pillar, "Mat_Concrete", false);
            Box(parent, name + "_Pillar_SE", new Vector3(center.x + size.x * 0.5f + 0.04f, y, center.z - size.z * 0.5f - 0.04f), pillar, "Mat_Concrete", false);
            Box(parent, name + "_Pillar_SW", new Vector3(center.x - size.x * 0.5f - 0.04f, y, center.z - size.z * 0.5f - 0.04f), pillar, "Mat_Concrete", false);
        }

        static void BuildFacade(Transform parent, string name, Vector3 center, Vector3 size, FacadeSide side, string accentMat, bool includeDoor = true)
        {
            float length = side is FacadeSide.East or FacadeSide.West ? size.z : size.x;
            int columns = Mathf.Clamp(Mathf.FloorToInt(length / 3.0f), 2, 5);
            int floors = Mathf.Clamp(Mathf.FloorToInt(size.y / 2.15f), 1, 4);
            float step = length / (columns + 1);
            float start = -length * 0.5f + step;
            for (int f = 0; f < floors; f++)
            {
                float y = 2.25f + f * 1.85f;
                if (y > size.y - 0.8f) continue;
                for (int c = 0; c < columns; c++)
                {
                    float along = start + c * step;
                    FramePanel(parent, $"{name}_Win_{side}_{f}_{c}", center, size, side, along, y, 1.25f, 1.12f, "Mat_Glass", "Mat_Trim", false);
                }
            }
            if (includeDoor)
            {
                FramePanel(parent, $"{name}_DoorRecess_{side}", center, size, side, 0f, 1.05f, 1.75f, 2.1f, "Mat_Trim", accentMat, false);
                var awningCenter = PanelCenter(center, size, side, 0f, 2.38f) + PanelNormal(side) * 0.18f;
                Box(parent, $"{name}_DoorAwning_{side}", awningCenter, PanelSize(side, 2.8f, 0.28f), accentMat, false);
            }
        }

        static void FramePanel(Transform parent, string name, Vector3 buildingCenter, Vector3 buildingSize, FacadeSide side,
            float along, float y, float width, float height, string fillMat, string frameMat, bool collider)
        {
            Box(parent, name + "_Fill", PanelCenter(buildingCenter, buildingSize, side, along, y), PanelSize(side, width, height), fillMat, collider);
            float t = 0.14f;
            Box(parent, name + "_Top", PanelCenter(buildingCenter, buildingSize, side, along, y + height * 0.5f + t * 0.5f), PanelSize(side, width + t * 2f, t), frameMat, false);
            Box(parent, name + "_Bottom", PanelCenter(buildingCenter, buildingSize, side, along, y - height * 0.5f - t * 0.5f), PanelSize(side, width + t * 2f, t), frameMat, false);
            Box(parent, name + "_Left", PanelCenter(buildingCenter, buildingSize, side, along - width * 0.5f - t * 0.5f, y), PanelSize(side, t, height + t * 2f), frameMat, false);
            Box(parent, name + "_Right", PanelCenter(buildingCenter, buildingSize, side, along + width * 0.5f + t * 0.5f, y), PanelSize(side, t, height + t * 2f), frameMat, false);
        }

        static Vector3 PanelNormal(FacadeSide side) => side switch
        {
            FacadeSide.East => Vector3.right,
            FacadeSide.West => Vector3.left,
            FacadeSide.North => Vector3.forward,
            _ => Vector3.back,
        };

        static Vector3 PanelCenter(Vector3 buildingCenter, Vector3 buildingSize, FacadeSide side, float along, float y) => side switch
        {
            FacadeSide.East => new Vector3(buildingCenter.x + buildingSize.x * 0.5f + 0.055f, y, buildingCenter.z + along),
            FacadeSide.West => new Vector3(buildingCenter.x - buildingSize.x * 0.5f - 0.055f, y, buildingCenter.z + along),
            FacadeSide.North => new Vector3(buildingCenter.x + along, y, buildingCenter.z + buildingSize.z * 0.5f + 0.055f),
            _ => new Vector3(buildingCenter.x + along, y, buildingCenter.z - buildingSize.z * 0.5f - 0.055f),
        };

        static Vector3 PanelSize(FacadeSide side, float width, float height) =>
            side is FacadeSide.East or FacadeSide.West
                ? new Vector3(0.09f, height, width)
                : new Vector3(width, height, 0.09f);
        #endregion

        #region Prop helpers
        static void RoofKit(Transform parent, Vector3 center, string suffix)
        {
            Box(parent, $"Roof_HVAC_{suffix}", center + new Vector3(1.1f, 0.32f, -0.6f), new Vector3(2f, 0.64f, 1.4f), "Mat_Metal", false);
            Box(parent, $"Roof_Duct_{suffix}", center + new Vector3(-1.2f, 0.18f, 0.75f), new Vector3(2.8f, 0.36f, 0.48f), "Mat_Metal", false);
            Cylinder(parent, $"Roof_WaterTank_{suffix}", center + new Vector3(0.1f, 0.95f, 1.5f), 1.15f, 1.9f, "Mat_Metal", Axis.Y, false);
            Cylinder(parent, $"Roof_Antenna_{suffix}", center + new Vector3(-2.1f, 1.45f, -1.1f), 0.08f, 2.9f, "Mat_Trim", Axis.Y, false);
            Cylinder(parent, $"Roof_Dish_{suffix}", center + new Vector3(1.8f, 0.5f, 1.2f), 1.2f, 0.15f, "Mat_Metal", Axis.Y, false);
        }

        static void StairStack(Transform parent, string name, Vector3 start, int steps, float rise, float width, float dir, float yaw)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = start;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            for (int i = 0; i < steps; i++)
                Box(root.transform, $"Step_{i}", new Vector3(0f, rise * (i + 0.5f), dir * i * 0.55f), new Vector3(width, rise, 0.62f), "Mat_Concrete");
        }

        static void CrateStack(Transform parent, string name, Vector3 basePos, int wide, int high, string mat)
        {
            // Root is a visible primitive so BreakableCover (RequireComponent Collider) and the
            // invisible-collider audit both see a Renderer + Collider on the same GameObject.
            float hTotal = high * 1.08f;
            float wTotal = wide * 1.15f;
            var root = Box(parent, name, basePos + new Vector3(0f, hTotal * 0.5f, 0f), new Vector3(wTotal, hTotal, 1.25f), mat);
            root.transform.SetParent(parent, true);
            // Detail crates are visual-only (root already collides).
            for (int h = 0; h < high; h++)
            for (int w = 0; w < wide; w++)
            {
                float jitter = (Hash01(w, h, name.GetHashCode()) - 0.5f) * 0.1f;
                Box(root.transform, $"Crate_{h}_{w}", new Vector3((w - (wide - 1) * 0.5f) * 1.15f, 0.55f + h * 1.08f - hTotal * 0.5f, jitter), new Vector3(1.05f, 1.05f, 1.05f), mat, false);
            }
        }

        static void SandbagWall(Transform parent, string name, Vector3 basePos, int count, float yaw)
        {
            float w = count * 0.72f + 0.75f;
            var root = Box(parent, name, basePos + new Vector3(0f, 0.52f, 0f), new Vector3(w, 1.05f, 0.68f), "Mat_Sandbag");
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            for (int row = 0; row < 3; row++)
            for (int i = 0; i < count; i++)
            {
                float x = (i - (count - 1) * 0.5f) * 0.72f + (row % 2) * 0.35f;
                Box(root.transform, $"Sandbag_{row}_{i}", new Vector3(x, 0.25f + row * 0.28f - 0.52f, 0f), new Vector3(0.68f, 0.26f, 0.52f), "Mat_Sandbag", false);
            }
        }

        static void ConcreteBarrier(Transform parent, string name, Vector3 basePos, float yaw, int count)
        {
            float w = count * 1.45f;
            var root = Box(parent, name, basePos + new Vector3(0f, 0.5f, 0f), new Vector3(w, 1.0f, 0.55f), "Mat_Concrete");
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            for (int i = 0; i < count; i++)
            {
                float x = (i - (count - 1) * 0.5f) * 1.45f;
                Box(root.transform, $"Jersey_{i}", new Vector3(x, 0f, 0f), new Vector3(1.32f, 1.0f, 0.55f), "Mat_Concrete", false);
            }
        }

        static void Dumpster(Transform parent, string name, Vector3 basePos, float yaw)
        {
            var root = Box(parent, name, basePos + new Vector3(0f, 0.82f, 0f), new Vector3(2.6f, 1.45f, 1.45f), "Mat_Metal");
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            Box(root.transform, "Lid", new Vector3(0f, 0.8f, 0.08f), new Vector3(2.7f, 0.18f, 1.35f), "Mat_Trim", false);
        }

        static void BarrelCluster(Transform parent, string name, Vector3 basePos)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = basePos;
            SetStatic(root);
            Cylinder(root.transform, "Barrel_A", new Vector3(-0.35f, 0.62f, 0f), 0.62f, 1.24f, "Mat_Metal");
            Cylinder(root.transform, "Barrel_B", new Vector3(0.35f, 0.62f, 0.12f), 0.62f, 1.24f, "Mat_Hazard");
            Cylinder(root.transform, "Barrel_C", new Vector3(0.05f, 0.62f, -0.62f), 0.62f, 1.24f, "Mat_Metal");
        }

        static void Scaffold(Transform parent, string name, Vector3 basePos, float yaw)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = basePos;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            for (int x = -1; x <= 1; x += 2)
            for (int z = -1; z <= 1; z += 2)
                Cylinder(root.transform, $"Post_{x}_{z}", new Vector3(x * 1.6f, 2f, z * 0.55f), 0.08f, 4f, "Mat_Metal");
            Box(root.transform, "Plank", new Vector3(0f, 2.25f, 0f), new Vector3(3.6f, 0.18f, 1.25f), "Mat_Wood");
            for (int y = 1; y <= 3; y++)
            {
                Cylinder(root.transform, $"Rail_F_{y}", new Vector3(0f, y, -0.62f), 0.06f, 3.4f, "Mat_Metal", Axis.X, y <= 2);
                Cylinder(root.transform, $"Rail_B_{y}", new Vector3(0f, y, 0.62f), 0.06f, 3.4f, "Mat_Metal", Axis.X, y <= 2);
            }
        }

        static void Billboard(Transform parent, string name, Vector3 pos, Vector3 size, float yaw)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = pos;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            Box(root.transform, "Panel", Vector3.zero, size, "Mat_Graffiti", false);
            Box(root.transform, "Support_L", new Vector3(-size.x * 0.35f, -size.y * 0.75f, 0f), new Vector3(0.15f, size.y * 0.75f, 0.15f), "Mat_Metal");
            Box(root.transform, "Support_R", new Vector3(size.x * 0.35f, -size.y * 0.75f, 0f), new Vector3(0.15f, size.y * 0.75f, 0.15f), "Mat_Metal");
        }

        static void TeamBanner(Transform parent, string name, Vector3 pos, string label, string mat)
        {
            Box(parent, name, pos, new Vector3(8.2f, 1.25f, 0.16f), mat, false);
            for (int i = 0; i < label.Length; i++)
            {
                float x = (i - (label.Length - 1) * 0.5f) * 1.05f;
                Box(parent, $"{name}_LetterBlock_{i}", pos + new Vector3(x, 0f, -0.12f), new Vector3(0.52f, 0.78f, 0.1f), "Mat_PaintWhite", false);
            }
        }

        static void LampPost(Transform parent, Vector3 basePos, float yaw)
        {
            var root = new GameObject($"Prop_LampPost_{basePos.x:0}_{basePos.z:0}");
            root.transform.SetParent(parent, true);
            root.transform.position = basePos;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            Cylinder(root.transform, "Pole", new Vector3(0f, 2.2f, 0f), 0.12f, 4.4f, "Mat_Metal");
            Box(root.transform, "Arm", new Vector3(0f, 4.25f, 0.75f), new Vector3(0.12f, 0.12f, 1.5f), "Mat_Metal", false);
            Box(root.transform, "Lamp", new Vector3(0f, 4.05f, 1.48f), new Vector3(0.48f, 0.25f, 0.38f), "Mat_PaintWhite", false);
        }

        static void Fence(Transform parent, string name, Vector3 pos, float length, bool alongZ)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = pos;
            SetStatic(root);
            int posts = Mathf.CeilToInt(length / 2f) + 1;
            for (int i = 0; i < posts; i++)
            {
                float t = -length * 0.5f + i * (length / (posts - 1));
                var p = alongZ ? new Vector3(0f, 0f, t) : new Vector3(t, 0f, 0f);
                Cylinder(root.transform, $"Post_{i}", p + Vector3.up * 1.15f, 0.08f, 2.3f, "Mat_Metal");
            }
            Box(root.transform, "Chain", Vector3.up * 1.18f, alongZ ? new Vector3(0.06f, 1.8f, length) : new Vector3(length, 1.8f, 0.06f), "Mat_ChainLink", false);
            Box(root.transform, "TopRail", Vector3.up * 2.15f, alongZ ? new Vector3(0.08f, 0.08f, length) : new Vector3(length, 0.08f, 0.08f), "Mat_Metal", false);
        }

        static void Wire(Transform parent, string name, Vector3 a, Vector3 b)
        {
            var mid = (a + b) * 0.5f;
            var dir = b - a;
            var go = Box(parent, name, mid, new Vector3(0.06f, 0.06f, dir.magnitude), "Mat_Trim", false);
            go.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }
        #endregion

        #region Primitives + collider policy
        static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, string matKey, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = size;
            go.layer = GameLayers.Default;
            go.isStatic = true;
            SetStatic(go);
            ApplyMaterial(go, matKey);
            var tag = go.GetComponent<MapMaterialTag>() ?? go.AddComponent<MapMaterialTag>();
            tag.materialKey = matKey;
            if (!collider)
            {
                var c = go.GetComponent<Collider>();
                if (c != null) Object.DestroyImmediate(c);
            }
            return go;
        }

        enum Axis { Y, X, Z }

        static GameObject Cylinder(Transform parent, string name, Vector3 center, float diameter, float length, string matKey, Axis axis = Axis.Y, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localScale = new Vector3(diameter, length * 0.5f, diameter);
            if (axis == Axis.X) go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            else if (axis == Axis.Z) go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            else go.transform.localRotation = Quaternion.identity;
            go.layer = GameLayers.Default;
            go.isStatic = true;
            SetStatic(go);
            ApplyMaterial(go, matKey);
            var tag = go.GetComponent<MapMaterialTag>() ?? go.AddComponent<MapMaterialTag>();
            tag.materialKey = matKey;
            if (!collider)
            {
                var c = go.GetComponent<Collider>();
                if (c != null) Object.DestroyImmediate(c);
            }
            return go;
        }

        static void ApplyMaterial(GameObject go, string key)
        {
            if (!Mats.TryGetValue(key, out var mat))
                mat = Mats.TryGetValue("Mat_Concrete", out var fallback) ? fallback : null;
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null && mat != null)
                renderer.sharedMaterial = mat;
        }

        /// <summary>
        /// Final safety net: destroy any collider whose GameObject has no enabled renderer,
        /// and strip decorative trim colliders whose world centre is above head height.
        /// </summary>
        static void StripIllegalColliders(Transform root)
        {
            int stripped = 0;
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c == null) continue;
                var r = c.GetComponent<Renderer>();
                bool noVisible = r == null || !r.enabled;
                bool highDecor = c.bounds.center.y > HeadHeight && IsDecorName(c.name);
                if (noVisible || highDecor)
                {
                    Object.DestroyImmediate(c);
                    stripped++;
                }
            }
            Debug.Log($"[ArenaFps] StripIllegalColliders removed {stripped} colliders.");
        }

        static bool IsDecorName(string n) =>
            n.Contains("Parapet") || n.Contains("RoofLedge") || n.Contains("Awning")
            || n.Contains("Sign_") || n.Contains("Cable_") || n.Contains("AC_Unit")
            || n.Contains("Dish") || n.Contains("Antenna") || n.Contains("Billboard")
            || n.Contains("Wire") || n.Contains("_Cap") || n.Contains("HVAC")
            || n.Contains("Pillar");

        static void SetStatic(GameObject go)
        {
            GameObjectUtility.SetStaticEditorFlags(go,
                StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
        }
        #endregion

        #region Spawns / lighting / capture / nav
        static void PlaceSpawnsAndPlayer()
        {
            // Spec §g positions * PosScale.
            EnsureSpawn("PlayerSpawn", P(0f, 1.7f, -56f), Quaternion.identity);
            EnsureSpawn("Spawn_Blue_1", P(-8f, 0.1f, -56f), Quaternion.identity);
            EnsureSpawn("Spawn_Blue_2", P(8f, 0.1f, -56f), Quaternion.identity);
            EnsureSpawn("Spawn_Blue_3", P(-24f, 0.1f, -52f), Quaternion.Euler(0f, 20f, 0f));
            EnsureSpawn("Spawn_Blue_4", P(26f, 0.1f, -52f), Quaternion.Euler(0f, -20f, 0f));
            EnsureSpawn("Spawn_Blue_5", P(0f, 0.1f, -50f), Quaternion.identity);
            EnsureSpawn("Spawn_Blue_6", P(-14f, 0.1f, -48f), Quaternion.Euler(0f, 10f, 0f));

            EnsureSpawn("Spawn_Red_1", P(8f, 0.1f, 56f), Quaternion.Euler(0f, 180f, 0f));
            EnsureSpawn("Spawn_Red_2", P(-8f, 0.1f, 56f), Quaternion.Euler(0f, 180f, 0f));
            EnsureSpawn("Spawn_Red_3", P(26f, 0.1f, 52f), Quaternion.Euler(0f, 200f, 0f));
            EnsureSpawn("Spawn_Red_4", P(-24f, 0.1f, 52f), Quaternion.Euler(0f, 160f, 0f));
            EnsureSpawn("Spawn_Red_5", P(0f, 0.1f, 50f), Quaternion.Euler(0f, 180f, 0f));
            EnsureSpawn("Spawn_Red_6", P(14f, 0.1f, 48f), Quaternion.Euler(0f, 190f, 0f));

            var player = GameObject.Find("Player");
            var spawn = GameObject.Find("PlayerSpawn");
            if (player != null && spawn != null)
            {
                player.transform.SetPositionAndRotation(spawn.transform.position, spawn.transform.rotation);
                GameLayers.ApplyRecursive(player, GameLayers.Player);
            }
        }

        static void EnsureSpawn(string name, Vector3 position, Quaternion rotation)
        {
            var go = GameObject.Find(name) ?? new GameObject(name);
            go.transform.SetPositionAndRotation(position, rotation);
        }

        static void TuneLighting()
        {
            var light = Object.FindAnyObjectByType<Light>();
            if (light != null && light.type == LightType.Directional)
            {
                light.color = new Color(1f, 0.92f, 0.78f);
                light.intensity = 1.35f;
                light.shadows = LightShadows.Soft;
                light.transform.rotation = Quaternion.Euler(46f, -32f, 0f);
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0045f;
            RenderSettings.fogColor = new Color(0.77f, 0.71f, 0.60f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.58f, 0.61f, 0.68f);
            RenderSettings.ambientEquatorColor = new Color(0.39f, 0.36f, 0.32f);
            RenderSettings.ambientGroundColor = new Color(0.16f, 0.15f, 0.13f);

            var probeGo = GameObject.Find("AAA_ReflectionProbe_Mid");
            if (probeGo == null)
            {
                probeGo = new GameObject("AAA_ReflectionProbe_Mid");
                probeGo.transform.position = new Vector3(0f, 4f, 0f);
                var rp = probeGo.AddComponent<ReflectionProbe>();
                rp.size = new Vector3(MapWidth + 10f, 22f, MapLength + 10f);
                rp.mode = ReflectionProbeMode.Realtime;
                rp.refreshMode = ReflectionProbeRefreshMode.OnAwake;
                rp.intensity = 0.55f;
            }
        }

        static void BuildCaptureRig()
        {
            var rig = new GameObject("__AaaCaptureRig");
            CreateCaptureCamera(rig.transform, "AAA_Aerial_Camera", new Vector3(0f, 90f, -6f), new Vector3(0f, 0f, 0f), 54f);
            CreateCaptureCamera(rig.transform, "AAA_EyeLevel_Camera", new Vector3(SX(-21.5f), 1.75f, SZ(-25f)), new Vector3(SX(-1f), 1.6f, SZ(3.5f)), 72f);
            CreateCaptureCamera(rig.transform, "AAA_MidLane_Camera", new Vector3(SX(4.5f), 1.8f, SZ(-18f)), new Vector3(SX(-1.2f), 1.5f, SZ(5f)), 68f);
        }

        static void CreateCaptureCamera(Transform parent, string name, Vector3 pos, Vector3 lookAt, float fov)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((lookAt - pos).normalized, Vector3.up);
            var cam = go.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 320f;
            cam.fieldOfView = fov;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.enabled = false;
        }

        static void BakeNavMeshFallback()
        {
            var surfaceType = System.Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
            if (surfaceType == null) return;

            foreach (var existing in Object.FindObjectsByType(surfaceType))
            {
                if (existing is Component c)
                    Object.DestroyImmediate(c.gameObject);
            }

            var go = new GameObject("__NavMeshSurface");
            var surface = go.AddComponent(surfaceType);
            var useGeometry = surfaceType.GetProperty("useGeometry");
            if (useGeometry != null && useGeometry.CanWrite)
            {
                var geometryType = System.Type.GetType("UnityEngine.AI.NavMeshCollectGeometry, UnityEngine");
                if (geometryType != null)
                    useGeometry.SetValue(surface, System.Enum.Parse(geometryType, "PhysicsColliders"));
            }
            surfaceType.GetMethod("BuildNavMesh")?.Invoke(surface, null);
            Debug.Log("[ArenaFps] Fallback NavMesh bake complete.");
        }
        #endregion
    }
}
#endif
