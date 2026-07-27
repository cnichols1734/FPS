using ArenaFps.Ballistics;
using ArenaFps.Combat;
using ArenaFps.Core;
using ArenaFps.Player;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaFps.AI
{
    /// <summary>
    /// Combat bot: hunt the nearest enemy (player or bot), take cover, peek and shoot.
    /// Sight queries use world geometry only so limb colliders never blind the AI.
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
        [SerializeField] float retargetInterval = 0.35f;

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
        TeamMember _team;

        Transform _target;
        Transform _targetAim;
        Damageable _targetHealth;
        CharacterController _targetBody;
        bool _targetIsPlayer;

        Transform _playerHead;

        float _nextThink;
        float _nextRetarget;
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
            _team = GetComponent<TeamMember>();
            _reaction = Random.Range(reactionTime.x, reactionTime.y);

            if (eyes == null)
                eyes = _rig != null && _rig.Head != null ? _rig.Head.Transform : transform;

            CoverBrokenBus.Broken += OnCoverBroken;
        }

        void OnDestroy() => CoverBrokenBus.Broken -= OnCoverBroken;

        void Start() => AcquireTarget(force: true);

        void Update()
        {
            if (_self.IsDead)
            {
                if (_agent.enabled && _agent.isOnNavMesh)
                    _agent.isStopped = true;
                return;
            }

            if (!_agent.enabled || !_agent.isOnNavMesh)
                return;

            if (Time.time >= _nextRetarget || _target == null || _targetHealth == null || _targetHealth.IsDead)
                AcquireTarget(force: false);

            if (_target == null)
                return;

            float dt = Time.deltaTime;
            _suppression = Mathf.MoveTowards(_suppression, 0f, dt * 0.4f);

            if (_pose != null && _agent.enabled)
                _agent.speed = baseSpeed * _pose.SpeedScale;

            bool canSee = CanSeeTarget();
            if (canSee)
            {
                if (_sightedAt < 0f)
                    _sightedAt = Time.time;
                FaceTarget(dt);
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
                float distance = Vector3.Distance(transform.position, _target.position);
                if (distance <= _weapon.Range)
                    _weapon.Fire(AimPoint(), _targetIsPlayer ? _playerHead : _targetAim);
            }
        }

        void AcquireTarget(bool force)
        {
            _nextRetarget = Time.time + retargetInterval;
            if (!force && _target != null && _targetHealth != null && !_targetHealth.IsDead)
            {
                // Stick to current target if still in engagement range.
                if (Vector3.Distance(transform.position, _target.position) < viewRange * 0.85f)
                    return;
            }

            _target = null;
            _targetHealth = null;
            _targetAim = null;
            _targetBody = null;
            _targetIsPlayer = false;

            float best = float.MaxValue;
            var myTeam = _team != null ? _team.Team : TeamId.None;

            foreach (var member in FindObjectsByType<TeamMember>())
            {
                if (member == null || member.gameObject == gameObject)
                    continue;
                if (!member.IsEnemyOf(myTeam))
                    continue;

                var dmg = member.GetComponent<Damageable>();
                if (dmg == null || dmg.IsDead)
                    continue;

                float dist = Vector3.Distance(transform.position, member.transform.position);
                if (dist >= best)
                    continue;

                best = dist;
                _target = member.transform;
                _targetHealth = dmg;
                _targetIsPlayer = dmg.IsPlayer;
                _targetBody = member.GetComponent<CharacterController>();

                if (_targetIsPlayer)
                {
                    var cam = member.GetComponentInChildren<Camera>();
                    _playerHead = cam != null ? cam.transform : _target;
                    _targetAim = _playerHead;
                }
                else
                {
                    var rig = member.GetComponent<BotRig>();
                    _targetAim = rig != null && rig.Head != null ? rig.Head.Transform : _target;
                }
            }

            // Fallback: old behaviour if no teams stamped yet.
            if (_target == null)
            {
                var fps = FindAnyObjectByType<FpsController>();
                if (fps == null)
                    return;
                var dmg = fps.GetComponent<Damageable>() ?? fps.gameObject.AddComponent<Damageable>();
                dmg.MarkAsPlayer();
                _target = fps.transform;
                _targetHealth = dmg;
                _targetBody = fps.GetComponent<CharacterController>();
                _targetIsPlayer = true;
                var cam = fps.GetComponentInChildren<Camera>();
                _playerHead = cam != null ? cam.transform : _target;
                _targetAim = _playerHead;
            }
        }

        Vector3 AimPoint()
        {
            var target = _target.position + Vector3.up * 1.15f;
            if (_targetAim != null)
                target = _targetAim.position;

            if (_targetBody != null)
            {
                var velocity = _targetBody.velocity;
                velocity.y = 0f;
                target += velocity * 0.11f;
            }
            return target;
        }

        void FaceTarget(float dt)
        {
            var look = _target.position - transform.position;
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
            float dist = Vector3.Distance(transform.position, _target.position);
            float engageRange = _weapon != null ? _weapon.Range * 0.6f : 22f;

            if (!_agent.enabled)
                return;

            if (!canSee && dist > hearRange)
            {
                _agent.isStopped = false;
                _agent.SetDestination(_target.position);
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
                _agent.SetDestination(_target.position);
                return;
            }

            if (Time.time >= _nextStrafe)
            {
                _nextStrafe = Time.time + strafeInterval + Random.Range(-0.4f, 0.6f);
                var side = Vector3.Cross(Vector3.up, (_target.position - transform.position).normalized);
                var candidate = transform.position + side * Random.Range(-5f, 5f) + Random.insideUnitSphere * 1.5f;
                _strafeTarget = NavMesh.SamplePosition(candidate, out var hit, 3f, NavMesh.AllAreas)
                    ? hit.position
                    : transform.position;
            }

            _agent.isStopped = false;
            _agent.SetDestination(_strafeTarget);
        }

        bool CanSeeTarget()
        {
            if (_target == null)
                return false;

            var aim = AimPoint();
            var to = aim - eyes.position;
            float dist = to.magnitude;
            if (dist > viewRange)
                return false;

            if (Vector3.Angle(transform.forward, to) > viewAngle * 0.5f && dist > hearRange * 0.5f)
                return false;

            return !Physics.Raycast(eyes.position, to / dist, dist - 0.2f, GameLayers.SightMask, QueryTriggerInteraction.Ignore);
        }

        void FindCover()
        {
            _hasCover = false;
            var away = (transform.position - _target.position).normalized;
            for (int i = 0; i < 10; i++)
            {
                var dir = Quaternion.Euler(0f, i * 36f, 0f) * away;
                var candidate = transform.position + dir * Random.Range(3f, coverSearchRadius);
                if (!NavMesh.SamplePosition(candidate, out var hit, 2.5f, NavMesh.AllAreas))
                    continue;

                var probe = hit.position + Vector3.up * 1.15f;
                var toTarget = AimPoint() - probe;
                if (Physics.Raycast(probe, toTarget.normalized, toTarget.magnitude * 0.92f, GameLayers.SightMask))
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
    }
}
