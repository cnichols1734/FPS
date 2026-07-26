#if UNITY_EDITOR
using ArenaFps.DevTools;
using UnityEditor;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Drops the self test into the open scene and enters play mode. Play mode runs against the
    /// in-memory scene, so the probe disappears again on exit and nothing is committed.
    /// </summary>
    public static class RunCombatSelfTest
    {
        const string ProbeName = "__CombatSelfTest";

        [MenuItem("Arena FPS/Run Combat Self Test", priority = 20)]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[RunCombatSelfTest] Already entering play mode.");
                return;
            }

            var probe = GameObject.Find(ProbeName) ?? new GameObject(ProbeName);
            if (probe.GetComponent<CombatSelfTest>() == null)
                probe.AddComponent<CombatSelfTest>();

            EditorApplication.EnterPlaymode();
        }
    }
}
#endif
