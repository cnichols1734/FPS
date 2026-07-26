using ArenaFps.Audio;
using ArenaFps.Ballistics;
using ArenaFps.Combat;
using ArenaFps.Core;
using ArenaFps.Feedback;
using UnityEngine;

namespace ArenaFps.AI
{
    /// <summary>
    /// A bot's rifle, made legible. Incoming fire has to be seen and heard to be survivable, so
    /// every round gets a muzzle flash, a tracer, a positional report, and a near-miss crack.
    /// Bots fire in bursts rather than an unbroken stream — that cadence is what gives the player
    /// windows to move, and it is most of what makes a firefight feel authored rather than random.
    /// </summary>
    public sealed class BotWeapon : MonoBehaviour
    {
        [Header("Ballistics")]
        [SerializeField] float roundsPerMinute = 620f;
        [SerializeField] float damage = 11f;
        [SerializeField] float range = 70f;
        [SerializeField] float aimErrorDegrees = 1.7f;

        [Header("Burst")]
        [SerializeField] Vector2Int burstLength = new Vector2Int(3, 6);
        [SerializeField] Vector2 burstRest = new Vector2(0.45f, 1.1f);

        [Header("Presentation")]
        [SerializeField] float nearMissRadius = 2.2f;

        Transform _muzzle;
        BotPoseDriver _pose;
        Damageable _self;
        Light _flashLight;
        float _flashUntil;

        float _nextShot;
        float _restUntil;
        int _remainingInBurst;

        public float Damage => damage;
        public float Range => range;

        void Awake()
        {
            _pose = GetComponent<BotPoseDriver>();
            _self = GetComponent<Damageable>();
            _remainingInBurst = Random.Range(burstLength.x, burstLength.y + 1);
            ResolveMuzzle();
        }

        void Update()
        {
            if (_flashLight == null)
                return;
            bool on = Time.time < _flashUntil;
            if (_flashLight.enabled != on)
                _flashLight.enabled = on;
        }

        void ResolveMuzzle()
        {
            var rig = GetComponent<BotRig>();
            if (rig != null)
            {
                var forearm = rig[Bone.LowerArmR];
                if (forearm != null)
                    _muzzle = forearm.Transform.Find("Muzzle");
            }
            if (_muzzle == null)
                _muzzle = transform;

            var flashGo = new GameObject("MuzzleLight");
            flashGo.transform.SetParent(_muzzle, false);
            _flashLight = flashGo.AddComponent<Light>();
            _flashLight.type = LightType.Point;
            _flashLight.range = 6f;
            _flashLight.intensity = 5.5f;
            _flashLight.color = new Color(1f, 0.72f, 0.35f);
            _flashLight.shadows = LightShadows.None;
            _flashLight.enabled = false;
        }

        public bool ReadyToFire => Time.time >= _nextShot && Time.time >= _restUntil;

        /// <summary>Fires one round at the point, with spread. Returns false if still on cooldown.</summary>
        public bool Fire(Vector3 targetPoint, Transform playerHead)
        {
            if (!ReadyToFire)
                return false;

            _nextShot = Time.time + 60f / Mathf.Max(60f, roundsPerMinute);
            if (--_remainingInBurst <= 0)
            {
                _remainingInBurst = Random.Range(burstLength.x, burstLength.y + 1);
                _restUntil = Time.time + Random.Range(burstRest.x, burstRest.y);
            }

            var origin = _muzzle.position;
            var direction = (targetPoint - origin).normalized;
            direction = Quaternion.Euler(
                Random.Range(-aimErrorDegrees, aimErrorDegrees),
                Random.Range(-aimErrorDegrees, aimErrorDegrees),
                0f) * direction;

            Present(origin, direction);

            var ballistic = PenetrationSolver.Trace(origin, direction, range, damage, GameLayers.BotBulletMask);
            Vector3 endPoint = origin + direction * range;

            if (ballistic.didHit)
            {
                endPoint = ballistic.hit.point;
                float dealt = PenetrationSolver.DamageAfter(ballistic, damage);

                ballistic.hit.collider.GetComponentInParent<BreakableCover>()
                    ?.ApplyBallisticDamage(dealt, ballistic.hit.point, direction);

                var target = ballistic.hit.collider.GetComponentInParent<Damageable>();
                if (target != null && target != _self)
                {
                    var info = new DamageInfo
                    {
                        Amount = dealt,
                        Point = ballistic.hit.point,
                        Direction = direction,
                        Normal = ballistic.hit.normal,
                        Collider = ballistic.hit.collider,
                        Attacker = gameObject,
                        FromPlayer = false,
                        Surface = ballistic.surface != null ? ballistic.surface.kind : SurfaceKind.Default,
                        Ricochet = ballistic.ricocheted,
                        Penetrated = ballistic.penetrated,
                        Multiplier = 1f,
                    };
                    target.ApplyDamage(info);
                }
                else
                {
                    var kind = ballistic.surface != null ? ballistic.surface.kind : SurfaceKind.Default;
                    ImpactFx.Instance.SurfaceImpact(ballistic.hit.point, ballistic.hit.normal, direction, kind);
                    if (ballistic.ricocheted)
                        ImpactFx.Instance.Ricochet(ballistic.hit.point, ballistic.hit.normal, ballistic.continuedDirection);
                }
            }

            ImpactFx.Instance.Tracer(origin, endPoint, 0.03f, 420f);
            ReportNearMiss(origin, endPoint, playerHead);
            return true;
        }

        void Present(Vector3 origin, Vector3 direction)
        {
            ImpactFx.Instance.MuzzleFlash(origin, direction, 0.85f);
            _flashUntil = Time.time + 0.035f;
            _pose?.AddRecoil(0.55f);

            // Distance decides the character of the report: close shots crack, far shots roll.
            var listener = Camera.main;
            float distance = listener != null ? Vector3.Distance(listener.transform.position, origin) : 30f;
            var sfx = distance > 26f ? Sfx.RifleShotDistant : Sfx.RifleShot;
            Sfx3D.Instance.Play(sfx, origin, distance > 26f ? 0.9f : 0.7f, 0.07f, 120f);
        }

        /// <summary>
        /// A round that passes close to the player's head snaps past their ear. Without this cue
        /// there is no difference between being shot at and being ignored.
        /// </summary>
        void ReportNearMiss(Vector3 from, Vector3 to, Transform playerHead)
        {
            if (playerHead == null)
                return;

            var segment = to - from;
            float length = segment.magnitude;
            if (length < 0.5f)
                return;

            var dir = segment / length;
            float along = Mathf.Clamp(Vector3.Dot(playerHead.position - from, dir), 0f, length);
            var closest = from + dir * along;
            float miss = Vector3.Distance(closest, playerHead.position);

            if (miss > nearMissRadius || miss < 0.35f)
                return;

            float volume = Mathf.Lerp(0.85f, 0.2f, miss / nearMissRadius);
            ImpactFx.Instance.Whizz(closest, volume);
        }
    }
}
