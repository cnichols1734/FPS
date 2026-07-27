using System;
using ArenaFps.Combat;
using UnityEngine;

namespace ArenaFps.Core
{
    /// <summary>
    /// Classic COD-style Team Deathmatch: score kills for your side until score or time limit.
    /// </summary>
    public sealed class MatchController : MonoBehaviour
    {
        static MatchController _instance;

        public static MatchController Instance
        {
            get
            {
                if (_instance == null)
                    _instance = FindAnyObjectByType<MatchController>();
                return _instance;
            }
            private set => _instance = value;
        }

        [SerializeField] int scoreLimit = 75;
        [SerializeField] float timeLimitSeconds = 600f;
        [SerializeField] bool running = true;

        public int BlueScore { get; private set; }
        public int RedScore { get; private set; }
        public float TimeRemaining { get; private set; }
        public int ScoreLimit => scoreLimit;
        public bool IsRunning => running && !IsOver;
        public bool IsOver { get; private set; }
        public TeamId WinningTeam { get; private set; }

        public static event Action<TeamId, int, int> ScoreChanged;
        public static event Action<TeamId> MatchEnded;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            TimeRemaining = timeLimitSeconds;
        }

        void OnEnable() => CombatEvents.Killed += OnKilled;
        void OnDisable() => CombatEvents.Killed -= OnKilled;

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        void Update()
        {
            if (!IsRunning)
                return;

            TimeRemaining = Mathf.Max(0f, TimeRemaining - Time.deltaTime);
            if (TimeRemaining <= 0f)
                EndByTime();
        }

        void OnKilled(Damageable target, DamageInfo info)
        {
            if (!IsRunning || target == null)
                return;

            var victim = target.GetComponent<TeamMember>();
            if (victim == null || victim.Team == TeamId.None)
                return;

            // Award the kill to the opposite of the victim's team (COD TDM scoring).
            if (victim.Team == TeamId.Blue)
                RedScore++;
            else if (victim.Team == TeamId.Red)
                BlueScore++;
            else
                return;

            ScoreChanged?.Invoke(victim.Team == TeamId.Blue ? TeamId.Red : TeamId.Blue, BlueScore, RedScore);

            if (BlueScore >= scoreLimit)
                EndMatch(TeamId.Blue);
            else if (RedScore >= scoreLimit)
                EndMatch(TeamId.Red);
        }

        void EndByTime()
        {
            if (BlueScore == RedScore)
                EndMatch(TeamId.None);
            else
                EndMatch(BlueScore > RedScore ? TeamId.Blue : TeamId.Red);
        }

        void EndMatch(TeamId winner)
        {
            if (IsOver)
                return;
            IsOver = true;
            running = false;
            WinningTeam = winner;
            MatchEnded?.Invoke(winner);
            Debug.Log($"[Match] TDM over — Blue {BlueScore} : {RedScore} Red. Winner={winner}");
        }

        public void Configure(int scoreTo, float seconds)
        {
            scoreLimit = Mathf.Max(1, scoreTo);
            timeLimitSeconds = Mathf.Max(30f, seconds);
            TimeRemaining = timeLimitSeconds;
        }

        public static string FormatClock(float seconds)
        {
            int total = Mathf.CeilToInt(Mathf.Max(0f, seconds));
            return $"{total / 60:00}:{total % 60:00}";
        }
    }
}
