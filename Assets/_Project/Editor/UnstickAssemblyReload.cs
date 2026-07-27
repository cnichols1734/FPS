#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace ArenaFps.Editor
{
    /// <summary>
    /// Recovery for a stuck assembly reload lock.
    ///
    /// <see cref="EditorApplication.LockReloadAssemblies"/> is a counter, not a flag. A tool that
    /// locks and then throws before unlocking leaves the counter above zero forever, and the editor
    /// silently stops recompiling and stops finishing script imports — it will even enter play mode
    /// on stale assemblies with only a warning. The MCP bridge does this when it loses its
    /// connection mid-operation.
    ///
    /// Unlocking is only safe to trigger by hand: draining the counter automatically would stomp on
    /// the legitimate locks the test runner and package manager take.
    /// </summary>
    public static class UnstickAssemblyReload
    {
        // Deep enough to drain any realistic leak, bounded so a genuine bug cannot spin here.
        const int MaxUnlocks = 32;

        [MenuItem("Arena FPS/Unstick Assembly Reload")]
        public static void Run()
        {
            for (int i = 0; i < MaxUnlocks; i++)
                EditorApplication.UnlockReloadAssemblies();

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation();
            Debug.Log("[ArenaFps] Reload lock cleared and recompilation requested.");
        }
    }
}
#endif
