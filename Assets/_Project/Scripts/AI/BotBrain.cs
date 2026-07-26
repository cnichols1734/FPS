using ArenaFps.Ballistics;
using ArenaFps.Combat;
using ArenaFps.Core;
using ArenaFps.Player;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaFps.AI
{
    /// <summary>
    /// Combat bot: sense the player, close or take cover, peek and shoot, repath when cover breaks.
    /// Sight and cover queries deliberately test world geometry only — masking against everything
    /// meant a bot's own limb colliders blocked its line of sight and it stood there blind.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(Damageable))]
    public sealed class BotBrain : MonoBehaviour
    {
        [Header("Perception")]
        [SerializeField] Transform eyes;
        [SerializeField] float viewRange = 55f;
        [SerializeField] float viewAngle = 120f;
        [SerializeField] float hearRange = 22f;
        [SerializeField] Vector2 reactionTime = new Vector2(0.18f, 0.42f);

        [Header("Movement")]
        [SerializeField] float baseSpeed = 3.7f;
        [SerializeField] float coverSearchRadius = 15f;
        [SerializeField] float rethinkInterval = 0.55f;
        [SerializeField] float strafeInterval = 1.8f;

        NavMeshAgent _agent;
        Damageable _self;
        BotWeapon _weapon;
        BotPoseDriver _pose;
        BotRig _rig;

        Transform _player;
        Transform _playerHead;
        Damageable _playerHealth;
        CharacterController _playerBody;

        float _nextThink;
        float _sightedAt = -1f;
        float _reaction;
        float _nextStrafe;
        Vector3 _strafeTarget;
        Vector3 _coverPoint;
        bool _hasCover;
        float _suppression;

        void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _self = GetComponent<Damageable>();
            _weapon = GetComponent<BotWeapon>();
            _pose = GetComponent<BotPoseDriver>();
            _rig = GetComponent<BotRig>();
            _reaction = Random.Range(reactionTime.x, reactionTime.y);

            if (eyes == null)
                eyes = _rig != null && _rig.Head != null ? _rig.Head.Transform : transform;

            CoverBrokenBus.Broken += OnCoverBroken;
        }

        void OnDestroy() => CoverBrokenBus.Broken -= OnCoverBroken;

        void Start() => FindPlayer();

        void Update()
        {
            if (_self.IsDead)
            {
                if (_agent.enabled)
                    _agent.isStopped = true;
                return;
            }

            if (_player == null)
            {
                FindPlayer();
                return;
            }

            float dt = Time.deltaTime;
            _suppression = Mathf.MoveTowards(_suppression, 0f, dt * 0.4f);

            if (_pose != null && _agent.enabled)
                _agent.speed = baseSpeed * _pose.SpeedScale;

            bool playerAlive = _playerHealth == null || !_playerHealth.IsDead;
            bool canSee = playerAlive && CanSeePlayer();
            if (canSee)
            {
                if (_sightedAt < 0f)
                    _sightedAt = Time.time;
                FacePlayer(dt);
                _pose?.SetAimTarget(AimPoint());
            }
            else
            {
                _sightedAt = -1f;
                _pose?.ClearAim();
            }

            if (Time.time >= _nextThink)
            {
                _nextThink = Time.time + rethinkInterval;
                Think(canSee);
            }

            if (canSee && _weapon != null && Time.time - _sightedAt >= _reaction)
            {
                float distance = Vector3.Distance(transform.position, _player.position);
                if (distance <= _weapon.Range)
                    _weapon.Fire(AimPoint(), _playerHead);
            }
        }

        Vector3 AimPoint()
        {
            var target = _player.position + Vector3.up * 1.15f;
            if (_playerBody != null)
            {
                // Modest lead so a moving player is not a free win, but never a perfect solution.
                var velocity = _playerBody.velocity;
                velocity.y = 0f;
                target += velocity * 0.11f;
            }
            return target;
        }

        void FacePlayer(float dt)
        {
            var look = _player.position - transform.position;
            look.y = 0f;
            if (look.sqrMagnitude < 0.001f)
                return;
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(look.normalized),
                1f - Mathf.Exp(-10f * dt));
        }

        void Think(bool canSee)
        {
            float dist = Vector3.Distance(transform.position, _player.position);
            float engageRange = _weapon != null ? _weapon.Range * 0.6f : 22f;

            if (!_agent.enabled)
                return;

            if (!canSee && dist > hearRange)
            {
                _agent.isStopped = false;
                _agent.SetDestination(_player.position);
                _hasCover = false;
                return;
            }

            if (_suppression > 0.45f)
            {
                if (!_hasCover || Vector3.Distance(transform.position, _coverPoint) < 1.2f)
                    FindCover();
                if (_hasCover)
                {
                    _agent.isStopped = false;
                    _agent.SetDestination(_coverPoint);
                    return;
                }
            }

            if (dist > engageRange)
            {
                _agent.isStopped = false;
                _agent.SetDestination(_player.position);
                return;
            }

            // In range and unsuppressed: keep moving laterally instead of standing in the open.
            if (Time.time >= _nextStrafe)
            {
                _nextStrafe = Time.time + strafeInterval + Random.Range(-0.4f, 0.6f);
                var side = Vector3.Cross(Vector3.up, (_player.position - transform.position).normalized);
                var candidate = transform.position + side * Random.Range(-5f, 5f) + Random.insideUnitSphere * 1.5f;
                _strafeTarget = NavMesh.SamplePosition(candidate, out var hit, 3f, NavMesh.AllAreas)
                    ? hit.position
                    : transform.position;
            }

            _agent.isStopped = false;
            _agent.SetDestination(_strafeTarget);
        }

        bool CanSeePlayer()
        {
            if (_player == null)
                return false;

            var target = _player.position + Vector3.up * 1.15f;
            var to = target - eyes.position;
            float dist = to.magnitude;
            if (dist > viewRange)
                return false;

            if (Vector3.Angle(transform.forward, to) > viewAngle * 0.5f && dist > hearRange * 0.5f)
                return false;

            // World-only mask: anything solid in the way blocks, and nothing else can.
            return !Physics.Raycast(eyes.position, to / dist, dist - 0.2f, GameLayers.SightMask, QueryTriggerInteraction.Ignore);
        }

        void FindCover()
        {
            _hasCover = false;
            var away = (transform.position - _player.position).normalized;
            for (int i = 0; i < 10; i++)
            {
                var dir = Quaternion.Euler(0f, i * 36f, 0f) * away;
                var candidate = transform.position + dir * Random.Range(3f, coverSearchRadius);
                if (!NavMesh.SamplePosition(candidate, out var hit, 2.5f, NavMesh.AllAreas))
                    continue;

                var probe = hit.position + Vector3.up * 1.15f;
                var toPlayer = _player.position + Vector3.up * 1.15f - probe;
                if (Physics.Raycast(probe, toPlayer.normalized, toPlayer.magnitude * 0.92f, GameLayers.SightMask))
                {
                    _coverPoint = hit.position;
                    _hasCover = true;
                    return;
                }
            }
        }

        void OnCoverBroken(Vector3 position)
        {
            if (_hasCover && Vector3.Distance(position, _coverPoint) < 4f)
            {
                _hasCover = false;
                _nextThink = 0f;
            }
            _suppression = Mathf.Max(_suppression, 0.7f);
        }

        public void NotifyIncomingFire()
        {
            _suppression = 1f;
            _nextThink = 0f;
        }

        void FindPlayer()
        {
            var fps = FindAnyObjectByType<FpsController>();
            if (fps == null)
                return;

            _player = fps.transform;
            _playerBody = fps.GetComponent<CharacterController>();
            _playerHealth = fps.GetComponent<Damageable>();
            if (_playerHealth == null)
                _playerHealth = fps.gameObject.AddComponent<Damageable>();
            _playerHealth.MarkAsPlayer();

            var cam = fps.GetComponentInChildren<Camera>();
            _playerHead = cam != null ? cam.transform : _player;
        }
    }
}
