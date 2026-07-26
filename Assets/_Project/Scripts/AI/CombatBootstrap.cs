using ArenaFps.Audio;
using ArenaFps.Ballistics;
using ArenaFps.Combat;
using ArenaFps.Core;
using ArenaFps.Feedback;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaFps.AI
{
    /// <summary>
    /// Stands the encounter up at runtime so pressing Play is enough — no editor menu required.
    /// Bots are built in code rather than from a prefab because the rig is procedural.
    /// </summary>
    public sealed class CombatBootstrap : MonoBehaviour
    {
        [SerializeField] int botCount = 1;
        [SerializeField] bool spawnIfMissing = true;
        [SerializeField] float respawnDelay = 2.5f;
        [SerializeField] bool respawn = true;

        int _spawnIndex;
        int _alive;

        static readonly Vector3[] Spots =
        {
            new(-9f, 0f, 9f),
            new(9f, 0f, 7f),
            new(0f, 0f, 13f),
            new(-5f, 0f, -3f),
            new(7f, 0f, 12f),
            new(-11f, 0f, 2f),
            new(11f, 0f, -2f),
            new(2f, 0f, 17f),
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoAttach()
        {
            if (FindAnyObjectByType<CombatBootstrap>() != null)
                return;
            var go = new GameObject("__CombatBootstrap");
            go.AddComponent<CombatBootstrap>();
        }

        void Start()
        {
            if (!spawnIfMissing)
                return;

            // Bake the audio bank and the FX batches before the first shot, not during it.
            SfxBank.Prewarm();
            _ = ImpactFx.Instance;
            _ = Sfx3D.Instance;

            if (FindObjectsByType<BotBrain>().Length > 0)
                return;

            EnsureNavMesh();
            TagBreakables();
            for (int i = 0; i < botCount; i++)
                SpawnNext();
        }

        void SpawnNext()
        {
            if (_alive >= botCount)
                return;

            int index = _spawnIndex++;
            var position = Spots[index % Spots.Length];
            if (NavMesh.SamplePosition(position, out var hit, 5f, NavMesh.AllAreas))
                position = hit.position;

            var rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            var bot = BotFactory.Create(position, rotation);
            bot.name = $"Bot_{index + 1}";
            _alive++;

            if (!respawn)
                return;

            var damageable = bot.GetComponent<Damageable>();
            if (damageable == null)
            {
                Debug.LogWarning($"[CombatBootstrap] {bot.name} has no Damageable — it will never respawn.");
                return;
            }

            damageable.onDeath.AddListener(() => StartCoroutine(Recycle(bot)));
        }

        System.Collections.IEnumerator Recycle(GameObject bot)
        {
            _alive = Mathf.Max(0, _alive - 1);
            yield return new WaitForSeconds(respawnDelay);
            if (bot != null)
                Destroy(bot);
            yield return new WaitForSeconds(0.1f);
            SpawnNext();
        }

        void EnsureNavMesh()
        {
            if (NavMesh.SamplePosition(Vector3.zero, out _, 8f, NavMesh.AllAreas))
                return;

            var surfaceType = System.Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
            if (surfaceType == null)
            {
                Debug.LogWarning("[CombatBootstrap] NavMeshSurface unavailable — bots cannot path. Run Arena FPS → Spawn Combat.");
                return;
            }

            var go = new GameObject("__RuntimeNavMesh");
            var surface = go.AddComponent(surfaceType);

            // Keep the viewmodel and debris out of the bake before it runs, not after.
            var layerMask = surfaceType.GetProperty("layerMask");
            if (layerMask != null && layerMask.CanWrite)
                layerMask.SetValue(surface, (LayerMask)GameLayers.NavMeshMask);

            surfaceType.GetMethod("BuildNavMesh")?.Invoke(surface, null);
        }

        void TagBreakables()
        {
            foreach (var name in new[] { "Cover_A", "Cover_B", "Cover_C" })
            {
                var go = GameObject.Find(name);
                if (go == null)
                    continue;
                if (go.GetComponent<BreakableCover>() == null)
                    go.AddComponent<BreakableCover>();
                if (go.GetComponent<SurfaceTag>() == null)
                    go.AddComponent<SurfaceTag>();
            }
        }
    }
}
