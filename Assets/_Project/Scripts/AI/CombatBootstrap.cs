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
    /// Boots a solo TDM match: player + ally bots vs enemy bots, score limit, team spawns.
    /// </summary>
    public sealed class CombatBootstrap : MonoBehaviour
    {
        [SerializeField] int allyBots = 4;
        [SerializeField] int enemyBots = 5;
        [SerializeField] int scoreLimit = 75;
        [SerializeField] float matchSeconds = 600f;
        [SerializeField] bool spawnIfMissing = true;
        [SerializeField] float respawnDelay = 2.5f;
        [SerializeField] bool respawn = true;

        int _blueIndex;
        int _redIndex;
        int _aliveBlue;
        int _aliveRed;

        // Overflow spawn pads (OVERFLOW_SPEC §g * 1.22 position scale). Matches AaaEnvironmentPass.
        static readonly Vector3[] BlueFallback =
        {
            new(-9.76f, 0f, -68.32f), new(9.76f, 0f, -68.32f), new(-29.28f, 0f, -63.44f),
            new(31.72f, 0f, -63.44f), new(0f, 0f, -61f), new(-17.08f, 0f, -58.56f),
        };

        static readonly Vector3[] RedFallback =
        {
            new(9.76f, 0f, 68.32f), new(-9.76f, 0f, 68.32f), new(31.72f, 0f, 63.44f),
            new(-29.28f, 0f, 63.44f), new(0f, 0f, 61f), new(17.08f, 0f, 58.56f),
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

            SfxBank.Prewarm();
            _ = ImpactFx.Instance;
            _ = Sfx3D.Instance;

            EnsureMatch();
            AssignPlayerTeam();

            if (FindObjectsByType<BotBrain>().Length > 0)
                return;

            EnsureNavMesh();
            TagBreakables();

            for (int i = 0; i < allyBots; i++)
                SpawnTeam(TeamId.Blue);
            for (int i = 0; i < enemyBots; i++)
                SpawnTeam(TeamId.Red);

            Debug.Log($"[CombatBootstrap] TDM live — Blue {allyBots}+player vs Red {enemyBots}. Score to {scoreLimit}.");
        }

        void EnsureMatch()
        {
            if (MatchController.Instance != null)
            {
                MatchController.Instance.Configure(scoreLimit, matchSeconds);
                return;
            }

            var go = new GameObject("__Match");
            var match = go.AddComponent<MatchController>();
            match.Configure(scoreLimit, matchSeconds);
        }

        void AssignPlayerTeam()
        {
            var player = GameObject.Find("Player");
            if (player == null)
            {
                var fps = FindAnyObjectByType<Player.FpsController>();
                player = fps != null ? fps.gameObject : null;
            }
            if (player == null)
                return;

            var team = player.GetComponent<TeamMember>() ?? player.AddComponent<TeamMember>();
            team.Team = TeamId.Blue;

            var damageable = player.GetComponent<Damageable>();
            damageable?.MarkAsPlayer();
        }

        void SpawnTeam(TeamId team)
        {
            bool blue = team == TeamId.Blue;
            if (blue && _aliveBlue >= allyBots)
                return;
            if (!blue && _aliveRed >= enemyBots)
                return;

            var position = NextSpawn(team);
            if (NavMesh.SamplePosition(position, out var hit, 6f, NavMesh.AllAreas))
                position = hit.position;

            // Face toward mid so the first peek is into the map.
            var face = Quaternion.LookRotation(blue ? Vector3.forward : Vector3.back);
            var bot = BotFactory.Create(position, face, 100f, team);
            int index = blue ? ++_blueIndex : ++_redIndex;
            bot.name = $"{(blue ? "Blue" : "Red")}_Bot_{index}";
            if (blue) _aliveBlue++; else _aliveRed++;

            if (!respawn)
                return;

            var damageable = bot.GetComponent<Damageable>();
            if (damageable == null)
                return;

            var capturedTeam = team;
            damageable.onDeath.AddListener(() => StartCoroutine(Recycle(bot, capturedTeam)));
        }

        System.Collections.IEnumerator Recycle(GameObject bot, TeamId team)
        {
            if (team == TeamId.Blue)
                _aliveBlue = Mathf.Max(0, _aliveBlue - 1);
            else
                _aliveRed = Mathf.Max(0, _aliveRed - 1);

            yield return new WaitForSeconds(respawnDelay);
            if (bot != null)
                Destroy(bot);
            yield return new WaitForSeconds(0.1f);

            if (MatchController.Instance != null && !MatchController.Instance.IsRunning)
                yield break;

            SpawnTeam(team);
        }

        Vector3 NextSpawn(TeamId team)
        {
            bool blue = team == TeamId.Blue;
            int idx = blue ? _blueIndex : _redIndex;
            string prefix = blue ? "Spawn_Blue_" : "Spawn_Red_";
            var named = GameObject.Find(prefix + ((idx % 6) + 1));
            if (named != null)
                return named.transform.position;

            var fallback = blue ? BlueFallback : RedFallback;
            return fallback[idx % fallback.Length];
        }

        void EnsureNavMesh()
        {
            // Prefer several sample points across the 118x154 Overflow footprint.
            if (NavMesh.SamplePosition(Vector3.zero, out _, 24f, NavMesh.AllAreas)
                || NavMesh.SamplePosition(new Vector3(0f, 0f, -50f), out _, 16f, NavMesh.AllAreas)
                || NavMesh.SamplePosition(new Vector3(0f, 0f, 50f), out _, 16f, NavMesh.AllAreas)
                || NavMesh.SamplePosition(new Vector3(-30f, 0f, 0f), out _, 16f, NavMesh.AllAreas)
                || NavMesh.SamplePosition(new Vector3(30f, 0f, 0f), out _, 16f, NavMesh.AllAreas))
                return;

            var surfaceType = System.Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
            if (surfaceType == null)
            {
                Debug.LogWarning("[CombatBootstrap] NavMeshSurface unavailable — bots cannot path.");
                return;
            }

            var go = new GameObject("__RuntimeNavMesh");
            var surface = go.AddComponent(surfaceType);

            // Physics colliders avoid "mesh not readable" spam from primitives / FBX.
            var useGeometry = surfaceType.GetProperty("useGeometry");
            if (useGeometry != null && useGeometry.CanWrite)
            {
                var geometryType = System.Type.GetType(
                    "UnityEngine.AI.NavMeshCollectGeometry, UnityEngine");
                if (geometryType != null)
                    useGeometry.SetValue(surface, System.Enum.Parse(geometryType, "PhysicsColliders"));
            }

            var layerMask = surfaceType.GetProperty("layerMask");
            if (layerMask != null && layerMask.CanWrite)
                layerMask.SetValue(surface, (LayerMask)GameLayers.NavMeshMask);

            var collectObjects = surfaceType.GetProperty("collectObjects");
            if (collectObjects != null && collectObjects.CanWrite)
            {
                var collectType = System.Type.GetType(
                    "Unity.AI.Navigation.CollectObjects, Unity.AI.Navigation");
                if (collectType != null)
                    collectObjects.SetValue(surface, System.Enum.Parse(collectType, "All"));
            }

            surfaceType.GetMethod("BuildNavMesh")?.Invoke(surface, null);
        }

        void TagBreakables()
        {
            foreach (var go in FindObjectsByType<Transform>())
            {
                if (!go.name.StartsWith("Cover_"))
                    continue;
                if (go.GetComponent<BreakableCover>() == null)
                    go.gameObject.AddComponent<BreakableCover>();
                if (go.GetComponent<SurfaceTag>() == null)
                    go.gameObject.AddComponent<SurfaceTag>();
            }
        }
    }
}
