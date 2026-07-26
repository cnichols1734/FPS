#if UNITY_EDITOR
using ArenaFps.Combat;
using ArenaFps.DevTools;
using ArenaFps.Input;
using ArenaFps.Player;
using ArenaFps.UI;
using ArenaFps.Weapons;
using UnityEditor;
using UnityEngine;

namespace ArenaFps.Editor
{
    public static class CreatePlayerPrefab
    {
        const string PrefabPath = "Assets/_Project/Prefabs/Player/Player.prefab";

        [MenuItem("Arena FPS/Create Player Prefab")]
        public static void Run()
        {
            var root = new GameObject("Player");
            var cc = root.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.35f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.stepOffset = 0.35f;
            cc.slopeLimit = 50f;

            root.tag = "Player";
            root.AddComponent<Damageable>();
            root.AddComponent<FpsController>();
            root.AddComponent<PlayerHealth>();
            root.AddComponent<WeaponController>();
            root.AddComponent<HudView>();
            root.AddComponent<DevCapture>();

            var systems = new GameObject("InputSystems");
            systems.transform.SetParent(root.transform, false);
            systems.AddComponent<GameInput>();
            systems.AddComponent<DualSenseDriver>();
            systems.AddComponent<LatencyProbe>();

            var camPivot = new GameObject("CameraPivot");
            camPivot.transform.SetParent(root.transform, false);
            camPivot.transform.localPosition = new Vector3(0f, 1.6f, 0f);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(camPivot.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 250f;
            cam.fieldOfView = 75f;
            camGo.AddComponent<AudioListener>();

            // Wire camera pivot via serialized object
            var fps = root.GetComponent<FpsController>();
            var so = new SerializedObject(fps);
            so.FindProperty("cameraPivot").objectReferenceValue = camPivot.transform;
            so.ApplyModifiedPropertiesWithoutUndo();

            // Weapon hold point
            var weaponRoot = new GameObject("WeaponRoot");
            weaponRoot.transform.SetParent(camPivot.transform, false);
            weaponRoot.transform.localPosition = new Vector3(0.2f, -0.2f, 0.35f);

            var viewmodel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            viewmodel.name = "PlaceholderAR";
            Object.DestroyImmediate(viewmodel.GetComponent<Collider>());
            viewmodel.transform.SetParent(weaponRoot.transform, false);
            viewmodel.transform.localPosition = Vector3.zero;
            viewmodel.transform.localScale = new Vector3(0.12f, 0.12f, 0.55f);
            var gunMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            gunMat.color = new Color(1f, 0.35f, 0.05f);
            System.IO.Directory.CreateDirectory("Assets/_Project/Art/Materials");
            AssetDatabase.CreateAsset(gunMat, "Assets/_Project/Art/Materials/PlaceholderGun_Orange.mat");
            viewmodel.GetComponent<MeshRenderer>().sharedMaterial = gunMat;

            System.IO.Directory.CreateDirectory("Assets/_Project/Prefabs/Player");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);

            Debug.Log($"[ArenaFps] Player prefab written to {PrefabPath}");
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }
    }
}
#endif
