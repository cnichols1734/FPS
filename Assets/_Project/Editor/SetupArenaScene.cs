#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ArenaFps.Editor
{
    public static class SetupArenaScene
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string PlayerPrefab = "Assets/_Project/Prefabs/Player/Player.prefab";

        [MenuItem("Arena FPS/Setup Arena Scene (Place Player)")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Remove placeholder camera if present
            foreach (var cam in Object.FindObjectsByType<Camera>())
            {
                if (cam.transform.root.name == "Main Camera")
                    Object.DestroyImmediate(cam.gameObject);
            }

            var existing = GameObject.Find("Player");
            if (existing != null)
                Object.DestroyImmediate(existing);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefab);
            if (prefab == null)
                throw new System.Exception("Player prefab missing — run Create Player Prefab first.");

            var spawn = GameObject.Find("PlayerSpawn");
            var pos = spawn != null ? spawn.transform.position : new Vector3(0f, 1.7f, -8f);
            var player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            player.name = "Player";
            player.transform.position = pos;
            player.transform.rotation = Quaternion.identity;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[ArenaFps] Player placed in Arena scene.");
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }
    }
}
#endif
