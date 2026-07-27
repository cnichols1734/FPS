using ArenaFps.Audio;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaFps.Combat
{
    /// <summary>
    /// Procedural animation for a living <see cref="BotRig"/>: stride, arm counter-swing, torso bob,
    /// aim pitch, firing recoil and stagger. Bots that slide around rigidly read as targets; bots
    /// that walk read as people, and that difference is most of the perceived AI quality.
    /// </summary>
    [RequireComponent(typeof(BotRig))]
    public sealed class BotPoseDriver : MonoBehaviour
    {
        [SerializeField] float strideFrequency = 1.55f;
        [SerializeField] float strideAmplitude = 26f;
        [SerializeField] float armSwing = 7f;
        [SerializeField] float bobHeight = 0.035f;
        [SerializeField] float footstepVolume = 0.5f;

        /// <summary>
        /// When false, skip rewriting bone localRotations (skinned Mixamo / Animator owns the pose).
        /// Footsteps, stagger scaling, aim bookkeeping still run.
        /// </summary>
        [SerializeField] bool driveBones = true;

        BotRig _rig;
        NavMeshAgent _agent;
        Animator _animator;
        SoldierAnimator _soldier;

        float _phase;
        int _strideSign;
        float _speed;
        float _stagger;
        float _recoil;
        float _breath;
        float _aimPitch;
        bool _hasAim;
        Vector3 _aimPoint;

        public bool DriveBones
        {
            get => driveBones;
            set => driveBones = value;
        }

        void Awake()
        {
            _rig = GetComponent<BotRig>();
            _agent = GetComponent<NavMeshAgent>();
            _animator = GetComponentInChildren<Animator>();
            _soldier = GetComponent<SoldierAnimator>();
            // A skinned humanoid is posed in muscle space by SoldierAnimator; twisting the same
            // Mixamo bones from here as well would fight it and shear the mesh.
            if (_soldier != null || (_animator != null && _animator.avatar != null && _animator.avatar.isHuman))
                driveBones = false;
            _phase = Random.Range(0f, Mathf.PI * 2f);
            _breath = Random.Range(0f, Mathf.PI * 2f);
        }

        public void SetAimTarget(Vector3 worldPoint)
        {
            _aimPoint = worldPoint;
            _hasAim = true;
            _soldier?.SetAimTarget(worldPoint);
        }

        public void ClearAim()
        {
            _hasAim = false;
            _soldier?.ClearAim();
        }

        public void AddRecoil(float amount)
        {
            _recoil = Mathf.Min(1.4f, _recoil + amount);
            _soldier?.AddRecoil(amount);
        }

        public void AddStagger(float amount)
        {
            _stagger = Mathf.Min(1f, _stagger + amount);
            _soldier?.AddStagger(amount);
        }

        public void PlayReload() => _soldier?.PlayReload();

        /// <summary>Stagger scales the bot's move speed, so sustained fire genuinely pins it down.</summary>
        public float SpeedScale => 1f - _stagger * 0.55f;

        void LateUpdate()
        {
            if (_rig == null || _rig.RigRoot == null)
                return;

            float dt = Time.deltaTime;
            _stagger = Mathf.MoveTowards(_stagger, 0f, dt * 1.6f);
            _recoil = Mathf.MoveTowards(_recoil, 0f, dt * 6.5f);
            _breath += dt * 1.1f;

            var velocity = _agent != null ? _agent.velocity : Vector3.zero;
            velocity.y = 0f;
            float target = velocity.magnitude;
            _speed = Mathf.Lerp(_speed, target, 1f - Mathf.Exp(-9f * dt));

            float normalised = Mathf.Clamp01(_speed / 4.2f);
            if (_soldier != null && _soldier.HasHumanoid)
            {
                // Borrow the real gait clock so a step is heard when a boot actually lands.
                _phase = _soldier.StridePhase;
                normalised = _soldier.NormalisedSpeed;
            }
            else
            {
                _phase += dt * strideFrequency * Mathf.PI * 2f * (0.6f + normalised * 1.5f);
            }

            float stride = Mathf.Sin(_phase);
            float swing = stride * strideAmplitude * normalised;
            float lift = Mathf.Sin(_phase * 2f);

            ReportFootfall(stride, normalised);
            UpdateAimPitch(dt);

            if (_animator != null)
                _animator.speed = Mathf.Lerp(0.85f, 1.35f, normalised) * (1f - _stagger * 0.35f);

            if (!driveBones)
                return;

            // Legs: thigh swings, knee flexes on the forward half of the stride.
            Pose(Bone.ThighL, new Vector3(-swing, 0f, 0f));
            Pose(Bone.ThighR, new Vector3(swing, 0f, 0f));
            Pose(Bone.ShinL, new Vector3(Mathf.Max(0f, swing) * 1.35f + normalised * 4f, 0f, 0f));
            Pose(Bone.ShinR, new Vector3(Mathf.Max(0f, -swing) * 1.35f + normalised * 4f, 0f, 0f));

            // Torso: counter-rotate against the stride, lean into the run, fold under fire.
            float breathe = Mathf.Sin(_breath) * 1.4f;
            float lean = normalised * 5f + _stagger * 9f;
            Pose(Bone.Hips, new Vector3(0f, -swing * 0.16f, lift * 1.5f * normalised));
            Pose(Bone.Spine, new Vector3(lean * 0.45f + breathe * 0.4f, swing * 0.1f, -lift * 1.1f * normalised));
            Pose(Bone.Chest, new Vector3(lean * 0.55f + _aimPitch * 0.35f + breathe * 0.6f - _recoil * 5f, swing * 0.08f, 0f));

            // Arms: rifle stays shouldered, so the counter-swing is small and the recoil is sharp.
            Pose(Bone.UpperArmL, new Vector3(swing * armSwing * 0.1f - _recoil * 7f, 0f, 0f));
            Pose(Bone.UpperArmR, new Vector3(-swing * armSwing * 0.1f - _recoil * 9f, 0f, 0f));
            Pose(Bone.LowerArmL, new Vector3(-_recoil * 5f, 0f, 0f));
            Pose(Bone.LowerArmR, new Vector3(_aimPitch * 0.55f - _recoil * 12f, 0f, 0f));

            Pose(Bone.Head, new Vector3(_aimPitch * 0.5f - lean * 0.6f, 0f, 0f));

            float capsuleBob = Mathf.Abs(lift) * bobHeight * normalised - _stagger * 0.045f;
            _rig.RigRoot.localPosition = new Vector3(0f, capsuleBob, 0f);
        }

        /// <summary>
        /// Plays a step as the stride crosses centre, which is where the foot is actually planted.
        /// Timer-driven footsteps drift out of phase with the legs and read as someone else walking;
        /// hearing an enemy move is half of knowing they are there.
        /// </summary>
        void ReportFootfall(float stride, float normalised)
        {
            int sign = stride >= 0f ? 1 : -1;
            if (sign == _strideSign)
                return;

            bool first = _strideSign == 0;
            _strideSign = sign;
            if (first || normalised < 0.18f)
                return;

            // Crossing to positive plants the left foot, negative the right.
            var foot = _rig[sign > 0 ? Bone.ShinL : Bone.ShinR];
            var at = foot?.Transform != null ? BotRig.Center(foot) : transform.position;

            Sfx3D.Instance.Play(
                normalised > 0.8f ? Sfx.FootstepSprint : Sfx.Footstep,
                at,
                footstepVolume * Mathf.Lerp(0.5f, 1f, normalised),
                0.09f,
                26f);
        }

        void UpdateAimPitch(float dt)
        {
            float wanted = 0f;
            if (_hasAim)
            {
                var chest = _rig[Bone.Chest];
                if (chest != null)
                {
                    var from = chest.Transform.position;
                    var to = _aimPoint;
                    float horizontal = Vector3.ProjectOnPlane(to - from, Vector3.up).magnitude;
                    wanted = Mathf.Clamp(-Mathf.Atan2(to.y - from.y, Mathf.Max(0.2f, horizontal)) * Mathf.Rad2Deg, -35f, 35f);
                }
            }
            _aimPitch = Mathf.Lerp(_aimPitch, wanted, 1f - Mathf.Exp(-11f * dt));
        }

        void Pose(Bone id, Vector3 euler)
        {
            var bone = _rig[id];
            if (bone == null)
                return;
            bone.Transform.localRotation = bone.BindRotation * Quaternion.Euler(euler + bone.PunchAngles);
        }
    }
}
