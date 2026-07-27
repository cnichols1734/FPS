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
    /// Overflow polish: sky exposure, ground/sky balance, eye-level lane densify.
    /// Idempotent under OP_OverflowPolish. Does not touch player/prefabs.
    /// Menu: Arena FPS / AAA Overflow Polish Pass
    /// </summary>
    public static class AaaOverflowPolishPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string RootName = "OP_OverflowPolish";
        const string MatDir = "Assets/_Project/Art/Materials/OverflowDressing";
        const string OdMatDir = "Assets/_Project/Art/Materials/OverflowDressing";
        const string GenDir = "Assets/_Project/Art/Models/Environment/Generated";
        const string SignDir = "Assets/_Project/Art/Decals/Signage";
        const string CarDir = "Assets/_Project/Art/Models/Environment/City/Kenney_CarKit/Models/OBJ format";
        const string ShotDir = "Assets/_Project/Art/Screenshots/Polish";
        const string SkyboxPath = "Assets/_Project/Settings/Lighting/Arena_Overflow_Overcast_Skybox.mat";
        const string OvercastHdri = "Assets/_Project/Art/Textures/HDRI/overcast_industrial_courtyard_4k.hdr";
        const float PosScale = 1.22f;

        static readonly System.Random Rng = new(20260728);
        static readonly Dictionary<string, Material> Mats = new();
        static Transform _root;
        static Transform _map;
        static int _addedProps;
        static int _addedDecals;
        static int _addedKerbs;
        static int _addedSigns;
        static int _addedAc;
        static int _addedCables;
        static int _addedCars;
        static int _addedRubble;

        [MenuItem("Arena FPS/AAA Overflow Polish Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[OP] Exit play mode and re-run.");
                return;
            }

            OpenArena();
            _map = GameObject.Find("ThreeLaneMap")?.transform;
            if (_map == null)
            {
                Debug.LogError("[OP] ThreeLaneMap missing.");
                return;
            }

            Mats.Clear();
            _addedProps = _addedDecals = _addedKerbs = _addedSigns = _addedAc = _addedCables = _addedCars = _addedRubble = 0;
            EnsureDir(MatDir);
            EnsureDir(ShotDir);

            ClearPrevious();
            _root = new GameObject(RootName).transform;
            _root.SetParent(_map, false);

            try
            {
                Stage0_SkyAndGrade();
                Stage1_GroundMaterials();
                Stage2_LaneDensify();
                Stage3_WallTransitions();
                Stage4_FacadeFillAlongLanes();
                Stage5_FramePackStandardViews();

                SetStatic(_root.gameObject);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();

                CaptureAndMeasure();
                AuditInvisibleColliders();

                Debug.Log($"[OP] DONE props={_addedProps} signs={_addedSigns} ac={_addedAc} cables={_addedCables} cars={_addedCars} rubble={_addedRubble} kerbs={_addedKerbs} decals={_addedDecals}");
            }
            catch (Exception ex)
            {
                Debug.LogError("[OP] FATAL: " + ex);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                throw;
            }
        }

        [MenuItem("Arena FPS/AAA Overflow Polish Pass/Sky Only")]
        public static void RunSkyOnly()
        {
            OpenArena();
            Stage0_SkyAndGrade();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            CaptureAndMeasure();
            Debug.Log("[OP] Sky-only polish applied.");
        }

        [MenuItem("Arena FPS/AAA Overflow Polish Pass/Capture Only")]
        public static void RunCaptureOnly()
        {
            OpenArena();
            EnsureDir(ShotDir);
            CaptureAndMeasure();
            AuditInvisibleColliders();
        }

        static void OpenArena()
        {
            var active = SceneManager.GetActiveScene();
            if (active.path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static void EnsureDir(string d)
        {
            var full = Path.GetFullPath(d);
            if (!Directory.Exists(full)) Directory.CreateDirectory(full);
            AssetDatabase.Refresh();
        }

        static void ClearPrevious()
        {
            var doomed = new List<GameObject>();
            foreach (Transform t in _map)
            {
                if (t.name == RootName || t.name.StartsWith("OP_"))
                    doomed.Add(t.gameObject);
            }
            var orphan = GameObject.Find(RootName);
            if (orphan != null && !doomed.Contains(orphan)) doomed.Add(orphan);
            foreach (var go in doomed) Object.DestroyImmediate(go);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 0 — sky not clipped, warm-grey overcast, even light
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage0_SkyAndGrade()
        {
            var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyboxPath);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Panoramic")) { name = "Arena_Overflow_Overcast_Skybox" };
                AssetDatabase.CreateAsset(sky, SkyboxPath);
            }

            var hdri = AssetDatabase.LoadAssetAtPath<Texture>(OvercastHdri);
            if (hdri != null && sky.HasProperty("_MainTex"))
                sky.SetTexture("_MainTex", hdri);

            // BEFORE was 1.75 → pure white. Sky-only hunt: exp~0.55 + contrast~18
            // keeps cloud bands below the tonemap knee (std≈0.21) while max≈0.84.
            if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", 0.58f);
            if (sky.HasProperty("_Rotation")) sky.SetFloat("_Rotation", 90f); // higher structure than 210
            if (sky.HasProperty("_Tint"))
                sky.SetColor("_Tint", new Color(0.96f, 0.93f, 0.88f));
            if (sky.HasProperty("_ImageType")) sky.SetFloat("_ImageType", 0f);
            if (sky.HasProperty("_Mapping")) sky.SetFloat("_Mapping", 1f);
            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;

            // Thin fog — haze without ghosting distant masses into the sky plate.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.76f, 0.71f, 0.60f);
            RenderSettings.fogDensity = 0.0042f;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.78f, 0.76f, 0.70f);
            RenderSettings.ambientEquatorColor = new Color(0.74f, 0.68f, 0.56f);
            RenderSettings.ambientGroundColor = new Color(0.56f, 0.50f, 0.40f);
            RenderSettings.ambientIntensity = 1.40f;
            RenderSettings.reflectionIntensity = 0.28f;

            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (l.type != LightType.Directional) continue;
                // Ground fill: brighter sun so street stays readable under darker sky
                l.intensity = 1.35f;
                l.color = new Color(1f, 0.96f, 0.88f);
                l.shadowStrength = 0.30f;
                l.shadows = LightShadows.Soft;
                l.transform.rotation = Quaternion.Euler(56f, -38f, 0f);
                EditorUtility.SetDirty(l);
            }

            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
            {
                if (cam.farClipPlane > 180f) cam.farClipPlane = 160f;
                EditorUtility.SetDirty(cam);
            }

            var volume = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include)
                .FirstOrDefault(v => v.isGlobal);
            if (volume == null || volume.sharedProfile == null)
            {
                Debug.LogWarning("[OP] Global Volume missing; grade skipped.");
                return;
            }

            var profile = volume.sharedProfile;

            var color = GetOrAdd<ColorAdjustments>(profile);
            Set(color.postExposure, 0.02f);

            GetOrAdd<WhiteBalance>(profile);
            GetOrAdd<LiftGammaGain>(profile);
            GetOrAdd<ShadowsMidtonesHighlights>(profile);
            AaaUrpGradeUtil.ApplyCanonicalDustyGrade(profile, "AaaOverflowPolishPass");

            var bloom = GetOrAdd<Bloom>(profile);
            Set(bloom.threshold, 1.45f);
            Set(bloom.intensity, 0.02f);
            Set(bloom.tint, new Color(1f, 0.94f, 0.86f));

            var vignette = GetOrAdd<Vignette>(profile);
            Set(vignette.intensity, 0.08f);
            Set(vignette.smoothness, 0.50f);
            Set(vignette.color, new Color(0.10f, 0.07f, 0.04f));

            EditorUtility.SetDirty(profile);
            EditorUtility.SetDirty(volume);
            DynamicGI.UpdateEnvironment();

            Debug.Log("[OP] Stage0 sky exp 1.75→0.58 rot 90, contrast 18, fog 0.0042, sun I 1.35, ambI 1.40 (cloud structure below tonemap knee)");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 1 — ground materials: asphalt / dirt / gravel + decals
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage1_GroundMaterials()
        {
            // Dark asphalt — 0.58 read as pale sand cards on Road_/Conn_ slabs (CRITIQUE_01 #1).
            var asphalt = UpsertPbr("OD_Asphalt",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Asphalt031/Asphalt031_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Asphalt031/Asphalt031_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Asphalt031/Asphalt031_2K-JPG_Roughness.jpg",
                new Color(0.22f, 0.20f, 0.18f), 0f, 0.18f, 10f);
            Mats["asphalt"] = asphalt;

            var dirt = UpsertPbr("OD_DirtGround",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground054/Ground054_2K-JPG_Roughness.jpg",
                new Color(0.34f, 0.28f, 0.20f), 0f, 0.12f, 7f);
            Mats["dirt"] = dirt;

            var gravel = UpsertPbr("OP_Gravel",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Gravel023/Gravel023_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Gravel023/Gravel023_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Gravel023/Gravel023_2K-JPG_Roughness.jpg",
                new Color(0.30f, 0.27f, 0.22f), 0f, 0.14f, 6f);
            Mats["gravel"] = gravel;

            var packed = UpsertPbr("OP_PackedDust",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground079S/Ground079S_2K-JPG_Color.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground079S/Ground079S_2K-JPG_NormalGL.jpg",
                "Assets/_Project/Art/Textures/Incoming/AmbientCG/Ground079S/Ground079S_2K-JPG_Roughness.jpg",
                new Color(0.34f, 0.28f, 0.20f), 0f, 0.12f, 8f);
            Mats["packed"] = packed;

            // Facade concrete stays mid; kerbs use a darker instance via GroundQuad pass.
            Mats["concrete"] = UpsertPbr("OD_Concrete",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/cracked_concrete_wall/cracked_concrete_wall_diff_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/cracked_concrete_wall/cracked_concrete_wall_nor_gl_2k.jpg",
                "Assets/_Project/Art/Textures/Incoming/PolyHaven/cracked_concrete_wall/cracked_concrete_wall_rough_2k.jpg",
                new Color(0.55f, 0.52f, 0.46f), 0f, 0.20f, 3.5f);

            Mats["rubble"] = LoadOrSolid("OD_Rubble", new Color(0.55f, 0.48f, 0.40f));
            Mats["metal"] = LoadOrSolid("OD_Metal", new Color(0.55f, 0.55f, 0.52f));
            Mats["wood"] = LoadOrSolid("OD_Wood", new Color(0.55f, 0.42f, 0.28f));
            Mats["cable"] = LoadOrSolid("OD_Cable", new Color(0.08f, 0.07f, 0.06f));
            Mats["pipe"] = LoadOrSolid("OD_Pipe", new Color(0.35f, 0.32f, 0.28f));
            Mats["shutter"] = LoadOrSolid("OD_Shutter", new Color(0.45f, 0.42f, 0.38f));

            // Roads / connectors — dark asphalt (never paint-stripe children)
            ApplyNamed("Ground", asphalt, 12f);
            ApplyNamed("Beach_Dirt", dirt, 9f);
            foreach (Transform t in _map)
            {
                if (t.name.EndsWith("_Stripe")) continue; // paint lines — not ground mats
                if (t.name.StartsWith("Road_") || t.name.StartsWith("Conn_"))
                {
                    bool dirtish = t.name.Contains("Beach") || t.name.Contains("Vault") || (t.name.GetHashCode() & 3) == 0;
                    ApplyMat(t.GetComponent<Renderer>(), dirtish ? dirt : asphalt, dirtish ? 8f : 10f);
                }
                if (t.name.StartsWith("Sidewalk_"))
                    ApplyMat(t.GetComponent<Renderer>(), Mats["concrete"], 4f);
            }

            // Blend patches as thin quads over ground (asphalt / dirt / gravel / packed dust)
            var patches = new (Vector3 p, float s, string key)[]
            {
                (new Vector3(0, 0.03f, -8), 14f, "packed"),
                (new Vector3(2, 0.03f, 12), 12f, "dirt"),
                (new Vector3(-4, 0.03f, 28), 10f, "gravel"),
                (new Vector3(4, 0.03f, -28), 11f, "dirt"),
                (new Vector3(28, 0.03f, -6), 9f, "packed"),
                (new Vector3(32, 0.03f, 10), 8f, "dirt"),
                (new Vector3(30, 0.03f, 30), 9f, "gravel"),
                (new Vector3(26, 0.03f, -30), 8f, "dirt"),
                (new Vector3(-32, 0.03f, 0), 8f, "packed"),
                (new Vector3(-34, 0.03f, 18), 7f, "dirt"),
                (new Vector3(-30, 0.03f, -20), 8f, "gravel"),
                (new Vector3(0, 0.03f, 44), 12f, "dirt"),
                (new Vector3(14, 0.03f, -40), 9f, "packed"),
                (new Vector3(-16, 0.03f, -36), 8f, "gravel"),
                (new Vector3(18, 0.03f, 20), 7f, "dirt"),
                (new Vector3(-8, 0.03f, 6), 6f, "gravel"),
            };
            foreach (var (p, s, key) in patches)
            {
                if (!Mats.ContainsKey(key)) continue;
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = $"OP_GroundPatch_{_addedDecals}";
                go.transform.SetParent(_root, true);
                go.transform.position = p;
                go.transform.rotation = Quaternion.Euler(90f, Rng.Next(0, 360), 0f);
                go.transform.localScale = new Vector3(s, s * (0.7f + (float)Rng.NextDouble() * 0.5f), 1f);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                ApplyMat(go.GetComponent<Renderer>(), Mats[key], 1f);
                SetStatic(go);
                _addedDecals++;
            }

            // Crack / oil / dirt / puddle decals denser along lanes
            Mats["oil"] = MakeDecal("OP_Oil", new Color(0.06f, 0.05f, 0.04f, 0.70f));
            Mats["decalDirt"] = MakeDecal("OP_DecalDirt", new Color(0.42f, 0.34f, 0.22f, 0.55f));
            Mats["crack"] = MakeDecal("OP_Crack", new Color(0.18f, 0.15f, 0.12f, 0.60f));
            Mats["puddle"] = MakeDecal("OP_Puddle", new Color(0.22f, 0.24f, 0.20f, 0.45f));

            var decals = new List<(Vector3 p, float s, string mk)>();
            void AddLaneDecals(float x, float z0, float z1, float step)
            {
                for (float z = z0; z <= z1; z += step)
                {
                    float jx = (float)(Rng.NextDouble() - 0.5) * 4f;
                    float jz = (float)(Rng.NextDouble() - 0.5) * 2f;
                    string[] keys = { "decalDirt", "oil", "crack", "puddle", "decalDirt" };
                    decals.Add((new Vector3(x + jx, 0.025f, z + jz), 2.2f + (float)Rng.NextDouble() * 3f, keys[decals.Count % keys.Length]));
                }
            }
            AddLaneDecals(0f, -48f, 48f, 7f);
            AddLaneDecals(30f, -48f, 48f, 8f);
            AddLaneDecals(-34f, -40f, 40f, 8f);
            // Extra around standard viewpoints
            foreach (var p in new[]
            {
                new Vector3(24f, 0.025f, -6f), new Vector3(28f, 0.025f, 0f), new Vector3(-28f, 0.025f, -4f),
                new Vector3(-1.5f, 0.025f, -10f), new Vector3(2f, 0.025f, 14f), new Vector3(0f, 0.025f, 2f)
            })
            {
                decals.Add((p, 3.5f, "decalDirt"));
                decals.Add((p + new Vector3(2f, 0, 1.5f), 2.5f, "oil"));
                decals.Add((p + new Vector3(-1.5f, 0, -2f), 3f, "crack"));
            }

            foreach (var (p, s, mk) in decals)
            {
                if (!Mats.ContainsKey(mk)) continue;
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = $"OP_Decal_{_addedDecals}";
                go.transform.SetParent(_root, true);
                go.transform.position = p;
                go.transform.rotation = Quaternion.Euler(90f, Rng.Next(0, 360), 0f);
                go.transform.localScale = new Vector3(s, s, 1f);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                ApplyMat(go.GetComponent<Renderer>(), Mats[mk], 1f);
                SetStatic(go);
                _addedDecals++;
            }

            Debug.Log($"[OP] Stage1 ground mats brightened; patches+decals={_addedDecals}");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 2 — densify along walkable lanes (player-visible frames)
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage2_LaneDensify()
        {
            var rubbleMeshes = LoadMeshes("rubble_");
            var jerseyMeshes = LoadMeshes("jersey_barrier_");
            var cableMeshes = LoadMeshes("cable_");
            var acMeshes = LoadMeshes("window_ac_");
            var signs = LoadSignPaths();
            var carNames = new[] { "van.obj", "suv.obj", "sedan.obj", "taxi.obj", "delivery.obj" };

            // Stations every ~15 m along three lanes + connectors
            var stations = new List<(Vector3 eye, Vector3 look, string lane)>();
            void WalkLane(string lane, float x, float z0, float z1, float lookX)
            {
                for (float z = z0; z <= z1; z += 15f)
                {
                    var eye = new Vector3(x, 1.7f, z);
                    var look = new Vector3(lookX, 2.8f, z + 12f);
                    stations.Add((eye, look, lane));
                }
            }
            WalkLane("Main", 0f, -50f, 50f, 8f);
            WalkLane("MainW", -2f, -50f, 50f, -10f);
            WalkLane("Market", 28f, -50f, 50f, 40f);
            WalkLane("West", -32f, -42f, 42f, -40f);
            // Connectors
            stations.Add((new Vector3(12f, 1.7f, -8f), new Vector3(20f, 2.5f, -4f), "Conn"));
            stations.Add((new Vector3(-12f, 1.7f, 2f), new Vector3(-22f, 2.5f, 4f), "Conn"));
            stations.Add((new Vector3(10f, 1.7f, 20f), new Vector3(20f, 2.5f, 24f), "Conn"));

            int stationIdx = 0;
            foreach (var (eye, look, lane) in stations)
            {
                stationIdx++;
                var fwd = (look - eye); fwd.y = 0; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();
                var right = Vector3.Cross(Vector3.up, fwd).normalized;

                // Side offsets — keep lane centers walkable
                float sideClear = lane.StartsWith("Main") ? 3.2f : 2.4f;

                // Rubble pile (visible, cover height ~1.0)
                {
                    var p = eye + fwd * 6f + right * (sideClear + (float)Rng.NextDouble() * 1.2f);
                    p.y = 0f;
                    if (!IsBlocked(p))
                    {
                        var mesh = rubbleMeshes.Count > 0 ? rubbleMeshes[_addedRubble % rubbleMeshes.Count] : null;
                        var go = SpawnMesh($"OP_Rubble_{_addedRubble}", mesh, p,
                            Quaternion.Euler(0, Rng.Next(360), 0),
                            Vector3.one * (1.3f + (float)Rng.NextDouble() * 0.4f), Mats["rubble"], true, 1.1f);
                        if (go != null) { _addedRubble++; _addedProps++; }
                    }
                }

                // Second rubble opposite side
                if (stationIdx % 2 == 0)
                {
                    var p = eye + fwd * 4f - right * (sideClear + 0.5f);
                    p.y = 0f;
                    if (!IsBlocked(p))
                    {
                        var mesh = rubbleMeshes.Count > 0 ? rubbleMeshes[_addedRubble % rubbleMeshes.Count] : null;
                        var go = SpawnMesh($"OP_Rubble_{_addedRubble}", mesh, p,
                            Quaternion.Euler(0, Rng.Next(360), 0), Vector3.one * 1.2f, Mats["rubble"], true, 1.0f);
                        if (go != null) { _addedRubble++; _addedProps++; }
                    }
                }

                // Vehicle every station on Market/West, every other on Main (parked to side)
                if (lane != "Main" || stationIdx % 2 == 0)
                {
                    var p = eye + fwd * 8f + right * (sideClear + 1.8f) * ((stationIdx % 2 == 0) ? 1f : -1f);
                    p.y = 0f;
                    if (!IsBlocked(p) && !NearExistingCar(p, 5f))
                    {
                        var meshPath = $"{CarDir}/{carNames[_addedCars % carNames.Length]}";
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(meshPath);
                        if (prefab != null)
                        {
                            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                            go.name = $"OP_Car_{_addedCars}";
                            go.transform.SetParent(_root, true);
                            go.transform.position = p;
                            go.transform.rotation = Quaternion.Euler(0, Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg + 90f + Rng.Next(-15, 15), 0);
                            go.transform.localScale = Vector3.one * 1.12f;
                            DustCar(go);
                            if (go.GetComponentInChildren<Collider>() == null)
                            {
                                var box = go.AddComponent<BoxCollider>();
                                var b = BoundsOf(go);
                                box.center = go.transform.InverseTransformPoint(b.center);
                                box.size = b.size;
                            }
                            Seat(go.transform);
                            SetStatic(go);
                            _addedCars++;
                            _addedProps++;
                        }
                    }
                }

                // Cable tangle overhead in front of camera
                {
                    var a = eye + Vector3.up * 7.2f - right * 6f + fwd * 2f;
                    var b = eye + Vector3.up * 7.6f + right * 7f + fwd * 10f;
                    SpawnCable($"OP_Cable_{_addedCables}", a, b, cableMeshes);
                    _addedCables++;
                    _addedProps++;
                    if (stationIdx % 2 == 0)
                    {
                        var a2 = a + Vector3.up * 0.25f + fwd * 1.5f;
                        var b2 = b + Vector3.up * 0.15f - fwd * 1f;
                        SpawnCable($"OP_Cable_{_addedCables}", a2, b2, cableMeshes);
                        _addedCables++;
                        _addedProps++;
                    }
                }

                // Crates / barrels / jersey as mid-ground clutter
                {
                    var p = eye + fwd * 5f + right * (sideClear * 0.85f) * ((stationIdx % 2 == 0) ? -1f : 1f);
                    p.y = 0f;
                    if (!IsBlocked(p))
                    {
                        if (stationIdx % 3 == 0 && jerseyMeshes.Count > 0)
                        {
                            var go = SpawnMesh($"OP_Jersey_{_addedProps}", jerseyMeshes[stationIdx % jerseyMeshes.Count],
                                p, Quaternion.Euler(0, Rng.Next(360), 0), Vector3.one, Mats["concrete"], true, 1.0f);
                            if (go != null) _addedProps++;
                        }
                        else
                        {
                            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            go.name = $"OP_Crate_{_addedProps}";
                            go.transform.SetParent(_root, true);
                            go.transform.position = p + Vector3.up * 0.4f;
                            go.transform.rotation = Quaternion.Euler(0, Rng.Next(360), 0);
                            go.transform.localScale = new Vector3(0.85f, 0.8f, 0.85f);
                            ApplyMat(go.GetComponent<Renderer>(), Mats["wood"], 1f);
                            Seat(go.transform);
                            SetStatic(go);
                            _addedProps++;
                            if (stationIdx % 2 == 0)
                            {
                                var top = Object.Instantiate(go, _root);
                                top.name = $"OP_Crate_{_addedProps}_stack";
                                top.transform.position = go.transform.position + Vector3.up * 0.8f;
                                SetStatic(top);
                                _addedProps++;
                            }
                        }
                    }
                }

                // Signs facing toward eye (placed on nearest building facade approx)
                PlaceSignsNear(eye, fwd, right, signs, 3);
                // AC units on nearby walls
                PlaceAcNear(eye, fwd, right, acMeshes, 2);
            }

            Debug.Log($"[OP] Stage2 densified {stations.Count} stations; props={_addedProps} cars={_addedCars} cables={_addedCables}");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 3 — ground-to-wall transitions (kerbs, dirt piles, weeds)
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage3_WallTransitions()
        {
            foreach (Transform bldg in _map)
            {
                if (!bldg.name.StartsWith("Bldg_") || bldg.name.Contains("Fountain")) continue;
                var mass = bldg.Find(bldg.name + "_Mass");
                var r = mass != null ? mass.GetComponent<Renderer>() : bldg.GetComponentInChildren<Renderer>();
                if (r == null) continue;
                var b = r.bounds;

                // Four face midpoints at ground — place kerb + dirt mound
                var faces = new (Vector3 center, Vector3 normal, float width)[]
                {
                    (new Vector3(b.center.x, 0, b.max.z), Vector3.forward, b.size.x),
                    (new Vector3(b.center.x, 0, b.min.z), Vector3.back, b.size.x),
                    (new Vector3(b.max.x, 0, b.center.z), Vector3.right, b.size.z),
                    (new Vector3(b.min.x, 0, b.center.z), Vector3.left, b.size.z),
                };

                foreach (var (center, normal, width) in faces)
                {
                    // Skip faces far from playable lanes
                    float distLane = Mathf.Min(Mathf.Abs(center.x), Mathf.Abs(center.x - 30f), Mathf.Abs(center.x + 34f));
                    if (distLane > 18f && Mathf.Abs(center.z) > 55f) continue;

                    // Concrete kerb strip
                    float kerbLen = Mathf.Clamp(width * 0.85f, 2.5f, 14f);
                    var kerb = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    kerb.name = $"OP_Kerb_{_addedKerbs}";
                    kerb.transform.SetParent(_root, true);
                    kerb.transform.position = center + normal * 0.55f + Vector3.up * 0.12f;
                    kerb.transform.rotation = Quaternion.LookRotation(normal);
                    kerb.transform.localScale = new Vector3(kerbLen, 0.24f, 0.45f);
                    Object.DestroyImmediate(kerb.GetComponent<Collider>()); // decorative curb, low — keep walkable; no blocker
                    ApplyMat(kerb.GetComponent<Renderer>(), Mats["concrete"], 2f);
                    SetStatic(kerb);
                    _addedKerbs++;
                    _addedProps++;

                    // Dirt / rubble accumulation against wall base
                    int piles = width > 8f ? 3 : 2;
                    for (int i = 0; i < piles; i++)
                    {
                        float along = (i + 0.5f) / piles - 0.5f;
                        var tangent = Vector3.Cross(Vector3.up, normal).normalized;
                        var p = center + tangent * (along * kerbLen * 0.8f) + normal * 0.9f;
                        p.y = 0f;
                        if (IsBlocked(p)) continue;

                        var mound = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        mound.name = $"OP_WallDirt_{_addedProps}";
                        mound.transform.SetParent(_root, true);
                        mound.transform.position = p + Vector3.up * 0.18f;
                        mound.transform.rotation = Quaternion.Euler(0, Rng.Next(360), (float)(Rng.NextDouble() - 0.5) * 8f);
                        mound.transform.localScale = new Vector3(
                            0.9f + (float)Rng.NextDouble() * 1.2f,
                            0.25f + (float)Rng.NextDouble() * 0.35f,
                            0.7f + (float)Rng.NextDouble() * 0.8f);
                        Object.DestroyImmediate(mound.GetComponent<Collider>());
                        ApplyMat(mound.GetComponent<Renderer>(), Mats.ContainsKey("dirt") ? Mats["dirt"] : Mats["rubble"], 2f);
                        SetStatic(mound);
                        _addedProps++;

                        // Occasional rubble chunk with collider (cover)
                        if (i == 0 && (_addedProps % 3 == 0))
                        {
                            var chunk = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            chunk.name = $"OP_BaseRubble_{_addedRubble}";
                            chunk.transform.SetParent(_root, true);
                            chunk.transform.position = p + normal * 0.4f + Vector3.up * 0.35f;
                            chunk.transform.rotation = Quaternion.Euler(0, Rng.Next(360), 10f);
                            chunk.transform.localScale = new Vector3(1.1f, 0.7f, 0.9f);
                            ApplyMat(chunk.GetComponent<Renderer>(), Mats["rubble"], 1.5f);
                            Seat(chunk.transform);
                            SetStatic(chunk);
                            _addedRubble++;
                            _addedProps++;
                        }
                    }

                    // Stain strip on lower wall (decal-like thin box)
                    var stain = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    stain.name = $"OP_WallStain_{_addedDecals}";
                    stain.transform.SetParent(_root, true);
                    stain.transform.position = center + normal * 0.02f + Vector3.up * 0.9f;
                    stain.transform.rotation = Quaternion.LookRotation(-normal);
                    stain.transform.localScale = new Vector3(kerbLen * 0.9f, 1.6f, 1f);
                    Object.DestroyImmediate(stain.GetComponent<Collider>());
                    ApplyMat(stain.GetComponent<Renderer>(), MakeDecal("OP_WallStain", new Color(0.35f, 0.28f, 0.18f, 0.40f)), 1f);
                    SetStatic(stain);
                    _addedDecals++;
                }
            }

            Debug.Log($"[OP] Stage3 kerbs={_addedKerbs}");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 4 — more layered signage / pipes / shutters on lane-facing walls
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage4_FacadeFillAlongLanes()
        {
            var signs = LoadSignPaths();
            var acMeshes = LoadMeshes("window_ac_");
            int pipes = 0, shutters = 0;

            foreach (Transform bldg in _map)
            {
                if (!bldg.name.StartsWith("Bldg_") || bldg.name.Contains("Fountain")) continue;
                var mass = bldg.Find(bldg.name + "_Mass");
                var r = mass != null ? mass.GetComponent<Renderer>() : null;
                if (r == null) continue;
                var b = r.bounds;

                // Prefer faces toward Main (x~0), Market (x~30), West (x~-34)
                var candidates = new List<(Vector3 c, Vector3 n, float w)>();
                candidates.Add((new Vector3(b.center.x, b.center.y, b.max.z), Vector3.forward, b.size.x));
                candidates.Add((new Vector3(b.center.x, b.center.y, b.min.z), Vector3.back, b.size.x));
                candidates.Add((new Vector3(b.max.x, b.center.y, b.center.z), Vector3.right, b.size.z));
                candidates.Add((new Vector3(b.min.x, b.center.y, b.center.z), Vector3.left, b.size.z));

                foreach (var (c, n, w) in candidates)
                {
                    float laneScore = 0f;
                    if (Mathf.Abs(c.x) < 14f) laneScore += 2f;           // Main
                    if (Mathf.Abs(c.x - 30f) < 14f) laneScore += 2.5f;   // Market
                    if (Mathf.Abs(c.x + 34f) < 14f) laneScore += 2f;     // West
                    if (laneScore < 1.5f) continue;

                    // Extra signs (2–3 layered)
                    int nSigns = laneScore >= 2.5f ? 3 : 2;
                    for (int s = 0; s < nSigns && signs.Count > 0; s++)
                    {
                        var path = signs[(_addedSigns + s) % signs.Count];
                        float along = (s + 0.5f) / nSigns - 0.5f;
                        var tangent = Vector3.Cross(Vector3.up, n).normalized;
                        float y = b.min.y + 3.0f + s * 1.1f;
                        var pos = new Vector3(c.x, y, c.z) + tangent * (along * w * 0.55f) + n * 0.06f;
                        PlaceSignBoard($"OP_Sign_{_addedSigns}", pos, -n, path, 1.6f + (float)Rng.NextDouble() * 1.2f, 0.7f + (float)Rng.NextDouble() * 0.5f);
                        _addedSigns++;
                        _addedProps++;
                    }

                    // AC units
                    for (int a = 0; a < 2; a++)
                    {
                        float along = (a + 0.5f) / 2f - 0.5f;
                        var tangent = Vector3.Cross(Vector3.up, n).normalized;
                        float y = b.min.y + 2.6f + a * 2.0f;
                        var pos = new Vector3(c.x, y, c.z) + tangent * (along * w * 0.5f) + n * 0.08f;
                        var mesh = acMeshes.Count > 0 ? acMeshes[_addedAc % acMeshes.Count] : null;
                        SpawnMesh($"OP_AC_{_addedAc}", mesh, pos, Quaternion.LookRotation(-n), Vector3.one, Mats["metal"], false, 0f);
                        _addedAc++;
                        _addedProps++;
                    }

                    // Vertical pipe
                    if (pipes < 60)
                    {
                        var tangent = Vector3.Cross(Vector3.up, n).normalized;
                        var bottom = new Vector3(c.x, b.min.y + 0.3f, c.z) + tangent * (w * 0.3f) + n * 0.05f;
                        float len = Mathf.Min(b.size.y * 0.7f, 6f);
                        var pipe = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        pipe.name = $"OP_Pipe_{pipes}";
                        pipe.transform.SetParent(_root, true);
                        pipe.transform.position = bottom + Vector3.up * (len * 0.5f);
                        pipe.transform.localScale = new Vector3(0.11f, len * 0.5f, 0.11f);
                        Object.DestroyImmediate(pipe.GetComponent<Collider>());
                        ApplyMat(pipe.GetComponent<Renderer>(), Mats["pipe"], 1f);
                        SetStatic(pipe);
                        pipes++;
                        _addedProps++;
                    }

                    // Shutter
                    if (shutters < 40)
                    {
                        bool open = shutters % 2 == 0;
                        float h = open ? 0.35f : 2.2f;
                        float y = b.min.y + (open ? 2.4f : 1.15f);
                        var pos = new Vector3(c.x, y, c.z) + n * 0.04f;
                        var shut = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        shut.name = $"OP_Shutter_{shutters}";
                        shut.transform.SetParent(_root, true);
                        shut.transform.position = pos;
                        shut.transform.rotation = Quaternion.LookRotation(-n);
                        shut.transform.localScale = new Vector3(Mathf.Min(2.6f, w * 0.35f), h, 0.07f);
                        Object.DestroyImmediate(shut.GetComponent<Collider>());
                        ApplyMat(shut.GetComponent<Renderer>(), Mats["shutter"], 1.5f);
                        SetStatic(shut);
                        shutters++;
                        _addedProps++;
                    }
                }
            }

            Debug.Log($"[OP] Stage4 signs={_addedSigns} ac={_addedAc} pipes={pipes} shutters={shutters}");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Stage 5 — pack clutter INTO the five standard view frustums
        // Spec: each frame ≥3 signs, 1 vehicle, 1 cable tangle, 1 rubble, 2 AC
        // ═══════════════════════════════════════════════════════════════════════

        static void Stage5_FramePackStandardViews()
        {
            var rubbleMeshes = LoadMeshes("rubble_");
            var cableMeshes = LoadMeshes("cable_");
            var acMeshes = LoadMeshes("window_ac_");
            var signs = LoadSignPaths();
            var carNames = new[] { "suv.obj", "van.obj", "sedan.obj", "taxi.obj" };

            var frames = new (string name, Vector3 eye, Vector3 look)[]
            {
                ("01", new Vector3(-1.5f, 1.65f, -10f), new Vector3(10f, 3.2f, 8f)),
                ("02", new Vector3(2f, 1.65f, 14f), new Vector3(-8f, 4f, 28f)),
                ("03", new Vector3(24f, 1.65f, -6f), new Vector3(40f, 3.5f, 4f)),
                ("04", new Vector3(-28f, 1.65f, -4f), new Vector3(-36f, 3f, 12f)),
                ("05", new Vector3(0f, 1.65f, 2f), new Vector3(14f, 4f, 18f)),
            };

            int packed = 0;
            foreach (var (name, eye, look) in frames)
            {
                var fwd = look - eye; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();
                var right = Vector3.Cross(Vector3.up, fwd).normalized;

                // Vehicle parked mid-frame, off centerline
                {
                    var p = eye + fwd * 12f + right * 4.5f;
                    p.y = 0f;
                    if (!IsBlocked(p) && !NearExistingCar(p, 4f))
                    {
                        var meshPath = $"{CarDir}/{carNames[packed % carNames.Length]}";
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(meshPath);
                        if (prefab != null)
                        {
                            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                            go.name = $"OP_FrameCar_{name}";
                            go.transform.SetParent(_root, true);
                            go.transform.position = p;
                            go.transform.rotation = Quaternion.LookRotation(right);
                            go.transform.localScale = Vector3.one * 1.15f;
                            DustCar(go);
                            if (go.GetComponentInChildren<Collider>() == null)
                                go.AddComponent<BoxCollider>();
                            Seat(go.transform);
                            SetStatic(go);
                            _addedCars++;
                            _addedProps++;
                        }
                    }
                }

                // 2 rubble piles in mid-ground
                for (int i = 0; i < 2; i++)
                {
                    var p = eye + fwd * (7f + i * 5f) + right * ((i % 2 == 0) ? -3.8f : 3.2f);
                    p.y = 0f;
                    if (IsBlocked(p)) continue;
                    var mesh = rubbleMeshes.Count > 0 ? rubbleMeshes[(packed + i) % rubbleMeshes.Count] : null;
                    SpawnMesh($"OP_FrameRubble_{name}_{i}", mesh, p,
                        Quaternion.Euler(0, packed * 40f + i * 70f, 0),
                        Vector3.one * (1.5f + i * 0.2f), Mats["rubble"], true, 1.1f);
                    _addedRubble++;
                    _addedProps++;
                }

                // Cable tangle crossing the view
                {
                    var a = eye + Vector3.up * 6.8f - right * 8f + fwd * 3f;
                    var b = eye + Vector3.up * 7.4f + right * 9f + fwd * 14f;
                    SpawnCable($"OP_FrameCable_{name}_a", a, b, cableMeshes);
                    SpawnCable($"OP_FrameCable_{name}_b", a + Vector3.up * 0.3f + fwd * 1f, b + Vector3.up * 0.2f - fwd * 0.5f, cableMeshes);
                    SpawnCable($"OP_FrameCable_{name}_c", a + right * 0.4f, b - right * 0.3f + Vector3.up * 0.4f, cableMeshes);
                    _addedCables += 3;
                    _addedProps += 3;
                }

                // 3+ signs on walls flanking the look direction
                for (int i = 0; i < 4 && signs.Count > 0; i++)
                {
                    var side = (i % 2 == 0) ? right : -right;
                    var guess = eye + fwd * (6f + i * 3.5f) + side * 5.5f + Vector3.up * (2.8f + (i % 3) * 0.9f);
                    var face = NearestFacade(guess);
                    Vector3 p = face.hit ? face.point + face.normal * 0.07f : guess;
                    p.y = Mathf.Clamp(p.y, 2.5f, 6.5f);
                    PlaceSignBoard($"OP_FrameSign_{name}_{i}", p, face.hit ? -face.normal : -side,
                        signs[(_addedSigns) % signs.Count],
                        1.8f + (float)Rng.NextDouble() * 1.0f,
                        0.75f + (float)Rng.NextDouble() * 0.45f);
                    _addedSigns++;
                    _addedProps++;
                }

                // 2 AC units on flanking walls
                for (int i = 0; i < 2; i++)
                {
                    var side = (i % 2 == 0) ? right : -right;
                    var guess = eye + fwd * (8f + i * 4f) + side * 5.2f + Vector3.up * (3.2f + i * 1.5f);
                    var face = NearestFacade(guess);
                    Vector3 p = face.hit ? face.point + face.normal * 0.09f : guess;
                    p.y = Mathf.Clamp(p.y, 2.4f, 7f);
                    var mesh = acMeshes.Count > 0 ? acMeshes[_addedAc % acMeshes.Count] : null;
                    SpawnMesh($"OP_FrameAC_{name}_{i}", mesh, p,
                        Quaternion.LookRotation(face.hit ? -face.normal : -side),
                        Vector3.one * 1.15f, Mats["metal"], false, 0f);
                    _addedAc++;
                    _addedProps++;
                }

                // Wall-base dirt mounds visible in frame
                for (int i = 0; i < 3; i++)
                {
                    var side = (i % 2 == 0) ? right : -right;
                    var p = eye + fwd * (5f + i * 4f) + side * 4.8f;
                    p.y = 0f;
                    if (IsBlocked(p)) continue;
                    var mound = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    mound.name = $"OP_FrameDirt_{name}_{i}";
                    mound.transform.SetParent(_root, true);
                    mound.transform.position = p + Vector3.up * 0.22f;
                    mound.transform.rotation = Quaternion.Euler(0, Rng.Next(360), 5f);
                    mound.transform.localScale = new Vector3(1.6f, 0.4f, 1.1f);
                    Object.DestroyImmediate(mound.GetComponent<Collider>());
                    ApplyMat(mound.GetComponent<Renderer>(), Mats["dirt"], 2f);
                    SetStatic(mound);
                    _addedProps++;
                }

                // Kerb strip across near ground in view
                {
                    var p = eye + fwd * 4f;
                    p.y = 0.12f;
                    var kerb = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    kerb.name = $"OP_FrameKerb_{name}";
                    kerb.transform.SetParent(_root, true);
                    kerb.transform.position = p + right * 3.5f;
                    kerb.transform.rotation = Quaternion.LookRotation(fwd);
                    kerb.transform.localScale = new Vector3(0.4f, 0.28f, 6f);
                    Object.DestroyImmediate(kerb.GetComponent<Collider>());
                    ApplyMat(kerb.GetComponent<Renderer>(), Mats["concrete"], 2f);
                    SetStatic(kerb);
                    _addedKerbs++;
                    _addedProps++;
                }

                packed++;
            }

            // Frustum-filler OP_Force* anchors DISABLED — they placed signs/AC/cables in open
            // air with no wall/pole support (visible dark cubes against the sky). Wall-mounted
            // detail must come from Stage4 facade fill or ZZZ placement with lateral snap.
            // ForceVisibleAnchors();  // retired 2026-07-27 (AaaCorrectnessPass)

            Debug.Log($"[OP] Stage5 frame-packed {frames.Length} standard views (no ForceVisibleAnchors)");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Capture + luminance measure
        // ═══════════════════════════════════════════════════════════════════════

        static void CaptureAndMeasure()
        {
            EnsureDir(ShotDir);
            var shots = new (string name, Vector3 pos, Vector3 look)[]
            {
                ("01_MainStreet_Mid", new Vector3(-1.5f, 1.65f, -10f), new Vector3(10f, 3.2f, 8f)),
                ("02_MainStreet_North", new Vector3(2f, 1.65f, 14f), new Vector3(-8f, 4f, 28f)),
                ("03_Market_Lane", new Vector3(24f, 1.65f, -6f), new Vector3(40f, 3.5f, 4f)),
                ("04_West_Alley", new Vector3(-28f, 1.65f, -4f), new Vector3(-36f, 3f, 12f)),
                ("05_DeathTriangle", new Vector3(0f, 1.65f, 2f), new Vector3(14f, 4f, 18f)),
                // Eye-level walk series
                ("EL_Main_S", new Vector3(0f, 1.7f, -30f), new Vector3(6f, 2.8f, -12f)),
                ("EL_Main_0", new Vector3(0f, 1.7f, 0f), new Vector3(8f, 3f, 14f)),
                ("EL_Main_N", new Vector3(0f, 1.7f, 30f), new Vector3(-6f, 3f, 48f)),
                ("EL_Market_S", new Vector3(28f, 1.7f, -30f), new Vector3(38f, 3f, -12f)),
                ("EL_Market_0", new Vector3(28f, 1.7f, 0f), new Vector3(40f, 3f, 14f)),
                ("EL_Market_N", new Vector3(28f, 1.7f, 30f), new Vector3(36f, 3f, 48f)),
                ("EL_West_0", new Vector3(-32f, 1.7f, 0f), new Vector3(-40f, 3f, 14f)),
                ("EL_West_N", new Vector3(-32f, 1.7f, 24f), new Vector3(-38f, 3f, 40f)),
            };

            var temp = new GameObject("OP_CaptureCam");
            var cam = temp.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.allowHDR = true;
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null) data.renderPostProcessing = true;

            float maxSkyLum = 0f;
            float avgSkyLum = 0f;
            int skySamples = 0;

            foreach (var (name, pos, look) in shots)
            {
                cam.transform.position = pos;
                cam.transform.LookAt(look);
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

                var path = $"{ShotDir}/{name}.png";
                File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());

                // Sample upper third for sky luminance (Rec.709)
                float localMax = 0f, localSum = 0f;
                int n = 0;
                for (int y = (int)(h * 0.72f); y < h; y += 8)
                {
                    for (int x = 0; x < w; x += 12)
                    {
                        var c = tex.GetPixel(x, y);
                        float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                        localMax = Mathf.Max(localMax, lum);
                        localSum += lum;
                        n++;
                    }
                }
                float localAvg = n > 0 ? localSum / n : 0f;
                maxSkyLum = Mathf.Max(maxSkyLum, localMax);
                avgSkyLum += localAvg;
                skySamples++;

                // Ground sample (lower third)
                float gSum = 0f; int gn = 0;
                for (int y = 0; y < (int)(h * 0.28f); y += 8)
                {
                    for (int x = w / 4; x < w * 3 / 4; x += 12)
                    {
                        var c = tex.GetPixel(x, y);
                        gSum += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                        gn++;
                    }
                }
                float gAvg = gn > 0 ? gSum / gn : 0f;
                Debug.Log($"[OP] shot {name}: skyMax={localMax:F3} skyAvg={localAvg:F3} groundAvg={gAvg:F3}");

                Object.DestroyImmediate(tex);
            }

            Object.DestroyImmediate(temp);
            AssetDatabase.Refresh();
            float skyAvgAll = skySamples > 0 ? avgSkyLum / skySamples : 0f;
            Debug.Log($"[OP] SKY LUMINANCE max={maxSkyLum:F3} avg={skyAvgAll:F3} (target brightest ~0.85-0.95, not 1.0)");
        }

        static void AuditInvisibleColliders()
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
                    // Strip OP_ decorative invisible colliders if any slipped in
                    if (c.name.StartsWith("OP_") || (c.transform.parent != null && c.transform.root.name == RootName))
                    {
                        // Only keep if has visible renderer somewhere up
                        bool anyVis = c.GetComponentsInChildren<Renderer>().Any(x => x.enabled);
                        if (!anyVis)
                        {
                            Object.DestroyImmediate(c);
                            continue;
                        }
                    }
                    invis++;
                    if (invis <= 12)
                        Debug.LogWarning($"[OP] Invisible collider: {PathOf(c.transform)}");
                }
            }
            // Recount after strip
            all = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include);
            invis = 0;
            foreach (var c in all)
            {
                if (c is CharacterController) continue;
                var r = c.GetComponent<Renderer>();
                if (r == null) r = c.GetComponentInChildren<Renderer>();
                if (r == null || !r.enabled) invis++;
            }
            Debug.Log($"[OP] colliders={all.Length} invisible={invis}");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Placement helpers
        // ═══════════════════════════════════════════════════════════════════════

        static void PlaceSignsNear(Vector3 eye, Vector3 fwd, Vector3 right, List<string> signs, int count)
        {
            if (signs.Count == 0) return;
            for (int i = 0; i < count; i++)
            {
                var side = (i % 2 == 0) ? right : -right;
                var pos = eye + fwd * (4f + i * 3f) + side * 5.5f + Vector3.up * (2.6f + i * 0.7f);
                // Snap to nearest building face if close
                var face = NearestFacade(pos);
                Vector3 n = face.normal;
                Vector3 p = face.hit ? face.point + face.normal * 0.06f : pos;
                p.y = Mathf.Max(p.y, 2.4f);
                PlaceSignBoard($"OP_Sign_{_addedSigns}", p, face.hit ? -n : -side, signs[_addedSigns % signs.Count],
                    1.5f + (float)Rng.NextDouble(), 0.65f + (float)Rng.NextDouble() * 0.4f);
                _addedSigns++;
                _addedProps++;
            }
        }

        static void PlaceAcNear(Vector3 eye, Vector3 fwd, Vector3 right, List<Mesh> acMeshes, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var side = (i % 2 == 0) ? right : -right;
                var guess = eye + fwd * (5f + i * 4f) + side * 5.2f + Vector3.up * (2.5f + i * 1.6f);
                var face = NearestFacade(guess);
                Vector3 p = face.hit ? face.point + face.normal * 0.08f : guess;
                p.y = Mathf.Clamp(p.y, 2.3f, 7f);
                var mesh = acMeshes.Count > 0 ? acMeshes[_addedAc % acMeshes.Count] : null;
                SpawnMesh($"OP_AC_{_addedAc}", mesh, p,
                    Quaternion.LookRotation(face.hit ? -face.normal : -side), Vector3.one, Mats["metal"], false, 0f);
                _addedAc++;
                _addedProps++;
            }
        }

        struct FaceHit
        {
            public bool hit;
            public Vector3 point;
            public Vector3 normal;
        }

        static FaceHit NearestFacade(Vector3 from)
        {
            FaceHit best = default;
            float bestD = 12f;
            foreach (Transform bldg in _map)
            {
                if (!bldg.name.StartsWith("Bldg_")) continue;
                var mass = bldg.Find(bldg.name + "_Mass");
                var r = mass != null ? mass.GetComponent<Renderer>() : null;
                if (r == null) continue;
                var b = r.bounds;
                var candidates = new (Vector3 p, Vector3 n)[]
                {
                    (new Vector3(Mathf.Clamp(from.x, b.min.x, b.max.x), from.y, b.max.z), Vector3.forward),
                    (new Vector3(Mathf.Clamp(from.x, b.min.x, b.max.x), from.y, b.min.z), Vector3.back),
                    (new Vector3(b.max.x, from.y, Mathf.Clamp(from.z, b.min.z, b.max.z)), Vector3.right),
                    (new Vector3(b.min.x, from.y, Mathf.Clamp(from.z, b.min.z, b.max.z)), Vector3.left),
                };
                foreach (var (p, n) in candidates)
                {
                    float d = Vector3.Distance(from, p);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = new FaceHit { hit = true, point = p, normal = n };
                    }
                }
            }
            return best;
        }

        static void PlaceSignBoard(string name, Vector3 pos, Vector3 outwardNormal, string texPath, float w, float h)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(_root, true);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(outwardNormal.sqrMagnitude > 0.01f ? outwardNormal : Vector3.forward);
            go.transform.localScale = new Vector3(w, h, 0.05f);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            ApplyMat(go.GetComponent<Renderer>(), MakeSignMat(texPath), 1f);
            SetStatic(go);
        }

        static void SpawnCable(string name, Vector3 a, Vector3 b, List<Mesh> cableMeshes)
        {
            var dir = b - a;
            float len = dir.magnitude;
            if (len < 0.5f) return;
            var rot = Quaternion.LookRotation(dir.normalized, Vector3.up);

            if (cableMeshes.Count > 0)
            {
                var mesh = cableMeshes[Rng.Next(cableMeshes.Count)];
                var go = new GameObject(name);
                go.transform.SetParent(_root, true);
                go.transform.position = a;
                go.transform.rotation = rot * Quaternion.Euler(0, -90, 0);
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                ApplyMat(mr, Mats["cable"], 1f);
                float meshLen = Mathf.Max(mesh.bounds.size.x, 0.1f);
                go.transform.localScale = new Vector3(len / meshLen, 1f, 1f);
                go.transform.Rotate(0, 0, -5f - (float)Rng.NextDouble() * 8f, Space.Self);
                SetStatic(go);
            }
            else
            {
                int segs = 5;
                for (int s = 0; s < segs; s++)
                {
                    float t0 = s / (float)segs;
                    float t1 = (s + 1) / (float)segs;
                    var p0 = Vector3.Lerp(a, b, t0); p0.y -= Mathf.Sin(t0 * Mathf.PI) * len * 0.08f;
                    var p1 = Vector3.Lerp(a, b, t1); p1.y -= Mathf.Sin(t1 * Mathf.PI) * len * 0.08f;
                    var seg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    seg.name = name + "_" + s;
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

        static GameObject SpawnMesh(string name, Mesh mesh, Vector3 pos, Quaternion rot, Vector3 scale, Material mat, bool withCollider, float coverH)
        {
            GameObject go;
            if (mesh != null)
            {
                go = new GameObject(name);
                go.transform.SetParent(_root, true);
                go.transform.SetPositionAndRotation(pos, rot);
                go.transform.localScale = scale;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                ApplyMat(go.AddComponent<MeshRenderer>(), mat, 1f);
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
                go.transform.SetPositionAndRotation(pos + Vector3.up * 0.4f, rot);
                go.transform.localScale = scale;
                if (!withCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
                ApplyMat(go.GetComponent<Renderer>(), mat, 1f);
            }
            Seat(go.transform);
            if (withCollider && coverH > 0f) CapCover(go, coverH);
            SetStatic(go);
            return go;
        }

        static void CapCover(GameObject go, float h)
        {
            var box = go.GetComponent<BoxCollider>();
            if (box == null) box = go.GetComponentInChildren<BoxCollider>();
            if (box == null && go.GetComponent<MeshCollider>() != null) return;
            if (box == null)
            {
                box = go.AddComponent<BoxCollider>();
                var b = BoundsOf(go);
                box.center = go.transform.InverseTransformPoint(b.center);
                box.size = go.transform.InverseTransformVector(b.size);
                box.size = new Vector3(Mathf.Abs(box.size.x), Mathf.Abs(box.size.y), Mathf.Abs(box.size.z));
            }
            if (box.size.y > h * 1.3f)
            {
                float shrink = h / box.size.y;
                box.center = new Vector3(box.center.x, box.center.y * shrink, box.center.z);
                var s = box.size; s.y = h; box.size = s;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Materials / util
        // ═══════════════════════════════════════════════════════════════════════

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
            var alb = AssetDatabase.LoadAssetAtPath<Texture2D>(albedo);
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(normal);
            if (alb != null) { mat.SetTexture("_BaseMap", alb); mat.mainTexture = alb; }
            mat.SetColor("_BaseColor", tint);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
            mat.mainTextureScale = new Vector2(tiling, tiling);
            if (nrm != null)
            {
                mat.SetTexture("_BumpMap", nrm);
                mat.EnableKeyword("_NORMALMAP");
                mat.SetFloat("_BumpScale", 0.9f);
                mat.SetTextureScale("_BumpMap", new Vector2(tiling, tiling));
            }
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material LoadOrSolid(string name, Color c)
        {
            var path = $"{OdMatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            return Solid(name, c);
        }

        static Material Solid(string name, Color c)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = name;
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", c);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material MakeDecal(string name, Color c)
        {
            var path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = name;
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", c);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.2f);
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = 3000;
            }
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material MakeSignMat(string texPath)
        {
            var key = "OP_Sign_" + Path.GetFileNameWithoutExtension(texPath);
            if (Mats.TryGetValue(key, out var existing)) return existing;
            var path = $"{MatDir}/{key}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = key;
                AssetDatabase.CreateAsset(mat, path);
            }
            var alb = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (alb != null) { mat.SetTexture("_BaseMap", alb); mat.mainTexture = alb; }
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 0.35f);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            Mats[key] = mat;
            return mat;
        }

        static List<Mesh> LoadMeshes(string prefix)
        {
            var list = new List<Mesh>();
            foreach (var guid in AssetDatabase.FindAssets(prefix, new[] { GenDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(p);
                if (mesh != null) { list.Add(mesh); continue; }
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go != null)
                {
                    var mf = go.GetComponentInChildren<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) list.Add(mf.sharedMesh);
                }
            }
            return list;
        }

        static List<string> LoadSignPaths()
        {
            var list = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { SignDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var file = Path.GetFileName(p);
                if (file.Contains("_normal") || file.Contains("_rough")) continue;
                if (!(file.StartsWith("fascia_") || file.StartsWith("vertical_") || file.StartsWith("plate_") || file.StartsWith("banner_")))
                    continue;
                list.Add(p);
            }
            return list;
        }

        static void ApplyMat(Renderer r, Material mat, float tiling)
        {
            if (r == null || mat == null) return;
            r.sharedMaterial = mat;
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
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

        static void Seat(Transform t)
        {
            if (t == null) return;
            var b = BoundsOf(t.gameObject);
            float delta = 0.01f - b.min.y;
            if (Mathf.Abs(delta) > 0.001f) t.position += Vector3.up * delta;
        }

        static Bounds BoundsOf(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
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
                        m.SetColor("_BaseColor", Color.Lerp(c, new Color(0.55f, 0.48f, 0.38f), 0.4f));
                    }
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
            }
        }

        static bool IsBlocked(Vector3 p)
        {
            if (Mathf.Abs(p.x) < 12f && p.z < -60f) return true;
            if (Mathf.Abs(p.x) < 12f && p.z > 60f) return true;
            if (Mathf.Abs(p.x) < 1.3f && Mathf.Abs(p.z) < 55f) return true; // Main clear
            if (Mathf.Abs(p.x - 30f) < 1.1f && Mathf.Abs(p.z) < 55f) return true; // Market clear
            if (Mathf.Abs(p.x + 34f) < 1.1f && Mathf.Abs(p.z) < 45f) return true; // West clear
            return false;
        }

        static bool NearExistingCar(Vector3 p, float r)
        {
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (!(t.name.Contains("Car") || t.name.Contains("Van") || t.name.Contains("SUV") || t.name.Contains("taxi") || t.name.Contains("sedan")))
                    continue;
                if (Vector3.Distance(t.position, p) < r) return true;
            }
            return false;
        }

        static void SetStatic(GameObject go)
        {
            if (go == null) return;
            go.isStatic = true;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                t.gameObject.isStatic = true;
        }

        static string PathOf(Transform t)
        {
            var p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (!profile.TryGet(out T c)) c = profile.Add<T>(true);
            c.active = true;
            return c;
        }

        static void Set<T>(VolumeParameter<T> p, T v)
        {
            p.overrideState = true;
            p.value = v;
        }
    }
}
#endif
