#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Overflow set-dressing pass for the current Bldg_* Arena footprint (PosScale 1.22).
    /// Idempotent: deletes OD_OverflowDressing root before rebuild.
    /// Menu: Arena FPS / AAA Overflow Dressing Pass
    /// Do NOT run AaaCityKitSwap / KenneyPbr / PolyHavenFacade / FacadeDetail / EnvPass2 / EyeLevelDensify.
    /// </summary>
    public static class AaaOverflowDressingPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string RootName = "OD_OverflowDressing";
        const string MatDir = "Assets/_Project/Art/Materials/OverflowDressing";
        const string GenDir = "Assets/_Project/Art/Models/Environment/Generated";
        const string SignDir = "Assets/_Project/Art/Decals/Signage";
        const string CarDir = "Assets/_Project/Art/Models/Environment/City/Kenney_CarKit/Models/OBJ format";
        const string ShotDir = "Assets/_Project/Art/Screenshots/Dressed";
        const float PosScale = 1.22f;

        // Scaled targets (~1.6x of 96x126 spec)
        const int TargetSigns = 45;
        const int TargetAc = 48;
        const int TargetDishes = 35;
        const int TargetAwnings = 26;
        const int TargetCables = 19;
        const int TargetPoles = 10;
        const int TargetCars = 11;
        const int TargetJersey = 23;
        const int TargetRubble = 29;
        const int TargetDumpsters = 13;
        const int TargetCrates = 39;
        const int TargetBarrels = 26;
        const int TargetShutters = 19;
        const int TargetPipes = 32;
        const int TargetAntennas = 23;

        static readonly Dictionary<string, Material> Mats = new();
        static readonly Dictionary<string, int> Counts = new();
        static readonly System.Random Rng = new(20260727);

        static Transform _root;
        static Transform _map;

        [MenuItem("Arena FPS/AAA Overflow Dressing Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[OD] Exiting play mode; run again in edit mode.");
                return;
            }

            OpenArena();
            _map = GameObject.Find("ThreeLaneMap")?.transform;
            if (_map == null)
            {
                Debug.LogError("[OD] ThreeLaneMap missing; aborting.");
                return;
            }

            Counts.Clear();
            Mats.Clear();
            _contactMat = null;
            _importTouched.Clear();
            EnsureFolders();

            try
            {
                ClearPrevious();
                HideLegacyGreyboxDressing();

                _root = new GameObject(RootName).transform;
                _root.SetParent(_map, false);

                Stage0_FixExposure();
                Stage1_BuildingMaterials();
                Stage2_Signage();
                Stage3_FacadeClutter();
                Stage4_OverheadCables();
                Stage5_GroundDressing();
                Stage6_GroundingAndDecals();

                SetStaticRecursive(_root.gameObject);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();

                LogCounts();
                Debug.Log("[OD] Overflow dressing pass complete. Re-run menu item to rebuild.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[OD] FATAL: " + ex);
                try
                {
                    EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                    EditorSceneManager.SaveOpenScenes();
                }
                catch { /* ignore */ }
                throw;
            }
        }

        [MenuItem("Arena FPS/AAA Overflow Dressing Pass/Stage 0 Exposure Only")]
        public static void RunStage0Only()
        {
            OpenArena();
            Stage0_FixExposure();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            Debug.Log("[OD] Stage 0 exposure fix applied.");
        }

        [MenuItem("Arena FPS/AAA Overflow Dressing Pass/Capture Verification Shots")]
        public static void CaptureVerificationShots()
        {
            OpenArena();
            EnsureFolders();
            Stage0_FixExposure();

            // Aim AT dressed facades / clutter clusters, not empty corridor centerlines.
            var shots = new (string name, Vector3 pos, Vector3 look)[]
            {
                ("01_MainStreet_Mid", new Vector3(-1.5f, 1.65f, -10f), new Vector3(10f, 3.2f, 8f)),
                ("02_MainStreet_North", new Vector3(2f, 1.65f, 14f), new Vector3(-8f, 4f, 28f)),
                ("03_Market_Lane", new Vector3(24f, 1.65f, -6f), new Vector3(40f, 3.5f, 4f)),
                ("04_West_Alley", new Vector3(-28f, 1.65f, -4f), new Vector3(-36f, 3f, 12f)),
                ("05_DeathTriangle", new Vector3(0f, 1.65f, 2f), new Vector3(14f, 4f, 18f)),
            };

            // Prefer existing scene camera or create temp
            Camera cam = Camera.main;
            if (cam == null)
                cam = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include).FirstOrDefault();
            GameObject temp = null;
            if (cam == null)
            {
                temp = new GameObject("OD_CaptureCam");
                cam = temp.AddComponent<Camera>();
                cam.tag = "MainCamera";
            }

            cam.allowHDR = true;
            cam.fieldOfView = 70f;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null) data.renderPostProcessing = true;

            foreach (var (name, pos, look) in shots)
            {
                cam.transform.position = pos;
                cam.transform.LookAt(look);
                var path = $"{ShotDir}/{name}.png";
                // Use Camera capture via RenderTexture
                int w = 1920, h = 1080;
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                var prev = cam.targetTexture;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                cam.targetTexture = prev;
                RenderTexture.active = null;
                Object.DestroyImmediate(rt);
                File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Debug.Log("[OD] Wrote " + path);
            }

            if (temp != null) Object.DestroyImmediate(temp);
            AssetDatabase.Refresh();
            Debug.Log("[OD] Verification shots written to " + ShotDir);
        }

        /// <summary>Batch entry: capture shots + collider audit after dressing.</summary>
        public static void RunVerify()
        {
            CaptureVerificationShots();
            AuditInvisibleColliders();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Arena FPS/AAA Overflow Dressing Pass/Audit Invisible Colliders")]
        public static void AuditInvisibleColliders()
        {
            var all = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include);
            int invis = 0;
            foreach (var c in all)
            {
                if (c is CharacterController) continue;
                var r = c.GetComponent<Renderer>();
                if (r == null) r = c.GetComponentInChildren<Renderer>();
                if (r == null || !r.enabled)
                {
                    invis++;
                    if (invis <= 15)
                        Debug.LogWarning($"[OD] Invisible collider: {GetHierarchyPath(c.transform)} ({c.GetType().Name})");
                }
            }
            Debug.Log($"[OD] colliders={all.Length} invisible={invis}");
        }

        static string GetHierarchyPath(Transform t)
        {
            var p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        static void OpenArena()
        {
            var active = SceneManager.GetActiveScene();
            if (active.path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static void EnsureFolders()
        {
            foreach (var d in new[] { MatDir, ShotDir })
            {
                var full = Path.GetFullPath(d);
                if (!Directory.Exists(full)) Directory.CreateDirectory(full);
            }
            AssetDatabase.Refresh();
        }

        static void ClearPrevious()
        {
            var doomed = new List<GameObject>();
            foreach (Transform t in _map)
            {
                if (t.name == RootName || t.name.StartsWith("OD_"))
                    doomed.Add(t.gameObject);
            }
            foreach (var go in doomed)
                Object.DestroyImmediate(go);
        }

        /// <summary>
        /// Hide primitive EnvironmentPass placards that read as greybox once real art is down.
        /// Does not touch Cover_*, Prop_ vehicles, poles, or gameplay geometry.
        /// </summary>
        static void HideLegacyGreyboxDressing()
        {
            string[] prefixes =
            {
                "Sign_Market_", "Sign_Main_", "AC_Unit_", "Poster_", "Billboard_",
                "Cable_Main_", "Cable_Market_", "Cable_West_", "AC_"
            };
            int hidden = 0;
            foreach (Transform t in _map)
            {
                bool match = false;
                foreach (var p in prefixes)
                {
                    if (t.name.StartsWith(p)) { match = true; break; }
                }
                // Keep Practical_* lights; hide only AC_0..AC_7 box props (not AC_Unit which already matched).
                if (!match) continue;
                if (t.name.StartsWith("AC_") && !t.name.StartsWith("AC_Unit_") && t.name.Length > 4)
                {
                    // AC_0 style — hide. AC_Unit already matched.
                }
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                    r.enabled = false;
                foreach (var c in t.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(c);
                hidden++;
            }
            Debug.Log($"[OD] Hidden {hidden} legacy greybox dressing objects (renderers off, colliders removed).");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 0 — exposure / grade
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage0_FixExposure()
        {
            // Soft midday haze — calibrated with polish pass (cloud structure below tonemap knee).
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (l.type != LightType.Directional) continue;
                l.intensity = 1.35f;
                l.color = new Color(1f, 0.96f, 0.88f);
                l.shadowStrength = 0.30f;
                l.transform.rotation = Quaternion.Euler(56f, -38f, 0f);
                EditorUtility.SetDirty(l);
            }

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.76f, 0.71f, 0.60f);
            RenderSettings.fogDensity = 0.0042f;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.78f, 0.76f, 0.70f);
            RenderSettings.ambientEquatorColor = new Color(0.74f, 0.68f, 0.56f);
            RenderSettings.ambientGroundColor = new Color(0.56f, 0.50f, 0.40f);
            RenderSettings.ambientIntensity = 1.40f;

            var sky = RenderSettings.skybox;
            if (sky != null)
            {
                if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", 0.58f);
                if (sky.HasProperty("_Rotation")) sky.SetFloat("_Rotation", 90f);
                if (sky.HasProperty("_Tint")) sky.SetColor("_Tint", new Color(0.96f, 0.93f, 0.88f));
                EditorUtility.SetDirty(sky);
            }

            var volume = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include)
                .FirstOrDefault(v => v.isGlobal);
            if (volume == null || volume.sharedProfile == null)
            {
                Debug.LogWarning("[OD] Global Volume missing; grade skipped.");
                return;
            }

            var profile = volume.sharedProfile;

            var color = GetOrAddVolume<ColorAdjustments>(profile);
            SetV(color.postExposure, 0.02f);

            GetOrAddVolume<WhiteBalance>(profile);
            GetOrAddVolume<LiftGammaGain>(profile);
            GetOrAddVolume<ShadowsMidtonesHighlights>(profile);
            AaaUrpGradeUtil.ApplyCanonicalDustyGrade(profile, "AaaOverflowDressingPass");

            var bloom = GetOrAddVolume<Bloom>(profile);
            SetV(bloom.threshold, 1.45f);
            SetV(bloom.intensity, 0.02f);
            SetV(bloom.tint, new Color(1f, 0.94f, 0.86f));

            var vignette = GetOrAddVolume<Vignette>(profile);
            SetV(vignette.intensity, 0.08f);
            SetV(vignette.smoothness, 0.50f);
            SetV(vignette.color, new Color(0.10f, 0.07f, 0.04f));

            EditorUtility.SetDirty(profile);
            EditorUtility.SetDirty(volume);
            DynamicGI.UpdateEnvironment();
            Bump("stage0");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 1 — building materials
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage1_BuildingMaterials()
        {
            BuildPbrLibrary();

            // Per-building primary / plinth / accent assignments
            var recipes = new Dictionary<string, (string primary, string plinth, string accent)>
            {
                ["Bldg_Bank"] = ("concrete", "concreteDark", "plasterTan"),
                ["Bldg_Shoes"] = ("brickWarm", "concreteDark", "plasterCream"),
                ["Bldg_Baskets"] = ("brickDeep", "concrete", "plasterOchre"),
                ["Bldg_Electronics"] = ("plasterTan", "concreteDark", "plasterCream"),
                ["Bldg_Spices"] = ("plasterOchre", "brickWarm", "plasterCream"),
                ["Bldg_Deli"] = ("brickWarm", "concreteDark", "plasterTan"),
                ["Bldg_Construction"] = ("concrete", "concreteDark", "brickDeep"),
                ["Bldg_FruitShed"] = ("wood", "concreteDark", "plasterTan"),
                ["Bldg_StallsWest"] = ("wood", "brickWarm", "plasterOchre"),
                ["Bldg_GlassCurve"] = ("concrete", "concreteDark", "glass"),
                ["Bldg_ShopRow_E1"] = ("brickDeep", "concrete", "plasterTan"),
                ["Bldg_ShopRow_E2"] = ("brickWarm", "concreteDark", "plasterCream"),
                ["Bldg_ShopRow_E3"] = ("plasterCream", "brickWarm", "plasterOchre"),
                ["Bldg_WestBlock_S"] = ("concrete", "concreteDark", "plasterTan"),
                ["Bldg_WestBlock_N"] = ("plasterTan", "concrete", "brickDeep"),
                ["Bldg_BlueSpawnHall"] = ("concrete", "concreteDark", "plasterCream"),
                ["Bldg_RedSpawnHall"] = ("concreteDark", "concrete", "plasterOchre"),
                ["Bldg_TopBottom"] = ("brickWarm", "concreteDark", "plasterTan"),
                ["Bldg_MarketAnnex_S"] = ("plasterOchre", "brickWarm", "plasterCream"),
                ["Bldg_MarketAnnex_N"] = ("brickDeep", "concrete", "plasterTan"),
                ["Bldg_WestAnnex_Mid"] = ("plasterCream", "concreteDark", "brickWarm"),
                ["Bldg_PlazaKiosk_N"] = ("wood", "concrete", "plasterTan"),
                ["Bldg_PlazaKiosk_S"] = ("wood", "brickWarm", "plasterOchre"),
            };

            int assigned = 0;
            foreach (Transform bldg in _map)
            {
                if (!bldg.name.StartsWith("Bldg_") || bldg.name.Contains("Fountain")) continue;
                if (!recipes.TryGetValue(bldg.name, out var recipe))
                    recipe = ("plasterTan", "concreteDark", "brickWarm");

                foreach (var r in bldg.GetComponentsInChildren<Renderer>(true))
                {
                    var n = r.name;
                    Material mat;
                    float tiling;

                    if (n.Contains("_Win_") && n.EndsWith("_Fill"))
                    {
                        mat = Mats["glass"];
                        tiling = 1f;
                    }
                    else if (n.Contains("_Win_") || n.Contains("Trim") || n.Contains("Pillar") || n.Contains("Parapet"))
                    {
                        mat = Mats["trim"];
                        tiling = 2.5f;
                    }
                    else if (n.Contains("RoofLedge") || n.Contains("Plinth") || n.Contains("Base"))
                    {
                        mat = Mats[recipe.plinth];
                        tiling = TilingFor(r, 2.8f);
                    }
                    else if (n.Contains("_Mass") || n.Contains("Wall") || n == bldg.name)
                    {
                        // Ground-floor band via second material on lower third — approximate with primary
                        mat = Mats[recipe.primary];
                        tiling = TilingFor(r, 3.2f);
                    }
                    else
                    {
                        mat = Mats[recipe.accent];
                        tiling = TilingFor(r, 3f);
                    }

                    ApplyMat(r, mat, tiling);
                    assigned++;
                }

                // Add a plinth band cube on street-facing mass to break flat walls
                AddPlinthBand(bldg, Mats[recipe.plinth]);
            }

            // Ground + roads
            ApplyNamed("Ground", Mats["asphalt"], 14f);
            ApplyNamed("Beach_Dirt", Mats.ContainsKey("dirt") ? Mats["dirt"] : Mats["dirtGround"], 10f);
            foreach (Transform t in _map)
            {
                if (t.name.EndsWith("_Stripe")) continue;
                if (t.name.StartsWith("Road_") || t.name.StartsWith("Conn_"))
                    ApplyMat(t.GetComponent<Renderer>(), Mats["asphalt"], 10f);
                if (t.name.StartsWith("Sidewalk_"))
                    ApplyMat(t.GetComponent<Renderer>(), Mats["concrete"], 4f);
                if (t.name.StartsWith("Wall_"))
                    ApplyMat(t.GetComponentInChildren<Renderer>(), Mats["concreteDark"], 5f);
            }

            Bump("materials", assigned);
            Debug.Log($"[OD] Stage 1 materials assigned to {assigned} renderers.");
        }

        static float TilingFor(Renderer r, float metresPerTile)
        {
            if (r == null) return 3f;
            var s = r.bounds.size;
            float major = Mathf.Max(s.x, s.y, s.z);
            return Mathf.Clamp(major / metresPerTile, 1.5f, 8f);
        }

        static void AddPlinthBand(Transform bldg, Material mat)
        {
            var mass = bldg.Find(bldg.name + "_Mass");
            if (mass == null) return;
            var mr = mass.GetComponent<Renderer>();
            if (mr == null) return;
            var b = mr.bounds;
            float h = 1.15f;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"OD_Plinth_{bldg.name}";
            go.transform.SetParent(_root, true);
            go.transform.position = new Vector3(b.center.x, h * 0.5f, b.center.z);
            go.transform.localScale = new Vector3(b.size.x + 0.08f, h, b.size.z + 0.08f);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            ApplyMat(go.GetComponent<Renderer>(), mat, 2.2f);
            SetStatic(go);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 2 — signage
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage2_Signage()
        {
            var fascia = LoadSigns("fascia_");
            var vertical = LoadSigns("vertical_");
            var plates = LoadSigns("plate_");
            var banners = LoadSigns("banner_");
            if (fascia.Count == 0)
            {
                Debug.LogError("[OD] No signage textures found in " + SignDir);
                return;
            }

            // Prefer street-facing buildings along Main (near X~0) and Market (east X~30+)
            var hosts = CollectBuildingFacades()
                .OrderByDescending(f => FacadePriority(f))
                .ToList();

            int placed = 0;
            int yellow = 0, blue = 0, white = 0, red = 0;

            // Stack 2–3 signs on high-priority facades first
            foreach (var face in hosts)
            {
                if (placed >= TargetSigns) break;
                int stack = face.priority >= 2 ? 3 : (face.priority >= 1 ? 2 : 1);
                for (int s = 0; s < stack && placed < TargetSigns; s++)
                {
                    bool projecting = (placed % 5 == 0) || (placed % 7 == 0);
                    bool verticalSign = placed % 4 == 3;
                    bool banner = placed % 6 == 5;

                    List<SignAsset> pool;
                    if (verticalSign) pool = vertical.Count > 0 ? vertical : fascia;
                    else if (banner) pool = banners.Count > 0 ? banners : fascia;
                    else if (s == 1 && plates.Count > 0) pool = plates;
                    else pool = fascia;

                    var asset = PickByColorQuota(pool, ref yellow, ref blue, ref white, ref red);
                    PlaceSign(face, asset, s, projecting, verticalSign || asset.isVertical);
                    placed++;
                }
            }

            Bump("signs", placed);
            Debug.Log($"[OD] Stage 2 signs={placed} (Y{yellow}/B{blue}/W{white}/R{red}).");
        }

        struct SignAsset
        {
            public string path;
            public string color;
            public bool isVertical;
            public bool isBanner;
            public float aspect; // width/height
        }

        struct FacadeSlot
        {
            public Transform bldg;
            public Vector3 center;
            public Vector3 normal;
            public Vector3 tangent;
            public float width;
            public float height;
            public int priority;
        }

        static List<SignAsset> LoadSigns(string prefix)
        {
            var list = new List<SignAsset>();
            foreach (var guid in AssetDatabase.FindAssets(prefix + " t:Texture2D", new[] { SignDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var file = Path.GetFileNameWithoutExtension(path);
                if (file.EndsWith("_normal") || file.EndsWith("_rough")) continue;
                if (!file.StartsWith(prefix.TrimEnd('_')) && !file.StartsWith(prefix)) continue;
                // Only albedo
                if (file.Contains("_normal") || file.Contains("_rough")) continue;

                string color = "mixed";
                var lower = file.ToLowerInvariant();
                if (lower.Contains("yellow") || lower.Contains("gold") || lower.Contains("ochre")) color = "yellow";
                else if (lower.Contains("blue") || lower.Contains("cobalt") || lower.Contains("teal")) color = "blue";
                else if (lower.Contains("white") || lower.Contains("cream")) color = "white";
                else if (lower.Contains("red")) color = "red";

                bool vert = prefix.StartsWith("vertical");
                bool banner = prefix.StartsWith("banner") || prefix.StartsWith("awning");
                float aspect = vert ? 0.25f : (banner ? 2.6f : (prefix.StartsWith("plate") ? 1f : 4f));

                list.Add(new SignAsset { path = path, color = color, isVertical = vert, isBanner = banner, aspect = aspect });
            }
            return list;
        }

        static SignAsset PickByColorQuota(List<SignAsset> pool, ref int y, ref int b, ref int w, ref int r)
        {
            string want = null;
            if (y < 8) want = "yellow";
            else if (b < 8) want = "blue";
            else if (w < 6) want = "white";
            else if (r < 6) want = "red";

            SignAsset pick;
            if (want != null)
            {
                var matches = pool.Where(p => p.color == want).ToList();
                pick = matches.Count > 0 ? matches[Rng.Next(matches.Count)] : pool[Rng.Next(pool.Count)];
            }
            else
            {
                pick = pool[Rng.Next(pool.Count)];
            }

            if (pick.color == "yellow") y++;
            else if (pick.color == "blue") b++;
            else if (pick.color == "white") w++;
            else if (pick.color == "red") r++;
            return pick;
        }

        static List<FacadeSlot> CollectBuildingFacades()
        {
            var list = new List<FacadeSlot>();
            foreach (Transform bldg in _map)
            {
                if (!bldg.name.StartsWith("Bldg_") || bldg.name.Contains("Fountain")) continue;
                if (bldg.name.Contains("SpawnHall")) continue;

                var mass = bldg.Find(bldg.name + "_Mass") ?? bldg;
                var r = mass.GetComponent<Renderer>() ?? mass.GetComponentInChildren<Renderer>();
                if (r == null) continue;
                var bounds = r.bounds;

                // Four faces
                AddFace(list, bldg, bounds, Vector3.forward, 0);  // +Z
                AddFace(list, bldg, bounds, Vector3.back, 0);     // -Z
                AddFace(list, bldg, bounds, Vector3.right, 0);    // +X
                AddFace(list, bldg, bounds, Vector3.left, 0);     // -X
            }
            return list;
        }

        static void AddFace(List<FacadeSlot> list, Transform bldg, Bounds bounds, Vector3 normal, int _)
        {
            // Face center on surface
            var center = bounds.center + Vector3.Scale(normal, bounds.extents);
            center.y = bounds.min.y + Mathf.Clamp(bounds.size.y * 0.35f, 2.2f, 5.5f);

            var tangent = Vector3.Cross(Vector3.up, normal).normalized;
            if (tangent.sqrMagnitude < 0.01f) tangent = Vector3.right;
            float width = Mathf.Abs(Vector3.Dot(bounds.size, tangent));
            float height = bounds.size.y;

            int pri = FacadePriorityName(bldg.name, normal);
            list.Add(new FacadeSlot
            {
                bldg = bldg,
                center = center,
                normal = normal,
                tangent = tangent,
                width = width,
                height = height,
                priority = pri
            });
        }

        static int FacadePriorityName(string name, Vector3 normal)
        {
            // Main street faces (toward X=0) and market (east shop rows west faces)
            bool towardMain = (name.Contains("Shoes") || name.Contains("Bank") || name.Contains("TopBottom")
                || name.Contains("Construction") || name.Contains("Deli") || name.Contains("Spices")
                || name.Contains("Baskets") || name.Contains("Electronics") || name.Contains("Glass"))
                && (Mathf.Abs(normal.x) > 0.5f || Mathf.Abs(normal.z) > 0.5f);

            bool marketWestFace = (name.Contains("ShopRow") || name.Contains("MarketAnnex") || name.Contains("Spices")
                || name.Contains("Deli") || name.Contains("Baskets")) && normal.x < -0.5f;

            bool westAlley = (name.Contains("Stalls") || name.Contains("West") || name.Contains("Fruit")) && normal.x > 0.5f;

            if (marketWestFace || towardMain) return 2;
            if (westAlley) return 1;
            return 0;
        }

        static int FacadePriority(FacadeSlot f) => f.priority;

        static void PlaceSign(FacadeSlot face, SignAsset asset, int stackIndex, bool projecting, bool vertical)
        {
            float w, h;
            if (vertical) { w = 0.7f; h = 2.4f; }
            else if (asset.isBanner) { w = Mathf.Min(3.6f, face.width * 0.55f); h = w / 2.6f; }
            else if (asset.aspect >= 3f) { w = Mathf.Min(3.8f, face.width * 0.7f); h = w / 4f; }
            else { w = Mathf.Min(1.6f, face.width * 0.35f); h = w; }

            float yOff = stackIndex * (h + 0.12f);
            float xOff = (float)(Rng.NextDouble() * 0.4 - 0.2) * face.width * 0.3f;
            float yawJitter = (float)(Rng.NextDouble() * 8 - 4);

            Vector3 pos = face.center + face.tangent * xOff + Vector3.up * yOff + face.normal * 0.06f;
            Quaternion rot;

            if (projecting && !vertical)
            {
                // Project perpendicular from wall
                pos = face.center + face.tangent * (face.width * 0.35f * ((placedParity() % 2 == 0) ? 1f : -1f))
                      + Vector3.up * yOff + face.normal * (w * 0.5f + 0.1f);
                rot = Quaternion.LookRotation(face.tangent, Vector3.up) * Quaternion.Euler(0f, yawJitter, 0f);
                // Thin board facing along tangent
                CreateSignBoard($"OD_Sign_{Counts.GetValueOrDefault("signs", 0)}", pos, rot, w, h, 0.06f, asset, face.normal);
            }
            else
            {
                rot = Quaternion.LookRotation(-face.normal, Vector3.up) * Quaternion.Euler(0f, yawJitter, 0f);
                CreateSignBoard($"OD_Sign_{Counts.GetValueOrDefault("signs", 0)}", pos, rot, w, h, 0.05f, asset, face.normal);
            }
        }

        static int _signParity;
        static int placedParity() => _signParity++;

        static void CreateSignBoard(string name, Vector3 pos, Quaternion rot, float w, float h, float depth, SignAsset asset, Vector3 wallNormal)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(_root, true);
            go.transform.position = pos;
            go.transform.rotation = rot;
            go.transform.localScale = new Vector3(w, h, depth);
            Object.DestroyImmediate(go.GetComponent<Collider>()); // decorative, above street — no collider

            var mat = MakeSignMaterial(asset);
            ApplyMat(go.GetComponent<Renderer>(), mat, 1f);
            SetStatic(go);
        }

        static Material MakeSignMaterial(SignAsset asset)
        {
            var key = "sign_" + Path.GetFileNameWithoutExtension(asset.path);
            if (Mats.TryGetValue(key, out var existing)) return existing;

            var path = $"{MatDir}/{key}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = key;
                AssetDatabase.CreateAsset(mat, path);
            }

            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(asset.path);
            var normalPath = asset.path.Replace(".png", "_normal.png");
            var roughPath = asset.path.Replace(".png", "_rough.png");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);

            EnsureTextureImport(asset.path, false);
            if (normal != null) EnsureTextureImport(normalPath, true);

            if (albedo != null)
            {
                mat.SetTexture("_BaseMap", albedo);
                mat.mainTexture = albedo;
            }
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 0.05f);
            mat.SetFloat("_Smoothness", 0.35f);
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
                mat.SetFloat("_BumpScale", 1.1f);
            }

            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            Mats[key] = mat;
            return mat;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 3 — facade clutter
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage3_FacadeClutter()
        {
            var acMeshes = LoadMeshes("window_ac_");
            var dishMeshes = LoadMeshes("sat_dish_");
            var awningMeshes = LoadMeshes("awning_");
            var faces = CollectBuildingFacades().Where(f => f.priority >= 1).OrderByDescending(f => f.priority).ToList();
            var allFaces = CollectBuildingFacades();

            // AC units
            int ac = 0;
            foreach (var face in faces.Concat(allFaces))
            {
                if (ac >= TargetAc) break;
                int perFace = face.priority >= 2 ? 3 : 2;
                for (int i = 0; i < perFace && ac < TargetAc; i++)
                {
                    float along = (i + 0.5f) / perFace - 0.5f;
                    float y = 2.4f + (i % 3) * 1.8f;
                    if (y > face.height - 0.5f) y = face.height * 0.55f;
                    var pos = new Vector3(face.center.x, face.bldg.position.y, face.center.z)
                              + face.tangent * (along * face.width * 0.7f)
                              + Vector3.up * y
                              + face.normal * 0.08f;
                    // Recompute from bounds bottom
                    var mass = face.bldg.Find(face.bldg.name + "_Mass");
                    float baseY = mass != null && mass.GetComponent<Renderer>() != null
                        ? mass.GetComponent<Renderer>().bounds.min.y : 0f;
                    pos.y = baseY + y;

                    var mesh = acMeshes.Count > 0 ? acMeshes[ac % acMeshes.Count] : null;
                    SpawnPropMesh($"OD_AC_{ac}", mesh, pos,
                        Quaternion.LookRotation(-face.normal), Vector3.one, Mats["metal"], false);
                    ac++;
                }
            }
            Bump("ac", ac);

            // Satellite dishes + antennas on roofs of 2F+ buildings
            int dishes = 0, ants = 0;
            foreach (Transform bldg in _map)
            {
                if (!bldg.name.StartsWith("Bldg_") || bldg.name.Contains("Fountain")) continue;
                var mass = bldg.Find(bldg.name + "_Mass");
                var r = mass != null ? mass.GetComponent<Renderer>() : null;
                if (r == null) continue;
                float h = r.bounds.size.y;
                if (h < 5f) continue;
                var roof = r.bounds.max;
                int nDish = h >= 7f ? 3 : 2;
                for (int i = 0; i < nDish && dishes < TargetDishes; i++)
                {
                    var pos = new Vector3(
                        r.bounds.center.x + (float)(Rng.NextDouble() - 0.5) * r.bounds.size.x * 0.6f,
                        roof.y + 0.05f,
                        r.bounds.center.z + (float)(Rng.NextDouble() - 0.5) * r.bounds.size.z * 0.6f);
                    var mesh = dishMeshes.Count > 0 ? dishMeshes[dishes % dishMeshes.Count] : null;
                    float yaw = Rng.Next(0, 360);
                    SpawnPropMesh($"OD_Dish_{dishes}", mesh, pos, Quaternion.Euler(0, yaw, 0),
                        Vector3.one * (0.9f + (float)Rng.NextDouble() * 0.4f), Mats["metal"], false);
                    dishes++;
                }
                int nAnt = h >= 7f ? 2 : 1;
                for (int i = 0; i < nAnt && ants < TargetAntennas; i++)
                {
                    var pos = new Vector3(
                        r.bounds.center.x + (float)(Rng.NextDouble() - 0.5) * r.bounds.size.x * 0.5f,
                        roof.y,
                        r.bounds.center.z + (float)(Rng.NextDouble() - 0.5) * r.bounds.size.z * 0.5f);
                    SpawnYagi($"OD_Yagi_{ants}", pos);
                    ants++;
                }
            }
            // Top up dishes/antennas on remaining roofs
            foreach (Transform bldg in _map)
            {
                if (dishes >= TargetDishes && ants >= TargetAntennas) break;
                if (!bldg.name.StartsWith("Bldg_")) continue;
                var mass = bldg.Find(bldg.name + "_Mass");
                var r = mass != null ? mass.GetComponent<Renderer>() : null;
                if (r == null || r.bounds.size.y < 4.5f) continue;
                while (dishes < TargetDishes)
                {
                    var pos = new Vector3(
                        r.bounds.center.x + (float)(Rng.NextDouble() - 0.5) * r.bounds.size.x * 0.5f,
                        r.bounds.max.y + 0.05f,
                        r.bounds.center.z + (float)(Rng.NextDouble() - 0.5) * r.bounds.size.z * 0.5f);
                    var mesh = dishMeshes.Count > 0 ? dishMeshes[dishes % dishMeshes.Count] : null;
                    SpawnPropMesh($"OD_Dish_{dishes}", mesh, pos, Quaternion.Euler(0, Rng.Next(360), 0),
                        Vector3.one, Mats["metal"], false);
                    dishes++;
                    if (dishes % 3 == 0) break; // distribute
                }
            }
            Bump("dishes", dishes);
            Bump("antennas", ants);

            // Awnings over shopfronts
            int awn = 0;
            foreach (var face in faces)
            {
                if (awn >= TargetAwnings) break;
                float y = 2.9f;
                var mass = face.bldg.Find(face.bldg.name + "_Mass");
                float baseY = mass?.GetComponent<Renderer>()?.bounds.min.y ?? 0f;
                var pos = new Vector3(face.center.x, baseY + y, face.center.z) + face.normal * 0.05f;
                var mesh = awningMeshes.Count > 0 ? awningMeshes[awn % awningMeshes.Count] : null;
                SpawnPropMesh($"OD_Awning_{awn}", mesh, pos, Quaternion.LookRotation(-face.normal),
                    Vector3.one, Mats["awning"], false);
                awn++;
            }
            Bump("awnings", awn);

            // Roll-up shutters (ground floor)
            int shut = 0;
            foreach (var face in faces)
            {
                if (shut >= TargetShutters) break;
                var mass = face.bldg.Find(face.bldg.name + "_Mass");
                float baseY = mass?.GetComponent<Renderer>()?.bounds.min.y ?? 0f;
                bool open = shut % 2 == 0;
                float h = open ? 0.4f : 2.4f;
                float y = baseY + (open ? 2.5f : 1.2f);
                var pos = new Vector3(face.center.x, y, face.center.z) + face.normal * 0.04f
                          + face.tangent * ((float)(Rng.NextDouble() - 0.5) * face.width * 0.25f);
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"OD_Shutter_{shut}";
                go.transform.SetParent(_root, true);
                go.transform.position = pos;
                go.transform.rotation = Quaternion.LookRotation(-face.normal);
                go.transform.localScale = new Vector3(Mathf.Min(2.8f, face.width * 0.4f), h, 0.08f);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                ApplyMat(go.GetComponent<Renderer>(), Mats["shutter"], 1.5f);
                SetStatic(go);
                shut++;
            }
            Bump("shutters", shut);

            // Facade pipes / conduits
            int pipes = 0;
            foreach (var face in allFaces.Where(f => f.priority >= 0))
            {
                if (pipes >= TargetPipes) break;
                if (Rng.NextDouble() > 0.55 && face.priority < 1) continue;
                var mass = face.bldg.Find(face.bldg.name + "_Mass");
                var r = mass?.GetComponent<Renderer>();
                if (r == null) continue;
                float xOff = (float)(Rng.NextDouble() - 0.5) * face.width * 0.6f;
                var bottom = new Vector3(face.center.x, r.bounds.min.y + 0.3f, face.center.z)
                             + face.tangent * xOff + face.normal * 0.05f;
                float len = r.bounds.size.y * (0.5f + (float)Rng.NextDouble() * 0.4f);
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = $"OD_Pipe_{pipes}";
                go.transform.SetParent(_root, true);
                go.transform.position = bottom + Vector3.up * (len * 0.5f);
                go.transform.localScale = new Vector3(0.12f, len * 0.5f, 0.12f);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                ApplyMat(go.GetComponent<Renderer>(), Mats["pipe"], 1f);
                SetStatic(go);
                pipes++;
            }
            Bump("pipes", pipes);

            Debug.Log($"[OD] Stage 3 AC={ac} dishes={dishes} ants={ants} awnings={awn} shutters={shut} pipes={pipes}");
        }

        static void SpawnYagi(string name, Vector3 pos)
        {
            var root = new GameObject(name);
            root.transform.SetParent(_root, true);
            root.transform.position = pos;
            root.transform.rotation = Quaternion.Euler(0, Rng.Next(360), 0);

            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Mast";
            pole.transform.SetParent(root.transform, false);
            pole.transform.localPosition = new Vector3(0, 0.9f, 0);
            pole.transform.localScale = new Vector3(0.04f, 0.9f, 0.04f);
            Object.DestroyImmediate(pole.GetComponent<Collider>());
            ApplyMat(pole.GetComponent<Renderer>(), Mats["metal"], 1f);

            for (int i = 0; i < 5; i++)
            {
                var el = GameObject.CreatePrimitive(PrimitiveType.Cube);
                el.name = "Element_" + i;
                el.transform.SetParent(root.transform, false);
                el.transform.localPosition = new Vector3(0, 1.2f + i * 0.18f, 0.15f - i * 0.04f);
                el.transform.localScale = new Vector3(0.55f - i * 0.06f, 0.025f, 0.025f);
                Object.DestroyImmediate(el.GetComponent<Collider>());
                ApplyMat(el.GetComponent<Renderer>(), Mats["metal"], 1f);
            }
            SetStatic(root);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 4 — overhead cables + poles
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage4_OverheadCables()
        {
            var crossarms = LoadMeshes("crossarm_");
            var cableMeshes = LoadMeshes("cable_");

            // Pole positions (scaled). Spec had 6; we need 10.
            var poleXZ = new List<Vector2>
            {
                new(-2f, -10f), new(-20f, 20f), new(18f, -24f), new(8f, 36f),
                new(-28f, -40f), new(30f, 12f), new(-12f, -8f), new(14f, 20f),
                new(-8f, 48f), new(22f, -8f)
            };

            var poles = new List<Transform>();
            for (int i = 0; i < TargetPoles && i < poleXZ.Count; i++)
            {
                var xz = poleXZ[i] * PosScale;
                // Prefer existing pole if near
                Transform existing = null;
                foreach (Transform t in _map)
                {
                    if (!t.name.StartsWith("Prop_UtilityPole")) continue;
                    var d = Vector2.Distance(new Vector2(t.position.x, t.position.z), xz);
                    if (d < 4f) { existing = t; break; }
                }

                Transform poleT;
                if (existing != null)
                {
                    poleT = existing;
                    // Retint wood
                    foreach (var r in existing.GetComponentsInChildren<Renderer>())
                        ApplyMat(r, Mats["wood"], 2f);
                }
                else
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    go.name = $"OD_Pole_{i}";
                    go.transform.SetParent(_root, true);
                    go.transform.position = new Vector3(xz.x, 4.5f, xz.y);
                    go.transform.localScale = new Vector3(0.35f, 4.5f, 0.35f);
                    // Ground cover poles get collider — visible mesh OK
                    ApplyMat(go.GetComponent<Renderer>(), Mats["wood"], 2f);
                    SetStatic(go);
                    poleT = go.transform;
                }

                // Crossarm
                if (crossarms.Count > 0)
                {
                    var armPos = poleT.position + Vector3.up * 3.8f;
                    SpawnPropMesh($"OD_Crossarm_{i}", crossarms[i % crossarms.Count], armPos,
                        Quaternion.Euler(0, Rng.Next(0, 180), 0), Vector3.one, Mats["wood"], false);
                }
                poles.Add(poleT);
            }
            Bump("poles", poles.Count);

            // Cable runs — at least 6 crossing Main Street (roughly |X|<8 span)
            int cables = 0;
            int mainCross = 0;

            // Pair poles across Main Street
            var westPoles = poles.Where(p => p.position.x < -2f).OrderBy(p => p.position.z).ToList();
            var eastPoles = poles.Where(p => p.position.x > 2f).OrderBy(p => p.position.z).ToList();
            int pairs = Mathf.Min(westPoles.Count, eastPoles.Count);
            for (int i = 0; i < pairs && cables < TargetCables; i++)
            {
                SpawnCableRun(westPoles[i].position + Vector3.up * 7.5f,
                    eastPoles[Mathf.Min(i, eastPoles.Count - 1)].position + Vector3.up * 7.5f,
                    cableMeshes, cables);
                cables++;
                mainCross++;
            }

            // Extra tangled main crossings between buildings
            var mainPairs = new (Vector3 a, Vector3 b)[]
            {
                (V(-8, 8, -20), V(10, 8, -18)),
                (V(-6, 7.5f, 0), V(12, 7.8f, 2)),
                (V(-10, 8.2f, 18), V(14, 7.6f, 20)),
                (V(-4, 7.2f, 40), V(8, 7.5f, 42)),
                (V(-12, 7.8f, -40), V(6, 7.4f, -36)),
            };
            foreach (var (a, b) in mainPairs)
            {
                if (cables >= TargetCables) break;
                SpawnCableRun(a, b, cableMeshes, cables);
                cables++;
                mainCross++;
            }

            // Market alley + west alley
            var sidePairs = new (Vector3 a, Vector3 b)[]
            {
                (V(22, 7, -24), V(36, 7.2f, -20)),
                (V(20, 7.5f, 0), V(38, 7, 4)),
                (V(18, 7, 20), V(36, 7.4f, 24)),
                (V(24, 7.2f, 40), V(40, 7, 38)),
                (V(-40, 7, -20), V(-28, 7.3f, -8)),
                (V(-38, 7.1f, 8), V(-24, 7.4f, 16)),
                (V(-36, 7, 32), V(-22, 7.2f, 40)),
                (V(-6, 8, -8), V(-2, 9, 10)), // tangle near main pole
            };
            foreach (var (a, b) in sidePairs)
            {
                if (cables >= TargetCables) break;
                SpawnCableRun(a, b, cableMeshes, cables);
                cables++;
            }

            Bump("cables", cables);
            Debug.Log($"[OD] Stage 4 poles={poles.Count} cables={cables} mainCrossings~={mainCross}");
        }

        static Vector3 V(float x, float y, float z) => new(x * PosScale, y, z * PosScale);

        static void SpawnCableRun(Vector3 a, Vector3 b, List<Mesh> cableMeshes, int idx)
        {
            var mid = (a + b) * 0.5f;
            mid.y -= Vector3.Distance(a, b) * 0.08f; // sag
            var dir = (b - a);
            float len = dir.magnitude;
            var rot = Quaternion.LookRotation(dir.normalized, Vector3.up);

            // Use cable mesh stretched, or a thin cylinder arc approximation
            if (cableMeshes.Count > 0)
            {
                var mesh = cableMeshes[idx % cableMeshes.Count];
                // Mesh authored ~5–11m along X; scale to fit span
                var go = new GameObject($"OD_Cable_{idx}");
                go.transform.SetParent(_root, true);
                go.transform.position = a;
                go.transform.rotation = rot * Quaternion.Euler(0, -90, 0);
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                ApplyMat(mr, Mats["cable"], 1f);
                float meshLen = Mathf.Max(mesh.bounds.size.x, 0.1f);
                go.transform.localScale = new Vector3(len / meshLen, 1f, 1f);
                // Sag via slight pitch
                go.transform.Rotate(0, 0, -6f - (float)Rng.NextDouble() * 6f, Space.Self);
                SetStatic(go);

                // Bundle companion cable
                if (idx % 2 == 0)
                {
                    var go2 = Object.Instantiate(go, _root);
                    go2.name = $"OD_Cable_{idx}_b";
                    go2.transform.position = a + Vector3.up * 0.15f + Vector3.Cross(dir.normalized, Vector3.up) * 0.12f;
                    SetStatic(go2);
                }
            }
            else
            {
                // Fallback: segmented cylinders
                int segs = 6;
                for (int s = 0; s < segs; s++)
                {
                    float t0 = s / (float)segs;
                    float t1 = (s + 1) / (float)segs;
                    var p0 = Vector3.Lerp(a, b, t0); p0.y -= Mathf.Sin(t0 * Mathf.PI) * len * 0.08f;
                    var p1 = Vector3.Lerp(a, b, t1); p1.y -= Mathf.Sin(t1 * Mathf.PI) * len * 0.08f;
                    var seg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    seg.name = $"OD_Cable_{idx}_{s}";
                    seg.transform.SetParent(_root, true);
                    seg.transform.position = (p0 + p1) * 0.5f;
                    seg.transform.up = (p1 - p0).normalized;
                    seg.transform.localScale = new Vector3(0.04f, (p1 - p0).magnitude * 0.5f, 0.04f);
                    Object.DestroyImmediate(seg.GetComponent<Collider>());
                    ApplyMat(seg.GetComponent<Renderer>(), Mats["cable"], 1f);
                    SetStatic(seg);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 5 — ground dressing
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage5_GroundDressing()
        {
            var jersey = LoadMeshes("jersey_barrier_");
            var rubble = LoadMeshes("rubble_");
            var sandbags = LoadMeshes("sandbag_");
            var stalls = LoadMeshes("stall_frame_");
            var cratePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Art/Models/Environment/PolyHaven/old_military_crate/old_military_crate_1k.fbx");
            var barrierPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Art/Models/Environment/PolyHaven/concrete_road_barrier/concrete_road_barrier_1k.fbx");
            var barrelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Art/Models/Environment/PolyHaven/Barrel_02/Barrel_02_1k.gltf");
            if (barrelPrefab == null)
                barrelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/_Project/Art/Models/Environment/Props/Barrel_01/Barrel_01_1k.fbx");
            var tyrePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Art/Models/Environment/PolyHaven/old_tyre/old_tyre_1k.gltf");
            var trashPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Art/Models/Environment/PolyHaven/metal_trash_can/metal_trash_can_1k.gltf");

            // --- Cars: upgrade existing box cars + add Kenney ---
            DressExistingCars();
            var carMeshes = new[] { "van.obj", "suv.obj", "sedan.obj", "taxi.obj", "delivery.obj" };
            var carSlots = new (Vector3 pos, float yaw)[]
            {
                (V(-1, 0, -4), 8f), (V(5, 0, 8), -15f), (V(-3, 0, -38), 70f),
                (V(6, 0, -42), -20f), (V(2, 0, 38), 110f), (V(22, 0, -48), -50f),
                (V(-18, 0, -50), 25f), (V(-8, 0, 12), 95f), (V(10, 0, -22), -40f),
                (V(-14, 0, 28), 160f), (V(4, 0, -8), 20f)
            };
            int cars = 0;
            // Count existing Prop_* cars that still have renderers enabled
            foreach (Transform t in _map)
            {
                if (t.name.StartsWith("Prop_") && (t.name.Contains("Car") || t.name.Contains("Van") || t.name.Contains("SUV") || t.name.Contains("Container")))
                {
                    bool any = t.GetComponentsInChildren<Renderer>().Any(r => r.enabled);
                    if (any) cars++;
                }
            }
            for (int i = 0; i < carSlots.Length && cars < TargetCars; i++)
            {
                // Skip if an existing car is already very close
                bool near = false;
                foreach (Transform t in _map)
                {
                    if (!t.name.StartsWith("Prop_")) continue;
                    if (Vector3.Distance(t.position, carSlots[i].pos) < 3f) { near = true; break; }
                }
                if (near) continue;

                var meshPath = $"{CarDir}/{carMeshes[i % carMeshes.Length]}";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(meshPath);
                if (prefab == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.name = $"OD_Car_{cars}";
                go.transform.SetParent(_root, true);
                go.transform.position = carSlots[i].pos;
                go.transform.rotation = Quaternion.Euler(0, carSlots[i].yaw, 0);
                go.transform.localScale = Vector3.one * 1.15f;
                DustCar(go);
                // Collider for cover
                if (go.GetComponentInChildren<Collider>() == null)
                {
                    var box = go.AddComponent<BoxCollider>();
                    var b = BoundsOf(go);
                    box.center = go.transform.InverseTransformPoint(b.center);
                    box.size = b.size;
                }
                SeatToGround(go.transform);
                SetStatic(go);
                cars++;
            }
            Bump("cars", cars);

            // Jersey barriers along Main + Plaza
            int jer = 0;
            var jerseySlots = BuildCadenceSlots(
                new Vector3(0, 0, -50f * PosScale), new Vector3(0, 0, 50f * PosScale), 9f, 0.8f);
            jerseySlots.AddRange(BuildCadenceSlots(
                new Vector3(-20f * PosScale, 0, 40f * PosScale), new Vector3(20f * PosScale, 0, 40f * PosScale), 8f, 1.2f));
            jerseySlots.AddRange(new List<Vector3>
            {
                V(-4, 0, -12), V(5, 0, -6), V(-3, 0, 6), V(4, 0, 14),
                V(-6, 0, 24), V(3, 0, -28), V(8, 0, 32), V(-2, 0, 44),
                V(-5, 0, -36), V(6, 0, -40), V(-7, 0, 36), V(5, 0, 48),
                V(-9, 0, -2), V(9, 0, 2), V(-3, 0, -48), V(4, 0, 16),
                V(-8, 0, 28), V(7, 0, -16), V(-1, 0, 22), V(2, 0, -24)
            });
            foreach (var p in jerseySlots)
            {
                if (jer >= TargetJersey) break;
                if (IsSpawnBlocked(p)) continue;
                Mesh mesh = jersey.Count > 0 ? jersey[jer % jersey.Count] : null;
                GameObject go;
                if (barrierPrefab != null && jer % 3 == 0)
                {
                    go = (GameObject)PrefabUtility.InstantiatePrefab(barrierPrefab);
                    go.name = $"OD_Jersey_{jer}";
                    go.transform.SetParent(_root, true);
                    go.transform.position = p;
                    go.transform.rotation = Quaternion.Euler(0, (jer % 2 == 0) ? 90f : 0f, 0);
                    go.transform.localScale = Vector3.one * 1.1f;
                }
                else
                {
                    go = SpawnPropMesh($"OD_Jersey_{jer}", mesh, p,
                        Quaternion.Euler(0, jer * 37f, 0), Vector3.one, Mats["concrete"], true);
                }
                if (go != null)
                {
                    EnsureCoverCollider(go, 1.0f);
                    SeatToGround(go.transform);
                    jer++;
                }
            }
            Bump("jersey", jer);

            // Rubble
            int rub = 0;
            var rubbleSlots = new List<Vector3>
            {
                V(-6,0,-16), V(8,0,-14), V(-10,0,8), V(12,0,10), V(0,0,2),
                V(-20,0,-40), V(-14,0,-24), V(18,0,-36), V(28,0,-20), V(30,0,8),
                V(26,0,28), V(-30,0,4), V(-32,0,20), V(-24,0,40), V(4,0,36),
                V(-8,0,48), V(10,0,-48), V(-16,0,-8), V(16,0,18), V(-4,0,-32),
                V(6,0,22), V(-22,0,-28), V(20,0,-8), V(-28,0,-16), V(14,0,44),
                V(-12,0,32), V(2,0,-20), V(-18,0,12), V(22,0,36), V(-10,0,-44)
            };
            foreach (var p in rubbleSlots)
            {
                if (rub >= TargetRubble) break;
                if (IsSpawnBlocked(p)) continue;
                var mesh = rubble.Count > 0 ? rubble[rub % rubble.Count] : null;
                var go = SpawnPropMesh($"OD_Rubble_{rub}", mesh, p,
                    Quaternion.Euler(0, rub * 41f, 0),
                    Vector3.one * (1.2f + (rub % 3) * 0.25f), Mats["rubble"], true);
                if (go != null)
                {
                    EnsureCoverCollider(go, 1.2f);
                    SeatToGround(go.transform);
                    rub++;
                }
            }
            Bump("rubble", rub);

            // Dumpsters
            int dump = 0;
            var dumpSlots = new[]
            {
                V(-20,0,-12), V(18,0,-10), V(-8,0,16), V(10,0,20), V(-30,0,28),
                V(34,0,-8), V(32,0,16), V(-36,0,-24), V(4,0,-44), V(-4,0,50),
                V(24,0,40), V(-16,0,-36), V(14,0,-28)
            };
            foreach (var p in dumpSlots)
            {
                if (dump >= TargetDumpsters) break;
                GameObject go;
                if (trashPrefab != null)
                {
                    go = (GameObject)PrefabUtility.InstantiatePrefab(trashPrefab);
                    go.name = $"OD_Dumpster_{dump}";
                    go.transform.SetParent(_root, true);
                    go.transform.position = p;
                    go.transform.localScale = Vector3.one * 1.8f;
                }
                else
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = $"OD_Dumpster_{dump}";
                    go.transform.SetParent(_root, true);
                    go.transform.position = p + Vector3.up * 0.7f;
                    go.transform.localScale = new Vector3(1.4f, 1.4f, 2.2f);
                    ApplyMat(go.GetComponent<Renderer>(), Mats["metal"], 1f);
                }
                EnsureCoverCollider(go, 1.3f);
                SeatToGround(go.transform);
                SetStatic(go);
                dump++;
            }
            Bump("dumpsters", dump);

            // Crates + barrels — west camper alley denser
            int crates = 0, barrels = 0;
            var coverSlots = BuildCadenceSlots(
                new Vector3(-34f * PosScale, 0, -40f * PosScale),
                new Vector3(-30f * PosScale, 0, 40f * PosScale), 7f, 1.5f);
            coverSlots.AddRange(BuildCadenceSlots(
                new Vector3(28f * PosScale, 0, -40f * PosScale),
                new Vector3(30f * PosScale, 0, 40f * PosScale), 8f, 1.2f));
            coverSlots.AddRange(new List<Vector3>
            {
                V(-8,0,-6), V(7,0,4), V(-5,0,18), V(6,0,-18), V(-12,0,6),
                V(16,0,12), V(-22,0,-4), V(20,0,-24), V(-26,0,14), V(12,0,28),
                V(-28,0,-8), V(-30,0,8), V(-32,0,18), V(-28,0,32), V(-24,0,-20),
                V(26,0,-28), V(32,0,-12), V(34,0,6), V(30,0,22), V(28,0,36),
                V(-10,0,-22), V(9,0,-26), V(-14,0,22), V(15,0,32), V(-18,0,0),
                V(18,0,-4), V(-6,0,40), V(8,0,44), V(-20,0,36), V(22,0,-40),
                V(-36,0,-4), V(36,0,12), V(-16,0,-32), V(12,0,-36), V(-4,0,10),
                V(-25,0,24), V(24,0,14), V(-15,0,-14), V(11,0,8), V(-9,0,-40),
                V(19,0,42), V(-33,0,-30), V(33,0,-32), V(-7,0,46), V(3,0,-14)
            });

            foreach (var p in coverSlots)
            {
                if (crates >= TargetCrates && barrels >= TargetBarrels) break;
                if (IsSpawnBlocked(p)) continue;
                bool crate = (crates + barrels) % 3 != 2;
                if (crate && crates < TargetCrates)
                {
                    GameObject go;
                    if (cratePrefab != null)
                    {
                        go = (GameObject)PrefabUtility.InstantiatePrefab(cratePrefab);
                        go.name = $"OD_Crate_{crates}";
                        go.transform.SetParent(_root, true);
                        go.transform.position = p;
                        go.transform.rotation = Quaternion.Euler(0, crates * 29f, 0);
                        go.transform.localScale = Vector3.one * (0.9f + (crates % 3) * 0.15f);
                    }
                    else
                    {
                        go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        go.name = $"OD_Crate_{crates}";
                        go.transform.SetParent(_root, true);
                        go.transform.position = p + Vector3.up * 0.45f;
                        go.transform.localScale = new Vector3(0.9f, 0.9f, 0.9f);
                        ApplyMat(go.GetComponent<Renderer>(), Mats["wood"], 1f);
                    }
                    // Stack sometimes
                    if (crates % 4 == 0)
                    {
                        var top = Object.Instantiate(go, _root);
                        top.name = $"OD_Crate_{crates}_stack";
                        top.transform.position = go.transform.position + Vector3.up * 0.85f;
                        top.transform.rotation = Quaternion.Euler(0, 15f, 0);
                        SetStatic(top);
                    }
                    EnsureCoverCollider(go, 0.9f);
                    SeatToGround(go.transform);
                    SetStatic(go);
                    crates++;
                }
                else if (barrels < TargetBarrels)
                {
                    GameObject go;
                    if (barrelPrefab != null)
                    {
                        go = (GameObject)PrefabUtility.InstantiatePrefab(barrelPrefab);
                        go.name = $"OD_Barrel_{barrels}";
                        go.transform.SetParent(_root, true);
                        go.transform.position = p;
                        go.transform.localScale = Vector3.one * 1.05f;
                    }
                    else
                    {
                        go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        go.name = $"OD_Barrel_{barrels}";
                        go.transform.SetParent(_root, true);
                        go.transform.position = p + Vector3.up * 0.55f;
                        go.transform.localScale = new Vector3(0.55f, 0.55f, 0.55f);
                        ApplyMat(go.GetComponent<Renderer>(), Mats["metal"], 1f);
                    }
                    EnsureCoverCollider(go, 1.0f);
                    SeatToGround(go.transform);
                    SetStatic(go);
                    barrels++;
                }
            }
            Bump("crates", crates);
            Bump("barrels", barrels);

            // Sandbag emplacements
            int sb = 0;
            var sbSlots = new[]
            {
                (V(-4,0,-30), 0f), (V(5,0,30), 180f), (V(-32,0,0), 90f),
                (V(30,0,-4), -90f), (V(-2,0,8), 15f), (V(8,0,-8), -20f)
            };
            foreach (var (p, yaw) in sbSlots)
            {
                if (sandbags.Count == 0) break;
                var mesh = sandbags[2 + (sb % 5)]; // cover-height variants
                var go = SpawnPropMesh($"OD_Sandbag_{sb}", mesh, p, Quaternion.Euler(0, yaw, 0),
                    Vector3.one, Mats["sandbag"], true);
                if (go != null)
                {
                    EnsureCoverCollider(go, 1.05f);
                    SeatToGround(go.transform);
                    sb++;
                }
            }
            Bump("sandbags", sb);

            // Market stall frames
            int st = 0;
            foreach (var p in new[] { V(-34,0,6), V(-34,0,12), V(-32,0,-2), V(28,0,-16), V(30,0,4), V(28,0,20) })
            {
                if (stalls.Count == 0) break;
                var go = SpawnPropMesh($"OD_Stall_{st}", stalls[st % stalls.Count], p,
                    Quaternion.Euler(0, st * 40f, 0), Vector3.one, Mats["wood"], true);
                if (go != null) { SeatToGround(go.transform); st++; }
            }
            Bump("stalls", st);

            // Tyres scatter
            if (tyrePrefab != null)
            {
                for (int i = 0; i < 10; i++)
                {
                    var p = new Vector3((float)(Rng.NextDouble() - 0.5) * 60f, 0,
                        (float)(Rng.NextDouble() - 0.5) * 80f);
                    if (IsSpawnBlocked(p)) continue;
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(tyrePrefab);
                    go.name = $"OD_Tyre_{i}";
                    go.transform.SetParent(_root, true);
                    go.transform.position = p;
                    go.transform.rotation = Quaternion.Euler(Rng.Next(0, 40), Rng.Next(360), Rng.Next(0, 80));
                    SeatToGround(go.transform);
                    // No collider for small debris / or low collider
                    foreach (var c in go.GetComponentsInChildren<Collider>())
                        Object.DestroyImmediate(c);
                    SetStatic(go);
                }
            }

            Debug.Log($"[OD] Stage 5 cars={cars} jersey={jer} rubble={rub} dump={dump} crates={crates} barrels={barrels}");
        }

        static void DressExistingCars()
        {
            foreach (Transform t in _map)
            {
                if (!(t.name.StartsWith("Prop_") && (t.name.Contains("Car") || t.name.Contains("Van")
                    || t.name.Contains("SUV") || t.name.Contains("Container"))))
                    continue;
                foreach (var r in t.GetComponentsInChildren<Renderer>())
                {
                    if (r.name.Contains("Wheel")) ApplyMat(r, Mats["rubber"], 1f);
                    else if (r.name.Contains("Cabin") || r.name.Contains("Glass")) ApplyMat(r, Mats["glass"], 1f);
                    else ApplyMat(r, Mats["carBody"], 1.5f);
                }
                // Contact shadow blob
                AddContactBlob(t.position, 2.2f);
            }
        }

        static void DustCar(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    var m = new Material(mats[i]);
                    if (m.HasProperty("_BaseColor"))
                    {
                        var c = m.GetColor("_BaseColor");
                        m.SetColor("_BaseColor", Color.Lerp(c, new Color(0.55f, 0.48f, 0.38f), 0.45f));
                    }
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 6 — grounding + decals
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage6_GroundingAndDecals()
        {
            // Snapshot children first — AddContactBlob parents under _root and must not be re-iterated.
            var groundProps = new List<Transform>();
            for (int i = 0; i < _root.childCount; i++)
            {
                var t = _root.GetChild(i);
                if (t.name.StartsWith("OD_Sign") || t.name.StartsWith("OD_AC") || t.name.StartsWith("OD_Dish")
                    || t.name.StartsWith("OD_Yagi") || t.name.StartsWith("OD_Awning") || t.name.StartsWith("OD_Shutter")
                    || t.name.StartsWith("OD_Pipe") || t.name.StartsWith("OD_Cable") || t.name.StartsWith("OD_Crossarm")
                    || t.name.StartsWith("OD_Plinth") || t.name.StartsWith("OD_Pole")
                    || t.name.StartsWith("OD_ContactShadow") || t.name.StartsWith("OD_Decal"))
                    continue;
                groundProps.Add(t);
            }

            int seated = 0;
            foreach (var t in groundProps)
            {
                SeatToGround(t);
                AddContactBlob(t.position, EstimateFootprint(t));
                seated++;
            }

            // Ground decals: dirt, oil, cracks
            int decals = 0;
            var decalSlots = new List<(Vector3 p, float s, string mat)>
            {
                (V(0,0.02f,-4), 4f, "oil"), (V(2,0.02f,8), 3.5f, "decalDirt"),
                (V(-3,0.02f,-20), 5f, "decalDirt"), (V(5,0.02f,20), 4f, "crack"),
                (V(-16,0.02f,-40), 6f, "decalDirt"), (V(20,0.02f,-36), 4f, "decalDirt"),
                (V(-30,0.02f,0), 3f, "oil"), (V(28,0.02f,10), 3.5f, "decalDirt"),
                (V(0,0.02f,40), 5f, "decalDirt"), (V(-8,0.02f,12), 2.5f, "oil"),
                (V(10,0.02f,-12), 3f, "crack"), (V(-4,0.02f,-8), 2.8f, "oil"),
                (V(14,0.02f,28), 4f, "decalDirt"), (V(-22,0.02f,24), 3.5f, "decalDirt"),
                (V(6,0.02f,-44), 4.5f, "decalDirt"), (V(-12,0.02f,36), 3f, "crack"),
                (V(32,0.02f,-16), 3f, "oil"), (V(-34,0.02f,-12), 3.5f, "decalDirt"),
                (V(0,0.02f,0), 3f, "crack"), (V(8,0.02f,4), 2.5f, "oil"),
            };
            foreach (var (p, s, mk) in decalSlots)
            {
                if (!Mats.ContainsKey(mk)) continue;
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = $"OD_Decal_{decals}";
                go.transform.SetParent(_root, true);
                go.transform.position = p;
                go.transform.rotation = Quaternion.Euler(90f, Rng.Next(0, 360), 0f);
                go.transform.localScale = new Vector3(s, s, 1f);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                var mr = go.GetComponent<Renderer>();
                ApplyMat(mr, Mats[mk], 1f);
                // Transparent-ish
                var m = mr.sharedMaterial;
                if (m != null && m.HasProperty("_Surface"))
                {
                    // leave as-is; materials built as transparent below
                }
                SetStatic(go);
                decals++;
            }
            Bump("decals", decals);
            Bump("seated", seated);
            Debug.Log($"[OD] Stage 6 seated={seated} decals={decals}");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Materials library
        // ═══════════════════════════════════════════════════════════════════════

        static void BuildPbrLibrary()
        {
            Mats["plasterTan"] = UpsertPbr("OD_PlasterTan",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/yellow_plaster/yellow_plaster_diff_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/yellow_plaster/yellow_plaster_nor_gl_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/yellow_plaster/yellow_plaster_rough_2k.jpg",
                new Color(0.92f, 0.82f, 0.64f), 0f, 0.22f, 3.5f);

            Mats["plasterOchre"] = UpsertPbr("OD_PlasterOchre",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/painted_plaster_wall/painted_plaster_wall_diff_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/painted_plaster_wall/painted_plaster_wall_nor_gl_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/painted_plaster_wall/painted_plaster_wall_rough_2k.jpg",
                new Color(0.88f, 0.72f, 0.48f), 0f, 0.20f, 3.2f);

            Mats["plasterCream"] = UpsertPbr("OD_PlasterCream",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/rough_plaster_broken/rough_plaster_broken_diff_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/rough_plaster_broken/rough_plaster_broken_nor_gl_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/rough_plaster_broken/rough_plaster_broken_rough_2k.jpg",
                new Color(0.90f, 0.86f, 0.78f), 0f, 0.18f, 3.0f);

            Mats["brickWarm"] = UpsertPbr("OD_BrickWarm",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Bricks060/Bricks060_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Bricks060/Bricks060_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Bricks060/Bricks060_2K-JPG_Roughness.jpg",
                new Color(0.78f, 0.58f, 0.42f), 0f, 0.16f, 2.6f);

            Mats["brickDeep"] = UpsertPbr("OD_BrickDeep",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Bricks097/Bricks097_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Bricks097/Bricks097_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Bricks097/Bricks097_2K-JPG_Roughness.jpg",
                new Color(0.62f, 0.42f, 0.32f), 0f, 0.15f, 2.4f);

            Mats["concrete"] = UpsertPbr("OD_Concrete",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/cracked_concrete_wall/cracked_concrete_wall_diff_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/cracked_concrete_wall/cracked_concrete_wall_nor_gl_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/cracked_concrete_wall/cracked_concrete_wall_rough_2k.jpg",
                new Color(0.72f, 0.70f, 0.66f), 0f, 0.20f, 3.5f);

            Mats["concreteDark"] = UpsertPbr("OD_ConcreteDark",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/concrete_wall_004/concrete_wall_004_diff_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/concrete_wall_004/concrete_wall_004_nor_gl_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/concrete_wall_004/concrete_wall_004_rough_2k.jpg",
                new Color(0.55f, 0.53f, 0.50f), 0f, 0.18f, 3.2f);

            Mats["asphalt"] = UpsertPbr("OD_Asphalt",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Asphalt031/Asphalt031_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Asphalt031/Asphalt031_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Asphalt031/Asphalt031_2K-JPG_Roughness.jpg",
                new Color(0.22f, 0.20f, 0.18f), 0f, 0.18f, 10f);

            Mats["dirt"] = UpsertPbr("OD_Dirt",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_Roughness.jpg",
                new Color(0.34f, 0.28f, 0.20f), 0f, 0.12f, 7f);

            Mats["wood"] = UpsertPbr("OD_Wood",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/weathered_brown_planks/weathered_brown_planks_diff_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/weathered_brown_planks/weathered_brown_planks_nor_gl_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/weathered_brown_planks/weathered_brown_planks_rough_2k.jpg",
                new Color(0.55f, 0.42f, 0.28f), 0f, 0.18f, 2.5f);

            Mats["metal"] = UpsertPbr("OD_Metal",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Metal046B/Metal046B_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Metal046B/Metal046B_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Metal046B/Metal046B_2K-JPG_Roughness.jpg",
                new Color(0.55f, 0.55f, 0.52f), 0.65f, 0.35f, 2f);

            Mats["shutter"] = UpsertPbr("OD_Shutter",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/rusty_metal_shutter/rusty_metal_shutter_diff_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/rusty_metal_shutter/rusty_metal_shutter_nor_gl_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/rusty_metal_shutter/rusty_metal_shutter_rough_2k.jpg",
                new Color(0.45f, 0.42f, 0.38f), 0.55f, 0.25f, 1.5f);

            Mats["awning"] = UpsertPbr("OD_Awning",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Fabric066/Fabric066_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Fabric066/Fabric066_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Fabric066/Fabric066_2K-JPG_Roughness.jpg",
                new Color(0.15f, 0.35f, 0.22f), 0f, 0.25f, 2f);

            Mats["rubble"] = UpsertPbr("OD_Rubble",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Rock064/Rock064_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Rock064/Rock064_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Rock064/Rock064_2K-JPG_Roughness.jpg",
                new Color(0.55f, 0.48f, 0.40f), 0f, 0.12f, 2f);

            Mats["sandbag"] = UpsertPbr("OD_Sandbag",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/hessian_230/hessian_230_diff_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/hessian_230/hessian_230_nor_gl_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/hessian_230/hessian_230_rough_2k.jpg",
                new Color(0.62f, 0.52f, 0.36f), 0f, 0.12f, 2f);

            Mats["glass"] = UpsertGlass("OD_GlassDark", new Color(0.05f, 0.07f, 0.08f, 0.65f), 0.05f, 0.7f);
            Mats["trim"] = SolidMat("OD_Trim", new Color(0.22f, 0.20f, 0.18f), 0.1f, 0.25f);
            Mats["pipe"] = SolidMat("OD_Pipe", new Color(0.35f, 0.32f, 0.28f), 0.4f, 0.3f);
            Mats["cable"] = SolidMat("OD_Cable", new Color(0.08f, 0.07f, 0.06f), 0.05f, 0.15f);
            Mats["carBody"] = SolidMat("OD_CarBody", new Color(0.55f, 0.52f, 0.45f), 0.2f, 0.25f);
            Mats["rubber"] = SolidMat("OD_Rubber", new Color(0.08f, 0.08f, 0.08f), 0f, 0.1f);
            Mats["oil"] = MakeDecalMat("OD_OilDecal", new Color(0.05f, 0.04f, 0.03f, 0.75f));
            Mats["dirt"] = MakeDecalMat("OD_DirtDecal", new Color(0.35f, 0.28f, 0.18f, 0.55f));
            // overwrite dirt ground key collision — keep ground as dirtGround
            Mats["dirtGround"] = Mats["dirt"];
            Mats["dirt"] = UpsertPbr("OD_DirtGround",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_Roughness.jpg",
                new Color(0.55f, 0.45f, 0.32f), 0f, 0.12f, 6f);
            // Fix: Beach uses dirt ground; decals need separate keys
            Mats["decalDirt"] = MakeDecalMat("OD_DecalDirt", new Color(0.35f, 0.28f, 0.18f, 0.55f));
            Mats["crack"] = MakeDecalMat("OD_CrackDecal", new Color(0.15f, 0.13f, 0.11f, 0.65f));
            // Remap stage6 keys
            Mats["oil"] = MakeDecalMat("OD_OilDecal", new Color(0.05f, 0.04f, 0.03f, 0.75f));
        }

        static Material UpsertPbr(string name, string albedo, string normal, string rough,
            Color tint, float metallic, float smoothness, float tiling)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = name;
                AssetDatabase.CreateAsset(mat, path);
            }

            EnsureTextureImport(albedo, false);
            if (!string.IsNullOrEmpty(normal)) EnsureTextureImport(normal, true);

            var alb = AssetDatabase.LoadAssetAtPath<Texture2D>(albedo);
            var nrm = string.IsNullOrEmpty(normal) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(normal);
            var rgh = string.IsNullOrEmpty(rough) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(rough);

            if (alb != null)
            {
                mat.SetTexture("_BaseMap", alb);
                mat.mainTexture = alb;
            }
            mat.SetColor("_BaseColor", tint);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            mat.mainTextureScale = new Vector2(tiling, tiling);
            mat.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));

            if (nrm != null)
            {
                mat.SetTexture("_BumpMap", nrm);
                mat.EnableKeyword("_NORMALMAP");
                mat.SetFloat("_BumpScale", 0.85f);
                mat.SetTextureScale("_BumpMap", new Vector2(tiling, tiling));
            }
            if (rgh != null && mat.HasProperty("_SpecGlossMap") == false)
            {
                // keep smoothness scalar; optional mask
            }
            if (mat.HasProperty("_OcclusionStrength"))
                mat.SetFloat("_OcclusionStrength", 0.6f);

            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material UpsertGlass(string name, Color color, float metallic, float smoothness)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = name;
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f); // Transparent
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material SolidMat(string name, Color color, float metallic, float smoothness)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = name;
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material MakeDecalMat(string name, Color color)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = name;
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.15f);
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = 3000;
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════════════════

        static List<Mesh> LoadMeshes(string prefix)
        {
            var list = new List<Mesh>();
            foreach (var guid in AssetDatabase.FindAssets(prefix + " t:Mesh", new[] { GenDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh != null) list.Add(mesh);
            }
            // OBJ may import as GameObject with MeshFilter
            if (list.Count == 0)
            {
                foreach (var guid in AssetDatabase.FindAssets(prefix, new[] { GenDir }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!path.EndsWith(".obj")) continue;
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (go == null) continue;
                    var mf = go.GetComponentInChildren<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) list.Add(mf.sharedMesh);
                    else
                    {
                        var all = AssetDatabase.LoadAllAssetsAtPath(path);
                        foreach (var a in all)
                            if (a is Mesh m) list.Add(m);
                    }
                }
            }
            return list;
        }

        static GameObject SpawnPropMesh(string name, Mesh mesh, Vector3 pos, Quaternion rot, Vector3 scale, Material mat, bool withCollider)
        {
            GameObject go;
            if (mesh != null)
            {
                go = new GameObject(name);
                go.transform.SetParent(_root, true);
                go.transform.SetPositionAndRotation(pos, rot);
                go.transform.localScale = scale;
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                ApplyMat(mr, mat, 1f);
                if (withCollider)
                {
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh;
                }
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                go.transform.SetParent(_root, true);
                go.transform.SetPositionAndRotation(pos + Vector3.up * 0.5f, rot);
                go.transform.localScale = scale;
                if (!withCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
                ApplyMat(go.GetComponent<Renderer>(), mat, 1f);
            }
            SetStatic(go);
            return go;
        }

        static void ApplyMat(Renderer r, Material mat, float tiling)
        {
            if (r == null || mat == null) return;
            // Prefer shared materials + MaterialPropertyBlock tiling to keep draw calls down.
            r.sharedMaterial = mat;
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            // URP Lit reads _BaseMap_ST; set via vector
            block.SetVector("_BaseMap_ST", new Vector4(tiling, tiling, 0f, 0f));
            r.SetPropertyBlock(block);
        }

        static void ApplyNamed(string name, Material mat, float tiling)
        {
            var go = GameObject.Find(name);
            if (go == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
                ApplyMat(r, mat, tiling);
        }

        static readonly HashSet<string> _importTouched = new();

        static void EnsureTextureImport(string path, bool asNormal)
        {
            // Never SaveAndReimport during the pass — that stalls the Editor for minutes.
            // Only queue settings; a one-shot AssetDatabase.Refresh at the end is enough
            // if anything actually changed (most Incoming textures are already configured).
            if (string.IsNullOrEmpty(path) || _importTouched.Contains(path)) return;
            _importTouched.Add(path);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            if (asNormal && importer.textureType != TextureImporterType.NormalMap)
                importer.textureType = TextureImporterType.NormalMap;
            if (importer.wrapMode != TextureWrapMode.Repeat)
                importer.wrapMode = TextureWrapMode.Repeat;
            // Do not call SaveAndReimport here.
        }

        static void SeatToGround(Transform t)
        {
            if (t == null) return;
            // Arena ground plane is Y=0. Prefer bounds seating over Physics (edit-mode raycasts are flaky).
            var b = BoundsOf(t.gameObject);
            float delta = 0.01f - b.min.y;
            if (Mathf.Abs(delta) > 0.001f)
                t.position += Vector3.up * delta;
        }

        static Bounds BoundsOf(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }

        static void EnsureCoverCollider(GameObject go, float targetHeight)
        {
            if (go == null) return;
            var existing = go.GetComponentsInChildren<Collider>();
            bool hasVisible = go.GetComponentInChildren<Renderer>() != null;
            if (!hasVisible) return;
            if (existing.Length == 0)
            {
                var box = go.AddComponent<BoxCollider>();
                var b = BoundsOf(go);
                box.center = go.transform.InverseTransformPoint(b.center);
                var size = go.transform.InverseTransformVector(b.size);
                size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                // Cap height for cover feel
                if (size.y > targetHeight * 1.3f)
                {
                    float shrink = targetHeight / size.y;
                    box.center = new Vector3(box.center.x, box.center.y * shrink, box.center.z);
                    size.y = targetHeight;
                }
                box.size = size;
            }
        }

        static Material _contactMat;

        static void AddContactBlob(Vector3 pos, float size)
        {
            if (_contactMat == null)
            {
                _contactMat = SolidMat("OD_ContactShadow", new Color(0.05f, 0.04f, 0.03f, 0.45f), 0f, 0.05f);
                if (_contactMat.HasProperty("_Surface"))
                {
                    _contactMat.SetFloat("_Surface", 1f);
                    _contactMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    _contactMat.renderQueue = 2900;
                    var c = _contactMat.GetColor("_BaseColor");
                    c.a = 0.4f;
                    _contactMat.SetColor("_BaseColor", c);
                    EditorUtility.SetDirty(_contactMat);
                }
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "OD_ContactShadow";
            go.transform.SetParent(_root, true);
            go.transform.position = new Vector3(pos.x, 0.015f, pos.z);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = new Vector3(size, size * 0.7f, 1f);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = _contactMat;
            SetStatic(go);
        }

        static float EstimateFootprint(Transform t)
        {
            var b = BoundsOf(t.gameObject);
            return Mathf.Clamp(Mathf.Max(b.size.x, b.size.z) * 1.1f, 0.8f, 4f);
        }

        static List<Vector3> BuildCadenceSlots(Vector3 a, Vector3 b, float spacing, float lateralJitter)
        {
            var list = new List<Vector3>();
            float dist = Vector3.Distance(a, b);
            int n = Mathf.Max(1, Mathf.FloorToInt(dist / spacing));
            var dir = (b - a).normalized;
            var side = Vector3.Cross(Vector3.up, dir).normalized;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                var p = Vector3.Lerp(a, b, t) + side * ((float)(Rng.NextDouble() - 0.5) * lateralJitter * 2f);
                p.y = 0f;
                list.Add(p);
            }
            return list;
        }

        static bool IsSpawnBlocked(Vector3 p)
        {
            // Keep clear of spawn halls
            if (Mathf.Abs(p.x) < 12f && p.z < -60f) return true;
            if (Mathf.Abs(p.x) < 12f && p.z > 60f) return true;
            // Keep Main Street walkable corridor (~3m clear center)
            if (Mathf.Abs(p.x) < 1.2f && Mathf.Abs(p.z) < 55f) return true;
            return false;
        }

        static void SetStatic(GameObject go)
        {
            if (go == null) return;
            go.isStatic = true;
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                t.gameObject.isStatic = true;
        }

        static void SetStaticRecursive(GameObject go) => SetStatic(go);

        static void Bump(string key, int amount = 1)
        {
            Counts[key] = Counts.GetValueOrDefault(key, 0) + amount;
        }

        // Fix stage6 to use decalDirt
        // (oil/crack keys already set; dirt decal uses "decalDirt" — patch Stage6 calls via alias)
        static void LogCounts()
        {
            // Alias dirt decal count if needed
            Debug.Log("[OD] === COUNTS vs TARGETS ===");
            void Line(string label, string key, int target)
            {
                int v = Counts.GetValueOrDefault(key, 0);
                // Also count children by prefix for accuracy
                int live = CountPrefix("OD_" + LabelToPrefix(label));
                if (live > v) v = live;
                Debug.Log($"[OD] {label}: {v} / {target}  {(v >= target * 0.9f ? "OK" : "SHORT")}");
            }
            // Recount from hierarchy for truth
            RecountFromHierarchy();
            Line("signs", "signs", TargetSigns);
            Line("ac", "ac", TargetAc);
            Line("dishes", "dishes", TargetDishes);
            Line("antennas", "antennas", TargetAntennas);
            Line("awnings", "awnings", TargetAwnings);
            Line("cables", "cables", TargetCables);
            Line("poles", "poles", TargetPoles);
            Line("cars", "cars", TargetCars);
            Line("jersey", "jersey", TargetJersey);
            Line("rubble", "rubble", TargetRubble);
            Line("dumpsters", "dumpsters", TargetDumpsters);
            Line("crates", "crates", TargetCrates);
            Line("barrels", "barrels", TargetBarrels);
            Line("shutters", "shutters", TargetShutters);
            Line("pipes", "pipes", TargetPipes);
        }

        static string LabelToPrefix(string label) => label switch
        {
            "signs" => "Sign_",
            "ac" => "AC_",
            "dishes" => "Dish_",
            "antennas" => "Yagi_",
            "awnings" => "Awning_",
            "cables" => "Cable_",
            "poles" => "Pole_",
            "cars" => "Car_",
            "jersey" => "Jersey_",
            "rubble" => "Rubble_",
            "dumpsters" => "Dumpster_",
            "crates" => "Crate_",
            "barrels" => "Barrel_",
            "shutters" => "Shutter_",
            "pipes" => "Pipe_",
            _ => label
        };

        static int CountPrefix(string prefix)
        {
            if (_root == null) return 0;
            int n = 0;
            foreach (Transform t in _root)
                if (t.name.StartsWith(prefix)) n++;
            return n;
        }

        static void RecountFromHierarchy()
        {
            if (_root == null) return;
            void Set(string key, string prefix)
            {
                int n = 0;
                foreach (Transform t in _root)
                    if (t.name.StartsWith(prefix)) n++;
                Counts[key] = n;
            }
            Set("signs", "OD_Sign_");
            Set("ac", "OD_AC_");
            Set("dishes", "OD_Dish_");
            Set("antennas", "OD_Yagi_");
            Set("awnings", "OD_Awning_");
            Set("cables", "OD_Cable_");
            Set("poles", "OD_Pole_");
            // poles may reuse existing — count Prop_UtilityPole + OD_Pole
            int poles = CountPrefix("OD_Pole_");
            foreach (Transform t in _map)
                if (t.name.StartsWith("Prop_UtilityPole")) poles++;
            Counts["poles"] = poles;
            Set("cars", "OD_Car_");
            int cars = Counts["cars"];
            foreach (Transform t in _map)
            {
                if (t.name.StartsWith("Prop_") && (t.name.Contains("Car") || t.name.Contains("Van")
                    || t.name.Contains("SUV") || t.name.Contains("Container")))
                {
                    if (t.GetComponentsInChildren<Renderer>().Any(r => r.enabled)) cars++;
                }
            }
            Counts["cars"] = cars;
            Set("jersey", "OD_Jersey_");
            Set("rubble", "OD_Rubble_");
            Set("dumpsters", "OD_Dumpster_");
            Set("crates", "OD_Crate_");
            Set("barrels", "OD_Barrel_");
            Set("shutters", "OD_Shutter_");
            Set("pipes", "OD_Pipe_");
        }

        static T GetOrAddVolume<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (!profile.TryGet(out T component))
            {
                component = profile.Add<T>(true);
            }
            component.active = true;
            return component;
        }

        static void SetV<T>(VolumeParameter<T> p, T value)
        {
            p.overrideState = true;
            p.value = value;
        }
    }
}
#endif
