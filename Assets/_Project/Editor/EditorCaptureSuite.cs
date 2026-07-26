#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Headless-friendly capture: renders from named bookmarks without requiring Play mode.
    /// Batch: Unity -batchmode -executeMethod ArenaFps.Editor.EditorCaptureSuite.Run
    /// </summary>
    public static class EditorCaptureSuite
    {
        const string ScenePath = "Assets/_Project/Scenes/Arena.unity";
        const string OutRoot = "Tools/VisualQA/out";

        [MenuItem("Arena FPS/Capture Suite (Editor)")]
        public static void Run()
        {
            if (!EditorSceneManager.GetActiveScene().path.EndsWith("Arena.unity"))
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", OutRoot, $"editor_{stamp}"));
            Directory.CreateDirectory(dir);

            var bookmarks = new (string name, Vector3 pos, Vector3 lookAt)[]
            {
                ("establishing", new Vector3(0f, 12f, -22f), new Vector3(0f, 1f, 0f)),
                ("alley", new Vector3(-8f, 1.6f, -6f), new Vector3(0f, 1.2f, 4f)),
                ("cover", new Vector3(4f, 1.6f, -2f), new Vector3(-6f, 1f, 4f)),
                ("player_fp", new Vector3(0f, 1.7f, -8f), new Vector3(0f, 1.6f, 0f)),
            };

            var camGo = new GameObject("__CaptureCam");
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 250f;
            cam.fieldOfView = 75f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.allowHDR = true;

            int w = 1280, h = 800;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);

            try
            {
                foreach (var b in bookmarks)
                {
                    camGo.transform.position = b.pos;
                    camGo.transform.rotation = Quaternion.LookRotation((b.lookAt - b.pos).normalized, Vector3.up);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    tex.Apply();
                    RenderTexture.active = null;
                    cam.targetTexture = null;

                    string path = Path.Combine(dir, $"{b.name}.png");
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                    Debug.Log($"[CaptureSuite] {path}");
                }

                File.WriteAllText(Path.Combine(dir, "manifest.json"),
                    $"{{\"count\":{bookmarks.Length},\"dir\":\"{dir.Replace("\\", "/")}\"}}");
            }
            finally
            {
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(camGo);
            }

            Debug.Log($"[CaptureSuite] done → {dir}");
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }
    }
}
#endif
