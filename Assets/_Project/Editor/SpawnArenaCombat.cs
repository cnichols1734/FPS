#if UNITY_EDITOR
using ArenaFps.AI;
using ArenaFps.Ballistics;
using ArenaFps.Combat;
using ArenaFps.Core;
using ArenaFps.Player;
using ArenaFps.UI;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Prepares the arena for combat: ballistics surfaces, breakable cover, navigation and the
    /// player's combat components. Bots are intentionally not authored into the scene — the rig is
    /// procedural, so <c>CombatBootstrap</c> builds them at runtime.
    /// </summary>
    public static class SpawnArenaCombat
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string StaleBotPrefab = "Assets/_Project/Prefabs/AI/Bot.prefab";
        const string MaterialDir = "Assets/_Project/Art/Materials";

        [MenuItem("Arena FPS/Spawn Combat (Surfaces + NavMesh + Player)")]
        public static void Run()
        {
            var scene = EditorSceneManager.GetActiveScene().path.EndsWith("Arena.unity")
                ? EditorSceneManager.GetActiveScene()
                : EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            RemoveStaleBots();
            TagSurfacesAndBreakables();
            EnsurePlayerCombatComponents();
            BakeNavMesh();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ArenaFps] Arena ready: surfaces tagged, cover breakable, navmesh baked. Bots spawn on Play.");
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }

        /// <summary>
        /// The old single-capsule bot would suppress runtime spawning, leaving a scene full of
        /// blobs and no rigged soldiers.
        /// </summary>
        static void RemoveStaleBots()
        {
            foreach (var brain in Object.FindObjectsByType<BotBrain>())
                Object.DestroyImmediate(brain.gameObject);

            if (AssetDatabase.LoadAssetAtPath<GameObject>(StaleBotPrefab) != null)
                AssetDatabase.DeleteAsset(StaleBotPrefab);
        }

        static void TagSurfacesAndBreakables()
        {
            var concrete = Surface("Surface_Concrete", SurfaceKind.Concrete, 2400f, 0.78f, 0.3f, false);
            var wood = Surface("Surface_Wood", SurfaceKind.Wood, 700f, 0.3f, 0.08f, true);
            var metal = Surface("Surface_MetalThin", SurfaceKind.MetalThin, 7800f, 0.92f, 0.006f, true);
            var drywall = Surface("Surface_Drywall", SurfaceKind.Drywall, 750f, 0.12f, 0.014f, true);

            // Three different cover materials so penetration, ricochet and spall are all visible in
            // one encounter rather than needing a specific wall to be found.
            Cover("Cover_A", wood, 0.08f, 90f);
            Cover("Cover_B", metal, 0.006f, 70f);
            Cover("Cover_C", drywall, 0.014f, 45f);

            foreach (var name in new[] { "Wall_West", "Wall_East", "Wall_North", "Wall_South", "Ground" })
            {
                var go = GameObject.Find(name);
                if (go == null)
                    continue;
                var tag = go.GetComponent<SurfaceTag>() ?? go.AddComponent<SurfaceTag>();
                tag.surface = concrete;
                tag.thicknessOverride = name == "Ground" ? 1f : 0.35f;
                go.layer = GameLayers.Default;
            }
        }

        static void Cover(string name, SurfaceDefinition surface, float thickness, float health)
        {
            var go = GameObject.Find(name);
            if (go == null)
                return;

            go.layer = GameLayers.Default;

            var tag = go.GetComponent<SurfaceTag>() ?? go.AddComponent<SurfaceTag>();
            tag.surface = surface;
            tag.thicknessOverride = thickness;

            var breakable = go.GetComponent<BreakableCover>() ?? go.AddComponent<BreakableCover>();
            if (breakable == null)
            {
                Debug.LogWarning($"[SpawnArenaCombat] BreakableCover missing on {name}");
                return;
            }

            var so = new SerializedObject(breakable);
            var maxHealth = so.FindProperty("maxHealth");
            var surfaceProp = so.FindProperty("surface");
            var unbroken = so.FindProperty("unbrokenVisual");
            if (maxHealth != null) maxHealth.floatValue = health;
            if (surfaceProp != null) surfaceProp.objectReferenceValue = surface;
            if (unbroken != null) unbroken.objectReferenceValue = go;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static SurfaceDefinition Surface(string name, SurfaceKind kind, float density, float hardness, float thickness, bool canBreak)
        {
            if (!AssetDatabase.IsValidFolder(MaterialDir))
                AssetDatabase.CreateFolder("Assets/_Project/Art", "Materials");

            string path = $"{MaterialDir}/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<SurfaceDefinition>(path);
            if (existing != null)
            {
                existing.kind = kind;
                existing.density = density;
                existing.hardness = hardness;
                existing.defaultThickness = thickness;
                existing.canBreak = canBreak;
                EditorUtility.SetDirty(existing);
                return existing;
            }

            var created = ScriptableObject.CreateInstance<SurfaceDefinition>();
            created.kind = kind;
            created.density = density;
            created.hardness = hardness;
            created.defaultThickness = thickness;
            created.canBreak = canBreak;
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        static void EnsurePlayerCombatComponents()
        {
            var player = GameObject.Find("Player");
            if (player == null)
                return;

            player.tag = "Player";
            player.layer = GameLayers.Player;

            if (player.GetComponent<Damageable>() == null)
                player.AddComponent<Damageable>();
            if (player.GetComponent<PlayerHealth>() == null)
                player.AddComponent<PlayerHealth>();
            if (player.GetComponent<PlayerCombatFeedback>() == null)
                player.AddComponent<PlayerCombatFeedback>();
            if (player.GetComponent<ScreenLook>() == null)
                player.AddComponent<ScreenLook>();
            if (player.GetComponent<HudView>() == null)
                player.AddComponent<HudView>();
        }

        static void BakeNavMesh()
        {
            foreach (var name in new[]
                     {
                         "Ground", "Cover_A", "Cover_B", "Cover_C",
                         "Wall_West", "Wall_East", "Wall_North", "Wall_South"
                     })
            {
                var go = GameObject.Find(name);
                if (go == null)
                    continue;
                GameObjectUtility.SetStaticEditorFlags(go,
                    StaticEditorFlags.BatchingStatic);
            }

            var surfaceGo = GameObject.Find("__NavMeshSurface") ?? new GameObject("__NavMeshSurface");
            var surface = surfaceGo.GetComponent<NavMeshSurface>() ?? surfaceGo.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = GameLayers.WorldMask;
            surface.BuildNavMesh();
        }
    }
}
#endif
