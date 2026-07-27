#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Places imported Zzz CC0 assets into Arena: wrecks, barriers, litter, roof/facade, cloth, graffiti.
    /// Idempotent under ThreeLaneMap/ZZZ_Placement. Does not touch player, grade, or ArenaDresser.
    /// Menu: Arena FPS / AAA Zzz Placement Pass
    /// </summary>
    public static class AaaZzzPlacementPass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string RootName = "ZZZ_Placement";
        const string MatDir = "Assets/_Project/Art/Materials/Zzz";
        const string ImportRoot = "Assets/_Project/Art/Imported/Zzz";
        const string ReportPath = "_research/zzz_placement_log.txt";
        const string ShotDir = "_research/critique/placed_after";
        static readonly Vector3 SpawnClear = new(0f, 0.05f, -63f);

        static readonly System.Random Rng = new(20260727);
        static Transform _root;
        static Transform _map;
        static readonly StringBuilder Log = new();
        static long _trisBefore;
        static int _carsPlaced, _barriersPlaced, _litterPlaced, _roofPlaced, _clothPlaced, _graffitiPlaced;
        static readonly List<string> CarAssignments = new();

        // ── Menu ──────────────────────────────────────────────────────────────

        [MenuItem("Arena FPS/AAA Zzz Placement Pass/Run All Phases")]
        public static void RunAll()
        {
            if (!EnsureEditMode()) return;
            OpenArena();
            EnsureRoot(clear: true);
            _trisBefore = CountSceneTris();
            Log.Clear();
            Log.AppendLine($"trisBefore={_trisBefore}");
            EnsureVehicleMaterials();
            Phase1_ReplaceCars();
            Phase2_ReplaceBarriers();
            Phase3_GroundClutter();
            Phase4_RoofFacade();
            Phase5_Cloth();
            Phase6_Graffiti();
            FinalizeAndSave("ALL");
        }

        [MenuItem("Arena FPS/AAA Zzz Placement Pass/Phase 1 Cars Only")]
        public static void RunPhase1()
        {
            if (!EnsureEditMode()) return;
            OpenArena();
            EnsureRoot(clear: false);
            ClearTagged("ZZZ_Car_");
            _trisBefore = CountSceneTris();
            Log.Clear();
            Log.AppendLine($"trisBefore={_trisBefore}");
            EnsureVehicleMaterials();
            Phase1_ReplaceCars();
            FinalizeAndSave("P1");
        }

        [MenuItem("Arena FPS/AAA Zzz Placement Pass/Phase 2 Barriers Only")]
        public static void RunPhase2()
        {
            if (!EnsureEditMode()) return;
            OpenArena();
            EnsureRoot(clear: false);
            ClearTagged("ZZZ_Barrier_");
            Phase2_ReplaceBarriers();
            FinalizeAndSave("P2");
        }

        [MenuItem("Arena FPS/AAA Zzz Placement Pass/Phase 3 Litter Only")]
        public static void RunPhase3()
        {
            if (!EnsureEditMode()) return;
            OpenArena();
            EnsureRoot(clear: false);
            ClearTagged("ZZZ_Litter_");
            ClearTagged("ZZZ_Debris_");
            ClearTagged("ZZZ_Rock_");
            Phase3_GroundClutter();
            FinalizeAndSave("P3");
        }

        [MenuItem("Arena FPS/AAA Zzz Placement Pass/Phase 4 Roof Facade Only")]
        public static void RunPhase4()
        {
            if (!EnsureEditMode()) return;
            OpenArena();
            EnsureRoot(clear: false);
            ClearTagged("ZZZ_Roof_");
            ClearTagged("ZZZ_Facade_");
            Phase4_RoofFacade();
            FinalizeAndSave("P4");
        }

        [MenuItem("Arena FPS/AAA Zzz Placement Pass/Phase 5 Cloth Only")]
        public static void RunPhase5()
        {
            if (!EnsureEditMode()) return;
            OpenArena();
            EnsureRoot(clear: false);
            ClearTagged("ZZZ_Cloth_");
            Phase5_Cloth();
            FinalizeAndSave("P5");
        }

        [MenuItem("Arena FPS/AAA Zzz Placement Pass/Phase 6 Graffiti Only")]
        public static void RunPhase6()
        {
            if (!EnsureEditMode()) return;
            OpenArena();
            EnsureRoot(clear: false);
            ClearTagged("ZZZ_Graffiti_");
            Phase6_Graffiti();
            FinalizeAndSave("P6");
        }

        [MenuItem("Arena FPS/AAA Zzz Placement Pass/Verify Screenshots")]
        public static void RunVerify()
        {
            if (!EnsureEditMode()) return;
            OpenArena();
            CaptureAndMeasure();
            WriteLog("VERIFY");
        }

        // ── Phase 1: Cars ─────────────────────────────────────────────────────

        static void Phase1_ReplaceCars()
        {
            _carsPlaced = 0;
            CarAssignments.Clear();

            var placeholders = FindPlaceholderCars();
            Log.AppendLine($"placeholderCarsFound={placeholders.Count}");

            // 4 usable vehicle kinds (burned sedan, burned crossover, junk, red wreck)
            var kinds = new[] { "burned_sedan", "burned_crossover", "junk", "red_wreck" };

            // Prefer LOD0 near spawn / mid map, LOD1 for distant
            for (int i = 0; i < placeholders.Count; i++)
            {
                var ph = placeholders[i];
                var worldPos = ph.transform.position;
                var worldRot = ph.transform.rotation;
                var footprint = CaptureFootprint(ph);

                string kind = kinds[i % kinds.Length];
                // Extra variation: every 5th swap kind
                if (i % 5 == 4) kind = kinds[(i + 2) % kinds.Length];

                bool distant = Vector3.Distance(new Vector3(worldPos.x, 0, worldPos.z), Vector3.zero) > 38f
                               || Mathf.Abs(worldPos.z) > 40f;

                // Preserve roughly the placeholder yaw, add settle tilt
                float yawJitter = (float)(Rng.NextDouble() * 24.0 - 12.0);
                float pitchSettle = (float)(Rng.NextDouble() * 4.0 - 1.0);
                float rollSettle = (float)(Rng.NextDouble() * 6.0 - 3.0);
                var rot = Quaternion.Euler(pitchSettle, worldRot.eulerAngles.y + yawJitter, rollSettle);

                Object.DestroyImmediate(ph);
                var go = SpawnVehicle(kind, distant, worldPos, rot, footprint, i);
                if (go == null)
                {
                    Log.AppendLine($"FAIL car slot {i} kind={kind}");
                    continue;
                }

                // Soft dust tint variation (instance materials)
                TintVariation(go, i);
                SeatToGround(go.transform);
                // Keep spawn clear
                if (Vector3.Distance(new Vector3(go.transform.position.x, 0, go.transform.position.z),
                        new Vector3(SpawnClear.x, 0, SpawnClear.z)) < 6f)
                {
                    Log.AppendLine($"WARN car near spawn moved aside: {go.name}");
                    go.transform.position += new Vector3(4f, 0f, 2f);
                    SeatToGround(go.transform);
                }

                SetStatic(go);
                _carsPlaced++;
                CarAssignments.Add($"{go.name} <- {kind} pos={go.transform.position} distant={distant}");
            }

            // Gate: zero placeholders remaining
            var left = FindPlaceholderCars();
            Log.AppendLine($"carsPlaced={_carsPlaced} placeholdersRemaining={left.Count}");
            foreach (var a in CarAssignments) Log.AppendLine("  " + a);
        }

        static List<GameObject> FindPlaceholderCars()
        {
            var list = new List<GameObject>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var n = t.name;
                if (n.StartsWith("OP_Car_", StringComparison.Ordinal)
                    || n.StartsWith("OP_FrameCar_", StringComparison.Ordinal)
                    || n.StartsWith("OD_Car_", StringComparison.Ordinal)
                    || n == "Prop_BoatCar" || n == "Prop_BeachCar"
                    || n == "Prop_BlueMainCar" || n == "Prop_BlueMainCar2"
                    || n == "Prop_MidVan" || n == "Prop_RedMainCar")
                {
                    list.Add(t.gameObject);
                }
            }
            // Stable order by name then position
            return list.OrderBy(g => g.name).ThenBy(g => g.transform.position.z).ToList();
        }

        static Bounds CaptureFootprint(GameObject ph)
        {
            var box = ph.GetComponentInChildren<BoxCollider>();
            if (box != null) return new Bounds(box.bounds.center, box.bounds.size);
            return BoundsOf(ph);
        }

        static GameObject SpawnVehicle(string kind, bool distant, Vector3 pos, Quaternion rot, Bounds footprint, int index)
        {
            GameObject go = null;
            string name = $"ZZZ_Car_{index:00}_{kind}";

            switch (kind)
            {
                case "burned_sedan":
                    go = InstantiateBurnedChild("default001", name);
                    break;
                case "burned_crossover":
                    go = InstantiateBurnedChild("default002", name);
                    break;
                case "junk":
                    go = InstantiatePrefab(
                        $"{ImportRoot}/vehicles/abandoned-junk-car/source/SM_JUNKCAR1_DEFORMED2.fbx", name);
                    if (go != null)
                    {
                        go.transform.localScale = Vector3.one * 100f; // FBX stored in cm
                        ApplyJunkMaterials(go);
                    }
                    break;
                case "red_wreck":
                {
                    string lod = distant
                        ? $"{ImportRoot}/vehicles/red_car_wreck_rescued/red_renault_wreck_LOD1.fbx"
                        : $"{ImportRoot}/vehicles/red_car_wreck_rescued/red_renault_wreck_LOD0.fbx";
                    go = InstantiatePrefab(lod, name);
                    if (go != null)
                    {
                        // Near-cubic + baked ground plane — do NOT fit by bbox. Body ~1.5m tall.
                        go.transform.localScale = Vector3.one * 0.82f;
                        EnsureRedMaterials(go);
                    }
                    break;
                }
            }

            if (go == null) return null;

            go.transform.SetParent(_root, true);
            go.transform.SetPositionAndRotation(pos, rot);

            // Cover collider sized to prior footprint (gameplay cover)
            foreach (var c in go.GetComponentsInChildren<Collider>())
                Object.DestroyImmediate(c);

            var cover = go.AddComponent<BoxCollider>();
            // After seating we'll refresh; set approximate now from footprint xz + vehicle height
            var b = BoundsOf(go);
            float height = Mathf.Clamp(b.size.y, 1.1f, 2.0f);
            float sx = Mathf.Max(footprint.size.x, Mathf.Min(b.size.x, 5.5f));
            float sz = Mathf.Max(footprint.size.z, Mathf.Min(b.size.z, 5.5f));
            // Prefer measured mesh if footprint was degenerate
            if (sx < 0.5f) sx = Mathf.Clamp(b.size.x, 2.2f, 5.0f);
            if (sz < 0.5f) sz = Mathf.Clamp(b.size.z, 1.4f, 3.5f);
            cover.center = go.transform.InverseTransformPoint(new Vector3(b.center.x, b.min.y + height * 0.5f, b.center.z));
            var localSize = go.transform.InverseTransformVector(new Vector3(sx, height, sz));
            cover.size = new Vector3(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z));

            return go;
        }

        static GameObject InstantiateBurnedChild(string childName, string name)
        {
            const string path = ImportRoot + "/vehicles/burned-out-cars/source/Burnedcars/burnedcars.FBX";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Log.AppendLine("MISSING burnedcars.FBX"); return null; }

            // Extract one sub-car. Mesh length is authored along local Y, so lay flat with -90° X,
            // then fit longest horizontal axis to a real sedan/crossover length.
            var full = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (full == null) full = Object.Instantiate(prefab);
            full.name = name;
            full.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            full.transform.localScale = Vector3.one;

            Transform keep = null;
            foreach (Transform t in full.transform.Cast<Transform>().ToArray())
            {
                if (t.name == childName) keep = t;
                else Object.DestroyImmediate(t.gameObject);
            }
            if (keep == null)
            {
                Object.DestroyImmediate(full);
                Log.AppendLine("MISSING burned child " + childName);
                return null;
            }

            full.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            var b = BoundsOf(full);
            keep.position += new Vector3(-b.center.x, 0f, -b.center.z);

            b = BoundsOf(full);
            float longestHz = Mathf.Max(b.size.x, b.size.z);
            float target = childName == "default002" ? 4.6f : 4.4f;
            if (longestHz > 0.1f)
                full.transform.localScale = Vector3.one * (target / longestHz);

            b = BoundsOf(full);
            full.transform.position += Vector3.up * (0.02f - b.min.y);
            return full;
        }

        static void SeatLocalMeshToOrigin(GameObject go)
        {
            var b = BoundsOf(go);
            var delta = new Vector3(-b.center.x, -b.min.y, -b.center.z);
            foreach (Transform t in go.transform)
                t.position += delta;
        }

        // ── Phase 2: Barriers ─────────────────────────────────────────────────

        static void Phase2_ReplaceBarriers()
        {
            _barriersPlaced = 0;
            const string barrierPath = ImportRoot + "/props/weathered_concrete_barriers_fbx/weathered_concrete_barriers_fbx.fbx";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(barrierPath);
            if (prefab == null) { Log.AppendLine("MISSING barriers fbx"); return; }

            // Only jersey-like street cover — skip stairs, sandbags, lids, dumpsters
            var targets = new List<GameObject>();
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || mf.sharedMesh.name != "Cube") continue;
                var mat = r.sharedMaterial;
                if (mat == null) continue;
                string mn = mat.name.Replace(" (Instance)", "");
                if (mn != "Mat_Concrete" && mn != "P2_ConcreteVaried") continue;

                string path = PathOf(r.transform);
                // Facade geometry owned by other agents
                if (path.Contains("/FD_") || path.Contains("FD_") || path.Contains("OD_Trim") || path.Contains("OD_Glass"))
                    continue;
                if (path.Contains("Sandbag") || path.Contains("Stairs") || path.Contains("/Lid")
                    || path.Contains("Dumpster") || path.Contains("Step_"))
                    continue;

                var b = r.bounds;
                // Jersey-ish: ~0.7-1.4m tall, not building slabs
                bool jerseyish = b.size.y >= 0.7f && b.size.y <= 1.6f
                                 && b.center.y < 2.2f
                                 && Mathf.Max(b.size.x, b.size.z) >= 0.8f
                                 && Mathf.Max(b.size.x, b.size.z) <= 4.5f
                                 && Mathf.Min(b.size.x, b.size.z) <= 1.4f;
                bool namedJersey = r.transform.name.Contains("Jersey")
                                   || (r.transform.parent != null && r.transform.parent.name.Contains("Jersey"));
                if (!jerseyish && !namedJersey) continue;

                // Pick the top-most cover object (parent if named Cover_Jersey)
                var go = r.gameObject;
                if (r.transform.parent != null && r.transform.parent.name.StartsWith("Cover_Jersey", StringComparison.Ordinal))
                    go = r.transform.parent.gameObject;
                if (!targets.Contains(go)) targets.Add(go);
            }

            // Collapse nested jersey children into unique roots; destroying a parent
            // otherwise invalidates child entries mid-loop.
            var unique = new List<GameObject>();
            foreach (var go in targets.OrderByDescending(g => PathOf(g.transform).Length))
            {
                if (go == null) continue;
                bool underKept = unique.Any(u => u != null && go.transform.IsChildOf(u.transform));
                if (underKept) continue;
                // Prefer Cover_Jersey parent when present
                var use = go;
                if (go.transform.parent != null && go.transform.parent.name.StartsWith("Cover_Jersey", StringComparison.Ordinal))
                    use = go.transform.parent.gameObject;
                if (!unique.Contains(use)) unique.Add(use);
            }
            Log.AppendLine($"barrierTargets={targets.Count} unique={unique.Count}");

            int idx = 0;
            foreach (var old in unique)
            {
                if (old == null) continue;
                var pos = old.transform.position;
                var yaw = old.transform.eulerAngles.y;
                var oldBounds = BoundsOf(old);
                // Prefer longest axis along old longest horizontal
                bool red = (idx % 2 == 0);
                Object.DestroyImmediate(old);

                var go = ExtractBarrierVariant(prefab, red, $"ZZZ_Barrier_{idx:00}_{(red ? "red" : "yellow")}");
                if (go == null) continue;
                go.transform.SetParent(_root, true);

                // Fit length to old cover length
                float targetLen = Mathf.Clamp(Mathf.Max(oldBounds.size.x, oldBounds.size.z), 1.2f, 3.5f);
                var nb = BoundsOf(go);
                float curLen = Mathf.Max(nb.size.x, nb.size.z);
                float s = curLen > 0.01f ? targetLen / curLen : 1f;
                go.transform.localScale = Vector3.one * s;
                go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw + (float)(Rng.NextDouble() * 10 - 5), 0f));
                SeatToGround(go.transform);

                foreach (var c in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
                var box = go.AddComponent<BoxCollider>();
                var b = BoundsOf(go);
                box.center = go.transform.InverseTransformPoint(b.center);
                var ls = go.transform.InverseTransformVector(b.size);
                box.size = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));

                SetStatic(go);
                _barriersPlaced++;
                idx++;
            }
            Log.AppendLine($"barriersPlaced={_barriersPlaced}");
        }

        static GameObject ExtractBarrierVariant(GameObject prefab, bool red, string name)
        {
            var full = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (full == null) full = Object.Instantiate(prefab);
            string want = red ? "concrete_barriers_barrier_red" : "concrete_barriers_barrier_yellow";
            Transform keep = null;
            foreach (var t in full.GetComponentsInChildren<Transform>(true))
                if (t.name == want) { keep = t; break; }
            if (keep == null) { Object.DestroyImmediate(full); return null; }

            var go = new GameObject(name);
            var clone = Object.Instantiate(keep.gameObject, go.transform, false);
            clone.name = want;
            // Children have localScale 100 (cm FBX) — keep it; world bounds already correct at parent scale 1
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            Object.DestroyImmediate(full);
            SeatLocalMeshToOrigin(go);
            return go;
        }

        // ── Phase 3: Litter ───────────────────────────────────────────────────

        static void Phase3_GroundClutter()
        {
            _litterPlaced = 0;
            var trashPaths = AssetDatabase.FindAssets("t:Model", new[] { $"{ImportRoot}/props/urban_trash_low" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p)
                .ToList();
            Log.AppendLine($"trashMeshes={trashPaths.Count}");

            var debrisPath = $"{ImportRoot}/props/military_trenches_debris_pile_rock_s_ydyqbbls_mid/Military_Trenches_Debris_Pile_Rock_S_ydyqbbls_Mid.fbx";
            var rockPath = $"{ImportRoot}/props/rock_sandstone_rkhtq_mid/Rock_Sandstone_rkhtq_Mid.fbx";

            // Anchor points: near every ZZZ car, along map lanes (kerbs), alley corners — not open road center
            var anchors = new List<Vector3>();
            foreach (Transform t in _root)
            {
                if (t.name.StartsWith("ZZZ_Car_", StringComparison.Ordinal))
                {
                    var b = BoundsOf(t.gameObject);
                    anchors.Add(b.center + new Vector3(b.extents.x + 0.6f, 0, 0));
                    anchors.Add(b.center + new Vector3(-(b.extents.x + 0.55f), 0, 0.4f));
                    anchors.Add(b.center + new Vector3(0.2f, 0, b.extents.z + 0.7f));
                }
            }

            // Lane kerb lines (three-lane map roughly x=±8, ±20, ±36; z -55..55)
            float[] kerbXs = { -37.5f, -21.5f, -9.5f, 9.5f, 21.5f, 37.5f };
            foreach (float x in kerbXs)
            {
                for (float z = -52f; z <= 52f; z += 6.5f)
                {
                    float jz = (float)(Rng.NextDouble() * 2.0 - 1.0);
                    float jx = (float)(Rng.NextDouble() * 1.2 - 0.6);
                    anchors.Add(new Vector3(x + jx, 0f, z + jz));
                }
            }

            // Alley / corner pockets
            Vector3[] corners =
            {
                new(-14f,0,-44f), new(14f,0,-44f), new(-14f,0,-20f), new(14f,0,-20f),
                new(-14f,0,2f), new(14f,0,2f), new(-14f,0,24f), new(14f,0,24f),
                new(-14f,0,44f), new(14f,0,44f),
                new(-28f,0,-30f), new(28f,0,-30f), new(-28f,0,10f), new(28f,0,10f),
                new(-28f,0,35f), new(28f,0,35f), new(0f,0,50f), new(0f,0,-50f),
                new(-40f,0,-10f), new(40f,0,-10f), new(-40f,0,20f), new(40f,0,20f),
            };
            anchors.AddRange(corners);

            int ti = 0;
            foreach (var a in anchors)
            {
                if (IsSpawnBlocked(a)) continue;
                // Skip open center of main road
                if (Mathf.Abs(a.x) < 3.2f && Mathf.Abs(a.z) < 55f) continue;

                string path = trashPaths[ti % trashPaths.Count];
                ti++;
                var go = InstantiatePrefab(path, $"ZZZ_Litter_{_litterPlaced:000}");
                if (go == null) continue;
                go.transform.SetParent(_root, true);
                float s = FitLongestAxis(go, 0.35f + (float)Rng.NextDouble() * 0.85f, 0.15f, 2.2f);
                go.transform.localScale = Vector3.one * s;
                go.transform.SetPositionAndRotation(
                    a + new Vector3((float)(Rng.NextDouble() - 0.5) * 0.8f, 0, (float)(Rng.NextDouble() - 0.5) * 0.8f),
                    Quaternion.Euler(0, Rng.Next(360), (float)(Rng.NextDouble() * 8 - 4)));
                ApplyTrashMaterial(go, path);
                StripColliders(go); // small litter — no collision (or tiny)
                // Only add collider if piece is large enough to be cover-ish
                var b = BoundsOf(go);
                if (Mathf.Max(b.size.x, b.size.z) > 1.1f && b.size.y > 0.45f)
                {
                    var box = go.AddComponent<BoxCollider>();
                    box.center = go.transform.InverseTransformPoint(b.center);
                    var ls = go.transform.InverseTransformVector(b.size);
                    box.size = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                }
                SeatToGround(go.transform);
                SetStatic(go);
                _litterPlaced++;
            }

            // Debris piles + rocks around map (sparser)
            PlaceScaledProp(debrisPath, "ZZZ_Debris_", 18, 1.4f, 2.8f, true);
            PlaceScaledProp(rockPath, "ZZZ_Rock_", 14, 0.6f, 1.6f, true);

            Log.AppendLine($"litterPlaced={_litterPlaced}");
        }

        static void PlaceScaledProp(string path, string prefix, int count, float minAxis, float maxAxis, bool withCollider)
        {
            var slots = ScatterSlots(count);
            int i = 0;
            foreach (var p in slots)
            {
                if (IsSpawnBlocked(p)) continue;
                var go = InstantiatePrefab(path, $"{prefix}{i:00}");
                if (go == null) continue;
                go.transform.SetParent(_root, true);
                float target = minAxis + (float)Rng.NextDouble() * (maxAxis - minAxis);
                float s = FitLongestAxis(go, target, minAxis * 0.5f, maxAxis * 1.5f);
                go.transform.localScale = Vector3.one * s;
                go.transform.SetPositionAndRotation(p, Quaternion.Euler(0, Rng.Next(360), 0));
                ApplyMatIfExists(go, Path.GetFileNameWithoutExtension(Path.GetDirectoryName(path)));
                StripColliders(go);
                if (withCollider)
                {
                    var b = BoundsOf(go);
                    var box = go.AddComponent<BoxCollider>();
                    box.center = go.transform.InverseTransformPoint(b.center);
                    var ls = go.transform.InverseTransformVector(b.size);
                    box.size = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                }
                SeatToGround(go.transform);
                // Centre-pivoted rock: SeatToGround handles via bounds.min
                SetStatic(go);
                i++;
                _litterPlaced++;
            }
        }

        // ── Phase 4: Roof / facade ────────────────────────────────────────────

        static void Phase4_RoofFacade()
        {
            _roofPlaced = 0;
            var props = new (string path, string tag, float targetAxis, bool centrePivot)[]
            {
                ($"{ImportRoot}/props/cc0-antenna/source/Anchor.fbx", "antenna", 1.8f, true),
                ($"{ImportRoot}/vehicles/source_gltf/scene.gltf", "antenna2", 1.2f, true),
                ($"{ImportRoot}/props/metal_water_tank_wdklears_low/Metal_Water_Tank_wdklears_Low.fbx", "tank", 2.4f, true),
                ($"{ImportRoot}/props/electrical-transformer/source/scetchfab/scetchfab/transformator.fbx", "xformer", 1.6f, true),
                ($"{ImportRoot}/props/power-transformer/source/scetchfab/scetchfab/Oru_transformator.fbx", "pxformer", 1.8f, true),
                ($"{ImportRoot}/props/electrical_box_uiohbdnfa_low/Electrical_Box_uiohbdnfa_Low.fbx", "ebox", 0.7f, true),
                ($"{ImportRoot}/props/modular_building_balcony_ukjsdavdw_low/Modular_Building_Balcony_ukjsdavdw_Low.fbx", "balcony", 2.2f, false),
                (FindAssetPath("source_gltf_1", ".gltf", ".fbx", ".glb") ?? $"{ImportRoot}/vehicles/source_gltf_1/scene.gltf", "ac", 0.9f, false),
            };

            // Sample building rooftops: find high flat renderers (y>4) that look like roofs
            var roofPoints = new List<Vector3>();
            var facadePoints = new List<(Vector3 pos, Vector3 normal)>();
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var b = r.bounds;
                string path = PathOf(r.transform);
                if (path.StartsWith("ZZZ_", StringComparison.Ordinal) || path.Contains("/ZZZ_")) continue;
                if (path.Contains("Player")) continue;

                // Roof candidates
                if (b.min.y > 4.5f && b.size.y < 1.8f && b.size.x > 2f && b.size.z > 2f && b.center.y < 18f)
                {
                    roofPoints.Add(new Vector3(b.center.x, b.max.y, b.center.z));
                    if (roofPoints.Count > 80) break;
                }
            }

            // Facade AC mounts: walls facing lanes at ~2.5-5m height
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var b = r.bounds;
                string path = PathOf(r.transform);
                if (!(path.Contains("FD_") || path.Contains("OD_") || path.Contains("PB_") || path.Contains("CK_"))) continue;
                if (b.size.y < 2f || b.size.y > 20f) continue;
                // Side facing street (near |x| lane edges)
                float faceX = Mathf.Abs(b.center.x) > 5f ? Mathf.Sign(b.center.x) * -1f : 0f;
                if (Mathf.Abs(faceX) < 0.1f) continue;
                var pos = new Vector3(b.center.x + faceX * (b.extents.x + 0.05f), Mathf.Clamp(b.center.y, 2.4f, 5.5f), b.center.z);
                facadePoints.Add((pos, new Vector3(faceX, 0, 0)));
                if (facadePoints.Count > 60) break;
            }

            // Dedup roof points
            roofPoints = Dilute(roofPoints, 4.5f);
            facadePoints = facadePoints.OrderBy(_ => Rng.Next()).Take(40).ToList();

            int pi = 0;
            foreach (var rp in roofPoints)
            {
                var spec = props[pi % 6]; // roof props only (first 6)
                pi++;
                var go = InstantiatePrefab(spec.path, $"ZZZ_Roof_{_roofPlaced:00}_{spec.tag}");
                if (go == null) continue;
                go.transform.SetParent(_root, true);
                float s = FitLongestAxis(go, spec.targetAxis, spec.targetAxis * 0.4f, spec.targetAxis * 2.2f);
                go.transform.localScale = Vector3.one * s;
                var b = BoundsOf(go);
                Vector3 pos = rp;
                if (!spec.centrePivot)
                    pos.y = rp.y - b.min.y + go.transform.position.y;
                else
                    pos.y = rp.y + (b.extents.y); // sit on roof for centre pivot roughly
                // Better: place then seat relative to roof Y
                go.transform.SetPositionAndRotation(new Vector3(rp.x, rp.y, rp.z), Quaternion.Euler(0, Rng.Next(360), 0));
                // Lift so minY = roofY
                b = BoundsOf(go);
                go.transform.position += Vector3.up * (rp.y - b.min.y + 0.02f);
                ApplyMatIfExists(go, MatNameFromPath(spec.path));
                StripColliders(go); // above head — no collider
                SetStatic(go);
                _roofPlaced++;
            }

            int fi = 0;
            foreach (var (pos, normal) in facadePoints)
            {
                bool ac = fi % 3 != 2;
                string path = ac
                    ? (FindAssetPath("source_gltf_1", ".gltf", ".fbx", ".glb") ?? "")
                    : $"{ImportRoot}/props/electrical_box_uiohbdnfa_low/Electrical_Box_uiohbdnfa_Low.fbx";
                string tag = ac ? "ac" : "ebox";
                if (string.IsNullOrEmpty(path)) { fi++; continue; }
                var go = InstantiatePrefab(path, $"ZZZ_Facade_{fi:00}_{tag}");
                fi++;
                if (go == null) continue;
                go.transform.SetParent(_root, true);
                float target = ac ? 0.85f : 0.55f;
                float s = FitLongestAxis(go, target, 0.3f, 1.6f);
                go.transform.localScale = Vector3.one * s;
                var look = Quaternion.LookRotation(normal);
                go.transform.SetPositionAndRotation(pos, look);
                go.transform.position = pos - normal * 0.02f;
                ApplyMatIfExists(go, ac ? "source_gltf_1" : "electrical_box_uiohbdnfa_low");
                StripColliders(go);
                SetStatic(go);
                _roofPlaced++;
            }

            Log.AppendLine($"roofFacadePlaced={_roofPlaced} roofPts={roofPoints.Count} facadePts={facadePoints.Count}");
        }

        // ── Phase 5: Cloth ────────────────────────────────────────────────────

        static void Phase5_Cloth()
        {
            _clothPlaced = 0;
            var cloth = new (string path, string matKey, float axis)[]
            {
                ($"{ImportRoot}/cloth/cc0-awning/source/Awning.fbx", "cc0-awning", 3.0f),
                ($"{ImportRoot}/cloth/canopy-cloth-and-wood-stand/source/Canopy.fbx", "canopy-cloth-and-wood-stand", 3.2f),
                ($"{ImportRoot}/cloth/wrinkled_tarp_vh2mee1_low/Wrinkled_Tarp_vh2mee1_Low.fbx", "wrinkled_tarp_vh2mee1_low", 2.4f),
                ($"{ImportRoot}/cloth/wrinkled_tarp_vhtidbb_low/Wrinkled_Tarp_vhtidbb_Low.fbx", "wrinkled_tarp_vhtidbb_low", 2.4f),
                ($"{ImportRoot}/cloth/wrinkled_tarp_vieldbo_low/Wrinkled_Tarp_vieldbo_Low.fbx", "wrinkled_tarp_vieldbo_low", 2.4f),
                ($"{ImportRoot}/cloth/tarped_crate_vh3lbfy_low/Tarped_Crate_vh3lbfy_Low.fbx", "tarped_crate_vh3lbfy_low", 1.2f),
                ($"{ImportRoot}/cloth/industrial_junkyard_storage_pallet_wood_tarp_xiwcfassc_low/Industrial_Junkyard_Storage_Pallet_Wood_Tarp_xiwcfassc_Low.fbx",
                    "industrial_junkyard_storage_pallet_wood_tarp_xiwcfassc_low", 1.8f),
            };

            // Market / shopfront slots along lanes at ~2.4m height (awnings) and ground tarps
            var slots = new List<(Vector3 pos, float yaw, bool elevated)>();
            float[] xs = { -22f, -10f, 10f, 22f, -36f, 36f };
            for (int i = 0; i < xs.Length; i++)
            {
                for (float z = -48f; z <= 48f; z += 9f)
                {
                    float yaw = xs[i] < 0 ? 90f : -90f;
                    slots.Add((new Vector3(xs[i], 0f, z + (float)(Rng.NextDouble() * 2 - 1)), yaw, true));
                    if ((int)z % 18 == 0)
                        slots.Add((new Vector3(xs[i] + Mathf.Sign(xs[i]) * -1.5f, 0f, z + 2f), yaw + 180f, false));
                }
            }
            // Extra mid-plaza stalls
            for (float z = -40f; z <= 40f; z += 12f)
            {
                slots.Add((new Vector3(-5.5f, 0, z), 90f, true));
                slots.Add((new Vector3(5.5f, 0, z + 3f), -90f, true));
            }

            int ci = 0;
            foreach (var (pos, yaw, elevated) in slots)
            {
                if (IsSpawnBlocked(pos)) continue;
                var spec = cloth[ci % cloth.Length];
                // Prefer awning/canopy for elevated, tarps/crates for ground
                if (elevated) spec = cloth[ci % 2]; // awning or canopy
                else spec = cloth[2 + (ci % 5)];
                ci++;

                var go = InstantiatePrefab(spec.path, $"ZZZ_Cloth_{_clothPlaced:00}");
                if (go == null) continue;
                go.transform.SetParent(_root, true);
                float s = FitLongestAxis(go, spec.axis, spec.axis * 0.35f, spec.axis * 1.8f);
                go.transform.localScale = Vector3.one * s;
                float y = elevated ? 2.55f + (float)(Rng.NextDouble() * 0.4) : 0f;
                go.transform.SetPositionAndRotation(new Vector3(pos.x, y, pos.z), Quaternion.Euler(0, yaw, 0));
                if (!elevated) SeatToGround(go.transform);
                else
                {
                    // Centre-pivoted awning: nudge so it hangs above shopfront
                    var b = BoundsOf(go);
                    if (b.center.y < y) go.transform.position += Vector3.up * (y - b.center.y);
                }
                ApplyMatIfExists(go, spec.matKey);
                StripColliders(go); // cloth — no collider (awning above head / soft prop)
                // Tarped crates on ground can block lightly
                if (!elevated && spec.path.Contains("crate"))
                {
                    var b = BoundsOf(go);
                    var box = go.AddComponent<BoxCollider>();
                    box.center = go.transform.InverseTransformPoint(b.center);
                    var ls = go.transform.InverseTransformVector(b.size * 0.9f);
                    box.size = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                }
                SetStatic(go);
                _clothPlaced++;
            }
            Log.AppendLine($"clothPlaced={_clothPlaced}");
        }

        // ── Phase 6: Graffiti ─────────────────────────────────────────────────

        static void Phase6_Graffiti()
        {
            _graffitiPlaced = 0;
            string[] mats =
            {
                $"{MatDir}/Zzz_graffiti_tag_vkzicjwl_4k.mat",
                $"{MatDir}/Zzz_graffiti_vlrkdiyc_4k.mat",
                $"{MatDir}/Zzz_graffiti_wdcpdgzv_4k.mat",
            };
            var loaded = mats.Select(p => AssetDatabase.LoadAssetAtPath<Material>(p)).Where(m => m != null).ToArray();
            if (loaded.Length == 0) { Log.AppendLine("NO graffiti mats"); return; }

            // Eye-level quads on facade-like walls
            var walls = new List<(Vector3 pos, Vector3 normal)>();
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                string path = PathOf(r.transform);
                if (!(path.Contains("FD_") || path.Contains("OD_") || path.Contains("CK_") || path.Contains("PB_"))) continue;
                var b = r.bounds;
                if (b.size.y < 2.5f || b.extents.x < 0.3f && b.extents.z < 0.3f) continue;
                // Choose the thinner horizontal axis as wall depth
                Vector3 n;
                float push;
                if (b.size.x < b.size.z)
                {
                    n = new Vector3(Mathf.Sign(b.center.x == 0 ? 1 : -Mathf.Sign(b.center.x)), 0, 0);
                    // Face toward map center
                    n = new Vector3(-Mathf.Sign(b.center.x + 0.01f), 0, 0);
                    push = b.extents.x + 0.03f;
                }
                else
                {
                    n = new Vector3(0, 0, -Mathf.Sign(b.center.z + 0.01f));
                    push = b.extents.z + 0.03f;
                }
                var pos = new Vector3(b.center.x, 1.55f + (float)(Rng.NextDouble() * 0.6), b.center.z) + n * push;
                walls.Add((pos, n));
                if (walls.Count > 120) break;
            }

            walls = walls.OrderBy(_ => Rng.Next()).Take(48).ToList();
            int gi = 0;
            foreach (var (pos, n) in walls)
            {
                if (IsSpawnBlocked(pos)) continue;
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = $"ZZZ_Graffiti_{gi:00}";
                Object.DestroyImmediate(quad.GetComponent<Collider>()); // no collider on decals
                quad.transform.SetParent(_root, true);
                float w = 1.6f + (float)Rng.NextDouble() * 1.8f;
                float h = 1.1f + (float)Rng.NextDouble() * 1.2f;
                quad.transform.localScale = new Vector3(w, h, 1f);
                quad.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-n));
                quad.transform.Rotate(0, 0, (float)(Rng.NextDouble() * 16 - 8), Space.Self);
                var r = quad.GetComponent<MeshRenderer>();
                r.sharedMaterial = loaded[gi % loaded.Length];
                r.shadowCastingMode = ShadowCastingMode.Off;
                SetStatic(quad);
                gi++;
                _graffitiPlaced++;
            }
            Log.AppendLine($"graffitiPlaced={_graffitiPlaced}");
        }

        // ── Materials helpers ─────────────────────────────────────────────────

        static void EnsureVehicleMaterials()
        {
            // Junk car — single useful texture set
            BuildLit(
                $"{MatDir}/Zzz_abandoned-junk-car.mat",
                $"{ImportRoot}/vehicles/abandoned-junk-car/textures/JUNKCAR1_SUBSTANCE_Material__9077_BaseColo.png",
                $"{ImportRoot}/vehicles/abandoned-junk-car/textures/JUNKCAR1_SUBSTANCE_Material__9077_Normal.png",
                $"{ImportRoot}/vehicles/abandoned-junk-car/textures/JUNKCAR1_SUBSTANCE_Material__9077_Occlusio.png",
                null);

            // Ensure red LOD1 gets LOD0 textures
            var lod0 = AssetDatabase.LoadAssetAtPath<GameObject>(
                $"{ImportRoot}/vehicles/red_car_wreck_rescued/red_renault_wreck_LOD0.fbx");
            var lod1 = AssetDatabase.LoadAssetAtPath<GameObject>(
                $"{ImportRoot}/vehicles/red_car_wreck_rescued/red_renault_wreck_LOD1.fbx");
            if (lod0 != null && lod1 != null)
            {
                var src = lod0.GetComponentInChildren<MeshRenderer>()?.sharedMaterials;
                // materials are sub-assets; just keep reference for Apply at instance time
            }
        }

        static void ApplyJunkMaterials(GameObject go)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/Zzz_abandoned-junk-car.mat");
            if (mat == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var arr = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < arr.Length; i++) arr[i] = mat;
                r.sharedMaterials = arr;
            }
        }

        static void EnsureRedMaterials(GameObject go)
        {
            var lod0 = AssetDatabase.LoadAssetAtPath<GameObject>(
                $"{ImportRoot}/vehicles/red_car_wreck_rescued/red_renault_wreck_LOD0.fbx");
            var srcR = lod0 != null ? lod0.GetComponentInChildren<MeshRenderer>() : null;
            if (srcR == null || srcR.sharedMaterials == null || srcR.sharedMaterials.Length == 0) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (r.sharedMaterials == null || r.sharedMaterials.Length == 0
                    || r.sharedMaterials.Any(m => m == null || m.GetTexture("_BaseMap") == null))
                {
                    r.sharedMaterials = srcR.sharedMaterials;
                }
            }
        }

        static void ApplyTrashMaterial(GameObject go, string fbxPath)
        {
            // .../urban_trash_low/<id>/<id>.fbx -> Zzz_trash_<id>
            var dir = Path.GetFileName(Path.GetDirectoryName(fbxPath));
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/Zzz_trash_{dir}.mat");
            if (mat == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var arr = Enumerable.Repeat(mat, Mathf.Max(1, r.sharedMaterials.Length)).ToArray();
                r.sharedMaterials = arr;
            }
        }

        static void ApplyMatIfExists(GameObject go, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            string name = key.StartsWith("Zzz_") ? key : "Zzz_" + key;
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/{name}.mat");
            if (mat == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var arr = Enumerable.Repeat(mat, Mathf.Max(1, r.sharedMaterials.Length)).ToArray();
                r.sharedMaterials = arr;
            }
        }

        static string MatNameFromPath(string path)
        {
            // .../props/cc0-antenna/source/Anchor.fbx -> cc0-antenna
            var parts = path.Replace("\\", "/").Split('/');
            int idx = Array.IndexOf(parts, "Zzz");
            if (idx >= 0 && idx + 2 < parts.Length) return parts[idx + 2];
            return Path.GetFileNameWithoutExtension(path);
        }

        static Material BuildLit(string matPath, string albedo, string normal, string ao, string mask)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(matPath) };
                AssetDatabase.CreateAsset(mat, matPath);
            }
            if (!string.IsNullOrEmpty(albedo))
            {
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(albedo);
                if (t != null) mat.SetTexture("_BaseMap", t);
            }
            if (!string.IsNullOrEmpty(normal))
            {
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(normal);
                if (t != null) { mat.SetTexture("_BumpMap", t); mat.EnableKeyword("_NORMALMAP"); mat.SetFloat("_Smoothness", 0.18f); }
            }
            if (!string.IsNullOrEmpty(ao))
            {
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(ao);
                if (t != null) { mat.SetTexture("_OcclusionMap", t); mat.SetFloat("_OcclusionStrength", 1f); }
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void TintVariation(GameObject go, int seed)
        {
            float dust = 0.15f + (seed % 5) * 0.06f;
            var dustCol = new Color(0.55f, 0.48f, 0.38f, 1f);
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var mats = r.sharedMaterials;
                var inst = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) { inst[i] = null; continue; }
                    var m = new Material(mats[i]);
                    if (m.HasProperty("_BaseColor"))
                    {
                        var c = m.GetColor("_BaseColor");
                        // Slight per-instance hue shift + dust
                        float hue = ((seed * 17 + i * 3) % 7) * 0.02f - 0.06f;
                        c = Color.Lerp(c, dustCol, dust);
                        c.r = Mathf.Clamp01(c.r + hue);
                        c.b = Mathf.Clamp01(c.b - hue * 0.5f);
                        m.SetColor("_BaseColor", c);
                    }
                    // Keep as scene-owned instance (not asset) — fine for static batching with GPU instancing off
                    m.name = mats[i].name + "_zzz" + seed;
                    m.enableInstancing = true;
                    inst[i] = m;
                }
                r.sharedMaterials = inst;
            }
        }

        // ── Utility ───────────────────────────────────────────────────────────

        static bool EnsureEditMode()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[ZZZ] Exit play mode and re-run.");
                return false;
            }
            return true;
        }

        static void OpenArena()
        {
            var active = SceneManager.GetActiveScene();
            if (active.path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            _map = GameObject.Find("ThreeLaneMap")?.transform;
            if (_map == null) throw new Exception("ThreeLaneMap missing");
        }

        static void EnsureRoot(bool clear)
        {
            var existing = _map.Find(RootName);
            if (clear && existing != null) Object.DestroyImmediate(existing.gameObject);
            existing = _map.Find(RootName);
            if (existing == null)
            {
                var go = new GameObject(RootName);
                go.transform.SetParent(_map, false);
                _root = go.transform;
            }
            else _root = existing;
        }

        static void ClearTagged(string prefix)
        {
            if (_root == null) return;
            var doomed = new List<GameObject>();
            foreach (Transform t in _root)
                if (t.name.StartsWith(prefix, StringComparison.Ordinal)) doomed.Add(t.gameObject);
            foreach (var g in doomed) Object.DestroyImmediate(g);
        }

        static GameObject InstantiatePrefab(string path, string name)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Log.AppendLine("MISSING " + path);
                return null;
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (go == null) go = Object.Instantiate(prefab);
            go.name = name;
            return go;
        }

        static string FindAssetPath(string folderName, params string[] exts)
        {
            string[] guids = AssetDatabase.FindAssets("t:Model", new[] { ImportRoot });
            foreach (var g in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (!p.Contains("/" + folderName + "/") && !p.Contains("\\" + folderName + "\\")) continue;
                foreach (var e in exts)
                {
                    if (p.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
            }
            // Fallback: any GameObject under that folder
            guids = AssetDatabase.FindAssets(folderName, new[] { ImportRoot });
            foreach (var g in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(p) != null) return p;
            }
            return null;
        }

        static float FitLongestAxis(GameObject go, float target, float minS, float maxS)
        {
            // World-space fit at current scale=1. Hard-cap to avoid cm FBX / bad bounds → 100m monsters.
            var b = BoundsOf(go);
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (longest < 1e-4f) return 1f;
            float s = target / longest;
            return Mathf.Clamp(s, 0.05f, 25f);
        }

        static void SeatToGround(Transform t)
        {
            if (t == null) return;
            var b = BoundsOf(t.gameObject);
            float delta = 0.02f - b.min.y;
            if (Mathf.Abs(delta) > 0.001f)
                t.position += Vector3.up * delta;
        }

        static Bounds BoundsOf(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return new Bounds(go.transform.position, Vector3.one * 0.1f);
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }

        static void StripColliders(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);
        }

        static void SetStatic(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.isStatic = true;
                GameObjectUtility.SetStaticEditorFlags(t.gameObject,
                    StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
            }
        }

        static bool IsSpawnBlocked(Vector3 p)
        {
            if (Vector2.Distance(new Vector2(p.x, p.z), new Vector2(SpawnClear.x, SpawnClear.z)) < 8f)
                return true;
            return false;
        }

        static List<Vector3> ScatterSlots(int count)
        {
            var list = new List<Vector3>();
            float[] xs = { -36f, -22f, -10f, 10f, 22f, 36f, -15f, 15f };
            int i = 0;
            while (list.Count < count && i < count * 4)
            {
                float x = xs[i % xs.Length] + (float)(Rng.NextDouble() * 2 - 1);
                float z = -50f + (i * 7.3f) % 100f + (float)(Rng.NextDouble() * 2);
                var p = new Vector3(x, 0, z);
                if (!IsSpawnBlocked(p) && Mathf.Abs(p.x) > 4f)
                    list.Add(p);
                i++;
            }
            return list;
        }

        static List<Vector3> Dilute(List<Vector3> pts, float minDist)
        {
            var outp = new List<Vector3>();
            foreach (var p in pts.OrderBy(_ => Rng.Next()))
            {
                if (outp.All(o => Vector3.Distance(o, p) >= minDist))
                    outp.Add(p);
            }
            return outp;
        }

        static string PathOf(Transform t)
        {
            var p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        static long CountSceneTris()
        {
            long n = 0;
            foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (mf.sharedMesh != null) n += mf.sharedMesh.triangles.Length / 3;
            foreach (var smr in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (smr.sharedMesh != null) n += smr.sharedMesh.triangles.Length / 3;
            return n;
        }

        static int CountInvisibleColliders()
        {
            int invis = 0;
            foreach (var c in Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c is CharacterController) continue;
                var r = c.GetComponent<Renderer>();
                if (r == null) r = c.GetComponentInChildren<Renderer>();
                if (r == null || !r.enabled) invis++;
            }
            return invis;
        }

        static void FinalizeAndSave(string tag)
        {
            long trisAfter = CountSceneTris();
            int invis = CountInvisibleColliders();
            Log.AppendLine($"tag={tag} trisAfter={trisAfter} delta={trisAfter - _trisBefore} invisColliders={invis}");
            Log.AppendLine($"counts cars={_carsPlaced} barriers={_barriersPlaced} litter={_litterPlaced} roof={_roofPlaced} cloth={_clothPlaced} graffiti={_graffitiPlaced}");

            // ArenaDresser still disabled?
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb.GetType().Name != "ArenaDresser") continue;
                var so = new SerializedObject(mb);
                var prop = so.FindProperty("runtimeDressingEnabled");
                Log.AppendLine($"ArenaDresser on {mb.gameObject.name} enabledComp={mb.enabled} runtimeDressingEnabled={(prop != null ? prop.boolValue.ToString() : "n/a")}");
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            WriteLog(tag);
            Debug.Log($"[ZZZ] {tag} done. cars={_carsPlaced} barriers={_barriersPlaced} litter={_litterPlaced} roof={_roofPlaced} cloth={_clothPlaced} graffiti={_graffitiPlaced} tris={_trisBefore}->{trisAfter} invis={invis}");
        }

        static void WriteLog(string tag)
        {
            var abs = Path.GetFullPath(ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            File.WriteAllText(abs, Log.ToString());
            Debug.Log($"[ZZZ] log -> {ReportPath} ({tag})");
        }

        static void CaptureAndMeasure()
        {
            Directory.CreateDirectory(Path.GetFullPath(ShotDir));
            var views = new List<(string name, Vector3 pos, float yaw, float pitch)>
            {
                ("EL_10", new Vector3(-13.58f, 1.70f, -44.34f), 11.3f, 2.4f),
                ("EL_14", new Vector3(49.92f, 1.70f, 2.33f), 138.5f, 4.8f),
                ("EL_02", new Vector3(0.57f, 1.70f, -9.30f), 10.1f, 0.2f),
                ("EL_18", new Vector3(57.90f, 1.70f, -34.87f), 247.2f, 4.3f),
                ("SPAWN", new Vector3(0f, 1.75f, -63f), 0f, 0f),
            };
            // 6 random eye points
            for (int i = 0; i < 6; i++)
            {
                float x = (float)(Rng.NextDouble() * 70 - 35);
                float z = (float)(Rng.NextDouble() * 100 - 50);
                float yaw = (float)(Rng.NextDouble() * 360);
                views.Add(($"RND_{i}", new Vector3(x, 1.70f, z), yaw, (float)(Rng.NextDouble() * 6 - 1)));
            }

            var sb = new StringBuilder();
            sb.AppendLine("view,meanLuma,nearBlackPct,pureWhitePct");

            // 1.8m reference capsule at first car for scale check
            var refGo = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            refGo.name = "ZZZ_ScaleRef_1m8";
            refGo.transform.SetParent(_root != null ? _root : _map, true);
            refGo.transform.position = new Vector3(8.6f, 0.9f, -31.1f);
            refGo.transform.localScale = new Vector3(0.4f, 0.9f, 0.4f); // unity capsule 2m * 0.9 = 1.8m
            Object.DestroyImmediate(refGo.GetComponent<Collider>());
            var refMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            refMat.SetColor("_BaseColor", Color.magenta);
            refGo.GetComponent<Renderer>().sharedMaterial = refMat;

            foreach (var v in views)
            {
                var camGo = new GameObject("__ZZZ_CAM");
                var cam = camGo.AddComponent<Camera>();
                var main = Camera.main;
                if (main != null) cam.CopyFrom(main);
                cam.fieldOfView = 72f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 300f;
                cam.transform.position = v.pos;
                cam.transform.rotation = Quaternion.Euler(v.pitch, v.yaw, 0f);
                cam.enabled = false;
                var data = cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.renderShadows = true;

                int w = 1280, h = 720;
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.GetFullPath($"{ShotDir}/{v.name}.png"), tex.EncodeToPNG());

                var px = tex.GetPixels();
                double sum = 0; int nb = 0, white = 0;
                foreach (var p in px)
                {
                    float l = 0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b;
                    sum += l;
                    if (l < 0.02f) nb++;
                    if (p.r > 0.99f && p.g > 0.99f && p.b > 0.99f) white++;
                }
                double mean = sum / px.Length;
                double nbPct = 100.0 * nb / px.Length;
                double wPct = 100.0 * white / px.Length;
                sb.AppendLine($"{v.name},{mean:F4},{nbPct:F3},{wPct:F3}");

                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }

            Object.DestroyImmediate(refGo);
            File.WriteAllText(Path.GetFullPath($"{ShotDir}/metrics.csv"), sb.ToString());
            Log.AppendLine(sb.ToString());
            Debug.Log("[ZZZ] capture done\n" + sb);
        }
    }
}
#endif
