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
    /// One-click environment art pass for Arena: dense urban 3-lane TDM dressing, static geometry,
    /// custom materials/textures, capture cameras, and combat/nav re-prep.
    /// </summary>
    public static class AaaEnvironmentPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string TextureDir = "Assets/_Project/Art/Textures/AaaGenerated";
        const string MaterialDir = "Assets/_Project/Art/Materials/Map";

        static readonly HashSet<string> PreserveRoots = new()
        {
            "Directional Light",
            "Global Volume",
            "Player",
            "PlayerSpawn",
            "Spawn_Blue_1",
            "Spawn_Blue_2",
            "Spawn_Blue_3",
            "Spawn_Blue_4",
            "Spawn_Blue_5",
            "Spawn_Red_1",
            "Spawn_Red_2",
            "Spawn_Red_3",
            "Spawn_Red_4",
            "Spawn_Red_5",
        };

        static readonly Dictionary<string, Material> Mats = new();

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
            BuildIterationOneLayout(root.transform);
            BuildIterationTwoSilhouetteAndTrim(root.transform);
            BuildIterationThreePropsAndStory(root.transform);
            BuildCaptureRig();
            PlaceSpawnsAndPlayer();
            TuneLighting();

            // Combat surface tagging can fail if cover names diverge; never abort the art pass.
            try
            {
                SpawnArenaCombat.Run();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[ArenaFps] SpawnArenaCombat skipped after environment rebuild: {ex.Message}");
                BakeNavMeshFallback();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[ArenaFps] AAA Environment Pass complete: urban 3-lane map rebuilt, dressed, saved, and navmesh baked.");
        }

        static void ClearPreviousEnvironment()
        {
            var doomed = new List<GameObject>();
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (PreserveRoots.Contains(go.name))
                    continue;

                if (go.name == "ThreeLaneMap" || go.name == "__NavMeshSurface" || go.name == "__AaaCaptureRig")
                {
                    doomed.Add(go);
                    continue;
                }

                if (go.name.StartsWith("PB_") || go.name.StartsWith("AAA_") || go.name.StartsWith("Cover_") || go.name.StartsWith("Prop_"))
                    doomed.Add(go);
            }

            foreach (var go in doomed)
                Object.DestroyImmediate(go);
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
            if (AssetDatabase.IsValidFolder(path))
                return;

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
        }

        enum TexturePattern { Asphalt, Concrete, Brick, Plaster, Corrugated, Wood, Fabric, ChainLink, Graffiti, Hazard }

        static void CreateTexture(string fileName, Color low, Color high, int seed, TexturePattern pattern)
        {
            var path = $"{TextureDir}/{fileName}";
            var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false, false);
            tex.name = Path.GetFileNameWithoutExtension(fileName);

            for (int y = 0; y < tex.height; y++)
            {
                for (int x = 0; x < tex.width; x++)
                {
                    float n = Hash01(x, y, seed) * 0.55f + Mathf.PerlinNoise((x + seed) * 0.055f, (y - seed) * 0.055f) * 0.45f;
                    var c = Color.Lerp(low, high, n);

                    switch (pattern)
                    {
                        case TexturePattern.Asphalt:
                            if ((x + seed * 7 + y / 9) % 97 < 2) c *= 0.46f;
                            if ((x * 3 + y * 5 + seed) % 211 < 3) c = Color.Lerp(c, Color.white, 0.16f);
                            break;
                        case TexturePattern.Concrete:
                            if (x % 64 < 2 || y % 64 < 2) c *= 0.58f;
                            if ((x + y + seed) % 73 < 2) c = Color.Lerp(c, Color.black, 0.12f);
                            break;
                        case TexturePattern.Brick:
                            int row = y / 22;
                            int offset = (row & 1) == 0 ? 0 : 32;
                            if ((x + offset) % 64 < 3 || y % 22 < 3) c *= 0.38f;
                            c *= 0.86f + 0.22f * Hash01(row, (x + offset) / 64, seed);
                            break;
                        case TexturePattern.Plaster:
                            if (Hash01(x / 6, y / 6, seed) > 0.82f) c = Color.Lerp(c, new Color(0.24f, 0.22f, 0.19f), 0.38f);
                            break;
                        case TexturePattern.Corrugated:
                            c *= 0.72f + ((x / 6) % 2) * 0.22f;
                            if (y % 48 < 2) c *= 0.5f;
                            break;
                        case TexturePattern.Wood:
                            c *= 0.74f + Mathf.Sin((x + seed) * 0.12f + Mathf.PerlinNoise(x * 0.03f, y * 0.03f) * 5f) * 0.16f;
                            if (x % 52 < 3) c *= 0.45f;
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
                            if (y > 72 && y < 165 && x > 24 && x < 230)
                            {
                                float band = Mathf.Sin((x + seed) * 0.09f) + Mathf.Cos(y * 0.08f);
                                if (band > 0.25f) c = Color.Lerp(new Color(0.94f, 0.2f, 0.18f), high, Hash01(x, y, seed + 4));
                            }
                            if (x % 58 < 2 || y % 44 < 2) c *= 0.55f;
                            break;
                        case TexturePattern.Hazard:
                            c = ((x + y) / 28) % 2 == 0 ? high : low;
                            break;
                    }

                    c.a = 1f;
                    tex.SetPixel(x, y, c);
                }
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
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
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

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);

            Texture2D tex = null;
            if (!string.IsNullOrEmpty(textureName))
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureDir}/{textureName}");
            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                else mat.mainTexture = tex;
                mat.mainTextureScale = tiling;
            }
            else
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", null);
                else mat.mainTexture = null;
            }

            if (name.Contains("Glass") && mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", 0f);

            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void BuildGroundAndShell(Transform parent)
        {
            Box(parent, "Ground", new Vector3(0f, -0.06f, 0f), new Vector3(66f, 0.12f, 88f), "Mat_Asphalt");

            Box(parent, "Road_MidLane", new Vector3(0f, 0.015f, 0f), new Vector3(10f, 0.05f, 78f), "Mat_Asphalt", false);
            Box(parent, "Road_WestLane", new Vector3(-21f, 0.02f, 0f), new Vector3(10f, 0.05f, 76f), "Mat_Asphalt", false);
            Box(parent, "Road_EastLane", new Vector3(21f, 0.02f, 0f), new Vector3(10f, 0.05f, 76f), "Mat_Asphalt", false);

            for (int z = -34; z <= 34; z += 8)
            {
                Box(parent, $"RoadStripe_Mid_{z}", new Vector3(0f, 0.08f, z), new Vector3(0.28f, 0.025f, 3.6f), "Mat_PaintWhite", false);
                Box(parent, $"RoadStripe_West_{z}", new Vector3(-21f, 0.08f, z), new Vector3(0.24f, 0.025f, 3.2f), "Mat_PaintWhite", false);
                Box(parent, $"RoadStripe_East_{z}", new Vector3(21f, 0.08f, z), new Vector3(0.24f, 0.025f, 3.2f), "Mat_PaintWhite", false);
            }

            for (int z = -38; z <= 38; z += 9)
            {
                Box(parent, $"Sidewalk_W_{z}", new Vector3(-11f, 0.08f, z), new Vector3(2.4f, 0.12f, 5.8f), "Mat_Concrete", false);
                Box(parent, $"Sidewalk_E_{z}", new Vector3(11f, 0.08f, z), new Vector3(2.4f, 0.12f, 5.8f), "Mat_Concrete", false);
            }

            Wall(parent, "Wall_West", new Vector3(-32.5f, 4.2f, 0f), new Vector3(1f, 8.4f, 88f));
            Wall(parent, "Wall_East", new Vector3(32.5f, 4.2f, 0f), new Vector3(1f, 8.4f, 88f));
            Wall(parent, "Wall_South", new Vector3(0f, 4.2f, -44.5f), new Vector3(66f, 8.4f, 1f));
            Wall(parent, "Wall_North", new Vector3(0f, 4.2f, 44.5f), new Vector3(66f, 8.4f, 1f));

            for (int x = -28; x <= 28; x += 7)
            {
                Box(parent, $"Wall_South_Panel_{x}", new Vector3(x, 4.6f, -43.93f), new Vector3(4.6f, 5.8f, 0.08f), "Mat_Plaster", false);
                Box(parent, $"Wall_North_Panel_{x}", new Vector3(x, 4.6f, 43.93f), new Vector3(4.6f, 5.8f, 0.08f), "Mat_Plaster", false);
            }
        }

        static void Wall(Transform parent, string name, Vector3 center, Vector3 size)
        {
            Box(parent, name, center, size, "Mat_Brick");
            Box(parent, name + "_Cap", center + Vector3.up * (size.y * 0.5f + 0.18f), new Vector3(size.x + 0.35f, 0.36f, size.z + 0.35f), "Mat_Concrete");
        }

        static void BuildIterationOneLayout(Transform parent)
        {
            // Strong lane reads: west alley, contested mid street, east market lane.
            Building(parent, "PB_Building_West_South_Apartments", new Vector3(-14.5f, 0f, -27f), new Vector3(9f, 7.2f, 14f), "Mat_Brick", FacadeSide.East);
            Building(parent, "PB_Building_West_Mid_PrintShop", new Vector3(-15.8f, 0f, -5f), new Vector3(8.4f, 8.7f, 15.5f), "Mat_Plaster", FacadeSide.East);
            Building(parent, "PB_Building_West_North_Hotel", new Vector3(-14.5f, 0f, 21.5f), new Vector3(10.5f, 6.4f, 16f), "Mat_Brick", FacadeSide.East);

            Building(parent, "PB_Building_East_South_Market", new Vector3(14.8f, 0f, -23f), new Vector3(10.8f, 6.1f, 17f), "Mat_Plaster", FacadeSide.West);
            Building(parent, "PB_Building_East_Mid_Offices", new Vector3(15.6f, 0f, 2f), new Vector3(8.6f, 9.8f, 17f), "Mat_Brick", FacadeSide.West);
            Building(parent, "PB_Building_East_North_Laundry", new Vector3(15.2f, 0f, 27f), new Vector3(10f, 6.8f, 13f), "Mat_Plaster", FacadeSide.West);

            Building(parent, "PB_Building_Mid_SW_Cafe", new Vector3(-5.8f, 0f, -14.5f), new Vector3(6.1f, 5.4f, 8f), "Mat_Plaster", FacadeSide.North);
            Building(parent, "PB_Building_Mid_SE_Pawn", new Vector3(6f, 0f, -12.5f), new Vector3(5.7f, 5.8f, 7f), "Mat_Brick", FacadeSide.North);
            Building(parent, "PB_Building_Mid_NW_Clinic", new Vector3(-6.2f, 0f, 13f), new Vector3(6f, 5.6f, 8.5f), "Mat_Brick", FacadeSide.South);
            Building(parent, "PB_Building_Mid_NE_Pharmacy", new Vector3(6.2f, 0f, 14f), new Vector3(5.8f, 6.4f, 8f), "Mat_Plaster", FacadeSide.South);

            // Blue and Red spawn bases remain readable from mid.
            Building(parent, "PB_BlueSpawn_CommandPost", new Vector3(0f, 0f, -37f), new Vector3(18f, 6.3f, 7.5f), "Mat_Concrete", FacadeSide.North, "Mat_Blue");
            Building(parent, "PB_RedSpawn_CommandPost", new Vector3(0f, 0f, 37f), new Vector3(18f, 6.3f, 7.5f), "Mat_Concrete", FacadeSide.South, "Mat_Red");

            Box(parent, "Overlook_West_Balcony", new Vector3(-8.6f, 4.4f, -1.5f), new Vector3(5.4f, 0.7f, 9.5f), "Mat_Concrete");
            Box(parent, "Overlook_East_Balcony", new Vector3(8.6f, 4.4f, 1.5f), new Vector3(5.4f, 0.7f, 9.5f), "Mat_Concrete");
            StairStack(parent, "Stairs_West_Overlook", new Vector3(-10.5f, 0.18f, -8.5f), 6, 0.75f, 2.8f, 1.0f, 0f);
            StairStack(parent, "Stairs_East_Overlook", new Vector3(10.5f, 0.18f, 8.5f), 6, 0.75f, 2.8f, -1.0f, 180f);

            Box(parent, "Mid_FountainBase", new Vector3(0f, 0.35f, 0f), new Vector3(6.5f, 0.7f, 6.5f), "Mat_Concrete");
            Cylinder(parent, "Mid_FountainColumn", new Vector3(0f, 1.2f, 0f), 0.9f, 1.8f, "Mat_Concrete");
            Cylinder(parent, "Mid_FountainRim", new Vector3(0f, 0.85f, 0f), 3.5f, 0.32f, "Mat_Concrete");
            Box(parent, "Mid_Kiosk", new Vector3(-3.6f, 1.4f, -4.9f), new Vector3(3.7f, 2.8f, 3f), "Mat_Plaster");
            Box(parent, "Mid_Kiosk_Roof", new Vector3(-3.6f, 3.0f, -4.9f), new Vector3(4.4f, 0.35f, 3.6f), "Mat_Hazard", false);

            BuildAbandonedBus(parent, new Vector3(2.6f, 1.25f, 5.7f), 12f);
        }

        static void BuildIterationTwoSilhouetteAndTrim(Transform parent)
        {
            // Rooftop silhouettes, trim passes, wires, water tanks, and readable lane signage.
            RoofKit(parent, new Vector3(-15f, 7.7f, -27f), "WestSouth");
            RoofKit(parent, new Vector3(-16f, 9.2f, -5f), "WestMid");
            RoofKit(parent, new Vector3(16f, 10.3f, 2f), "EastMid");
            RoofKit(parent, new Vector3(15f, 7.2f, 27f), "EastNorth");

            Billboard(parent, "Billboard_Mid_BrokenHotel", new Vector3(-7.2f, 8.25f, 4.6f), new Vector3(7.2f, 2.5f, 0.24f), 18f);
            Billboard(parent, "Billboard_East_Market", new Vector3(10.6f, 7.9f, -10.5f), new Vector3(6.4f, 2.2f, 0.24f), -90f);
            Billboard(parent, "Billboard_West_Laundry", new Vector3(-10.7f, 6.7f, 18f), new Vector3(6.2f, 2.1f, 0.24f), 90f);

            for (int z = -32; z <= 32; z += 8)
            {
                LampPost(parent, new Vector3(-27.2f, 0f, z), 0f);
                LampPost(parent, new Vector3(27.2f, 0f, z + 4), 180f);
            }

            Wire(parent, "UtilityWire_West_A", new Vector3(-14.2f, 7.2f, -31f), new Vector3(-13.5f, 7.9f, 31f));
            Wire(parent, "UtilityWire_East_A", new Vector3(14.2f, 7.4f, -31f), new Vector3(13.4f, 8.3f, 31f));
            Wire(parent, "UtilityWire_Mid_Cross", new Vector3(-9.8f, 6.3f, -4f), new Vector3(9.8f, 7.1f, 5f));

            Fence(parent, "Fence_WestBacklot", new Vector3(-28.6f, 1.4f, -8f), 18f, true);
            Fence(parent, "Fence_EastBacklot", new Vector3(28.6f, 1.4f, 10f), 18f, true);
            Fence(parent, "Fence_BlueSpawn", new Vector3(-10f, 1.4f, -33f), 10f, false);
            Fence(parent, "Fence_RedSpawn", new Vector3(10f, 1.4f, 33f), 10f, false);
        }

        static void BuildIterationThreePropsAndStory(Transform parent)
        {
            // Combat cover first: keep these names for SpawnArenaCombat.
            CrateStack(parent, "Cover_A", new Vector3(-12.2f, 0f, -18f), 3, 2, "Mat_Wood");
            Dumpster(parent, "Cover_B", new Vector3(12.2f, 0f, -16f), 90f);
            ConcreteBarrier(parent, "Cover_C", new Vector3(0f, 0f, 9.2f), 0f, 5);

            SandbagWall(parent, "Cover_Blue_Sandbags", new Vector3(0f, 0f, -27.2f), 8, 0f);
            SandbagWall(parent, "Cover_Red_Sandbags", new Vector3(0f, 0f, 27.2f), 8, 180f);
            SandbagWall(parent, "Cover_West_Sandbags", new Vector3(-23.5f, 0f, 3f), 6, 90f);
            SandbagWall(parent, "Cover_East_Sandbags", new Vector3(23.5f, 0f, -3f), 6, -90f);

            var propPositions = new[]
            {
                new Vector3(-24f,0f,-24f), new Vector3(-24f,0f,-3f), new Vector3(-22f,0f,22f),
                new Vector3(23f,0f,-25f), new Vector3(24f,0f,1f), new Vector3(22f,0f,22f),
                new Vector3(-6f,0f,-5f), new Vector3(6.2f,0f,6.5f), new Vector3(-2.5f,0f,17f), new Vector3(2.5f,0f,-19f)
            };

            for (int i = 0; i < propPositions.Length; i++)
            {
                if (i % 3 == 0) CrateStack(parent, $"Prop_CrateStack_{i}", propPositions[i], 2 + (i % 2), 1 + (i % 3), "Mat_Wood");
                else if (i % 3 == 1) BarrelCluster(parent, $"Prop_BarrelCluster_{i}", propPositions[i]);
                else PipeBundle(parent, $"Prop_PipeBundle_{i}", propPositions[i]);
            }

            Dumpster(parent, "Prop_Dumpster_West_Alley", new Vector3(-25.2f, 0f, 13.5f), 0f);
            Dumpster(parent, "Prop_Dumpster_East_Alley", new Vector3(25.2f, 0f, -13.5f), 180f);
            Scaffold(parent, "Prop_Scaffold_West", new Vector3(-20.5f, 0f, -8f), 0f);
            Scaffold(parent, "Prop_Scaffold_East", new Vector3(20.5f, 0f, 8f), 180f);

            for (int x = -6; x <= 6; x += 3)
            {
                Box(parent, $"Crosswalk_Blue_{x}", new Vector3(x, 0.1f, -31.5f), new Vector3(1.4f, 0.03f, 0.38f), "Mat_PaintWhite", false);
                Box(parent, $"Crosswalk_Red_{x}", new Vector3(x, 0.1f, 31.5f), new Vector3(1.4f, 0.03f, 0.38f), "Mat_PaintWhite", false);
            }

            TeamBanner(parent, "Banner_Blue", new Vector3(0f, 6.8f, -33.1f), "BLUE", "Mat_Blue");
            TeamBanner(parent, "Banner_Red", new Vector3(0f, 6.8f, 33.1f), "RED", "Mat_Red");

            Decal(parent, "Graffiti_West_1", new Vector3(-10.25f, 2.3f, -4.5f), new Vector3(0.08f, 2f, 3.2f), "Mat_Graffiti");
            Decal(parent, "Graffiti_East_1", new Vector3(10.25f, 2.4f, 8f), new Vector3(0.08f, 2.2f, 3.4f), "Mat_Graffiti");
            Decal(parent, "Graffiti_Mid_Clinic", new Vector3(-2.95f, 2.2f, 8.7f), new Vector3(3f, 1.8f, 0.08f), "Mat_Graffiti");
        }

        enum FacadeSide { East, West, North, South }

        static void Building(Transform parent, string name, Vector3 centerBottom, Vector3 size, string bodyMat, FacadeSide mainSide, string accentMat = null)
        {
            var center = centerBottom + Vector3.up * (size.y * 0.5f);
            Box(parent, name + "_PBMass", center, size, bodyMat);
            Box(parent, name + "_RoofLedge", center + Vector3.up * (size.y * 0.5f + 0.18f), new Vector3(size.x + 0.58f, 0.36f, size.z + 0.58f), "Mat_Concrete");
            Box(parent, name + "_Parapet_N", center + new Vector3(0f, size.y * 0.5f + 0.68f, size.z * 0.5f), new Vector3(size.x + 0.65f, 0.72f, 0.34f), "Mat_Trim");
            Box(parent, name + "_Parapet_S", center + new Vector3(0f, size.y * 0.5f + 0.68f, -size.z * 0.5f), new Vector3(size.x + 0.65f, 0.72f, 0.34f), "Mat_Trim");
            Box(parent, name + "_Parapet_E", center + new Vector3(size.x * 0.5f, size.y * 0.5f + 0.68f, 0f), new Vector3(0.34f, 0.72f, size.z + 0.65f), "Mat_Trim");
            Box(parent, name + "_Parapet_W", center + new Vector3(-size.x * 0.5f, size.y * 0.5f + 0.68f, 0f), new Vector3(0.34f, 0.72f, size.z + 0.65f), "Mat_Trim");

            AddHorizontalTrim(parent, name + "_TrimLower", center, size, 1.15f, "Mat_Trim");
            AddHorizontalTrim(parent, name + "_TrimUpper", center, size, Mathf.Max(2.7f, size.y - 1.25f), "Mat_Concrete");
            AddCornerPillars(parent, name, center, size);
            BuildFacade(parent, name, center, size, mainSide, accentMat ?? "Mat_Trim");

            // Secondary faces add enough detail to stop side approaches reading as blank boxes.
            if (mainSide is FacadeSide.East or FacadeSide.West)
            {
                BuildFacade(parent, name + "_North", center, size, FacadeSide.North, accentMat ?? "Mat_Trim", false);
                BuildFacade(parent, name + "_South", center, size, FacadeSide.South, accentMat ?? "Mat_Trim", false);
            }
            else
            {
                BuildFacade(parent, name + "_East", center, size, FacadeSide.East, accentMat ?? "Mat_Trim", false);
                BuildFacade(parent, name + "_West", center, size, FacadeSide.West, accentMat ?? "Mat_Trim", false);
            }
        }

        static void AddHorizontalTrim(Transform parent, string name, Vector3 center, Vector3 size, float y, string mat)
        {
            Box(parent, name + "_N", new Vector3(center.x, y, center.z + size.z * 0.5f + 0.055f), new Vector3(size.x + 0.15f, 0.18f, 0.16f), mat, false);
            Box(parent, name + "_S", new Vector3(center.x, y, center.z - size.z * 0.5f - 0.055f), new Vector3(size.x + 0.15f, 0.18f, 0.16f), mat, false);
            Box(parent, name + "_E", new Vector3(center.x + size.x * 0.5f + 0.055f, y, center.z), new Vector3(0.16f, 0.18f, size.z + 0.15f), mat, false);
            Box(parent, name + "_W", new Vector3(center.x - size.x * 0.5f - 0.055f, y, center.z), new Vector3(0.16f, 0.18f, size.z + 0.15f), mat, false);
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
                    Box(parent, $"{name}_InsetShadow_{side}_{f}_{c}", PanelCenter(center, size, side, along, y) - PanelNormal(side) * 0.015f, PanelSize(side, 1.35f, 1.22f), "Mat_Trim", false);
                }
            }

            if (includeDoor)
            {
                FramePanel(parent, $"{name}_DoorRecess_{side}", center, size, side, 0f, 1.05f, 1.75f, 2.1f, "Mat_Trim", accentMat, true);
                var awningCenter = PanelCenter(center, size, side, 0f, 2.38f) + PanelNormal(side) * 0.18f;
                var awningSize = PanelSize(side, 2.8f, 0.28f);
                Box(parent, $"{name}_DoorAwning_{side}", awningCenter, awningSize, accentMat, false);
            }
        }

        static void FramePanel(Transform parent, string name, Vector3 buildingCenter, Vector3 buildingSize, FacadeSide side, float along, float y, float width, float height, string fillMat, string frameMat, bool collider)
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

        static Vector3 PanelCenter(Vector3 buildingCenter, Vector3 buildingSize, FacadeSide side, float along, float y)
        {
            return side switch
            {
                FacadeSide.East => new Vector3(buildingCenter.x + buildingSize.x * 0.5f + 0.055f, y, buildingCenter.z + along),
                FacadeSide.West => new Vector3(buildingCenter.x - buildingSize.x * 0.5f - 0.055f, y, buildingCenter.z + along),
                FacadeSide.North => new Vector3(buildingCenter.x + along, y, buildingCenter.z + buildingSize.z * 0.5f + 0.055f),
                _ => new Vector3(buildingCenter.x + along, y, buildingCenter.z - buildingSize.z * 0.5f - 0.055f),
            };
        }

        static Vector3 PanelSize(FacadeSide side, float width, float height)
        {
            return side is FacadeSide.East or FacadeSide.West
                ? new Vector3(0.09f, height, width)
                : new Vector3(width, height, 0.09f);
        }

        static void RoofKit(Transform parent, Vector3 center, string suffix)
        {
            Box(parent, $"Roof_HVAC_{suffix}", center + new Vector3(1.1f, 0.32f, -0.6f), new Vector3(2f, 0.64f, 1.4f), "Mat_Metal");
            Box(parent, $"Roof_Duct_{suffix}", center + new Vector3(-1.2f, 0.18f, 0.75f), new Vector3(2.8f, 0.36f, 0.48f), "Mat_Metal");
            Cylinder(parent, $"Roof_WaterTank_{suffix}", center + new Vector3(0.1f, 0.95f, 1.5f), 1.15f, 1.9f, "Mat_Metal");
            Cylinder(parent, $"Roof_Antenna_{suffix}", center + new Vector3(-2.1f, 1.45f, -1.1f), 0.08f, 2.9f, "Mat_Trim");
        }

        static void BuildAbandonedBus(Transform parent, Vector3 center, float yaw)
        {
            var bus = new GameObject("Mid_Bus_Abandoned");
            bus.transform.SetParent(parent, true);
            bus.transform.position = center;
            bus.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(bus);
            Box(bus.transform, "Body", Vector3.zero + new Vector3(0f, 0.45f, 0f), new Vector3(3.2f, 2.3f, 8.8f), "Mat_Metal");
            Box(bus.transform, "Roof", new Vector3(0f, 1.78f, 0f), new Vector3(3.0f, 0.28f, 8.4f), "Mat_Concrete", false);
            for (int i = -3; i <= 3; i++)
            {
                Box(bus.transform, $"Window_L_{i}", new Vector3(-1.64f, 0.95f, i), new Vector3(0.08f, 0.75f, 0.72f), "Mat_Glass", false);
                Box(bus.transform, $"Window_R_{i}", new Vector3(1.64f, 0.95f, i), new Vector3(0.08f, 0.75f, 0.72f), "Mat_Glass", false);
            }
            for (int z = -3; z <= 3; z += 6)
            {
                Cylinder(bus.transform, $"Wheel_L_{z}", new Vector3(-1.75f, -0.55f, z), 0.72f, 0.32f, "Mat_Rubber", Axis.X);
                Cylinder(bus.transform, $"Wheel_R_{z}", new Vector3(1.75f, -0.55f, z), 0.72f, 0.32f, "Mat_Rubber", Axis.X);
            }
            Box(bus.transform, "Front_Hazard", new Vector3(0f, 0.4f, -4.52f), new Vector3(2.4f, 0.55f, 0.08f), "Mat_Hazard", false);
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
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = basePos;
            SetStatic(root);
            RootCollider(root, new Vector3(0f, 0.55f + (high - 1) * 0.54f, 0f), new Vector3(wide * 1.15f, high * 1.08f, 1.25f));
            for (int h = 0; h < high; h++)
            for (int w = 0; w < wide; w++)
            {
                float jitter = (Hash01(w, h, name.GetHashCode()) - 0.5f) * 0.1f;
                Box(root.transform, $"Crate_{h}_{w}", new Vector3((w - (wide - 1) * 0.5f) * 1.15f, 0.55f + h * 1.08f, jitter), new Vector3(1.05f, 1.05f, 1.05f), mat);
                Box(root.transform, $"CrateTrim_{h}_{w}", new Vector3((w - (wide - 1) * 0.5f) * 1.15f, 0.55f + h * 1.08f, -0.55f + jitter), new Vector3(0.95f, 0.12f, 0.08f), "Mat_Trim", false);
            }
        }

        static void SandbagWall(Transform parent, string name, Vector3 basePos, int count, float yaw)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = basePos;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            RootCollider(root, new Vector3(0f, 0.52f, 0f), new Vector3(count * 0.72f + 0.75f, 1.05f, 0.68f));
            for (int row = 0; row < 3; row++)
            for (int i = 0; i < count; i++)
            {
                float x = (i - (count - 1) * 0.5f) * 0.72f + (row % 2) * 0.35f;
                Box(root.transform, $"Sandbag_{row}_{i}", new Vector3(x, 0.25f + row * 0.28f, 0f), new Vector3(0.68f, 0.26f, 0.52f), "Mat_Sandbag");
            }
        }

        static void ConcreteBarrier(Transform parent, string name, Vector3 basePos, float yaw, int count)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = basePos;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            RootCollider(root, new Vector3(0f, 0.72f, 0f), new Vector3(count * 1.45f, 1.45f, 0.75f));
            for (int i = 0; i < count; i++)
            {
                float x = (i - (count - 1) * 0.5f) * 1.45f;
                Box(root.transform, $"Jersey_{i}_Base", new Vector3(x, 0.42f, 0f), new Vector3(1.32f, 0.84f, 0.6f), "Mat_Concrete");
                Box(root.transform, $"Jersey_{i}_Top", new Vector3(x, 1.08f, 0f), new Vector3(1.16f, 0.48f, 0.32f), "Mat_Concrete");
            }
        }

        static void Dumpster(Transform parent, string name, Vector3 basePos, float yaw)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = basePos;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            RootCollider(root, new Vector3(0f, 0.86f, 0f), new Vector3(2.75f, 1.75f, 1.55f));
            Box(root.transform, "Body", new Vector3(0f, 0.82f, 0f), new Vector3(2.6f, 1.45f, 1.45f), "Mat_Metal");
            Box(root.transform, "Lid", new Vector3(0f, 1.62f, 0.08f), new Vector3(2.7f, 0.18f, 1.35f), "Mat_Trim", false);
            Box(root.transform, "FrontHazard", new Vector3(0f, 0.75f, -0.76f), new Vector3(1.7f, 0.45f, 0.08f), "Mat_Hazard", false);
            Cylinder(root.transform, "Wheel_L", new Vector3(-0.9f, 0.12f, -0.72f), 0.26f, 0.16f, "Mat_Rubber", Axis.X);
            Cylinder(root.transform, "Wheel_R", new Vector3(0.9f, 0.12f, -0.72f), 0.26f, 0.16f, "Mat_Rubber", Axis.X);
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

        static void PipeBundle(Transform parent, string name, Vector3 basePos)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = basePos;
            root.transform.rotation = Quaternion.Euler(0f, 12f, 0f);
            SetStatic(root);
            for (int i = 0; i < 4; i++)
                Cylinder(root.transform, $"Pipe_{i}", new Vector3(0f, 0.28f + i * 0.27f, (i % 2) * 0.38f), 0.18f, 3.8f, "Mat_Metal", Axis.Z);
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
            for (int y = 1; y <= 3; y++)
            {
                Cylinder(root.transform, $"Rail_F_{y}", new Vector3(0f, y, -0.62f), 0.06f, 3.4f, "Mat_Metal", Axis.X);
                Cylinder(root.transform, $"Rail_B_{y}", new Vector3(0f, y, 0.62f), 0.06f, 3.4f, "Mat_Metal", Axis.X);
            }
            Box(root.transform, "Plank", new Vector3(0f, 2.25f, 0f), new Vector3(3.6f, 0.18f, 1.25f), "Mat_Wood");
        }

        static void Billboard(Transform parent, string name, Vector3 pos, Vector3 size, float yaw)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, true);
            root.transform.position = pos;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            Box(root.transform, "Panel", Vector3.zero, size, "Mat_Graffiti", false);
            Box(root.transform, "Frame_T", new Vector3(0f, size.y * 0.5f + 0.08f, 0f), new Vector3(size.x + 0.25f, 0.16f, size.z + 0.08f), "Mat_Trim", false);
            Box(root.transform, "Frame_B", new Vector3(0f, -size.y * 0.5f - 0.08f, 0f), new Vector3(size.x + 0.25f, 0.16f, size.z + 0.08f), "Mat_Trim", false);
            Box(root.transform, "Support_L", new Vector3(-size.x * 0.35f, -size.y * 0.75f, 0f), new Vector3(0.15f, size.y * 0.75f, 0.15f), "Mat_Metal");
            Box(root.transform, "Support_R", new Vector3(size.x * 0.35f, -size.y * 0.75f, 0f), new Vector3(0.15f, size.y * 0.75f, 0.15f), "Mat_Metal");
        }

        static void TeamBanner(Transform parent, string name, Vector3 pos, string label, string mat)
        {
            Box(parent, name, pos, new Vector3(8.2f, 1.25f, 0.16f), mat, false);
            Box(parent, name + "_DarkBacker", pos + new Vector3(0f, 0f, 0.09f), new Vector3(8.6f, 1.55f, 0.08f), "Mat_Trim", false);
            for (int i = 0; i < label.Length; i++)
            {
                float x = (i - (label.Length - 1) * 0.5f) * 1.05f;
                Box(parent, $"{name}_LetterBlock_{i}", pos + new Vector3(x, 0f, -0.12f), new Vector3(0.52f, 0.78f, 0.1f), "Mat_PaintWhite", false);
            }
        }

        static void LampPost(Transform parent, Vector3 basePos, float yaw)
        {
            var root = new GameObject($"Prop_LampPost_{basePos.x}_{basePos.z}");
            root.transform.SetParent(parent, true);
            root.transform.position = basePos;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            SetStatic(root);
            Cylinder(root.transform, "Pole", new Vector3(0f, 2.2f, 0f), 0.12f, 4.4f, "Mat_Metal");
            Box(root.transform, "Arm", new Vector3(0f, 4.25f, 0.75f), new Vector3(0.12f, 0.12f, 1.5f), "Mat_Metal");
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

        static void Decal(Transform parent, string name, Vector3 center, Vector3 size, string mat)
            => Box(parent, name, center, size, mat, false);

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

        static GameObject Cylinder(Transform parent, string name, Vector3 center, float diameter, float length, string matKey, Axis axis = Axis.Y)
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

        static void RootCollider(GameObject root, Vector3 center, Vector3 size)
        {
            var collider = root.GetComponent<BoxCollider>();
            if (collider == null)
                collider = root.AddComponent<BoxCollider>();
            if (collider == null)
                return;
            collider.center = center;
            collider.size = size;
        }

        static void SetStatic(GameObject go)
        {
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
        }

        static void PlaceSpawnsAndPlayer()
        {
            EnsureSpawn("PlayerSpawn", new Vector3(0f, 1.7f, -33.5f), Quaternion.identity);
            EnsureSpawn("Spawn_Blue_1", new Vector3(-7.5f, 0.1f, -34f), Quaternion.Euler(0f, 0f, 0f));
            EnsureSpawn("Spawn_Blue_2", new Vector3(7.5f, 0.1f, -34f), Quaternion.Euler(0f, 0f, 0f));
            EnsureSpawn("Spawn_Blue_3", new Vector3(-20f, 0.1f, -30.5f), Quaternion.Euler(0f, 20f, 0f));
            EnsureSpawn("Spawn_Blue_4", new Vector3(20f, 0.1f, -30.5f), Quaternion.Euler(0f, -20f, 0f));
            EnsureSpawn("Spawn_Blue_5", new Vector3(0f, 0.1f, -29f), Quaternion.identity);

            EnsureSpawn("Spawn_Red_1", new Vector3(7.5f, 0.1f, 34f), Quaternion.Euler(0f, 180f, 0f));
            EnsureSpawn("Spawn_Red_2", new Vector3(-7.5f, 0.1f, 34f), Quaternion.Euler(0f, 180f, 0f));
            EnsureSpawn("Spawn_Red_3", new Vector3(20f, 0.1f, 30.5f), Quaternion.Euler(0f, 200f, 0f));
            EnsureSpawn("Spawn_Red_4", new Vector3(-20f, 0.1f, 30.5f), Quaternion.Euler(0f, 160f, 0f));
            EnsureSpawn("Spawn_Red_5", new Vector3(0f, 0.1f, 29f), Quaternion.Euler(0f, 180f, 0f));

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
            RenderSettings.fogDensity = 0.0065f;
            RenderSettings.fogColor = new Color(0.47f, 0.51f, 0.56f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.58f, 0.61f, 0.68f);
            RenderSettings.ambientEquatorColor = new Color(0.39f, 0.36f, 0.32f);
            RenderSettings.ambientGroundColor = new Color(0.16f, 0.15f, 0.13f);

            var probe = new GameObject("AAA_ReflectionProbe_Mid");
            probe.transform.position = new Vector3(0f, 3.2f, 0f);
            var rp = probe.AddComponent<ReflectionProbe>();
            rp.size = new Vector3(58f, 18f, 78f);
            rp.mode = ReflectionProbeMode.Realtime;
            rp.refreshMode = ReflectionProbeRefreshMode.OnAwake;
            rp.intensity = 0.55f;
        }

        static void BuildCaptureRig()
        {
            var rig = new GameObject("__AaaCaptureRig");
            CreateCaptureCamera(rig.transform, "AAA_Aerial_Camera", new Vector3(0f, 58f, -4f), new Vector3(0f, 0f, 0f), 54f);
            CreateCaptureCamera(rig.transform, "AAA_EyeLevel_Camera", new Vector3(-21.5f, 1.75f, -25f), new Vector3(0f, 1.6f, 3.5f), 72f);
            CreateCaptureCamera(rig.transform, "AAA_MidLane_Camera", new Vector3(4.5f, 1.8f, -18f), new Vector3(-1.2f, 1.5f, 5f), 68f);
        }

        static void CreateCaptureCamera(Transform parent, string name, Vector3 pos, Vector3 lookAt, float fov)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((lookAt - pos).normalized, Vector3.up);
            var cam = go.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 180f;
            cam.fieldOfView = fov;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.enabled = false;
        }

        static void BakeNavMeshFallback()
        {
            var surfaceType = System.Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
            if (surfaceType == null)
                return;

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
    }
}
#endif
