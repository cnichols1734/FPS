using UnityEngine;

namespace ArenaFps.Core
{
    /// <summary>
    /// Runtime defaults that must apply before the first frame of gameplay.
    /// </summary>
    public static class GameBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ApplyRuntimeDefaults()
        {
            Application.targetFrameRate = 120;
            QualitySettings.vSyncCount = 0;
            QualitySettings.maxQueuedFrames = 1;

            // Prefer uninterrupted focus for FPS feel on laptop.
            Application.runInBackground = true;
        }
    }
}
