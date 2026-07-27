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
    /// Adds real geometric facade depth (recessed windows/doors, trim, parapets, chamfers)
    /// and dusty-overcast sky/grade. Idempotent under FD_FacadeDepth root.
    /// Menu: Arena FPS / AAA Facade Depth Pass
    /// </summary>
    public static class AaaFacadeDepthPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string RootName = "FD_FacadeDepth";
        const string MatDir = "Assets/_Project/Art/Materials/FacadeDepth";
        const string OdMatDir = "Assets/_Project/Art/Materials/OverflowDressing";
        const string LightingDir = "Assets/_Project/Settings/Lighting";
        const string SkyboxPath = LightingDir + "/Arena_Overflow_Overcast_Skybox.mat";
        const string OvercastHdri = "Assets/_Project/Art/Textures/HDRI/overcast_industrial_courtyard_4k.hdr";
        const string ShotDir = "Assets/_Project/Art/Screenshots/Depth";

        static readonly System.Random Rng = new(20260727);
        static readonly Dictionary<string, Material> Mats = new();
        static Transform _root;
        static Transform _map;
        static int _boxes;
        static int _buildings;
        static int _windows;
        static int _doors;
        static int _balconies;
        static int _stylesUsed;

        enum FacadeStyle { ClassicShop, ArcadeMarket, ColonialPlaster, Warehouse }

        struct Face
        {
            public Transform bldg;
            public Bounds mass;
            public Vector3 normal;
            public Vector3 tangent;
            public Vector3 center; // face center on surface
            public float width;
            public float height;
            public FacadeStyle style;
            public int seed;
        }

        [MenuItem("Arena FPS/AAA Facade Depth Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[FD] Exit play mode and run again.");
                return;
            }

            OpenArena();
            _map = GameObject.Find("ThreeLaneMap")?.transform;
            if (_map == null)
            {
                Debug.LogError("[FD] ThreeLaneMap missing.");
                return;
            }

            EnsureFolders();
            Mats.Clear();
            _boxes = _buildings = _windows = _doors = _balconies = _stylesUsed = 0;

            try
            {
                Stage0_SkyAndGrade();
                ClearPrevious();
                BuildMaterials();
                HideStickerWindows();
                DressAllFacades();
                StripInvisibleOdColliders();
                SetStaticRecursive(_root.gameObject);

                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                EditorSceneManager.SaveOpenScenes();
                AssetDatabase.SaveAssets();
                DynamicGI.UpdateEnvironment();

                int invis = CountInvisibleColliders();
                Debug.Log($"[FD] Done. buildings={_buildings} windows={_windows} doors={_doors} balconies={_balconies} boxes={_boxes} styles={_stylesUsed} invisibleColliders={invis}");
            }
            catch (Exception ex)
            {
                Debug.LogError("[FD] FATAL: " + ex);
                throw;
            }
        }

        [MenuItem("Arena FPS/AAA Facade Depth Pass/Sky And Grade Only")]
        public static void RunSkyOnly()
        {
            OpenArena();
            EnsureFolders();
            Stage0_SkyAndGrade();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            DynamicGI.UpdateEnvironment();
            Debug.Log("[FD] Sky and grade only applied.");
        }

        [MenuItem("Arena FPS/AAA Facade Depth Pass/Capture Verification Shots")]
        public static void CaptureVerificationShots()
        {
            OpenArena();
            EnsureFolders();
            if (!Directory.Exists(Path.GetFullPath(ShotDir)))
                Directory.CreateDirectory(Path.GetFullPath(ShotDir));

            // Oblique to facades so recess reveals read (straight-on hides depth)
            var shots = new (string name, Vector3 pos, Vector3 look)[]
            {
                ("01_MainStreet_Mid", new Vector3(1.5f, 1.75f, -6f), new Vector3(9.5f, 4.0f, 8f)),
                ("02_MainStreet_North", new Vector3(-1f, 1.75f, 10f), new Vector3(-10f, 4.5f, 24f)),
                ("03_Market_Lane", new Vector3(30f, 1.9f, -4f), new Vector3(42f, 4.5f, 12f)),
                ("04_West_Alley", new Vector3(-32f, 1.75f, -2f), new Vector3(-42f, 3.8f, 12f)),
                ("05_DeathTriangle", new Vector3(2f, 1.75f, -2f), new Vector3(14f, 4.5f, 14f)),
            };

            Camera cam = Camera.main;
            if (cam == null)
                cam = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include).FirstOrDefault();
            GameObject temp = null;
            if (cam == null)
            {
                temp = new GameObject("FD_CaptureCam");
                cam = temp.AddComponent<Camera>();
                cam.tag = "MainCamera";
            }

            cam.allowHDR = true;
            cam.allowMSAA = true;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 250f;
            cam.fieldOfView = 70f;

            foreach (var s in shots)
            {
                cam.transform.position = s.pos;
                cam.transform.rotation = Quaternion.LookRotation((s.look - s.pos).normalized, Vector3.up);
                SceneView.RepaintAll();
                cam.Render();

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

                var path = $"{ShotDir}/{s.name}.png";
                File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path);
                Debug.Log("[FD] Wrote " + path);
            }

            if (temp != null) Object.DestroyImmediate(temp);
            AssetDatabase.Refresh();
        }

        static void OpenArena()
        {
            var active = SceneManager.GetActiveScene();
            if (active.path == ScenePath) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Art/Materials"))
                AssetDatabase.CreateFolder("Assets/_Project/Art", "Materials");
            if (!AssetDatabase.IsValidFolder(MatDir))
                AssetDatabase.CreateFolder("Assets/_Project/Art/Materials", "FacadeDepth");
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Art/Screenshots"))
                AssetDatabase.CreateFolder("Assets/_Project/Art", "Screenshots");
            if (!AssetDatabase.IsValidFolder(ShotDir))
                AssetDatabase.CreateFolder("Assets/_Project/Art/Screenshots", "Depth");
            if (!AssetDatabase.IsValidFolder(LightingDir))
                AssetDatabase.CreateFolder("Assets/_Project/Settings", "Lighting");
        }

        static void ClearPrevious()
        {
            var existing = GameObject.Find(RootName);
            if (existing != null) Object.DestroyImmediate(existing);

            // Also clear any orphaned FD_ under map from older detail pass naming collisions
            if (_map != null)
            {
                var doomed = new List<GameObject>();
                foreach (Transform c in _map)
                {
                    if (c.name == RootName) doomed.Add(c.gameObject);
                }
                foreach (var g in doomed) Object.DestroyImmediate(g);
            }

            _root = new GameObject(RootName).transform;
            _root.SetParent(_map, false);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Sky + grade — dusty overcast midday (mood over cloud contrast)
        // ─────────────────────────────────────────────────────────────────────

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

            // Calibrated with polish pass — keep cloud bands below tonemap knee.
            if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", 0.58f);
            if (sky.HasProperty("_Rotation")) sky.SetFloat("_Rotation", 90f);
            if (sky.HasProperty("_Tint"))
                sky.SetColor("_Tint", new Color(0.96f, 0.93f, 0.88f));
            if (sky.HasProperty("_ImageType")) sky.SetFloat("_ImageType", 0f);
            if (sky.HasProperty("_Mapping")) sky.SetFloat("_Mapping", 1f);
            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;

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
                l.intensity = 1.35f;
                l.color = new Color(1f, 0.96f, 0.88f);
                l.shadowStrength = 0.30f;
                l.shadows = LightShadows.Soft;
                l.transform.rotation = Quaternion.Euler(56f, -38f, 0f);
                EditorUtility.SetDirty(l);
            }

            var volume = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include)
                .FirstOrDefault(v => v.isGlobal);
            if (volume == null || volume.sharedProfile == null)
            {
                Debug.LogWarning("[FD] Global Volume missing; grade skipped.");
                return;
            }

            var profile = volume.sharedProfile;

            var color = GetOrAdd<ColorAdjustments>(profile);
            Set(color.postExposure, 0.02f);

            GetOrAdd<WhiteBalance>(profile);
            GetOrAdd<LiftGammaGain>(profile);
            if (profile.TryGet(out ShadowsMidtonesHighlights smh)) smh.active = true;
            AaaUrpGradeUtil.ApplyCanonicalDustyGrade(profile, "AaaFacadeDepthPass");

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
            Debug.Log("[FD] Sky → overcast_industrial_courtyard_4k (exp 0.58, rot 90). Grade: contrast 18, soft highlight.");
        }

        static void StripInvisibleOdColliders()
        {
            int stripped = 0;
            foreach (var c in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (c is CharacterController) continue;
                if (!c.name.StartsWith("OD_")) continue;
                var r = c.GetComponent<Renderer>();
                bool bad = r == null;
                if (!bad) { try { bad = !r.enabled; } catch { bad = true; } }
                if (!bad) continue;
                Object.DestroyImmediate(c);
                stripped++;
            }
            if (stripped > 0) Debug.Log($"[FD] Stripped {stripped} OD invisible colliders.");
        }

        static int CountInvisibleColliders()
        {
            int invis = 0;
            foreach (var c in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (c is CharacterController) continue;
                var r = c.GetComponent<Renderer>();
                bool bad = r == null;
                if (!bad) { try { bad = !r.enabled; } catch { bad = true; } }
                if (bad) invis++;
            }
            return invis;
        }

        static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (!profile.TryGet(out T c))
            {
                c = profile.Add<T>(true);
            }
            c.active = true;
            return c;
        }

        static void Set<T>(VolumeParameter<T> p, T v)
        {
            p.overrideState = true;
            p.value = v;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Materials
        // ─────────────────────────────────────────────────────────────────────

        static void BuildMaterials()
        {
            Mats["trim"] = LoadOrClone("OD_Trim", "FD_Trim", new Color(0.18f, 0.16f, 0.14f), 0.05f, 0.22f);
            Mats["concrete"] = LoadOrClone("OD_Concrete", "FD_Concrete", new Color(0.62f, 0.60f, 0.55f), 0f, 0.28f);
            Mats["concreteDark"] = LoadOrClone("OD_ConcreteDark", "FD_ConcreteDark", new Color(0.42f, 0.40f, 0.36f), 0f, 0.22f);
            Mats["plaster"] = LoadOrClone("OD_PlasterTan", "FD_Plaster", new Color(0.78f, 0.70f, 0.56f), 0f, 0.20f);
            Mats["plasterCream"] = LoadOrClone("OD_PlasterCream", "FD_PlasterCream", new Color(0.84f, 0.80f, 0.70f), 0f, 0.18f);
            Mats["brick"] = LoadOrClone("OD_BrickWarm", "FD_Brick", new Color(0.62f, 0.42f, 0.32f), 0f, 0.20f);
            Mats["wood"] = LoadOrClone("OD_Wood", "FD_Wood", new Color(0.30f, 0.20f, 0.12f), 0f, 0.18f);
            Mats["metal"] = LoadOrClone("OD_Metal", "FD_Metal", new Color(0.40f, 0.42f, 0.40f), 0.65f, 0.35f);
            Mats["glass"] = MakeGlass("FD_GlassRecess", new Color(0.12f, 0.16f, 0.18f, 0.85f));
            Mats["void"] = Solid("FD_Void", new Color(0.04f, 0.035f, 0.03f), 0f, 0.05f);
            Mats["shutter"] = LoadOrClone("OD_Shutter", "FD_Shutter", new Color(0.28f, 0.30f, 0.28f), 0.2f, 0.25f);
            Mats["rail"] = Solid("FD_Rail", new Color(0.22f, 0.20f, 0.18f), 0.4f, 0.30f);
        }

        static Material LoadOrClone(string odName, string fdName, Color fallback, float metallic, float smooth)
        {
            var od = AssetDatabase.LoadAssetAtPath<Material>($"{OdMatDir}/{odName}.mat");
            var path = $"{MatDir}/{fdName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = od != null ? new Material(od) : new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                mat.name = fdName;
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (od != null)
            {
                mat.CopyPropertiesFromMaterial(od);
            }
            if (od == null)
            {
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", fallback);
                else mat.color = fallback;
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smooth);
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material Solid(string name, Color color, float metallic, float smooth)
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
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material MakeGlass(string name, Color color)
        {
            var mat = Solid(name, color, 0.05f, 0.90f);
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.renderQueue = (int)RenderQueue.Transparent;
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", new Color(0.04f, 0.05f, 0.055f));
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Hide thin sticker windows so real recesses read cleanly
        // ─────────────────────────────────────────────────────────────────────

        static void HideStickerWindows()
        {
            int hidden = 0;
            foreach (Transform bldg in _map)
            {
                if (!bldg.name.StartsWith("Bldg_") || bldg.name.Contains("Fountain")) continue;
                foreach (var r in bldg.GetComponentsInChildren<Renderer>(true))
                {
                    var n = r.gameObject.name;
                    // Existing OD/Env thin window cards + door recess sticker kits
                    if (n.Contains("_Win_") || n.Contains("DoorRecess") || n.Contains("DoorAwning")
                        || n.Contains("_Parapet") || n.Contains("RoofLedge") || n.Contains("_Pillar")
                        || n.Contains("Trim_") || n.Contains("_Lintel") || n.Contains("_Sill"))
                    {
                        r.enabled = false;
                        foreach (var col in r.GetComponents<Collider>())
                            Object.DestroyImmediate(col);
                        hidden++;
                    }
                }
            }
            Debug.Log($"[FD] Hidden {hidden} sticker/trim renderers on building trees.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Facade dressing
        // ─────────────────────────────────────────────────────────────────────

        static void DressAllFacades()
        {
            var faces = CollectStreetFaces();
            var byBldg = faces.GroupBy(f => f.bldg);
            foreach (var g in byBldg)
            {
                _buildings++;
                var style = PickStyle(g.Key.name, g.Key.name.GetHashCode());
                _stylesUsed |= 1 << (int)style;

                // Roof parapet + cornice once per building (top of mass)
                var first = g.First();
                AddRoofParapet(first.mass, style);

                // Corner quoins / chamfers on mass footprint
                AddCornerTreatment(first.mass, style);

                foreach (var face in g)
                {
                    var f = face;
                    f.style = style;
                    DressFace(f);
                }
            }
        }

        static FacadeStyle PickStyle(string name, int id)
        {
            // Deterministic but varied — 4 recipes distributed by name hash
            int h = (name.GetHashCode() ^ id) & 0x7fffffff;
            if (name.Contains("ShopRow") || name.Contains("Market") || name.Contains("Spices") || name.Contains("Deli") || name.Contains("Baskets"))
                return (h % 2 == 0) ? FacadeStyle.ArcadeMarket : FacadeStyle.ClassicShop;
            if (name.Contains("Bank") || name.Contains("Glass") || name.Contains("Electronics") || name.Contains("Shoes"))
                return FacadeStyle.ColonialPlaster;
            if (name.Contains("Construction") || name.Contains("West") || name.Contains("Fruit") || name.Contains("Stalls"))
                return FacadeStyle.Warehouse;
            return (FacadeStyle)(h % 4);
        }

        static List<Face> CollectStreetFaces()
        {
            // Approximate playable lane centerlines (world XZ)
            var lanes = new[]
            {
                new Vector3(0f, 0f, 0f),      // main / mid
                new Vector3(32f, 0f, 0f),     // market east
                new Vector3(-34f, 0f, 0f),    // west alley
            };

            var list = new List<Face>();
            foreach (Transform bldg in _map)
            {
                if (!bldg.name.StartsWith("Bldg_") || bldg.name.Contains("Fountain")) continue;
                // Spawn halls get lighter treatment but still visible from ends
                var massT = bldg.Find(bldg.name + "_Mass") ?? bldg;
                var r = massT.GetComponent<Renderer>() ?? massT.GetComponentInChildren<Renderer>();
                if (r == null) continue;
                var bounds = r.bounds;
                if (bounds.size.y < 2.2f) continue;

                var normals = new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
                foreach (var n in normals)
                {
                    var faceCenter = bounds.center + Vector3.Scale(n, bounds.extents);
                    faceCenter.y = bounds.center.y;

                    // Distance from face center to nearest lane along the face normal direction
                    float bestLaneScore = float.MaxValue;
                    foreach (var lane in lanes)
                    {
                        var toLane = lane - faceCenter;
                        toLane.y = 0f;
                        // Prefer faces that look toward a lane (normal · toLane > 0) and are close
                        float toward = Vector3.Dot(n, toLane.normalized);
                        float dist = toLane.magnitude;
                        float score = dist - toward * 40f; // bias faces looking at lanes
                        if (score < bestLaneScore) bestLaneScore = score;
                    }

                    // Keep street-facing; drop backs that look away from all lanes
                    var outwardProbe = faceCenter + n * 2f;
                    float minDistToLane = lanes.Min(l =>
                    {
                        var d = l - outwardProbe; d.y = 0; return d.magnitude;
                    });
                    float minDistFromBack = lanes.Min(l =>
                    {
                        var d = l - (faceCenter - n * 2f); d.y = 0; return d.magnitude;
                    });
                    if (minDistToLane > minDistFromBack + 4f) continue; // clearly back face
                    if (minDistToLane > 55f) continue; // far from playable

                    var tangent = Vector3.Cross(Vector3.up, n).normalized;
                    float width = Mathf.Abs(Vector3.Dot(bounds.size, tangent));
                    if (width < 3f) continue;

                    list.Add(new Face
                    {
                        bldg = bldg,
                        mass = bounds,
                        normal = n,
                        tangent = tangent,
                        center = faceCenter,
                        width = width,
                        height = bounds.size.y,
                        seed = (bldg.name + n).GetHashCode()
                    });
                }
            }
            Debug.Log($"[FD] Street faces collected: {list.Count}");
            return list;
        }

        static void DressFace(Face f)
        {
            var rng = new System.Random(f.seed ^ 9173);
            float storey = Mathf.Clamp(f.height / Mathf.Max(1, Mathf.RoundToInt(f.height / 3.2f)), 2.6f, 3.6f);
            int floors = Mathf.Clamp(Mathf.RoundToInt(f.height / storey), 1, 4);

            // Floor-separation string courses
            float beltProud = StyleBelt(f.style);
            for (int fl = 1; fl < floors; fl++)
            {
                float y = f.mass.min.y + fl * storey;
                Box(
                    $"Belt_{f.bldg.name}_{Dir(f.normal)}_{fl}",
                    f.center - Vector3.up * (f.center.y - y) + f.normal * (beltProud * 0.5f + 0.02f),
                    Along(f, f.width + 0.15f, 0.14f + (float)rng.NextDouble() * 0.06f, beltProud),
                    Mats["concreteDark"]);
            }

            // Watertable / plinth lip on street face
            Box(
                $"Watertable_{f.bldg.name}_{Dir(f.normal)}",
                new Vector3(f.center.x, f.mass.min.y + 0.55f, f.center.z) + f.normal * 0.10f,
                Along(f, f.width + 0.25f, 0.35f, 0.20f),
                Mats["concrete"]);

            // Pilasters for colonial / classic
            if (f.style == FacadeStyle.ColonialPlaster || f.style == FacadeStyle.ClassicShop)
            {
                int bays = Mathf.Clamp(Mathf.FloorToInt(f.width / 3.2f), 2, 6);
                for (int i = 0; i <= bays; i++)
                {
                    float along = -f.width * 0.5f + i * (f.width / bays);
                    float thick = f.style == FacadeStyle.ColonialPlaster ? 0.28f : 0.20f;
                    Box(
                        $"Pilaster_{f.bldg.name}_{Dir(f.normal)}_{i}",
                        f.center + f.tangent * along + f.normal * 0.10f,
                        Along(f, thick, f.height - 0.5f, 0.18f),
                        Mats["concrete"]);
                }
            }

            // Ground-floor door recess(es)
            int doorCount = f.style == FacadeStyle.ArcadeMarket
                ? Mathf.Clamp(Mathf.FloorToInt(f.width / 5.5f), 1, 3)
                : (f.width > 7f && rng.NextDouble() > 0.35 ? 2 : 1);
            if (f.bldg.name.Contains("Stalls") || f.height < 3.5f) doorCount = Mathf.Min(doorCount, 1);

            var doorSlots = new List<float>();
            for (int d = 0; d < doorCount; d++)
            {
                float t = doorCount == 1
                    ? (float)(rng.NextDouble() * 0.3 - 0.15)
                    : -0.35f + d * (0.7f / Mathf.Max(1, doorCount - 1));
                float along = t * f.width * 0.5f;
                doorSlots.Add(along);
                PlaceDoor(f, along, f.style, rng);
            }

            // Windows per floor
            float winW = f.style == FacadeStyle.Warehouse ? 1.05f : (f.style == FacadeStyle.ColonialPlaster ? 1.25f : 1.15f);
            float winH = f.style == FacadeStyle.Warehouse ? 1.05f : 1.30f;
            float recess = f.style == FacadeStyle.ArcadeMarket ? 0.28f : (f.style == FacadeStyle.Warehouse ? 0.18f : 0.22f);
            recess += (float)(rng.NextDouble() * 0.06);

            int cols = Mathf.Clamp(Mathf.FloorToInt(f.width / (winW + 1.1f)), 2, 7);
            for (int fl = 0; fl < floors; fl++)
            {
                float y = f.mass.min.y + fl * storey + storey * 0.55f;
                if (fl == 0) y = f.mass.min.y + 3.35f; // above door lintel band
                if (y + winH * 0.5f > f.mass.max.y - 0.6f) continue;

                for (int c = 0; c < cols; c++)
                {
                    float along = -f.width * 0.5f + (c + 0.5f) * (f.width / cols);
                    // Skip columns that collide with doors on ground floor
                    if (fl == 0 && doorSlots.Any(ds => Mathf.Abs(ds - along) < winW * 0.85f))
                        continue;
                    // Warehouse: skip some for sparse look
                    if (f.style == FacadeStyle.Warehouse && ((c + fl) % 3 == 0)) continue;

                    PlaceWindow(f, along, y, winW, winH, recess, fl, c, rng);

                    // Balconies on a subset of first-floor (index 1) colonial / classic
                    if (fl == 1 && (f.style == FacadeStyle.ColonialPlaster || f.style == FacadeStyle.ClassicShop)
                        && (c + f.seed) % 3 == 0 && f.width > 6f)
                    {
                        PlaceBalcony(f, along, y - winH * 0.5f - 0.05f, winW + 0.35f);
                    }
                }
            }
        }

        static float StyleBelt(FacadeStyle s) => s switch
        {
            FacadeStyle.ArcadeMarket => 0.18f,
            FacadeStyle.ColonialPlaster => 0.16f,
            FacadeStyle.Warehouse => 0.20f,
            _ => 0.12f
        };

        static void PlaceWindow(Face f, float along, float y, float w, float h, float recess, int fl, int c, System.Random rng)
        {
            // CRITICAL: cannot boolean into the mass cube. Build an OUTWARD socket so the
            // reveal faces are visible — dark back at the wall, tunnel projecting out.
            var origin = new Vector3(f.center.x, y, f.center.z) + f.tangent * along + f.normal * 0.02f;
            float depth = Mathf.Clamp(recess + 0.06f, 0.26f, 0.36f);
            float wallT = 0.085f; // reveal wall thickness

            // Dark backplane flush to facade (covers painted window stickers in that bay)
            Box($"WinVoid_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin,
                Along(f, w + 0.06f, h + 0.06f, 0.04f),
                Mats["void"]);

            // Light reveal walls (plaster) so the tunnel reads as wall thickness against dark glass
            float mid = depth * 0.5f;
            var revMat = Mats["plasterCream"];
            Box($"WinRevL_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin + f.tangent * (-w * 0.5f) + f.normal * mid,
                Along(f, wallT, h + wallT, depth), revMat);
            Box($"WinRevR_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin + f.tangent * (w * 0.5f) + f.normal * mid,
                Along(f, wallT, h + wallT, depth), revMat);
            Box($"WinRevT_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin + Vector3.up * (h * 0.5f) + f.normal * mid,
                Along(f, w + wallT, wallT, depth), revMat);
            Box($"WinRevB_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin - Vector3.up * (h * 0.5f) + f.normal * mid,
                Along(f, w + wallT, wallT, depth), revMat);

            // Glass / mullions deep in the pocket (near back)
            Box($"WinGlass_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin + f.normal * 0.05f,
                Along(f, w * 0.86f, h * 0.86f, 0.03f),
                Mats["glass"]);
            Box($"WinMullV_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin + f.normal * 0.07f,
                Along(f, 0.045f, h * 0.84f, 0.03f), Mats["trim"]);
            Box($"WinMullH_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin + f.normal * 0.07f,
                Along(f, w * 0.84f, 0.045f, 0.03f), Mats["trim"]);

            // Outer frame lip at the mouth — FOUR pieces (never a solid slab that seals the hole)
            float lip = 0.10f;
            float fw = 0.10f;
            var mouth = origin + f.normal * (depth + 0.01f);
            Box($"WinLipL_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                mouth + f.tangent * (-w * 0.5f - fw * 0.25f),
                Along(f, fw, h + fw * 2f, lip), Mats["trim"]);
            Box($"WinLipR_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                mouth + f.tangent * (w * 0.5f + fw * 0.25f),
                Along(f, fw, h + fw * 2f, lip), Mats["trim"]);
            Box($"WinLipT_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                mouth + Vector3.up * (h * 0.5f + fw * 0.25f),
                Along(f, w + fw * 2f, fw, lip), Mats["trim"]);
            Box($"WinLipB_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                mouth - Vector3.up * (h * 0.5f + fw * 0.25f),
                Along(f, w + fw * 2f, fw, lip), Mats["trim"]);

            // Projecting sill (below) + lintel (above)
            float sillProud = 0.16f + (float)rng.NextDouble() * 0.08f;
            Box($"WinSill_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin - Vector3.up * (h * 0.5f + 0.07f) + f.normal * (depth * 0.55f + sillProud * 0.35f),
                Along(f, w + 0.30f, 0.09f, depth * 0.7f + sillProud),
                Mats["concrete"]);
            Box($"WinLintel_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin + Vector3.up * (h * 0.5f + 0.12f) + f.normal * (depth * 0.45f),
                Along(f, w + 0.34f, 0.16f, depth * 0.55f + 0.08f),
                Mats["concrete"]);

            // Micro-bevel on sill front edge (light catcher)
            Box($"WinSillBevel_{f.bldg.name}_{Dir(f.normal)}_{fl}_{c}",
                origin - Vector3.up * (h * 0.5f + 0.02f) + f.normal * (depth + sillProud * 0.85f),
                Along(f, w + 0.28f, 0.035f, 0.035f),
                Mats["plasterCream"]);

            _windows++;
        }

        static void PlaceDoor(Face f, float along, FacadeStyle style, System.Random rng)
        {
            float doorW = style == FacadeStyle.ArcadeMarket ? 1.9f : 1.45f;
            float doorH = style == FacadeStyle.ArcadeMarket ? 2.55f : 2.35f;
            float depth = style == FacadeStyle.ArcadeMarket ? 0.48f : 0.38f;
            depth += (float)(rng.NextDouble() * 0.06f);
            depth = Mathf.Clamp(depth, 0.32f, 0.50f);

            var origin = new Vector3(f.center.x, f.mass.min.y + doorH * 0.5f + 0.05f, f.center.z)
                         + f.tangent * along + f.normal * 0.02f;
            float wallT = 0.09f;
            float mid = depth * 0.5f;

            // Dark back + door leaf deep in pocket
            Box($"DoorVoid_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                origin,
                Along(f, doorW + 0.08f, doorH + 0.08f, 0.05f),
                Mats["void"]);

            Box($"DoorRevL_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                origin + f.tangent * (-doorW * 0.5f) + f.normal * mid,
                Along(f, wallT, doorH + wallT, depth), Mats["plasterCream"]);
            Box($"DoorRevR_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                origin + f.tangent * (doorW * 0.5f) + f.normal * mid,
                Along(f, wallT, doorH + wallT, depth), Mats["plasterCream"]);
            Box($"DoorRevT_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                origin + Vector3.up * (doorH * 0.5f) + f.normal * mid,
                Along(f, doorW + wallT, wallT, depth), Mats["plasterCream"]);

            Box($"DoorLeaf_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                origin + f.normal * 0.06f,
                Along(f, doorW * 0.88f, doorH * 0.92f, 0.06f),
                style == FacadeStyle.Warehouse ? Mats["metal"] : Mats["wood"]);

            if (style == FacadeStyle.ArcadeMarket && rng.NextDouble() > 0.4)
            {
                Box($"Shutter_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                    origin + Vector3.up * (doorH * 0.12f) + f.normal * (depth * 0.35f),
                    Along(f, doorW * 0.95f, doorH * 0.55f, 0.05f),
                    Mats["shutter"]);
            }

            // Mouth frame — four pieces so the doorway stays open
            float dfw = 0.11f;
            float dlip = 0.11f;
            var dm = origin + f.normal * (depth + 0.01f);
            Box($"DoorLipL_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                dm + f.tangent * (-doorW * 0.5f - dfw * 0.2f),
                Along(f, dfw, doorH + dfw, dlip), Mats["trim"]);
            Box($"DoorLipR_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                dm + f.tangent * (doorW * 0.5f + dfw * 0.2f),
                Along(f, dfw, doorH + dfw, dlip), Mats["trim"]);
            Box($"DoorLipT_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                dm + Vector3.up * (doorH * 0.5f + dfw * 0.2f),
                Along(f, doorW + dfw * 2f, dfw, dlip), Mats["trim"]);

            Box($"DoorLintel_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                origin + Vector3.up * (doorH * 0.5f + 0.14f) + f.normal * (depth * 0.5f),
                Along(f, doorW + 0.45f, 0.18f, depth * 0.6f + 0.08f),
                Mats["concrete"]);

            _doors++;
        }

        static void PlaceBalcony(Face f, float along, float y, float width)
        {
            float depth = 0.70f;
            var origin = new Vector3(f.center.x, y, f.center.z) + f.tangent * along + f.normal * (depth * 0.5f + 0.02f);

            Box($"BalconySlab_{f.bldg.name}_{Dir(f.normal)}_{along:F1}",
                origin,
                Along(f, width, 0.10f, depth),
                Mats["concrete"]);

            // Simple railing posts + top rail
            float railH = 0.85f;
            int posts = 4;
            for (int i = 0; i < posts; i++)
            {
                float t = -0.5f + i / (float)(posts - 1);
                Box($"BalconyPost_{f.bldg.name}_{i}_{along:F1}",
                    origin + f.tangent * (t * width * 0.9f) + f.normal * (depth * 0.42f) + Vector3.up * (railH * 0.5f),
                    Along(f, 0.04f, railH, 0.04f),
                    Mats["rail"]);
            }
            Box($"BalconyRail_{f.bldg.name}_{along:F1}",
                origin + f.normal * (depth * 0.42f) + Vector3.up * railH,
                Along(f, width * 0.95f, 0.04f, 0.04f),
                Mats["rail"]);
            // Side rails
            Box($"BalconySideL_{f.bldg.name}_{along:F1}",
                origin - f.tangent * (width * 0.45f) + Vector3.up * (railH * 0.5f),
                Along(f, 0.04f, railH, depth * 0.9f),
                Mats["rail"]);
            Box($"BalconySideR_{f.bldg.name}_{along:F1}",
                origin + f.tangent * (width * 0.45f) + Vector3.up * (railH * 0.5f),
                Along(f, 0.04f, railH, depth * 0.9f),
                Mats["rail"]);

            _balconies++;
        }

        static void AddRoofParapet(Bounds mass, FacadeStyle style)
        {
            float parapetH = style == FacadeStyle.Warehouse ? 0.55f : 0.40f;
            float corniceProud = style == FacadeStyle.ColonialPlaster ? 0.28f : 0.20f;
            float top = mass.max.y;

            // Parapet wall sitting on roof
            Box($"Parapet_{mass.center.x:F0}_{mass.center.z:F0}",
                new Vector3(mass.center.x, top + parapetH * 0.5f, mass.center.z),
                new Vector3(mass.size.x + 0.12f, parapetH, mass.size.z + 0.12f),
                Mats["concreteDark"]);

            // Projecting cornice lip under parapet (breaks knife edge)
            Box($"Cornice_{mass.center.x:F0}_{mass.center.z:F0}",
                new Vector3(mass.center.x, top + 0.06f, mass.center.z),
                new Vector3(mass.size.x + corniceProud * 2f, 0.14f, mass.size.z + corniceProud * 2f),
                Mats["concrete"]);

            // Outer bevel strip (chamfer proxy on roof perimeter)
            float bevel = 0.03f;
            Box($"RoofBevel_{mass.center.x:F0}_{mass.center.z:F0}",
                new Vector3(mass.center.x, top + 0.02f, mass.center.z),
                new Vector3(mass.size.x + corniceProud * 2f + bevel, 0.05f, mass.size.z + corniceProud * 2f + bevel),
                Mats["plasterCream"]);
        }

        static void AddCornerTreatment(Bounds mass, FacadeStyle style)
        {
            // Vertical corner quoins / chamfer catches — 4 corners of footprint
            float qW = style == FacadeStyle.ColonialPlaster ? 0.42f : 0.28f;
            float qProud = 0.06f;
            float bevel = 0.03f; // 30mm chamfer proxy
            var corners = new[]
            {
                new Vector3(mass.min.x, 0f, mass.min.z),
                new Vector3(mass.max.x, 0f, mass.min.z),
                new Vector3(mass.min.x, 0f, mass.max.z),
                new Vector3(mass.max.x, 0f, mass.max.z),
            };

            for (int i = 0; i < 4; i++)
            {
                var c = corners[i];
                var outward = new Vector3(
                    Mathf.Sign(c.x - mass.center.x),
                    0f,
                    Mathf.Sign(c.z - mass.center.z)).normalized;

                if (style == FacadeStyle.ColonialPlaster || style == FacadeStyle.ClassicShop)
                {
                    // Stacked quoin blocks
                    int blocks = Mathf.Clamp(Mathf.FloorToInt(mass.size.y / 0.55f), 4, 14);
                    for (int b = 0; b < blocks; b++)
                    {
                        float y = mass.min.y + 0.3f + b * 0.55f;
                        if (y > mass.max.y - 0.2f) break;
                        float alt = (b % 2 == 0) ? qW : qW * 0.7f;
                        Box($"Quoin_{mass.center.x:F0}_{mass.center.z:F0}_{i}_{b}",
                            new Vector3(c.x, y, c.z) + outward * qProud,
                            new Vector3(alt, 0.48f, alt),
                            (b % 3 == 0) ? Mats["concrete"] : Mats["plasterCream"]);
                    }
                }

                // Chamfer catch strip — thin diagonal-feel vertical edge highlight
                Box($"Chamfer_{mass.center.x:F0}_{mass.center.z:F0}_{i}",
                    new Vector3(c.x, mass.center.y, c.z) + outward * (bevel + 0.01f),
                    new Vector3(bevel * 1.4f, mass.size.y * 0.96f, bevel * 1.4f),
                    Mats["plasterCream"]);
            }

            // Top horizontal edge chamfers along each side (light catchers)
            float yTop = mass.max.y - 0.02f;
            Box($"EdgeChamferN_{mass.center.x:F0}",
                new Vector3(mass.center.x, yTop, mass.max.z + bevel),
                new Vector3(mass.size.x, bevel * 1.2f, bevel * 1.2f), Mats["plasterCream"]);
            Box($"EdgeChamferS_{mass.center.x:F0}",
                new Vector3(mass.center.x, yTop, mass.min.z - bevel),
                new Vector3(mass.size.x, bevel * 1.2f, bevel * 1.2f), Mats["plasterCream"]);
            Box($"EdgeChamferE_{mass.center.z:F0}",
                new Vector3(mass.max.x + bevel, yTop, mass.center.z),
                new Vector3(bevel * 1.2f, bevel * 1.2f, mass.size.z), Mats["plasterCream"]);
            Box($"EdgeChamferW_{mass.center.z:F0}",
                new Vector3(mass.min.x - bevel, yTop, mass.center.z),
                new Vector3(bevel * 1.2f, bevel * 1.2f, mass.size.z), Mats["plasterCream"]);
        }

        // size: (along tangent, up, along normal)
        static Vector3 Along(Face f, float along, float up, float outward)
        {
            bool xz = Mathf.Abs(f.normal.x) > 0.5f;
            return xz ? new Vector3(outward, up, along) : new Vector3(along, up, outward);
        }

        static string Dir(Vector3 n)
        {
            if (n.z > 0.5f) return "N";
            if (n.z < -0.5f) return "S";
            if (n.x > 0.5f) return "E";
            return "W";
        }

        static GameObject Box(string name, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(_root, true);
            go.transform.position = pos;
            go.transform.localScale = size;
            Object.DestroyImmediate(go.GetComponent<Collider>()); // NEVER colliders on trim
            var r = go.GetComponent<MeshRenderer>();
            if (r != null && mat != null)
            {
                r.sharedMaterial = mat;
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows = true;
            }
            go.isStatic = true;
            _boxes++;
            return go;
        }

        static void SetStaticRecursive(GameObject go)
        {
            go.isStatic = true;
            foreach (Transform c in go.transform)
                SetStaticRecursive(c.gameObject);
        }
    }
}
#endif
