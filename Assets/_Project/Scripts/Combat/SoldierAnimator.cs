using ArenaFps.Core;
using UnityEngine;
using UnityEngine.AI;

namespace ArenaFps.Combat
{
    /// <summary>
    /// Drives a Humanoid soldier from mocap, with the layers a clip cannot know about solved on
    /// top: where the enemy is looking, where the ground actually is, and which limb was just hit.
    ///
    /// Mocap owns the body. There is no hand-authored gait any more — a synthesised walk cycle
    /// never reads as weight being carried, and every hour spent tuning one is an hour not spent
    /// on the clips. What remains procedural is only the part motion capture cannot supply: a
    /// static rifle stance used to calibrate hip height and to stand in if the clips are missing.
    ///
    /// The fallback pose is rebuilt from scratch every frame. Nothing accumulates, so a dropped
    /// frame or a teleport cannot leave the body bent into a shape it can't recover from.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SoldierAnimator : MonoBehaviour
    {
        [Header("Aim")]
        [SerializeField] float maxAimYaw = 62f;
        [SerializeField] float maxAimPitch = 40f;
        [SerializeField] float aimResponse = 12f;

        [Header("Foot IK")]
        [SerializeField] bool footIk = true;
        [SerializeField] float footProbeUp = 0.55f;
        [SerializeField] float footProbeDown = 1.1f;
        [Tooltip("Fallback ankle-to-sole height. Measured from the rig at spawn when a toe bone exists.")]
        [SerializeField] float ankleHeight = 0.105f;

        [Header("Debug")]
        [Tooltip("Logs the spawn calibration and a one-second follow-up to the console.")]
        [SerializeField] bool logDiagnostics = true;

        [Header("Mocap")]
        [Tooltip("Clear only to force the static fallback stance for debugging.")]
        [SerializeField] bool preferMocap = true;
        [Tooltip("Ground speed the walk/run clips were authored at, used to kill foot skate.")]
        [SerializeField] float walkClipSpeed = 1.45f;
        [SerializeField] float runClipSpeed = 4.6f;

        [Header("Weapon")]
        [Tooltip("Grip points from the shoulder line, in arm-lengths. Scale free — any rig fits.")]
        [SerializeField] Vector3 rightGripOffset = new(0.17f, -0.3f, 0.33f);
        [SerializeField] Vector3 leftGripOffset = new(-0.1f, -0.32f, 0.72f);
        [SerializeField] float gripIkWeight = 0.9f;
        [SerializeField] float handRollWeight = 0.7f;

        Animator _animator;
        HumanPoseHandler _handler;
        NavMeshAgent _agent;
        BotRig _rig;

        HumanPose _pose;
        Vector3 _baseBodyPosition;
        float _humanScale = 1f;
        bool _ready;

        SoldierClipPlayer _clips;

        // Cached bone transforms for the IK passes.
        Transform _model, _hips, _spine, _chest, _head;
        Transform _thighL, _shinL, _footL, _thighR, _shinR, _footR;
        Transform _upperArmL, _lowerArmL, _handL, _upperArmR, _lowerArmR, _handR;

        Muscles _m;

        float _phase;
        float _speed;
        Vector2 _moveLocal;
        float _breath;
        float _aimYaw, _aimPitch;
        float _aimWeight;
        bool _hasAim;
        Vector3 _aimPoint;
        float _recoil;
        float _stagger;
        float _reload;
        float _rootOffset;
        bool _onLink;
        float _modelBaseY;
        float _armLength = 0.55f;
        float _legLength = 0.85f;
        float _soleOffset = 0.105f;
        float _diagnosticAt = -1f;

        public bool HasHumanoid => _ready;

        /// <summary>True when mocap is driving the body rather than the static fallback stance.</summary>
        public bool UsingMocap => _clips != null && _clips.IsValid;
        public float NormalisedSpeed { get; private set; }
        public float StridePhase => _phase;

        #region Public API

        public void SetAimTarget(Vector3 worldPoint)
        {
            _aimPoint = worldPoint;
            _hasAim = true;
        }

        public void ClearAim() => _hasAim = false;

        public void AddRecoil(float amount)
        {
            _recoil = Mathf.Min(1.4f, _recoil + amount);
            _clips?.PlayFire();
        }

        public void AddStagger(float amount) => _stagger = Mathf.Min(1f, _stagger + amount);

        /// <summary>Drops the support hand to the magwell and back over ~2.2s.</summary>
        public void PlayReload()
        {
            if (_clips != null)
            {
                _clips.PlayReload();
                return;
            }
            if (_reload <= 0f)
                _reload = 1f;
        }

        public void PlayJump(bool backward = false) => _clips?.PlayJump(backward);

        /// <summary>
        /// Plays a death clip and holds its last frame, returning how long that takes. Zero means
        /// there is nothing to play and the caller should hand straight over to physics.
        /// </summary>
        public float PlayDeath()
        {
            if (_clips == null)
                return 0f;
            // A soldier cut down mid-stride falls differently from one shot standing still.
            return _clips.PlayDeath(NormalisedSpeed > 0.25f);
        }

        #endregion

        void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _rig = GetComponent<BotRig>();
            _breath = Random.Range(0f, Mathf.PI * 2f);
            Bind();
        }

        void OnEnable()
        {
            // Respawn re-enables this component after the ragdoll switched the Animator off.
            if (_clips != null && _animator != null)
                _animator.enabled = true;
            _clips?.Revive();
            _rootOffset = 0f;
        }

        void OnDestroy()
        {
            _handler?.Dispose();
            _clips?.Destroy();
        }

        /// <summary>
        /// The bot is assembled at runtime, so the skinned model may not exist at AddComponent time.
        /// Binding is idempotent and safe to call again after the rig builder has run.
        /// </summary>
        public void Bind()
        {
            if (_ready)
                return;

            _animator = GetComponentInChildren<Animator>();
            if (_animator == null || _animator.avatar == null || !_animator.avatar.isValid || !_animator.avatar.isHuman)
                return;

            // A live Animator with no controller would still fight us for the transforms on some
            // culling modes; the pose is written directly through the handler instead.
            _animator.enabled = false;

            _model = _animator.transform;
            _humanScale = Mathf.Max(0.01f, _animator.humanScale);
            _modelBaseY = _model.localPosition.y;

            _hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            _spine = _animator.GetBoneTransform(HumanBodyBones.Spine);
            _chest = _animator.GetBoneTransform(HumanBodyBones.UpperChest)
                     ?? _animator.GetBoneTransform(HumanBodyBones.Chest)
                     ?? _spine;
            _head = _animator.GetBoneTransform(HumanBodyBones.Head)
                    ?? _animator.GetBoneTransform(HumanBodyBones.Neck);

            _thighL = _animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            _shinL = _animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            _footL = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            _thighR = _animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            _shinR = _animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            _footR = _animator.GetBoneTransform(HumanBodyBones.RightFoot);

            _upperArmL = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _lowerArmL = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _handL = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
            _upperArmR = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _lowerArmR = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _handR = _animator.GetBoneTransform(HumanBodyBones.RightHand);

            _handler = new HumanPoseHandler(_animator.avatar, _model);
            _handler.GetHumanPose(ref _pose);

            _m = Muscles.Resolve();

            // Own the model's vertical placement outright; the rig builder's spawn-time foot plant
            // is superseded by the calibration below.
            var flat = _model.localPosition;
            flat.y = 0f;
            _model.localPosition = flat;
            _modelBaseY = 0f;

            _armLength = Vector3.Distance(_upperArmR.position, _lowerArmR.position)
                         + Vector3.Distance(_lowerArmR.position, _handR.position);
            _legLength = Vector3.Distance(_thighR.position, _shinR.position)
                         + Vector3.Distance(_shinR.position, _footR.position);

            // The gap from ankle to sole differs per character and boot. The toe bone sits at the
            // ball of the foot, near sole height, which is a far better estimate than a constant
            // — a wrong constant makes the whole body float or sink by that error.
            var toe = _animator.GetBoneTransform(HumanBodyBones.RightToes)
                      ?? _animator.GetBoneTransform(HumanBodyBones.LeftToes);
            var ankle = toe != null && toe == _animator.GetBoneTransform(HumanBodyBones.LeftToes) ? _footL : _footR;
            _soleOffset = toe != null
                ? Mathf.Clamp(ankle.position.y - toe.position.y + 0.015f, 0.03f, 0.2f)
                : ankleHeight;

            CalibrateStandingHeight();
            _ready = true;

            if (preferMocap && SoldierClipLibrary.HasLocomotion)
            {
                // Mocap owns the body; the Animator has to run for the Playables output to land.
                // Culling stays off because the IK passes below read bone transforms every frame
                // and would otherwise solve against a stale pose the moment the bot leaves view.
                _animator.enabled = true;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _clips = new SoldierClipPlayer(_animator, walkClipSpeed, runClipSpeed);
            }
            else if (preferMocap)
            {
                // Loud on purpose. The fallback stands and aims but does not walk, so a bot sliding
                // around in a fixed pose is the symptom, and it is easy to mistake for a bug in the
                // clips rather than for their absence.
                Debug.LogWarning(
                    "[Soldier] No locomotion mocap in Resources/Animations/Soldier — the enemy will " +
                    "slide in a static stance. Run 'Arena FPS/Import Soldier Animations'.");
            }

            // Stamp frame zero so the bot never renders in the FBX bind pose, not even for a frame.
            Evaluate(0f);
        }

        /// <summary>
        /// Solves for the hip height that stands this avatar on its own feet.
        ///
        /// The obvious shortcut — reading bodyPosition out of the FBX bind pose — is wrong for this
        /// character and quietly wrong for many others: the Male Warrior's bind pose is a lounge,
        /// so its hips sit about a knee's worth too low. Everything downstream then compensates,
        /// and the foot IK dutifully folds the legs into a squat to reach the ground.
        ///
        /// Measuring instead of assuming costs a handful of iterations once per bot and works for
        /// any rig, whatever pose it happened to be exported in.
        /// </summary>
        void CalibrateStandingHeight()
        {
            _baseBodyPosition = new Vector3(0f, 1f, 0f);
            if (_footL == null || _footR == null)
                return;

            float groundY = transform.position.y;
            float error = 0f;
            int used = 0;
            for (int i = 0; i < 12; i++)
            {
                BuildStancePose();
                _handler.SetHumanPose(ref _pose);

                float ankleY = Mathf.Min(_footL.position.y, _footR.position.y);
                error = groundY + _soleOffset - ankleY;
                used = i + 1;
                if (Mathf.Abs(error) < 0.002f)
                    break;
                // Hips and ankles move together, so the correction converges almost immediately.
                _baseBodyPosition.y += error / _humanScale;
            }

            if (logDiagnostics)
            {
                Debug.Log(
                    $"[Soldier] calibrated in {used} iter | residual {error:F4}m | " +
                    $"bodyY {_baseBodyPosition.y:F3} | humanScale {_humanScale:F3} | " +
                    $"sole {_soleOffset:F3}m | leg {_legLength:F3}m | arm {_armLength:F3}m | " +
                    $"hips {(_hips.position.y - groundY):F3}m | head {(_head.position.y - groundY):F3}m " +
                    $"| mocap {(UsingMocap ? "yes" : "no")}");
            }
        }

        /// <summary>The static rifle stance with no gait, aim or reactions — the calibration target.</summary>
        void BuildStancePose()
        {
            System.Array.Clear(_pose.muscles, 0, _pose.muscles.Length);
            ApplyRifleStance(_pose.muscles);
            _pose.bodyRotation = Quaternion.identity;
            _pose.bodyPosition = _baseBodyPosition;
        }

        void LateUpdate()
        {
            if (!_ready)
            {
                Bind();
                if (!_ready)
                    return;
            }

            Evaluate(Time.deltaTime);
            ReportSettledPose();
        }

        /// <summary>
        /// One measurement of the settled standing pose. Knee angle is the tell: near zero is
        /// standing, past about thirty degrees is a crouch, and it says immediately whether a
        /// bad-looking stance came from hip height or from the leg muscles themselves.
        /// </summary>
        void ReportSettledPose()
        {
            if (!logDiagnostics)
                return;
            if (_diagnosticAt < 0f)
            {
                _diagnosticAt = Time.time + 1f;
                return;
            }
            if (Time.time < _diagnosticAt)
                return;

            logDiagnostics = false;
            float groundY = transform.position.y;
            float knee = Vector3.Angle(
                _shinR.position - _thighR.position,
                _footR.position - _shinR.position);

            Debug.Log(
                $"[Soldier] settled | knee {knee:F1}deg | rootOffset {_rootOffset:F3}m | " +
                $"hips {(_hips.position.y - groundY):F3}m | ankleR {(_footR.position.y - groundY):F3}m | " +
                $"head {(_head.position.y - groundY):F3}m | speed {_speed:F2} | " +
                $"handR fwd {Vector3.Dot(_handR.position - _chest.position, transform.forward):F3}m " +
                $"up {(_handR.position.y - _chest.position.y):F3}m");
        }

        void Evaluate(float dt)
        {
            IntegrateState(dt);

            if (_clips != null)
            {
                // Mocap already wrote the body this frame; only the layers it cannot know about
                // — where the enemy is looking, and where the ground actually is — run here.
                _clips.Tick(dt, _moveLocal, _speed, NormalisedSpeed);

                // A dying body is no longer aiming at anything, and its feet are leaving the floor
                // on purpose — both solvers would fight the fall.
                if (_clips.IsDead)
                    return;

                ApplyAimToBones();
            }
            else
            {
                if (_handler == null || _pose.muscles == null)
                    return;
                BuildPose();
                _handler.SetHumanPose(ref _pose);
            }

            ApplyImpactPunch();
            SolveFootIk(dt);

            if (_clips == null)
                SolveGripIk();
        }

        /// <summary>
        /// Aim for the mocap path. Clips are authored facing straight ahead, so the look is layered
        /// on afterwards by splitting the offset across spine, chest and head — one joint taking the
        /// whole angle is what produces the owl-head snap.
        /// </summary>
        void ApplyAimToBones()
        {
            if (_aimWeight < 0.001f)
                return;

            float yaw = _aimYaw * _aimWeight;
            float pitch = _aimPitch * _aimWeight;
            var up = Vector3.up;
            var pitchAxis = Quaternion.AngleAxis(yaw, up) * transform.right;

            Twist(_spine, yaw * 0.28f, pitch * 0.22f, up, pitchAxis);
            Twist(_chest, yaw * 0.42f, pitch * 0.38f, up, pitchAxis);
            Twist(_head, yaw * 0.3f, pitch * 0.4f, up, pitchAxis);
        }

        static void Twist(Transform bone, float yaw, float pitch, Vector3 yawAxis, Vector3 pitchAxis)
        {
            if (bone == null)
                return;
            bone.rotation = Quaternion.AngleAxis(yaw, yawAxis)
                            * Quaternion.AngleAxis(pitch, pitchAxis)
                            * bone.rotation;
        }

        #region State

        void IntegrateState(float dt)
        {
            _recoil = Mathf.MoveTowards(_recoil, 0f, dt * 6.5f);
            _stagger = Mathf.MoveTowards(_stagger, 0f, dt * 1.6f);
            _reload = Mathf.MoveTowards(_reload, 0f, dt / 2.2f);
            _breath += dt * Mathf.Lerp(1.1f, 2.6f, NormalisedSpeed);

            var velocity = _agent != null && _agent.enabled && _agent.isOnNavMesh ? _agent.velocity : Vector3.zero;
            velocity.y = 0f;
            _speed = Mathf.Lerp(_speed, velocity.magnitude, 1f - Mathf.Exp(-9f * dt));
            NormalisedSpeed = Mathf.Clamp01(_speed / 4.2f);

            // Velocity in the body's own frame is what separates a walk from a sidestep from a
            // backpedal; a bot that plays a forward gait while strafing reads as sliding on ice.
            var local = transform.InverseTransformDirection(velocity);
            var wanted = new Vector2(local.x, local.z) / Mathf.Max(1f, _speed);
            _moveLocal = Vector2.Lerp(_moveLocal, wanted * NormalisedSpeed, 1f - Mathf.Exp(-10f * dt));

            // Footstep audio needs to know where in the stride the body is. The clip's own clock is
            // the only honest source of that; a parallel timer drifts and plays the sound between
            // the boots landing.
            _phase = _clips != null ? _clips.Phase * Mathf.PI * 2f : 0f;

            // An off-mesh link is the agent telling us it is about to leave the ground — a gap, a
            // drop, a railing. Reading it here means the jump clips need no cooperation from the AI.
            bool onLink = _agent != null && _agent.enabled && _agent.isOnNavMesh && _agent.isOnOffMeshLink;
            if (onLink && !_onLink)
                _clips?.PlayJump(_moveLocal.y < -0.15f);
            _onLink = onLink;

            UpdateAim(dt);
        }

        void UpdateAim(float dt)
        {
            float wantedYaw = 0f, wantedPitch = 0f, wantedWeight = 0f;

            if (_hasAim && _chest != null)
            {
                var to = _aimPoint - _chest.position;
                var flat = Vector3.ProjectOnPlane(to, Vector3.up);
                if (flat.sqrMagnitude > 1e-4f)
                {
                    wantedYaw = Mathf.Clamp(
                        Vector3.SignedAngle(transform.forward, flat, Vector3.up), -maxAimYaw, maxAimYaw);
                }
                wantedPitch = Mathf.Clamp(
                    -Mathf.Atan2(to.y, Mathf.Max(0.25f, flat.magnitude)) * Mathf.Rad2Deg, -maxAimPitch, maxAimPitch);
                wantedWeight = 1f;
            }

            float k = 1f - Mathf.Exp(-aimResponse * dt);
            _aimYaw = Mathf.Lerp(_aimYaw, wantedYaw, k);
            _aimPitch = Mathf.Lerp(_aimPitch, wantedPitch, k);
            _aimWeight = Mathf.Lerp(_aimWeight, wantedWeight, k);
        }

        #endregion

        #region Pose construction

        /// <summary>
        /// The fallback pose, used only when no mocap is present: a breathing rifle stance that
        /// aims and flinches but does not pretend to walk. Sliding a static stance around is an
        /// honest placeholder; a synthetic gait under it is not, and it hides that clips are
        /// missing until someone watches the bot closely.
        /// </summary>
        void BuildPose()
        {
            if (_pose.muscles == null)
                return;

            var muscles = _pose.muscles;
            System.Array.Clear(muscles, 0, muscles.Length);

            ApplyRifleStance(muscles);

            float breathe = Mathf.Sin(_breath) * Mathf.Lerp(0.022f, 0.05f, NormalisedSpeed);
            HumanoidMuscles.Add(muscles, _m.ChestFront, breathe);
            HumanoidMuscles.Add(muscles, _m.SpineFront, breathe * 0.4f + NormalisedSpeed * 0.06f);

            ApplyAim(muscles);
            ApplyCombatReactions(muscles);

            _pose.bodyRotation = Quaternion.Euler(_stagger * 10f, 0f, _moveLocal.x * 4f);
            _pose.bodyPosition = _baseBodyPosition + new Vector3(
                0f,
                -_stagger * 0.05f / _humanScale,
                0f);
        }

        /// <summary>
        /// Compact rifle carry: elbows in, weight slightly forward, knees soft. The grip IK pass
        /// finishes the hands, so this only has to land the arms in the right half of the pose
        /// space for the elbows to solve toward a natural bend.
        /// </summary>
        void ApplyRifleStance(float[] m)
        {
            HumanoidMuscles.Set(m, _m.ShoulderUpL, -0.18f);
            HumanoidMuscles.Set(m, _m.ShoulderFrontL, 0.3f);
            HumanoidMuscles.Set(m, _m.ArmUpL, -0.62f);
            HumanoidMuscles.Set(m, _m.ArmFrontL, 0.62f);
            HumanoidMuscles.Set(m, _m.ArmTwistL, -0.2f);
            HumanoidMuscles.Set(m, _m.ForearmStretchL, 0.5f);
            HumanoidMuscles.Set(m, _m.ForearmTwistL, 0.15f);

            HumanoidMuscles.Set(m, _m.ShoulderUpR, -0.12f);
            HumanoidMuscles.Set(m, _m.ShoulderFrontR, 0.36f);
            HumanoidMuscles.Set(m, _m.ArmUpR, -0.52f);
            HumanoidMuscles.Set(m, _m.ArmFrontR, 0.7f);
            HumanoidMuscles.Set(m, _m.ArmTwistR, 0.25f);
            HumanoidMuscles.Set(m, _m.ForearmStretchR, 0.62f);
            HumanoidMuscles.Set(m, _m.ForearmTwistR, -0.1f);

            HumanoidMuscles.Set(m, _m.SpineFront, 0.09f);
            HumanoidMuscles.Set(m, _m.ChestFront, 0.05f);

            // Knees never lock in a combat stance; a straight-legged soldier reads as a mannequin.
            // Kept shallow, because hip height is calibrated against this pose.
            HumanoidMuscles.Set(m, _m.KneeL, 0.07f);
            HumanoidMuscles.Set(m, _m.KneeR, 0.07f);
            HumanoidMuscles.Set(m, _m.HipFrontL, 0.04f);
            HumanoidMuscles.Set(m, _m.HipFrontR, 0.04f);
            HumanoidMuscles.Set(m, _m.HipOutL, 0.05f);
            HumanoidMuscles.Set(m, _m.HipOutR, 0.05f);
        }

        /// <summary>
        /// Spreads the aim across spine, chest, neck and head instead of snapping one joint. The
        /// upper body therefore tracks the player while the legs keep walking their own direction,
        /// which is the single biggest tell that an enemy is actually looking at you.
        /// </summary>
        void ApplyAim(float[] m)
        {
            float yaw = _aimYaw / 90f * _aimWeight;
            float pitch = _aimPitch / 90f * _aimWeight;

            HumanoidMuscles.Add(m, _m.SpineTwist, yaw * 0.5f);
            HumanoidMuscles.Add(m, _m.ChestTwist, yaw * 0.85f);
            HumanoidMuscles.Add(m, _m.NeckTurn, yaw * 0.5f);
            HumanoidMuscles.Add(m, _m.HeadTurn, yaw * 0.6f);

            HumanoidMuscles.Add(m, _m.ChestFront, pitch * 0.35f);
            HumanoidMuscles.Add(m, _m.NeckNod, -pitch * 0.6f);
            HumanoidMuscles.Add(m, _m.HeadNod, -pitch * 0.75f);
        }

        void ApplyCombatReactions(float[] m)
        {
            if (_recoil > 0.001f)
            {
                // Recoil travels shoulder -> chest -> head, arriving as a short sharp rock back.
                HumanoidMuscles.Add(m, _m.ForearmStretchR, _recoil * 0.13f);
                HumanoidMuscles.Add(m, _m.ArmUpR, _recoil * 0.09f);
                HumanoidMuscles.Add(m, _m.ShoulderUpR, _recoil * 0.11f);
                HumanoidMuscles.Add(m, _m.ChestFront, -_recoil * 0.1f);
                HumanoidMuscles.Add(m, _m.HeadNod, _recoil * 0.07f);
            }

            if (_stagger > 0.001f)
            {
                HumanoidMuscles.Add(m, _m.SpineFront, _stagger * 0.18f);
                HumanoidMuscles.Add(m, _m.ChestFront, _stagger * 0.14f);
                HumanoidMuscles.Add(m, _m.HeadNod, -_stagger * 0.2f);
                HumanoidMuscles.Add(m, _m.KneeL, _stagger * 0.2f);
                HumanoidMuscles.Add(m, _m.KneeR, _stagger * 0.2f);
            }
        }

        #endregion

        #region Post-pose passes

        /// <summary>
        /// Bullet impacts are sprung by <see cref="RagdollDriver"/> as additive angles. They are
        /// applied on top of the finished pose so a flinch reads on the limb that was actually hit.
        /// </summary>
        void ApplyImpactPunch()
        {
            if (_rig == null)
                return;

            foreach (var bone in _rig.Bones)
            {
                if (bone?.Transform == null || bone.PunchAngles.sqrMagnitude < 1e-6f)
                    continue;
                bone.Transform.localRotation *= Quaternion.Euler(bone.PunchAngles);
            }
        }

        void SolveFootIk(float dt)
        {
            if (!footIk || _footL == null || _footR == null || _model == null)
                return;

            bool hitL = Probe(_footL, out var groundL, out var normalL);
            bool hitR = Probe(_footR, out var groundR, out var normalR);
            if (!hitL && !hitR)
                return;

            // Move the whole body to wherever the feet need it, so a slope or a step plants both
            // boots instead of leaving one hovering.
            //
            // This has to be free to travel *up* as well as down. The usual foot-IK convention only
            // lowers the pelvis, which assumes the authored pose already stands at the right height.
            // When it does not, every bit of that error lands on the knees and the character squats
            // forever — and because the IK then plants the feet correctly, the error measures as
            // zero next frame and nothing ever corrects it.
            float needL = hitL ? groundL.y + _soleOffset - _footL.position.y : 0f;
            float needR = hitR ? groundR.y + _soleOffset - _footR.position.y : 0f;

            // Accumulated, not assigned. "need" is a correction measured from where the body already
            // is, so treating it as an absolute target would undo itself the moment it was applied:
            // the body rises, the error reads zero, the target collapses back to zero, and the
            // soldier bobs. At equilibrium need is zero and the offset simply holds.
            float wanted = Mathf.Clamp(_rootOffset + Mathf.Min(needL, needR), -0.5f, 0.5f);
            _rootOffset = Mathf.Lerp(_rootOffset, wanted, 1f - Mathf.Exp(-12f * dt));

            var local = _model.localPosition;
            local.y = _modelBaseY + _rootOffset;
            _model.localPosition = local;

            PlantFoot(_thighL, _shinL, _footL, hitL, groundL, normalL, StanceWeight(_footL, groundL, hitL));
            PlantFoot(_thighR, _shinR, _footR, hitR, groundR, normalR, StanceWeight(_footR, groundR, hitR));
        }

        /// <summary>
        /// 1 while the foot carries weight, 0 through swing — IK must never drag a lifted foot back
        /// to the floor. The plant is inferred from how far the ankle has actually risen, which
        /// works for any clip without the graph having to publish which leg is swinging.
        /// </summary>
        float StanceWeight(Transform foot, Vector3 ground, bool grounded)
        {
            if (!grounded)
                return 0f;
            float lift = foot.position.y - (ground.y + _soleOffset);
            return 1f - Mathf.Clamp01(lift / 0.12f);
        }

        bool Probe(Transform foot, out Vector3 point, out Vector3 normal)
        {
            var origin = foot.position + Vector3.up * footProbeUp;
            if (Physics.Raycast(origin, Vector3.down, out var hit, footProbeUp + footProbeDown,
                    GameLayers.WorldMask, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                normal = hit.normal;
                return true;
            }

            point = Vector3.zero;
            normal = Vector3.up;
            return false;
        }

        void PlantFoot(Transform thigh, Transform shin, Transform foot, bool grounded, Vector3 point, Vector3 normal, float weight)
        {
            if (!grounded || weight <= 0.01f || thigh == null || shin == null || foot == null)
                return;

            var target = point + Vector3.up * _soleOffset;

            // Never let a foot target pull in closer than a deep-crouch's worth of leg. Without this
            // floor, any error in hip height is absorbed silently by the knees and the bot squats
            // instead of looking obviously misplaced — a failure that is much harder to spot.
            var fromHip = target - thigh.position;
            float minReach = _legLength * 0.62f;
            if (fromHip.magnitude < minReach && fromHip.sqrMagnitude > 1e-6f)
                target = thigh.position + fromHip.normalized * minReach;

            // Knees bend forward; the pole sits ahead of the hip so the solver never inverts.
            var pole = thigh.position + transform.forward * 1.2f - Vector3.up * 0.35f;
            TwoBoneIk.Solve(thigh, shin, foot, target, pole, weight);

            // Roll the sole onto the surface so boots do not intersect a ramp edge-on.
            var tilt = Quaternion.FromToRotation(Vector3.up, normal);
            foot.rotation = Quaternion.Slerp(foot.rotation, tilt * foot.rotation, weight * 0.7f);
        }

        /// <summary>
        /// Pins both hands onto a shared weapon line derived from the aim direction. This is what
        /// stops the arms drifting apart into the "holding an invisible beach ball" pose that pure
        /// muscle posing always slides into.
        /// </summary>
        void SolveGripIk()
        {
            if (gripIkWeight <= 0f || _handR == null || _handL == null || _upperArmL == null)
                return;

            var aimDir = Quaternion.AngleAxis(_aimYaw * _aimWeight, Vector3.up) * transform.forward;
            aimDir = Quaternion.AngleAxis(_aimPitch * _aimWeight, Vector3.Cross(Vector3.up, aimDir)) * aimDir;
            if (aimDir.sqrMagnitude < 1e-4f)
                aimDir = transform.forward;

            var aim = Quaternion.LookRotation(aimDir.normalized, Vector3.up);

            // Anchored to the shoulder line and measured in arm-lengths rather than metres. Absolute
            // offsets only ever fit the one character they were tuned on, and any target the arm
            // cannot comfortably reach makes the solver straighten the elbow into a stiff point.
            var shoulders = (_upperArmL.position + _upperArmR.position) * 0.5f;
            float arm = _armLength;

            // Recoil shoves the whole weapon line back toward the shoulder rather than bending a
            // single joint, which is what makes the kick read from third person.
            var kick = new Vector3(0f, 0.02f, -0.1f) * _recoil;

            var rightTarget = shoulders + aim * ((rightGripOffset + kick) * arm);
            var leftTarget = shoulders + aim * ((leftGripOffset + kick) * arm);

            // Reload: the support hand leaves the foregrip for the magwell and comes back.
            if (_reload > 0.001f)
            {
                float reach = Mathf.Sin(Mathf.Clamp01(1f - _reload) * Mathf.PI);
                leftTarget += aim * (new Vector3(0.22f, -0.25f, -0.38f) * (reach * arm));
            }

            var rightPole = shoulders + aim * (new Vector3(0.95f, -1.05f, -0.15f) * arm);
            var leftPole = shoulders + aim * (new Vector3(-0.8f, -1.15f, 0.2f) * arm);

            TwoBoneIk.Solve(_upperArmR, _lowerArmR, _handR, rightTarget, rightPole, gripIkWeight);
            TwoBoneIk.Solve(_upperArmL, _lowerArmL, _handL, leftTarget, leftPole, gripIkWeight);

            // Curl the fingers toward the weapon. Rotating each hand so its bone-to-child axis hits
            // a chosen direction sidesteps having to know the rig's wrist axis convention, which
            // differs between exporters and is the usual source of snapped-looking wrists.
            PointBoneAlong(_handR, aim * new Vector3(-0.3f, -0.72f, 0.62f), handRollWeight);
            PointBoneAlong(_handL, aim * new Vector3(0.28f, -0.78f, 0.56f), handRollWeight);
        }

        static void PointBoneAlong(Transform bone, Vector3 worldDir, float weight)
        {
            if (bone == null || weight <= 0f || bone.childCount == 0 || worldDir.sqrMagnitude < 1e-6f)
                return;

            Transform child = null;
            for (int i = 0; i < bone.childCount; i++)
            {
                var c = bone.GetChild(i);
                if (c.name.Contains("End", System.StringComparison.Ordinal))
                    continue;
                child = c;
                break;
            }
            if (child == null)
                return;

            var current = child.position - bone.position;
            if (current.sqrMagnitude < 1e-6f)
                return;

            var target = Quaternion.FromToRotation(current.normalized, worldDir.normalized) * bone.rotation;
            bone.rotation = Quaternion.Slerp(bone.rotation, target, Mathf.Clamp01(weight));
        }

        #endregion

        /// <summary>Resolved muscle slots for this avatar. Optional bones resolve to -1 and are skipped.</summary>
        struct Muscles
        {
            public int SpineFront, SpineLeftRight, SpineTwist;
            public int ChestFront, ChestTwist;
            public int NeckNod, NeckTurn, HeadNod, HeadTurn;
            public int HipFrontL, HipOutL, KneeL, AnkleL, ToesL;
            public int HipFrontR, HipOutR, KneeR, AnkleR, ToesR;
            public int ShoulderUpL, ShoulderFrontL, ArmUpL, ArmFrontL, ArmTwistL, ForearmStretchL, ForearmTwistL;
            public int ShoulderUpR, ShoulderFrontR, ArmUpR, ArmFrontR, ArmTwistR, ForearmStretchR, ForearmTwistR;

            public static Muscles Resolve() => new()
            {
                SpineFront = HumanoidMuscles.Index("Spine Front-Back"),
                SpineLeftRight = HumanoidMuscles.Index("Spine Left-Right"),
                SpineTwist = HumanoidMuscles.Index("Spine Twist Left-Right"),
                ChestFront = HumanoidMuscles.Index("Chest Front-Back"),
                ChestTwist = HumanoidMuscles.Index("Chest Twist Left-Right"),
                NeckNod = HumanoidMuscles.Index("Neck Nod Down-Up"),
                NeckTurn = HumanoidMuscles.Index("Neck Turn Left-Right"),
                HeadNod = HumanoidMuscles.Index("Head Nod Down-Up"),
                HeadTurn = HumanoidMuscles.Index("Head Turn Left-Right"),

                HipFrontL = HumanoidMuscles.Index("Left Upper Leg Front-Back"),
                HipOutL = HumanoidMuscles.Index("Left Upper Leg In-Out"),
                KneeL = HumanoidMuscles.Index("Left Lower Leg Stretch"),
                AnkleL = HumanoidMuscles.Index("Left Foot Up-Down"),
                ToesL = HumanoidMuscles.Index("Left Toes Up-Down"),

                HipFrontR = HumanoidMuscles.Index("Right Upper Leg Front-Back"),
                HipOutR = HumanoidMuscles.Index("Right Upper Leg In-Out"),
                KneeR = HumanoidMuscles.Index("Right Lower Leg Stretch"),
                AnkleR = HumanoidMuscles.Index("Right Foot Up-Down"),
                ToesR = HumanoidMuscles.Index("Right Toes Up-Down"),

                ShoulderUpL = HumanoidMuscles.Index("Left Shoulder Down-Up"),
                ShoulderFrontL = HumanoidMuscles.Index("Left Shoulder Front-Back"),
                ArmUpL = HumanoidMuscles.Index("Left Arm Down-Up"),
                ArmFrontL = HumanoidMuscles.Index("Left Arm Front-Back"),
                ArmTwistL = HumanoidMuscles.Index("Left Arm Twist In-Out"),
                ForearmStretchL = HumanoidMuscles.Index("Left Forearm Stretch"),
                ForearmTwistL = HumanoidMuscles.Index("Left Forearm Twist In-Out"),

                ShoulderUpR = HumanoidMuscles.Index("Right Shoulder Down-Up"),
                ShoulderFrontR = HumanoidMuscles.Index("Right Shoulder Front-Back"),
                ArmUpR = HumanoidMuscles.Index("Right Arm Down-Up"),
                ArmFrontR = HumanoidMuscles.Index("Right Arm Front-Back"),
                ArmTwistR = HumanoidMuscles.Index("Right Arm Twist In-Out"),
                ForearmStretchR = HumanoidMuscles.Index("Right Forearm Stretch"),
                ForearmTwistR = HumanoidMuscles.Index("Right Forearm Twist In-Out"),
            };
        }
    }
}
