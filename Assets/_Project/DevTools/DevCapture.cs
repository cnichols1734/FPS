using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ArenaFps.DevTools
{
    /// <summary>
    /// Deterministic screenshot / frame-strip capture for the critic loop.
    /// Runtime: press F8. Also callable from code/tests.
    /// </summary>
    public sealed class DevCapture : MonoBehaviour
    {
        [SerializeField] Camera captureCamera;
        [SerializeField] string relativeOutDir = "Tools/VisualQA/out";
        [SerializeField] int stripFrames = 8;
        [SerializeField] float stripDeltaTime = 1f / 60f;

        void Awake()
        {
            if (captureCamera == null)
                captureCamera = Camera.main;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f8Key.wasPressedThisFrame)
                StartCoroutine(CaptureStrip($"strip_{System.DateTime.Now:yyyyMMdd_HHmmss}"));
        }

        public IEnumerator CaptureStrip(string label)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativeOutDir, label));
            Directory.CreateDirectory(root);

            var prev = Time.captureDeltaTime;
            Time.captureDeltaTime = stripDeltaTime;

            for (int i = 0; i < stripFrames; i++)
            {
                yield return new WaitForEndOfFrame();
                string path = Path.Combine(root, $"frame_{i:00}.png");
                ScreenCapture.CaptureScreenshot(path);
                Debug.Log($"[DevCapture] {path}");
            }

            Time.captureDeltaTime = prev;
        }
    }
}
