#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Replaces the Kenney City Kit mid-lane street walls with assembled Poly Haven (CC0)
    /// modular_urban_apartments_facade geometry plus real PBR materials, then dresses the
    /// lane with barriers / crates / chainlink / street lamps / covered cars.
    ///
    /// The Poly Haven facade FBX is a 147-piece modular KIT laid out as a catalog, not an
    /// assembled building. Pieces are authored Z-up and metric: each piece pivots at its
    /// bottom-left-front corner, modules are 3m wide, wall panels are 3m tall zero-thickness
    /// planes whose front face is local +Z. Stacking is contiguous:
    ///   base 0.75 -> wall 3.00 (xN storeys) -> cornice 0.20 -> crown 0.75.
    ///
    /// Everything is additive under PH_*; Match/TDM/HUD/spawn systems are untouched.
    /// No colliders are created, so the PhysicsColliders-based NavMesh is unaffected.
    /// Menu: Arena FPS / AAA Poly Haven Facade Pass
    /// </summary>
    public static class AaaPolyHavenFacadePass
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string PhDir = "Assets/_Project/Art/Models/Environment/PolyHaven";
        const string MatDir = "Assets/_Project/Art/Materials/PolyHaven";
        const string GenDir = "Assets/_Project/Art/Textures/Generated";
        const string RootName = "PH_PolyHavenRoot";

        const string FacadeFbx = PhDir + "/modular_urban_apartments_facade/modular_urban_apartments_facade_1k.fbx";
        const string FenceFbx = PhDir + "/modular_chainlink_fence/modular_chainlink_fence_1k.fbx";

        // Kit grammar (metres), measured from the FBX.
        const float ModuleWidth = 3.0f;
        const float BaseHeight = 0.75f;
        const float StoreyHeight = 3.0f;
        const float CorniceHeight = 0.20f;
        const float CrownHeight = 0.75f;

        // Mid-lane facade planes. Derived from the Kenney CK_Mid_* world bounds: the closest
        // west face sits at x = -2.75 and the closest east face at x = +3.15, so the new
        // facades sit just inside those to keep visuals flush with the existing lane blockers.
        const float WestFaceX = -2.70f;
        const float EastFaceX = 3.10f;

        // Backing masses fill the old Kenney footprints so the lane still reads as solid.
        const float WestBackX = -9.0f;
        const float EastBackX = 9.0f;

        static readonly Dictionary<string, Material> Mats = new();
        static GameObject _facadeSrc;
        static GameObject _fenceSrc;
        static int _modules, _pieces, _props, _lights, _hidden, _backings;
        static bool _collidersChanged;

        [MenuItem("Arena FPS/AAA Poly Haven Facade Pass")]
        public static void Run()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
                Debug.LogWarning("[AAA PolyHaven] Exiting play mode; run again in edit mode.");
                return;
            }

            var scene = EditorSceneManager.GetActiveScene().path.EndsWith("Arena.unity")
                ? EditorSceneManager.GetActiveScene()
                : EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var map = GameObject.Find("ThreeLaneMap");
            if (map == null)
            {
                Debug.LogError("[AAA PolyHaven] ThreeLaneMap missing; aborting.");
                return;
            }

            _modules = _pieces = _props = _lights = _hidden = _backings = 0;
            _collidersChanged = false;

            EnsureFolders();
            ClearPrevious(map.transform);
            BuildMaterials();

            var root = new GameObject(RootName);
            root.transform.SetParent(map.transform, false);

            OpenSources();
            try
            {
                HideKenneyMidVisuals(map.transform);
                HideLegacyMidClutter(map.transform);
                TameLegacyDressing(map.transform);
                FixLegacyHeroProps(map.transform);
                RetintRoadStripes(map.transform);
                BuildFacades(root.transform);
                BuildBackingMasses(root.transform);
                PlaceProps(root.transform);
                BuildReflectionProbes(root.transform);
            }
            finally
            {
                CloseSources();
            }

            SetStaticRecursive(root);
            ReframeCaptureCameras();
            DisableAaaCameras();

            try { SpawnArenaCombat.Run(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AAA PolyHaven] SpawnArenaCombat skipped: {ex.Message}");
            }

            if (_collidersChanged)
                RebakeNavMesh();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log($"[AAA PolyHaven] Done: modules={_modules} facadePieces={_pieces} backings={_backings} " +
                      $"props={_props} lights={_lights} kenneyRenderersHidden={_hidden} " +
                      $"collidersChanged={_collidersChanged}. Arena saved.");
        }

        static void EnsureFolders()
        {
            foreach (var d in new[] { MatDir, GenDir })
            {
                if (!Directory.Exists(Path.GetFullPath(d)))
                    Directory.CreateDirectory(Path.GetFullPath(d));
            }
            AssetDatabase.Refresh();
        }

        static void ClearPrevious(Transform map)
        {
            var doomed = new List<GameObject>();
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                if (t != map && t.parent == map && (t.name == RootName || t.name.StartsWith("PH_")))
                    doomed.Add(t.gameObject);
            }
            foreach (var go in doomed)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
        }

        // ── Sources ───────────────────────────────────────────────────────────

        static void OpenSources()
        {
            _facadeSrc = LoadSource(FacadeFbx);
            _fenceSrc = LoadSource(FenceFbx);
        }

        static GameObject LoadSource(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"[AAA PolyHaven] Missing model: {path}");
                return null;
            }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.name = "__PH_Src_" + Path.GetFileNameWithoutExtension(path);
            inst.hideFlags = HideFlags.HideAndDontSave;
            inst.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            inst.transform.localScale = Vector3.one;
            return inst;
        }

        static void CloseSources()
        {
            if (_facadeSrc != null) Object.DestroyImmediate(_facadeSrc);
            if (_fenceSrc != null) Object.DestroyImmediate(_fenceSrc);
            _facadeSrc = _fenceSrc = null;
        }

        /// <summary>
        /// Clone a named piece out of a kit FBX. The source child's localRotation/localScale
        /// carry the Z-up -> Y-up conversion and the 100x file scale; dropping either lays the
        /// wall planes flat, so both are copied verbatim and only position is authored.
        /// </summary>
        static GameObject ClonePiece(GameObject src, string pieceName, Transform parent, Vector3 localPos, Material mat)
        {
            if (src == null) return null;

            Transform found = null;
            foreach (var t in src.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == pieceName) { found = t; break; }
            }
            if (found == null)
            {
                Debug.LogWarning($"[AAA PolyHaven] Kit piece not found: {pieceName}");
                return null;
            }

            var clone = Object.Instantiate(found.gameObject);
            clone.name = pieceName;
            clone.hideFlags = HideFlags.None;
            clone.transform.SetParent(parent, false);
            clone.transform.localPosition = localPos;
            clone.transform.localRotation = found.localRotation;
            clone.transform.localScale = found.localScale;

            foreach (var c in clone.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);

            if (mat != null)
            {
                Mats.TryGetValue("glass", out var glass);
                foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
                {
                    var arr = r.sharedMaterials;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        // Glazing keeps its own slot: painting frame and pane with one material
                        // is what makes the windows read as flat black holes.
                        bool isGlass = glass != null && arr[i] != null
                            && arr[i].name.IndexOf("glass", System.StringComparison.OrdinalIgnoreCase) >= 0;
                        arr[i] = isGlass ? glass : mat;
                    }
                    r.sharedMaterials = arr;
                }
            }

            _pieces++;
            return clone;
        }

        // ── Kenney mid-lane visuals ───────────────────────────────────────────

        /// <summary>
        /// Hide the Kenney mid-lane row plus the KP_ facade overlays stamped onto it.
        /// Colliders are destroyed with the renderers — no invisible walls.
        /// </summary>
        static void HideKenneyMidVisuals(Transform map)
        {
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                bool midKenney = n.StartsWith("CK_Mid_");
                bool midOverlay = n.StartsWith("KP_Glass_CK_Mid_")
                                  || n.StartsWith("KP_Dirt_CK_Mid_")
                                  || n.StartsWith("KP_Wear_CK_Mid_");
                bool midAwning = n.StartsWith("CK_Awning") || n.StartsWith("CK_Overhang")
                                 || n.StartsWith("CK_Parasol") || n.StartsWith("CK_Detail");

                if (!midKenney && !midOverlay && !midAwning)
                    continue;

                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled) continue;
                    r.enabled = false;
                    _hidden++;
                }

                foreach (var c in t.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(c);
            }
        }

        /// <summary>
        /// The earlier Kenney pass scattered primitive barrels / sandbags / trash and flat
        /// decal boxes down the middle of the lane. At the new facade scale they read as
        /// oversized floating props and coloured rugs, so they are retired here. Their
        /// colliders were already stripped by that pass, so nothing gameplay-facing changes.
        /// </summary>
        static void HideLegacyMidClutter(Transform map)
        {
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                bool clutter = n.StartsWith("KP_Barrel")
                               || n.StartsWith("KP_Sandbags")
                               || n.StartsWith("KP_Trash")
                               || n.StartsWith("KP_Oil")
                               || n.StartsWith("KP_Crack");
                if (!clutter) continue;

                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled) continue;
                    r.enabled = false;
                    _hidden++;
                }
            }
        }

        /// <summary>
        /// An earlier lane-dressing pass left two artefacts that fight the new facades: tarps
        /// and scuff quads hovering ~1m off the deck with nothing beneath them, and hazard
        /// paint at full highway saturation. Cover volumes (P2_Barricades, Cover_*, the bus,
        /// the fountain) are deliberately left alone — those are gameplay, not dressing.
        /// </summary>
        /// <summary>
        /// Flat opaque quads from the pre-PBR dressing passes. They were authored to read from
        /// a top-down camera; at 1.7m eye height they are unmistakably rugs lying on the road.
        /// Three-dimensional dressing from those passes (LD_krail_chunk, LD_oil_barrel) is kept.
        /// </summary>
        static readonly string[] LegacyFlatQuadPrefixes =
        {
            "LD_ground_grime", "LD_broken_lane_mark", "LD_folded_tarp", "LD_yellow_scuff",
            "FD_Oil", "FD_Tire", "FD_Crack",
        };

        static void TameLegacyDressing(Transform map)
        {
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                bool flatQuad = false;
                foreach (var prefix in LegacyFlatQuadPrefixes)
                {
                    if (n.StartsWith(prefix)) { flatQuad = true; break; }
                }
                // Some FD_Barrel entries imported with zero-extent meshes; they render nothing
                // but still cost a draw call and confuse later bounds queries.
                bool degenerate = n.StartsWith("FD_Barrel");

                if (!flatQuad && !degenerate) continue;

                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (degenerate && r.bounds.size.magnitude > 0.05f) continue;
                    if (!r.enabled) continue;
                    r.enabled = false;
                    _hidden++;
                }
            }

            // Retint through the renderers actually using the material rather than guessing an
            // asset path — the earlier passes did not use a consistent materials folder.
            var retinted = new HashSet<Material>();
            foreach (var r in map.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || retinted.Contains(m)) continue;
                    if (!m.name.Contains("HazardPaint")) continue;
                    if (m.HasProperty("_BaseColor"))
                        m.SetColor("_BaseColor", new Color(0.34f, 0.27f, 0.14f));
                    if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.06f);
                    EditorUtility.SetDirty(m);
                    retinted.Add(m);
                }
            }
        }

        /// <summary>
        /// Two pre-existing props sit dead centre of the hero sightlines and undo the new
        /// architecture. Both are cosmetic-only edits — colliders and transforms that gameplay
        /// depends on (the bus body volume) are untouched.
        /// </summary>
        static void FixLegacyHeroProps(Transform map)
        {
            // The bus reads pure black: its material is near-fully metallic, and a metal with
            // no environment to reflect resolves to black. Painted sheet metal instead.
            var bus = map.Find("Mid_Bus_Abandoned");
            if (bus != null)
            {
                var body = Solid("PH_BusBody", new Color(0.42f, 0.44f, 0.38f), 0.12f, 0.34f);
                var trim = Solid("PH_BusTrim", new Color(0.30f, 0.31f, 0.29f), 0.15f, 0.28f);
                Mats.TryGetValue("glass", out var glass);

                foreach (var r in bus.GetComponentsInChildren<Renderer>(true))
                {
                    string n = r.gameObject.name;
                    Material pick = null;
                    if (n == "Body") pick = body;
                    else if (n == "Roof") pick = trim;
                    else if (n.StartsWith("Window_")) pick = glass;
                    else if (n.StartsWith("Wheel_")) pick = Solid("PH_BusTyre", new Color(0.09f, 0.09f, 0.10f), 0f, 0.18f);
                    if (pick == null) continue;

                    var arr = r.sharedMaterials;
                    for (int i = 0; i < arr.Length; i++) arr[i] = pick;
                    r.sharedMaterials = arr;
                }
            }

            // Poster_Mid hovered in open air mid-lane with nothing behind it. Flatten it onto
            // the west facade so it reads as a flyposted wall bill.
            var poster = map.Find("Poster_Mid");
            if (poster != null)
            {
                poster.localScale = new Vector3(0.04f, 2.0f, 1.4f);
                poster.position = new Vector3(WestFaceX + 0.06f, 2.75f, -3.4f);
            }

            DemetalPaintedProps(map);
        }

        /// <summary>
        /// Several legacy props (the newsstand, kiosk framing, pipes) were authored as rough
        /// metal at metallic 0.75+. A rough conductor has no diffuse term, so with no local
        /// cubemap to reflect it renders as a black cutout — which is exactly how the newsstand
        /// was reading mid-lane. Painted street furniture is a dielectric, so the metallic is
        /// dropped and the albedo does the work. Genuinely polished metal (smoothness > 0.6)
        /// and the new Poly Haven materials are left alone.
        /// </summary>
        static void DemetalPaintedProps(Transform map)
        {
            var seen = new HashSet<Material>();
            foreach (var r in map.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || seen.Contains(m)) continue;
                    seen.Add(m);
                    if (m.name.StartsWith("PH_")) continue;
                    if (!m.HasProperty("_Metallic") || !m.HasProperty("_Smoothness")) continue;

                    if (m.GetFloat("_Metallic") > 0.45f && m.GetFloat("_Smoothness") < 0.6f)
                    {
                        m.SetFloat("_Metallic", 0.18f);
                        EditorUtility.SetDirty(m);
                    }
                }
            }
        }

        /// <summary>Mid-lane lane markings were saturated highway yellow; worn paint reads better.</summary>
        static void RetintRoadStripes(Transform map)
        {
            var worn = Solid("PH_RoadStripe_Worn", new Color(0.55f, 0.53f, 0.47f), 0f, 0.07f);
            foreach (var t in map.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("RoadStripe_Mid_")) continue;
                var r = t.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = worn;
            }
        }

        // ── Facade assembly ───────────────────────────────────────────────────

        readonly struct Section
        {
            public readonly float zMin;
            public readonly float zMax;
            public readonly int storeys;

            public Section(float zMin, float zMax, int storeys)
            {
                this.zMin = zMin;
                this.zMax = zMax;
                this.storeys = storeys;
            }
        }

        // Section rhythms are deliberately offset between the two sides so the lane does not
        // read as a mirrored corridor.
        static readonly Section[] WestSections =
        {
            new Section(-27f, -15f, 2),
            new Section(-15f, -6f, 3),
            new Section(-6f, 6f, 2),
            new Section(6f, 15f, 3),
            new Section(15f, 27f, 2),
        };

        static readonly Section[] EastSections =
        {
            new Section(-27f, -18f, 3),
            new Section(-18f, -6f, 2),
            new Section(-6f, 3f, 3),
            new Section(3f, 15f, 2),
            new Section(15f, 27f, 2),
        };

        static void BuildFacades(Transform root)
        {
            var west = new GameObject("PH_Facade_West");
            west.transform.SetParent(root, false);
            var east = new GameObject("PH_Facade_East");
            east.transform.SetParent(root, false);

            int idx = 0;
            int tint = 0;
            for (int s = 0; s < WestSections.Length; s++)
                BuildSection(west.transform, WestSections[s], true, s, ref idx, tint++);
            for (int s = 0; s < EastSections.Length; s++)
                BuildSection(east.transform, EastSections[s], false, s, ref idx, tint++);
        }

        /// <summary>
        /// One contiguous run of modules at a single height. The section transform carries the
        /// yaw that turns the kit's local +Z front into the world-space lane-facing direction;
        /// modules then simply march along the section's local +X.
        /// </summary>
        static void BuildSection(Transform parent, Section sec, bool westSide, int sectionIndex, ref int moduleIndex, int tintIndex)
        {
            int count = Mathf.Max(1, Mathf.RoundToInt((sec.zMax - sec.zMin) / ModuleWidth));

            // West faces +X: yaw 90 sends local +X to world -Z, so modules march down from zMax.
            // East faces -X: yaw -90 sends local +X to world +Z, so modules march up from zMin.
            float yaw = westSide ? 90f : -90f;
            float xFace = westSide ? WestFaceX : EastFaceX;
            float zStart = westSide ? sec.zMax : sec.zMin;

            var go = new GameObject($"PH_Sec_{(westSide ? "W" : "E")}{sectionIndex}_{sec.storeys}st");
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(new Vector3(xFace, 0f, zStart), Quaternion.Euler(0f, yaw, 0f));

            var plaster = PlasterVariants[tintIndex % PlasterVariants.Length];
            for (int i = 0; i < count; i++)
            {
                BuildModule(go.transform, i * ModuleWidth, sec.storeys, moduleIndex, plaster);
                moduleIndex++;
                _modules++;
            }
        }

        static void BuildModule(Transform section, float localX, int storeys, int moduleIndex, Material plaster)
        {
            var mod = new GameObject($"PH_Mod_{moduleIndex:D2}");
            mod.transform.SetParent(section, false);
            mod.transform.localPosition = new Vector3(localX, 0f, 0f);
            mod.transform.localRotation = Quaternion.identity;

            float y = 0f;
            ClonePiece(_facadeSrc, "base_standard_01", mod.transform, new Vector3(0f, y, 0f), Mats["trim01"]);
            y += BaseHeight;

            for (int s = 0; s < storeys; s++)
            {
                string wall = PickWall(moduleIndex, s);
                ClonePiece(_facadeSrc, wall, mod.transform, new Vector3(0f, y, 0f), plaster);

                // Inserts share the wall's cell name minus the "wall_" prefix.
                string insert = wall.StartsWith("wall_") ? wall.Substring(5) : null;
                if (!string.IsNullOrEmpty(insert) && HasPiece(_facadeSrc, insert))
                    ClonePiece(_facadeSrc, insert, mod.transform, new Vector3(0f, y, 0f), Mats["objects"]);

                y += StoreyHeight;
            }

            ClonePiece(_facadeSrc, "cornice_standard_standard_01", mod.transform, new Vector3(0f, y, 0f), Mats["trim01"]);
            y += CorniceHeight;
            ClonePiece(_facadeSrc, "crown_standard_standard_01", mod.transform, new Vector3(0f, y, 0f), Mats["trim02"]);
        }

        static readonly string[] GroundWalls =
        {
            "wall_door_centered_large_01",
            "wall_window_centered_large_01",
            "wall_door_window_small_01",
            "wall_window_centered_double_01",
            "wall_door_centered_small_01",
            "wall_window_centered_large_02",
        };

        static readonly string[] UpperWalls =
        {
            "wall_window_centered_large_01",
            "wall_window_centered_double_01",
            "wall_window_offset_small_01",
            "wall_window_centered_large_03",
            "wall_window_centered_double_02",
            "wall_window_offset_small_03",
            "wall_window_centered_small_01",
            "wall_window_centered_double_03",
        };

        /// <summary>Deterministic variant pick so repeat runs produce an identical street.</summary>
        static string PickWall(int moduleIndex, int storey)
        {
            int h = moduleIndex * 73 + storey * 17;
            return storey == 0
                ? GroundWalls[h % GroundWalls.Length]
                : UpperWalls[h % UpperWalls.Length];
        }

        static bool HasPiece(GameObject src, string name)
        {
            if (src == null) return false;
            foreach (var t in src.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return true;
            }
            return false;
        }

        // ── Backing masses ────────────────────────────────────────────────────

        /// <summary>
        /// The kit's wall panels are single-sided planes, so each run needs a solid mass behind
        /// it to stop the lane reading through to the industrial backdrop and to give a roof.
        /// No colliders: the Kenney *_PBMass blockers already occupy this volume.
        /// </summary>
        static void BuildBackingMasses(Transform root)
        {
            var holder = new GameObject("PH_BackingMass");
            holder.transform.SetParent(root, false);

            for (int i = 0; i < WestSections.Length; i++)
                Backing(holder.transform, WestSections[i], true, i);
            for (int i = 0; i < EastSections.Length; i++)
                Backing(holder.transform, EastSections[i], false, i);
        }

        static void Backing(Transform parent, Section sec, bool westSide, int index)
        {
            float height = BaseHeight + sec.storeys * StoreyHeight + CorniceHeight + CrownHeight - 0.10f;

            // Inset 0.05 behind the facade plane so the wall planes never z-fight the mass.
            float front = westSide ? WestFaceX - 0.05f : EastFaceX + 0.05f;
            float back = westSide ? WestBackX : EastBackX;

            float cx = (front + back) * 0.5f;
            float sx = Mathf.Abs(back - front);
            float cz = (sec.zMin + sec.zMax) * 0.5f;
            float sz = sec.zMax - sec.zMin;

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"PH_Back_{(westSide ? "W" : "E")}{index}";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(cx, height * 0.5f, cz);
            go.transform.localScale = new Vector3(sx, height, sz);

            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = Mats["backing"];
            _backings++;
        }

        // ── Props ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Storytelling dressing. Everything hugs the walls: the lane keeps a clear central
        /// corridor of roughly x in [-1.6, +2.0], and no prop carries a collider so bot
        /// navigation and the baked NavMesh are unchanged.
        /// </summary>
        static void PlaceProps(Transform root)
        {
            var holder = new GameObject("PH_Props");
            holder.transform.SetParent(root, false);
            var t = holder.transform;

            // Concrete barriers — lane-edge cover reads, long axis running with the street.
            Prop(t, "concrete_road_barrier", "PH_Barrier_W1", new Vector3(-2.15f, 0f, -20.6f), 92f, "barrier");
            Prop(t, "concrete_road_barrier", "PH_Barrier_W2", new Vector3(-2.20f, 0f, -18.9f), 87f, "barrier");
            Prop(t, "concrete_road_barrier", "PH_Barrier_W3", new Vector3(-2.10f, 0f, 3.8f), 84f, "barrier");
            Prop(t, "concrete_road_barrier", "PH_Barrier_W4", new Vector3(-2.18f, 0f, 5.5f), 95f, "barrier");
            Prop(t, "concrete_road_barrier", "PH_Barrier_E1", new Vector3(2.58f, 0f, -12.1f), 90f, "barrier");
            Prop(t, "concrete_road_barrier", "PH_Barrier_E2", new Vector3(2.55f, 0f, -10.4f), 96f, "barrier");
            Prop(t, "concrete_road_barrier", "PH_Barrier_E3", new Vector3(2.60f, 0f, 16.2f), 88f, "barrier");

            // Covered cars — parked tight against the kerb, nose-in with the street.
            Prop(t, "covered_car", "PH_Car_W", new Vector3(-1.95f, 0f, -25.0f), 4f, "car");
            Prop(t, "covered_car", "PH_Car_E", new Vector3(2.35f, 0f, 11.5f), 183f, "car");

            // Crates and boxes — clustered as if unloaded against the storefronts.
            Prop(t, "old_military_crate", "PH_Crate_Mil_W", new Vector3(-2.20f, 0f, -8.2f), 78f, "crate");
            Prop(t, "old_military_crate", "PH_Crate_Mil_E", new Vector3(2.55f, 0f, 21.0f), -84f, "crate");
            Prop(t, "wooden_crate_01", "PH_Crate_Wood_W1", new Vector3(-2.30f, 0f, -6.4f), 12f, "crate");
            Prop(t, "wooden_crate_01", "PH_Crate_Wood_W2", new Vector3(-2.25f, 0.35f, -6.3f), 26f, "crate");
            Prop(t, "wooden_crate_01", "PH_Crate_Wood_E1", new Vector3(2.62f, 0f, -2.1f), -21f, "crate");
            Prop(t, "plastic_crate_01", "PH_Crate_Plastic_W", new Vector3(-2.34f, 0f, 12.6f), 33f, "crate");
            Prop(t, "plastic_crate_01", "PH_Crate_Plastic_E", new Vector3(2.66f, 0f, -16.4f), -47f, "crate");
            Prop(t, "cardboard_box_01", "PH_Box_W1", new Vector3(-2.40f, 0f, 13.4f), 8f, "crate");
            Prop(t, "cardboard_box_01", "PH_Box_E1", new Vector3(2.70f, 0f, -15.6f), -63f, "crate");
            Prop(t, "cardboard_box_01", "PH_Box_E2", new Vector3(2.62f, 0.41f, -15.7f), -18f, "crate");

            // Street lamps — vertical rhythm plus warm practicals over the lane.
            Lamp(t, "PH_Lamp_W1", new Vector3(-2.42f, 0f, -14.0f), 90f);
            Lamp(t, "PH_Lamp_E1", new Vector3(2.82f, 0f, -3.5f), -90f);
            Lamp(t, "PH_Lamp_W2", new Vector3(-2.42f, 0f, 9.0f), 90f);
            Lamp(t, "PH_Lamp_E2", new Vector3(2.82f, 0f, 20.0f), -90f);

            // Chainlink runs fencing off a utility gap on each side.
            FenceRun(t, "PH_Fence_W", new Vector3(-2.05f, 0f, -3.0f), 90f, 3);
            FenceRun(t, "PH_Fence_E", new Vector3(2.42f, 0f, 6.6f), -90f, 2);
        }

        static readonly Dictionary<string, string> PropFbx = new()
        {
            { "concrete_road_barrier", PhDir + "/concrete_road_barrier/concrete_road_barrier_1k.fbx" },
            { "covered_car", PhDir + "/covered_car/covered_car_1k.fbx" },
            { "old_military_crate", PhDir + "/old_military_crate/old_military_crate_1k.fbx" },
            { "wooden_crate_01", PhDir + "/wooden_crate_01/wooden_crate_01_1k.fbx" },
            { "plastic_crate_01", PhDir + "/plastic_crate_01/plastic_crate_01_1k.fbx" },
            { "cardboard_box_01", PhDir + "/cardboard_box_01/cardboard_box_01_1k.fbx" },
            { "street_lamp_01", PhDir + "/street_lamp_01/street_lamp_01_1k.fbx" },
        };

        // These three FBX export their mesh without the 100x root node the others carry, so the
        // raw import is ~1/100 scale and still Z-up. Measured, not guessed.
        static readonly HashSet<string> NeedsUnitFix = new()
        {
            "street_lamp_01", "cardboard_box_01", "plastic_crate_01",
        };

        static GameObject Prop(Transform parent, string model, string name, Vector3 pos, float yaw, string matKey)
        {
            if (!PropFbx.TryGetValue(model, out var path))
                return null;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"[AAA PolyHaven] Missing prop model: {path}");
                return null;
            }

            // Yaw lives on a wrapper so the model keeps whatever axis fix it needs.
            var pivot = new GameObject(name);
            pivot.transform.SetParent(parent, false);
            pivot.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (go == null) go = Object.Instantiate(prefab);
            go.name = model;
            go.transform.SetParent(pivot.transform, false);
            go.transform.localPosition = Vector3.zero;

            if (NeedsUnitFix.Contains(model))
            {
                go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                go.transform.localScale = Vector3.one * 100f;
            }
            else
            {
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
            }

            KeepLod0Only(go);

            foreach (var c in go.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);

            if (Mats.TryGetValue(matKey, out var mat) && mat != null)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    var arr = r.sharedMaterials;
                    for (int i = 0; i < arr.Length; i++) arr[i] = mat;
                    r.sharedMaterials = arr;
                }
            }

            GroundSnap(pivot, pos);
            _props++;
            return pivot;
        }

        /// <summary>Several Poly Haven FBX ship stacked LOD meshes with no LODGroup; keep LOD0.</summary>
        static void KeepLod0Only(GameObject go)
        {
            if (go.GetComponentInChildren<LODGroup>(true) != null)
                return;

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.gameObject.name;
                int i = n.IndexOf("_LOD", System.StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                if (!n.EndsWith("_LOD0", System.StringComparison.OrdinalIgnoreCase))
                    r.gameObject.SetActive(false);
            }
        }

        static void GroundSnap(GameObject pivot, Vector3 pos)
        {
            var rs = pivot.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;

            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);

            // pos.y is treated as the desired base height above the road.
            float delta = pos.y - b.min.y;
            pivot.transform.position += new Vector3(0f, delta, 0f);
        }

        static void Lamp(Transform parent, string name, Vector3 pos, float yaw)
        {
            var pivot = Prop(parent, "street_lamp_01", name, pos, yaw, "lamp");
            if (pivot == null) return;

            var rs = pivot.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);

            var lightGo = new GameObject(name + "_Practical");
            lightGo.transform.SetParent(pivot.transform, true);
            lightGo.transform.position = new Vector3(b.center.x, b.max.y - 0.25f, b.center.z);

            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.78f, 0.52f);
            l.intensity = 1.35f;
            l.range = 11f;
            l.shadows = LightShadows.None;
            l.renderMode = LightRenderMode.ForcePixel;
            _lights++;
        }

        static void FenceRun(Transform parent, string name, Vector3 pos, float yaw, int panels)
        {
            if (_fenceSrc == null) return;

            var run = new GameObject(name);
            run.transform.SetParent(parent, false);
            run.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));

            const float panelWidth = 1.914f;
            for (int i = 0; i < panels; i++)
            {
                ClonePiece(_fenceSrc, "modular_chainlink_fence_double", run.transform,
                    new Vector3(i * panelWidth, 0f, 0f), Mats["fence"]);
                ClonePiece(_fenceSrc, "modular_chainlink_fence_post", run.transform,
                    new Vector3(i * panelWidth, 0f, 0f), Mats["fencepost"]);
            }
            ClonePiece(_fenceSrc, "modular_chainlink_fence_end_01", run.transform,
                new Vector3(panels * panelWidth, 0f, 0f), Mats["fencepost"]);

            _props++;
        }

        /// <summary>
        /// Without a local probe every smooth surface in the canyon falls back to the skybox,
        /// which the facades block — so glazing, lamp metal and wet asphalt all resolve to
        /// black. Three probes along the lane give them the street to reflect instead.
        /// Rendered via scripting so this needs no full lighting bake.
        /// </summary>
        static void BuildReflectionProbes(Transform root)
        {
            var holder = new GameObject("PH_ReflectionProbes");
            holder.transform.SetParent(root, false);

            float[] zs = { -18f, 0f, 18f };
            for (int i = 0; i < zs.Length; i++)
            {
                var go = new GameObject($"PH_ReflProbe_{i}");
                go.transform.SetParent(holder.transform, false);
                go.transform.localPosition = new Vector3(0.2f, 3.2f, zs[i]);

                var probe = go.AddComponent<ReflectionProbe>();
                probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
                probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
                probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.NoTimeSlicing;
                probe.size = new Vector3(9f, 14f, 22f);
                probe.boxProjection = true;
                probe.resolution = 128;
                probe.cullingMask = ~0;
                probe.clearFlags = UnityEngine.Rendering.ReflectionProbeClearFlags.Skybox;
                probe.intensity = 1f;
                probe.nearClipPlane = 0.2f;
                probe.farClipPlane = 90f;
                probe.RenderProbe();
            }
        }

        // ── Materials ─────────────────────────────────────────────────────────

        static Material[] PlasterVariants = System.Array.Empty<Material>();

        static void BuildMaterials()
        {
            Mats.Clear();
            const string F = PhDir + "/modular_urban_apartments_facade/modular_urban_apartments_facade";

            string plasterDiff = First(F + "_plaster_diff_1k.png", F + "_plaster_diff_1k.jpg");
            string plasterNrm = First(F + "_plaster_nor_gl_1k.png", F + "_plaster_nor_gl_1k.jpg");
            string plasterRough = First(F + "_plaster_rough_1k.png", F + "_plaster_rough_1k.jpg");

            Mats["plaster"] = Pbr("PH_Facade_Plaster",
                plasterDiff, plasterNrm, plasterRough,
                null, new Color(0.92f, 0.90f, 0.87f), 0f, 0.16f);

            // The kit ships one terracotta plaster. Thirty-six identical modules read as a
            // single extruded block, so the diffuse is rebaked at several desaturation levels
            // to give each building along the street its own render-block identity.
            PlasterVariants = new[]
            {
                PlasterVariant("PH_Facade_Plaster_Terracotta", plasterDiff, plasterNrm, plasterRough,
                    0.08f, new Color(1.00f, 0.94f, 0.88f)),
                PlasterVariant("PH_Facade_Plaster_Grey", plasterDiff, plasterNrm, plasterRough,
                    0.82f, new Color(0.74f, 0.76f, 0.78f)),
                PlasterVariant("PH_Facade_Plaster_Sand", plasterDiff, plasterNrm, plasterRough,
                    0.48f, new Color(1.00f, 0.96f, 0.84f)),
                PlasterVariant("PH_Facade_Plaster_Olive", plasterDiff, plasterNrm, plasterRough,
                    0.62f, new Color(0.84f, 0.86f, 0.74f)),
                PlasterVariant("PH_Facade_Plaster_Slate", plasterDiff, plasterNrm, plasterRough,
                    0.90f, new Color(0.62f, 0.65f, 0.70f)),
            };

            Mats["trim01"] = Pbr("PH_Facade_Trim01",
                First(F + "_trim_01_diff_1k.png"),
                First(F + "_trim_01_nor_gl_1k.png", F + "_trim_01_nor_gl_1k.jpg"),
                First(F + "_trim_01_rough_1k.png"),
                null, new Color(0.88f, 0.87f, 0.85f), 0f, 0.20f);

            Mats["trim02"] = Pbr("PH_Facade_Trim02",
                First(F + "_trim_02_diff_1k.png"),
                First(F + "_trim_02_nor_gl_1k.png"),
                First(F + "_trim_02_rough_1k.png", F + "_trim_02_rough_1k.jpg"),
                null, new Color(0.86f, 0.85f, 0.83f), 0.05f, 0.24f);

            Mats["objects"] = Pbr("PH_Facade_Objects",
                First(F + "_objects_diff_1k.png"),
                First(F + "_objects_nor_gl_1k.png"),
                First(F + "_objects_rough_1k.png"),
                First(F + "_objects_metal_1k.png"),
                new Color(0.90f, 0.89f, 0.88f), 0.25f, 0.42f);

            // Backing mass reuses the plaster set, tiled to building scale.
            Mats["backing"] = Pbr("PH_BackingMass",
                First(F + "_plaster_diff_1k.png", F + "_plaster_diff_1k.jpg"),
                First(F + "_plaster_nor_gl_1k.png", F + "_plaster_nor_gl_1k.jpg"),
                First(F + "_plaster_rough_1k.png", F + "_plaster_rough_1k.jpg"),
                null, new Color(0.55f, 0.53f, 0.50f), 0f, 0.12f, tiling: 3.5f);

            // Dark, very smooth, slightly metallic: picks up the sky gradient and the lamp
            // practicals so the glazing reads as glass rather than a hole in the wall.
            Mats["glass"] = Solid("PH_Facade_Glass", new Color(0.055f, 0.065f, 0.078f), 0.35f, 0.92f);

            Mats["barrier"] = PropPbr("PH_Prop_Barrier", "concrete_road_barrier",
                new Color(0.86f, 0.85f, 0.83f), 0f, 0.22f);
            Mats["car"] = PropPbr("PH_Prop_CoveredCar", "covered_car",
                new Color(0.88f, 0.87f, 0.86f), 0f, 0.28f);
            Mats["crate"] = PropPbr("PH_Prop_Crate", "old_military_crate",
                new Color(0.90f, 0.88f, 0.84f), 0f, 0.24f);
            Mats["lamp"] = PropPbr("PH_Prop_StreetLamp", "street_lamp_01",
                new Color(0.72f, 0.72f, 0.72f), 0.55f, 0.42f);

            const string CF = PhDir + "/modular_chainlink_fence/modular_chainlink_fence";
            Mats["fence"] = Pbr("PH_Prop_FenceWire",
                First(CF + "_wire_diff_1k.png", CF + "_wire_diff_1k.jpg"),
                First(CF + "_wire_nor_gl_1k.png", CF + "_wire_nor_gl_1k.jpg"),
                First(CF + "_wire_rough_1k.png"),
                First(CF + "_wire_metal_1k.png"),
                new Color(0.70f, 0.71f, 0.72f), 0.65f, 0.40f,
                alphaClipPath: First(CF + "_wire_alpha_1k.png"));

            Mats["fencepost"] = Pbr("PH_Prop_FencePost",
                First(CF + "_posts_diff_1k.png"),
                First(CF + "_posts_nor_gl_1k.png", CF + "_posts_nor_gl_1k.jpg"),
                First(CF + "_posts_rough_1k.png"),
                First(CF + "_posts_metal_1k.png"),
                new Color(0.68f, 0.69f, 0.70f), 0.70f, 0.38f);
        }

        static Material PlasterVariant(string name, string diff, string nrm, string rough, float desat, Color tint)
        {
            string baked = BakeDesaturated($"{name}_Diff.png", diff, desat);
            return Pbr(name, baked ?? diff, nrm, rough, null, tint, 0f, 0.15f);
        }

        /// <summary>
        /// Desaturating in the texture rather than via _BaseColor: a colour tint can only
        /// multiply, so it can darken terracotta but never turn it grey or olive.
        /// </summary>
        static string BakeDesaturated(string outName, string srcPath, float desat)
        {
            if (string.IsNullOrEmpty(srcPath)) return null;
            string outPath = $"{GenDir}/{outName}";
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(outPath) != null)
                return outPath;

            EnsureReadable(srcPath);
            var src = AssetDatabase.LoadAssetAtPath<Texture2D>(srcPath);
            if (src == null || !src.isReadable) return null;

            var px = src.GetPixels32();
            var dst = new Color32[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                r = Mathf.Lerp(r, lum, desat);
                g = Mathf.Lerp(g, lum, desat);
                b = Mathf.Lerp(b, lum, desat);
                dst[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(r * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(g * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255),
                    c.a);
            }

            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, true);
            tex.SetPixels32(dst);
            tex.Apply(true);
            File.WriteAllBytes(Path.GetFullPath(outPath), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(outPath);

            var imp = AssetImporter.GetAtPath(outPath) as TextureImporter;
            if (imp != null)
            {
                imp.sRGBTexture = true;
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.maxTextureSize = 1024;
                imp.SaveAndReimport();
            }
            return outPath;
        }

        static Material Solid(string name, Color color, float metallic, float smoothness)
        {
            string path = $"{MatDir}/{name}.mat";
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
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>Props ship a capitalised Diffuse/Rough/Metal JPG set alongside the raw EXRs.</summary>
        static Material PropPbr(string matName, string model, Color tint, float metallic, float smoothness)
        {
            string p = $"{PhDir}/{model}/{model}";
            return Pbr(matName,
                First(p + "_Diffuse_1k.jpg", p + "_diff_1k.jpg"),
                First(p + "_nor_gl_1k.jpg", p + "_nor_gl_1k.png"),
                First(p + "_Rough_1k.jpg", p + "_rough_1k.jpg"),
                First(p + "_Metal_1k.jpg"),
                tint, metallic, smoothness);
        }

        static string First(params string[] paths)
        {
            foreach (var p in paths)
            {
                if (!string.IsNullOrEmpty(p) && AssetDatabase.LoadAssetAtPath<Texture2D>(p) != null)
                    return p;
            }
            return null;
        }

        static Material Pbr(string name, string albedo, string normal, string rough, string metal,
            Color tint, float metallic, float smoothness, float tiling = 1f, string alphaClipPath = null)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
            else mat.color = tint;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);

            var alb = albedo != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(albedo) : null;
            if (alb != null)
            {
                EnsureRepeat(albedo);
                mat.SetTexture("_BaseMap", alb);
                mat.mainTextureScale = new Vector2(tiling, tiling);
            }

            if (normal != null)
            {
                var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(normal);
                if (nrm != null && mat.HasProperty("_BumpMap"))
                {
                    EnsureNormalImport(normal);
                    mat.SetTexture("_BumpMap", nrm);
                    mat.EnableKeyword("_NORMALMAP");
                    if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
                    mat.SetTextureScale("_BumpMap", new Vector2(tiling, tiling));
                }
            }

            if (rough != null && mat.HasProperty("_MetallicGlossMap"))
            {
                var packed = PackMask(name + "_Mask", rough, metal, metallic);
                if (packed != null)
                {
                    mat.SetTexture("_MetallicGlossMap", packed);
                    mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                    mat.SetTextureScale("_MetallicGlossMap", new Vector2(tiling, tiling));
                    if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f);
                }
            }

            if (alphaClipPath != null)
            {
                var a = AssetDatabase.LoadAssetAtPath<Texture2D>(alphaClipPath);
                if (a != null)
                {
                    // Chainlink wire needs cutout or the panels read as solid sheets.
                    if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
                    if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.5f);
                    mat.EnableKeyword("_ALPHATEST_ON");
                    mat.SetOverrideTag("RenderType", "TransparentCutout");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                    var merged = MergeAlbedoAlpha(name + "_Cutout", albedo, alphaClipPath);
                    if (merged != null) mat.SetTexture("_BaseMap", merged);
                }
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>URP Lit mask map: RGB metallic, A smoothness (Poly Haven ships linear roughness).</summary>
        static Texture2D PackMask(string name, string roughPath, string metalPath, float metallicFallback)
        {
            string outPath = $"{GenDir}/{name}.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (existing != null) return existing;

            EnsureReadable(roughPath);
            var rough = AssetDatabase.LoadAssetAtPath<Texture2D>(roughPath);
            if (rough == null || !rough.isReadable) return null;

            Texture2D metal = null;
            if (!string.IsNullOrEmpty(metalPath))
            {
                EnsureReadable(metalPath);
                metal = AssetDatabase.LoadAssetAtPath<Texture2D>(metalPath);
                if (metal != null && !metal.isReadable) metal = null;
            }

            int w = rough.width, h = rough.height;
            var rp = rough.GetPixels32();
            Color32[] mp = null;
            int mw = 1, mh = 1;
            if (metal != null)
            {
                mp = metal.GetPixels32();
                mw = metal.width;
                mh = metal.height;
            }

            byte fallback = (byte)Mathf.Clamp(Mathf.RoundToInt(metallicFallback * 255f), 0, 255);
            var dst = new Color32[rp.Length];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                byte met = fallback;
                if (mp != null)
                {
                    int mx = (x * mw / w) % mw;
                    int my = (y * mh / h) % mh;
                    met = mp[my * mw + mx].r;
                }
                byte sm = (byte)(255 - rp[i].r);
                dst[i] = new Color32(met, met, met, sm);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, true);
            tex.SetPixels32(dst);
            tex.Apply(true);
            File.WriteAllBytes(Path.GetFullPath(outPath), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(outPath);

            var imp = AssetImporter.GetAtPath(outPath) as TextureImporter;
            if (imp != null)
            {
                imp.sRGBTexture = false;
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.alphaSource = TextureImporterAlphaSource.FromInput;
                imp.alphaIsTransparency = false;
                imp.maxTextureSize = 1024;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        static Texture2D MergeAlbedoAlpha(string name, string albedoPath, string alphaPath)
        {
            if (string.IsNullOrEmpty(albedoPath)) return null;
            string outPath = $"{GenDir}/{name}.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (existing != null) return existing;

            EnsureReadable(albedoPath);
            EnsureReadable(alphaPath);
            var alb = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var alp = AssetDatabase.LoadAssetAtPath<Texture2D>(alphaPath);
            if (alb == null || alp == null || !alb.isReadable || !alp.isReadable) return null;

            int w = alb.width, h = alb.height;
            var ap = alb.GetPixels32();
            var kp = alp.GetPixels32();
            int kw = alp.width, kh = alp.height;
            var dst = new Color32[ap.Length];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int kx = (x * kw / w) % kw;
                int ky = (y * kh / h) % kh;
                var c = ap[i];
                dst[i] = new Color32(c.r, c.g, c.b, kp[ky * kw + kx].r);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            tex.SetPixels32(dst);
            tex.Apply(true);
            File.WriteAllBytes(Path.GetFullPath(outPath), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(outPath);

            var imp = AssetImporter.GetAtPath(outPath) as TextureImporter;
            if (imp != null)
            {
                imp.sRGBTexture = true;
                imp.alphaSource = TextureImporterAlphaSource.FromInput;
                imp.alphaIsTransparency = true;
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        static void EnsureRepeat(string texPath)
        {
            var imp = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (imp == null) return;
            if (imp.wrapMode == TextureWrapMode.Repeat) return;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.SaveAndReimport();
        }

        static void EnsureNormalImport(string texPath)
        {
            var imp = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (imp == null) return;
            bool dirty = false;
            if (imp.textureType != TextureImporterType.NormalMap)
            {
                imp.textureType = TextureImporterType.NormalMap;
                dirty = true;
            }
            if (imp.wrapMode != TextureWrapMode.Repeat)
            {
                imp.wrapMode = TextureWrapMode.Repeat;
                dirty = true;
            }
            if (dirty) imp.SaveAndReimport();
        }

        static void EnsureReadable(string texPath)
        {
            if (string.IsNullOrEmpty(texPath)) return;
            var imp = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (imp == null || imp.isReadable) return;
            imp.isReadable = true;
            imp.SaveAndReimport();
        }

        // ── Scene plumbing ────────────────────────────────────────────────────

        static void SetStaticRecursive(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t.GetComponent<Light>() != null) continue;
                t.gameObject.isStatic = true;
                GameObjectUtility.SetStaticEditorFlags(t.gameObject,
                    StaticEditorFlags.BatchingStatic |
                    StaticEditorFlags.OccludeeStatic |
                    StaticEditorFlags.OccluderStatic);
            }
        }

        static void RebakeNavMesh()
        {
            var surfaces = Object.FindObjectsByType<Unity.AI.Navigation.NavMeshSurface>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var s in surfaces)
            {
                s.BuildNavMesh();
                EditorUtility.SetDirty(s);
            }
            Debug.Log($"[AAA PolyHaven] NavMesh rebaked on {surfaces.Length} surface(s).");
        }

        static void ReframeCaptureCameras()
        {
            // Human eye height down the new canyon.
            SetCam("AAA_EyeLevel_Camera", new Vector3(0.15f, 1.70f, -22.5f), new Vector3(0.1f, 2.30f, -2f), 60f);
            SetCam("AAA_MidLane_Camera", new Vector3(-0.55f, 1.75f, -13.5f), new Vector3(0.4f, 3.20f, 9f), 55f);
            SetCam("AAA_Aerial_Camera", new Vector3(0f, 46f, -6f), new Vector3(0f, 0f, 3f), 48f);
        }

        static void DisableAaaCameras()
        {
            foreach (var name in new[] { "AAA_EyeLevel_Camera", "AAA_MidLane_Camera", "AAA_Aerial_Camera" })
            {
                var go = GameObject.Find(name);
                if (go == null) continue;
                var cam = go.GetComponent<Camera>();
                if (cam != null) cam.enabled = false;
            }
        }

        static void SetCam(string name, Vector3 pos, Vector3 lookAt, float fov)
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

            var cam = go.GetComponent<Camera>() ?? go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 280f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;
            cam.enabled = false;
        }
    }
}
#endif
