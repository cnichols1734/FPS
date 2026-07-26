using ArenaFps.Combat;
using ArenaFps.Core;
using ArenaFps.Input;
using ArenaFps.Weapons;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ArenaFps.Player
{
    /// <summary>
    /// Console aim assist in the COD mould: friction (slowdown) when the reticle is near a target,
    /// plus light rotational assist toward centre-mass. Tracks with player strafe / right-stick
    /// input — it does not hard-lock or yank to the head. Mouse look is left alone.
    /// </summary>
    public sealed class AimAssist : MonoBehaviour
    {
        [Header("Enable")]
        [SerializeField] bool enabledWhenGamepadPresent = true;
        [Tooltip("If true, assist only runs while a gamepad is the active device.")]
        [SerializeField] bool requireGamepad = true;

        [Header("Acquisition")]
        [SerializeField] float maxRange = 45f;
        [SerializeField] float hipConeDegrees = 6.5f;
        [SerializeField] float adsConeDegrees = 3.8f;
        [SerializeField] float stickySeconds = 0.28f;
        [SerializeField] float stickyScoreBonus = 1.6f;
        [Tooltip("Inside this many degrees of the aim point, rotational pull stops (friction holds).")]
        [SerializeField] float settleDegrees = 0.55f;

        [Header("Rotation Assist")]
        [Tooltip("Degrees/sec pulled toward centre-mass at full strength (hip). Keep modest — COD is friction-first.")]
        [SerializeField] float hipRotationRate = 42f;
        [SerializeField] float adsRotationRate = 30f;
        [Tooltip("Minimum RAA with stick centred. COD leaves a little tracking; too high feels like an aimbot.")]
        [SerializeField] float idleTrackingScale = 0.12f;
        [Tooltip("RAA scale when left-stick strafing with right stick idle (COD movement assist).")]
        [SerializeField] float moveTrackingScale = 0.55f;
        [Tooltip("Extra pull when the right stick tracks toward the target.")]
        [SerializeField] float towardStickBoost = 1.15f;
        [Tooltip("Assist left when peeling the stick away — should nearly die so you can break lock.")]
        [SerializeField] float awayStickScale = 0.08f;
        [SerializeField] float fireBoost = 1.05f;
        [SerializeField] float closeRangeMeters = 3.5f;
        [SerializeField] float closeRangeScale = 0.55f;

        [Header("Friction")]
        [Tooltip("Stick look multiplier when over the target (lower = stickier). COD slowdown is the main feel.")]
        [SerializeField] float onTargetFriction = 0.58f;
        [SerializeField] float coneEdgeFriction = 0.9f;
        [SerializeField] float frictionFalloffPower = 1.1f;

        [Header("Aim Point")]
        [Tooltip("Centre-mass height when no BotRig chest is available.")]
        [SerializeField] float fallbackChestHeight = 1.15f;
        [Tooltip("Blend from hips toward chest. Always body — never head.")]
        [SerializeField] [Range(0f, 1f)] float chestBias = 0.85f;

        FpsController _controller;
        WeaponController _weapon;
        Damageable _sticky;
        float _stickyUntil;
        float _scanTimer;
        float _gamepadLookUntil;
        Damageable[] _cache = System.Array.Empty<Damageable>();

        public Damageable CurrentTarget { get; private set; }
        public float CurrentConeNorm { get; private set; }

        void Awake()
        {
            _controller = GetComponent<FpsController>();
            _weapon = GetComponent<WeaponController>();
        }

        /// <summary>
        /// Called from FpsController before stick look is applied. Returns a mouse-convention look
        /// delta (yaw +, pitch-up +) and a friction multiplier for the stick contribution only.
        /// </summary>
        public void Evaluate(Vector2 stick, float dt, out Vector2 lookDeltaDegrees, out float stickFriction)
        {
            lookDeltaDegrees = Vector2.zero;
            stickFriction = 1f;
            CurrentTarget = null;
            CurrentConeNorm = 1f;

            var input = GameInput.Instance;

            // Stick activity (or gamepad fire/aim) latches controller mode so a parked pad does not
            // yank the mouse reticle around.
            if (stick.sqrMagnitude > 0.01f)
                _gamepadLookUntil = Time.time + 1.25f;
            else
            {
                var pad = Gamepad.current;
                if (pad != null && input != null && (input.FireHeld || input.AimHeld) &&
                    (pad.rightTrigger.isPressed || pad.leftTrigger.isPressed))
                    _gamepadLookUntil = Time.time + 1.25f;
            }

            if (!IsAssistActive())
            {
                ClearSticky();
                return;
            }

            var pivot = _controller != null ? _controller.CameraPivot : null;
            if (pivot == null)
                return;

            float ads = _weapon != null ? _weapon.AdsProgress : 0f;
            float cone = Mathf.Lerp(hipConeDegrees, adsConeDegrees, ads);
            RefreshCache(dt);

            if (!TryPickTarget(pivot, cone, out var target, out var aimPoint, out float angle, out float dist))
            {
                ClearSticky();
                return;
            }

            CurrentTarget = target;
            float coneNorm = cone > 0.01f ? Mathf.Clamp01(angle / cone) : 0f;
            CurrentConeNorm = coneNorm;

            // Friction is the primary COD "sticky" feel — rotational assist stays secondary.
            float inner = 1f - Mathf.Pow(coneNorm, frictionFalloffPower);
            stickFriction = Mathf.Lerp(coneEdgeFriction, onTargetFriction, inner);

            Vector3 local = pivot.InverseTransformDirection((aimPoint - pivot.position).normalized);
            float yawErr = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitchErr = Mathf.Atan2(-local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            // Mouse convention: +x look right, +y look up (FpsController does `_pitch -= delta.y`).
            Vector2 error = new Vector2(yawErr, -pitchErr);

            float errMag = error.magnitude;
            if (errMag < settleDegrees)
                return;

            Vector2 errorDir = error / errMag;
            float stickMag = Mathf.Min(1f, stick.magnitude);
            float moveMag = input != null ? Mathf.Min(1f, input.Move.magnitude) : 0f;

            // StickLookDelta: stick +Y looks down, so the stick direction that closes `error` flips Y.
            float alignment = 0f;
            if (stickMag > 0.05f)
            {
                var desiredStick = new Vector2(errorDir.x, -errorDir.y);
                alignment = Vector2.Dot(stick / stickMag, desiredStick);
            }

            // COD-style RAA: weak idle, moderate on strafe, strongest when right stick tracks in.
            float strength = idleTrackingScale;
            if (moveMag > 0.08f)
                strength = Mathf.Max(strength, moveTrackingScale * moveMag);

            if (stickMag > 0.05f)
            {
                float toward = Mathf.Clamp01(alignment);
                float away = Mathf.Clamp01(-alignment);
                float stickStrength = Mathf.Lerp(0.85f, towardStickBoost, toward);
                stickStrength = Mathf.Lerp(stickStrength, awayStickScale, away);
                strength = Mathf.Max(strength, stickStrength);
            }

            if (input != null && input.FireHeld)
                strength *= fireBoost;

            // Point-blank assist softens (BO6-style) so close fights aren't glued.
            if (dist < closeRangeMeters)
            {
                float closeT = 1f - Mathf.Clamp01(dist / closeRangeMeters);
                strength *= Mathf.Lerp(1f, closeRangeScale, closeT);
                stickFriction = Mathf.Lerp(stickFriction, Mathf.Lerp(stickFriction, 1f, 0.45f), closeT);
            }

            float rate = Mathf.Lerp(hipRotationRate, adsRotationRate, ads);
            // Outer cone: gentler pull. Near centre: still capped — never slam the last degrees shut.
            float proximity = Mathf.Lerp(0.55f, 1f, 1f - coneNorm);
            float maxStep = rate * strength * proximity * dt;
            // Never correct more than a fraction of the remaining error in one frame.
            float step = Mathf.Min(errMag * 0.35f, maxStep);
            lookDeltaDegrees = errorDir * step;
        }

        bool IsAssistActive()
        {
            if (!isActiveAndEnabled)
                return false;
            if (!requireGamepad)
                return true;
            if (!enabledWhenGamepadPresent || Gamepad.current == null)
                return false;
            return Time.time <= _gamepadLookUntil;
        }

        void RefreshCache(float dt)
        {
            _scanTimer -= dt;
            if (_scanTimer > 0f && _cache.Length > 0)
                return;
            _scanTimer = 0.2f;
            _cache = FindObjectsByType<Damageable>();
        }

        bool TryPickTarget(
            Transform pivot,
            float cone,
            out Damageable best,
            out Vector3 aimPoint,
            out float bestAngle,
            out float bestDist)
        {
            best = null;
            aimPoint = default;
            bestAngle = float.MaxValue;
            bestDist = 0f;
            float bestScore = float.MinValue;

            Vector3 origin = pivot.position;
            Vector3 forward = pivot.forward;
            float maxRangeSq = maxRange * maxRange;

            for (int i = 0; i < _cache.Length; i++)
            {
                var d = _cache[i];
                if (d == null || d.IsPlayer || d.IsDead)
                    continue;

                Vector3 point = AimPointFor(d);
                Vector3 to = point - origin;
                float distSq = to.sqrMagnitude;
                if (distSq < 0.25f || distSq > maxRangeSq)
                    continue;

                float dist = Mathf.Sqrt(distSq);
                float angle = Vector3.Angle(forward, to);
                bool sticky = d == _sticky && Time.time <= _stickyUntil;
                float acquireCone = sticky ? cone * 1.2f : cone;
                if (angle > acquireCone)
                    continue;

                if (Physics.Raycast(origin, to / dist, dist - 0.15f, GameLayers.SightMask, QueryTriggerInteraction.Ignore))
                    continue;

                // Closer to reticle wins; nearer targets beat distant ones; sticky keeps the lock.
                float score = (1f - angle / acquireCone) * 3.5f + (1f - dist / maxRange) * 1.2f;
                if (sticky)
                    score += stickyScoreBonus;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = d;
                    aimPoint = point;
                    bestAngle = angle;
                    bestDist = dist;
                }
            }

            if (best == null)
                return false;

            _sticky = best;
            _stickyUntil = Time.time + stickySeconds;
            // Keep controller mode alive while a target is held so idle tracking does not drop out.
            _gamepadLookUntil = Mathf.Max(_gamepadLookUntil, Time.time + 0.2f);
            return true;
        }

        Vector3 AimPointFor(Damageable target)
        {
            var rig = target.GetComponent<BotRig>();
            if (rig != null)
            {
                // Centre-mass only. Head lock is what made this feel like an aimbot.
                Vector3 chest = rig.Chest != null && rig.Chest.Transform != null
                    ? BotRig.Center(rig.Chest)
                    : target.transform.position + Vector3.up * fallbackChestHeight;

                Vector3 hips = rig[Bone.Hips] != null && rig[Bone.Hips].Transform != null
                    ? BotRig.Center(rig[Bone.Hips])
                    : target.transform.position + Vector3.up * (fallbackChestHeight * 0.55f);

                return Vector3.Lerp(hips, chest, chestBias);
            }

            return target.transform.position + Vector3.up * fallbackChestHeight;
        }

        void ClearSticky()
        {
            _sticky = null;
            _stickyUntil = 0f;
        }
    }
}
