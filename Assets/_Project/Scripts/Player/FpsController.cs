using ArenaFps.Input;
using UnityEngine;

namespace ArenaFps.Player
{
    /// <summary>
    /// CharacterController-based FPS locomotion: walk, tac sprint, crouch, jump, mantle, and an
    /// Apex-style momentum slide (boost + friction + slope + slide-jump). Circle/crouch is
    /// tap-to-toggle: press while walking locks a duck, press while running starts a slide.
    /// Slides take light left-stick brake/steer; look yaw also carves the path.
    /// L3 / sprint press cancels a slide back into a sprint.
    /// Camera pitch/yaw are applied late in LateUpdate for lower look latency.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class FpsController : MonoBehaviour
    {
        public enum GamepadLookCurve
        {
            /// <summary>Apex Classic: dampened near centre for micro-aim, full rate at the rim.</summary>
            Classic = 0,
            /// <summary>1:1 stick-to-turn mapping. Snappier tracking, less forgiveness.</summary>
            Linear = 1,
        }

        [Header("Move")]
        [SerializeField] float walkSpeed = 4.6f;
        [SerializeField] float sprintSpeed = 7.2f;
        [SerializeField] float crouchSpeed = 2.2f;
        [SerializeField] float gravity = -22f;
        [SerializeField] float jumpHeight = 1.15f;

        [Header("Slide (Apex-style)")]
        [Tooltip("Horizontal speed required to start a ground slide (~Apex 200hu).")]
        [SerializeField] float slideMinSpeed = 5.05f;
        [Tooltip("Burst added on slide enter when boost is off cooldown (~Apex 150hu).")]
        [SerializeField] float slideBoost = 2.8f;
        [SerializeField] float slideMaxSpeed = 10.2f;
        [SerializeField] float slideBoostCooldown = 2f;
        [Tooltip("Flat-ground speed bleed while sliding. Higher = shorter slides.")]
        [SerializeField] float slideFriction = 5.8f;
        [Tooltip("Drop out of slide into crouch-walk below this speed.")]
        [SerializeField] float slideEndSpeed = 3.35f;
        [Tooltip("How much look yaw redirects the slide (1 = slide follows camera fully).")]
        [SerializeField] [Range(0f, 1f)] float slideLookInfluence = 0.35f;
        [Tooltip("Slight left-stick steer while sliding (rad/sec at full deflection).")]
        [SerializeField] float slideStickSteer = 0.85f;
        [Tooltip("Extra bleed when pulling the stick against the slide.")]
        [SerializeField] float slideStickBrake = 6.5f;
        [Tooltip("Tiny speed feed when pushing with the slide.")]
        [SerializeField] float slideStickPush = 0.6f;
        [Tooltip("Acceleration along downhill slopes while sliding.")]
        [SerializeField] float slideSlopeAccel = 16f;
        [Tooltip("Landing slide: min horizontal speed when crouch-latched on touchdown.")]
        [SerializeField] float airSlideMinSpeed = 2.3f;
        [Tooltip("Landing slide: min downward speed (positive) required with crouch latched.")]
        [SerializeField] float airSlideMinFall = 5.0f;

        [Header("Look")]
        [SerializeField] Transform cameraPivot;
        [SerializeField] float mouseSensitivity = 1f;
        [SerializeField] float minPitch = -78f;
        [SerializeField] float maxPitch = 78f;

        [Header("Gamepad Look (Apex-style ALC)")]
        [SerializeField] float gamepadLookSensitivity = 1f;
        [Tooltip("Hip-fire yaw degrees/sec at full stick after the response curve.")]
        [SerializeField] float gamepadTurnRate = 240f;
        [Tooltip("Hip-fire pitch degrees/sec at full stick after the response curve.")]
        [SerializeField] float gamepadPitchRate = 170f;
        [Tooltip("Classic = soft centre / fast edge (Apex default). Linear = 1:1 raw stick.")]
        [SerializeField] GamepadLookCurve gamepadLookCurve = GamepadLookCurve.Classic;
        [Tooltip("Classic exponent. ~2.2 matches Apex Classic: ~23% turn at half-stick.")]
        [SerializeField] [Range(1f, 3.5f)] float gamepadClassicExponent = 2.2f;
        [Tooltip("Where max look speed starts before the physical stick edge (Apex Outer Threshold).")]
        [SerializeField] [Range(0f, 0.25f)] float gamepadOuterThreshold = 0.05f;
        [Tooltip("Extra yaw deg/sec that ramps in near the stick edge (Apex Turning Extra Yaw).")]
        [SerializeField] float gamepadExtraYaw = 180f;
        [Tooltip("Extra pitch deg/sec near the stick edge. Keep lower than yaw for recoil control.")]
        [SerializeField] float gamepadExtraPitch = 40f;
        [Tooltip("Stick magnitude (post-curve remap) that starts counting toward Turning Extra.")]
        [SerializeField] [Range(0.5f, 1f)] float gamepadExtraStart = 0.9f;
        [Tooltip("Seconds at the outer rim before Turning Extra begins (Apex Ramp-up Delay).")]
        [SerializeField] float gamepadRampDelay = 0f;
        [Tooltip("Seconds for Turning Extra to reach full strength after the delay (Apex Ramp-up Time).")]
        [SerializeField] float gamepadRampTime = 0.25f;

        [Header("Body")]
        [SerializeField] float standingHeight = 1.8f;
        [SerializeField] float crouchHeight = 1.15f;
        [SerializeField] float standingEyeHeight = 1.6f;
        [SerializeField] float crouchEyeHeight = 1.02f;
        [SerializeField] float heightLerp = 14f;
        [SerializeField] float eyeLerp = 16f;

        [Header("Mantle")]
        [SerializeField] float mantleMaxHeight = 1.1f;
        [SerializeField] float mantleReach = 0.55f;
        [SerializeField] float mantleSpeed = 8f;
        [SerializeField] LayerMask mantleMask = ~0;

        CharacterController _cc;
        AimAssist _aimAssist;
        Vector3 _velocity;
        float _yaw;
        float _pitch;
        bool _sliding;
        bool _crouchLatched;
        Vector3 _slideDir;
        float _slideSpeed;
        float _nextSlideBoost;
        float _slideAge;
        float _momentumCarryUntil;
        bool _mantling;
        Vector3 _mantleTarget;
        float _extraHoldTime;
        Vector3 _groundNormal = Vector3.up;

        public bool IsSliding => _sliding;
        public bool IsCrouching { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool IsGrounded { get; private set; }
        public bool IsMantling => _mantling;
        public Vector3 Velocity => _velocity;
        public float PlanarSpeed => new Vector2(_velocity.x, _velocity.z).magnitude;
        public Transform CameraPivot => cameraPivot;

        /// <summary>
        /// Multiplier on all look input, so aiming down sights can slow the turn without the weapon
        /// code owning rotation. 1 is hip-fire.
        /// </summary>
        public float LookScale { get; set; } = 1f;

        /// <summary>Raised on touchdown with the vertical speed absorbed, for landing feedback.</summary>
        public event System.Action<float> Landed;

        bool _wasGrounded = true;
        float _fallSpeed;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _aimAssist = GetComponent<AimAssist>();
            if (cameraPivot == null)
            {
                // Prefer a real pivot: the camera's parent owns pitch, the camera itself must stay
                // at the pivot origin. Falling back to the camera transform mis-wires look control.
                var cam = GetComponentInChildren<Camera>();
                if (cam != null)
                    cameraPivot = cam.transform.parent != null && cam.transform.parent != transform
                        ? cam.transform.parent
                        : cam.transform;
            }

            EnforceCameraRig();

            _yaw = transform.eulerAngles.y;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        /// <summary>
        /// Screenshot and level-dressing tooling has previously written a world-space pose onto the
        /// still-parented camera, which bakes a multi-metre local offset into the scene and puts the
        /// view on a boom arm far from the collision capsule. Snap it back before the first frame.
        /// </summary>
        void EnforceCameraRig()
        {
            if (cameraPivot == null)
                return;

            var cam = cameraPivot.GetComponentInChildren<Camera>();
            if (cam == null || cam.transform == cameraPivot)
                return;

            var t = cam.transform;
            if (t.localPosition.sqrMagnitude <= 0.0001f && Quaternion.Angle(t.localRotation, Quaternion.identity) <= 0.05f)
                return;

            Debug.LogWarning($"[FpsController] Camera rig was offset (local pos {t.localPosition}, rot {t.localEulerAngles}). " +
                             "Resetting to the pivot origin — check for editor tooling writing world poses onto the gameplay camera.", this);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
        }

        void Update()
        {
            if (_mantling)
            {
                TickMantle();
                return;
            }

            var input = GameInput.Instance;
            Vector2 move = input != null ? input.Move : Vector2.zero;
            bool sprint = input != null && input.SprintHeld;
            bool sprintPressed = input != null && input.SprintPressedThisFrame;
            bool crouchPressed = input != null && input.CrouchPressedThisFrame;
            bool jump = input != null && input.JumpPressedThisFrame;
            float dt = Time.deltaTime;

            Vector3 wish = transform.right * move.x + transform.forward * move.y;
            if (wish.sqrMagnitude > 1f)
                wish.Normalize();

            bool grounded = _cc.isGrounded;
            IsGrounded = grounded;
            SampleGroundNormal(grounded);

            if (!grounded)
                _fallSpeed = Mathf.Min(_fallSpeed, _velocity.y);
            else if (!_wasGrounded)
            {
                Landed?.Invoke(-_fallSpeed);
                TryAirSlide();
                _fallSpeed = 0f;
            }
            _wasGrounded = grounded;

            if (grounded && _velocity.y < 0f)
                _velocity.y = -2f;

            // L3 / Shift cancels a slide and pops you back into a sprint with carry speed.
            if (_sliding && sprintPressed)
                CancelSlideIntoSprint(move, input);
            else if (sprintPressed && _crouchLatched)
            {
                // Sprint always wins over crouch — tapping it stands you up so you can run.
                _crouchLatched = false;
                IsCrouching = false;
            }

            // Same-frame sprint+crouch: sprint already cleared the latch; don't re-duck.
            if (crouchPressed && !sprintPressed)
                HandleCrouchPress(wish, grounded);

            if (_sliding)
                TickSlide(dt, grounded, move);
            else
                TickGroundMove(dt, wish, sprint, grounded);

            if (jump && grounded)
                TryJump();

            _velocity.y += gravity * dt;

            var flags = _cc.Move(_velocity * dt);
            if ((flags & CollisionFlags.Above) != 0 && _velocity.y > 0f)
                _velocity.y = 0f;

            UpdateBodyShape(dt);

            if (!_sliding && grounded)
                TryStartMantle(wish);
        }

        /// <summary>
        /// Circle is tap-to-toggle: press once to lock crouch (or start a slide if running),
        /// press again to stand / cancel the slide.
        /// </summary>
        void HandleCrouchPress(Vector3 wish, bool grounded)
        {
            if (_sliding)
            {
                EndSlide(false);
                return;
            }

            if (_crouchLatched)
            {
                _crouchLatched = false;
                IsCrouching = false;
                return;
            }

            _crouchLatched = true;
            IsCrouching = true;

            float planarSpeed = PlanarSpeed;
            bool running = IsSprinting || planarSpeed >= slideMinSpeed;
            if (grounded && running && planarSpeed >= slideMinSpeed)
                BeginSlide(wish);
        }

        void CancelSlideIntoSprint(Vector2 move, GameInput input)
        {
            float keep = Mathf.Max(_slideSpeed, sprintSpeed);
            Vector3 dir = _slideDir.sqrMagnitude > 0.01f ? _slideDir : transform.forward;

            EndSlide(false);
            input?.ForceSprintLatch(true);

            if (move.sqrMagnitude > 0.01f)
            {
                var wish = (transform.right * move.x + transform.forward * move.y).normalized;
                dir = Vector3.Slerp(dir, wish, 0.4f).normalized;
            }

            _velocity.x = dir.x * keep;
            _velocity.z = dir.z * keep;
            _momentumCarryUntil = Time.time + 0.22f;
            IsSprinting = true;
        }

        void TickGroundMove(float dt, Vector3 wish, bool sprint, bool grounded)
        {
            IsCrouching = _crouchLatched;
            IsSprinting = sprint && !IsCrouching && grounded && wish.sqrMagnitude > 0.01f;

            float speed = IsCrouching ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed);

            // After a slide-cancel, keep carry speed briefly instead of snapping to sprintSpeed.
            if (Time.time < _momentumCarryUntil)
            {
                Vector3 planar = new Vector3(_velocity.x, 0f, _velocity.z);
                float current = planar.magnitude;
                Vector3 dir = wish.sqrMagnitude > 0.01f
                    ? Vector3.Slerp(planar.sqrMagnitude > 0.01f ? planar.normalized : transform.forward, wish, 1f - Mathf.Exp(-10f * dt)).normalized
                    : (planar.sqrMagnitude > 0.01f ? planar.normalized : transform.forward);
                float target = IsSprinting ? Mathf.Max(speed, current * 0.98f) : speed;
                float next = Mathf.MoveTowards(current, target, dt * 10f);
                _velocity.x = dir.x * next;
                _velocity.z = dir.z * next;
                return;
            }

            Vector3 horizontal = wish * speed;
            _velocity.x = horizontal.x;
            _velocity.z = horizontal.z;
        }

        void BeginSlide(Vector3 wish)
        {
            Vector3 planar = new Vector3(_velocity.x, 0f, _velocity.z);
            if (planar.sqrMagnitude > 0.01f)
                _slideDir = planar.normalized;
            else if (wish.sqrMagnitude > 0.01f)
                _slideDir = wish.normalized;
            else
                _slideDir = transform.forward;

            _slideSpeed = planar.magnitude;
            if (_slideSpeed < slideMinSpeed)
                _slideSpeed = Mathf.Max(_slideSpeed, sprintSpeed);

            // Apex slideboost: first-frame burst, then a cooldown so chaining isn't free.
            if (Time.time >= _nextSlideBoost)
            {
                _slideSpeed = Mathf.Min(_slideSpeed + slideBoost, slideMaxSpeed);
                _nextSlideBoost = Time.time + slideBoostCooldown;
            }
            else
            {
                _slideSpeed = Mathf.Min(_slideSpeed, slideMaxSpeed);
            }

            _sliding = true;
            _slideAge = 0f;
            _crouchLatched = true;
            IsCrouching = true;
            IsSprinting = false;
            _velocity.x = _slideDir.x * _slideSpeed;
            _velocity.z = _slideDir.z * _slideSpeed;
        }

        void TickSlide(float dt, bool grounded, Vector2 move)
        {
            _slideAge += dt;
            IsCrouching = true;
            IsSprinting = false;

            // Light left-stick influence: brake opposite, nudge left/right, tiny push with.
            // Look yaw still carves in LateUpdate — stick is the fine control.
            if (move.sqrMagnitude > 0.01f)
            {
                Vector3 wish = Vector3.ProjectOnPlane(
                    transform.right * move.x + transform.forward * move.y,
                    _groundNormal);
                if (wish.sqrMagnitude > 0.01f)
                {
                    wish.Normalize();
                    float mag = Mathf.Min(1f, move.magnitude);
                    float along = Vector3.Dot(wish, _slideDir);

                    _slideDir = Vector3.RotateTowards(
                        _slideDir, wish, slideStickSteer * mag * dt, 0f).normalized;

                    float brake = Mathf.Clamp01(-along) * mag;
                    float push = Mathf.Clamp01(along) * mag;
                    _slideSpeed -= brake * slideStickBrake * dt;
                    _slideSpeed += push * slideStickPush * dt;
                }
            }

            // Slope: downhill feeds speed, uphill bleeds it — the signature Apex carve.
            float slopeAlong = 0f;
            if (_groundNormal.y < 0.999f && grounded)
            {
                Vector3 downSlope = Vector3.ProjectOnPlane(Vector3.down, _groundNormal);
                if (downSlope.sqrMagnitude > 0.0001f)
                {
                    downSlope.Normalize();
                    slopeAlong = Vector3.Dot(_slideDir, downSlope);
                    float steep = Mathf.Clamp01(1f - _groundNormal.y);
                    _slideSpeed += slopeAlong * slideSlopeAccel * steep * dt;
                }
            }

            // Flat friction with a short grace so the boost reads before the bleed starts.
            // Downhill eases friction so slopes still build speed; flats dump momentum faster.
            float frictionScale = _slideAge < 0.1f ? 0.45f : 1f;
            if (grounded)
            {
                float steep = Mathf.Clamp01(1f - _groundNormal.y);
                float downhill = Mathf.Clamp01(slopeAlong) * steep;
                float flatFriction = slideFriction * Mathf.Lerp(1f, 0.4f, downhill);
                float uphill = Mathf.Max(0f, -slopeAlong);
                _slideSpeed -= (flatFriction * frictionScale + uphill * slideSlopeAccel * 0.55f) * dt;
            }

            _slideSpeed = Mathf.Clamp(_slideSpeed, 0f, slideMaxSpeed);
            _velocity.x = _slideDir.x * _slideSpeed;
            _velocity.z = _slideDir.z * _slideSpeed;

            if (!grounded)
                return;

            // Momentum death → stay crouched (toggle still latched) until circle is pressed again.
            if (_slideSpeed < slideEndSpeed && _slideAge > 0.18f)
                EndSlide(true);
        }

        void EndSlide(bool stayCrouched)
        {
            _sliding = false;
            _slideAge = 0f;
            _crouchLatched = stayCrouched;
            IsCrouching = stayCrouched;
        }

        void TryAirSlide()
        {
            if (!_crouchLatched || _sliding)
                return;

            float planar = PlanarSpeed;
            float fall = -_fallSpeed;
            if (planar >= airSlideMinSpeed && fall >= airSlideMinFall)
                BeginSlide(Vector3.zero);
            else if (planar >= slideMinSpeed)
                BeginSlide(Vector3.zero);
        }

        void TryJump()
        {
            // Slide-jump: exit the slide but keep boosted horizontal speed; crouch stays latched.
            if (_sliding)
                EndSlide(true);

            _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        void UpdateBodyShape(float dt)
        {
            bool ducked = IsCrouching || _sliding;
            float targetHeight = ducked ? crouchHeight : standingHeight;
            _cc.height = Mathf.Lerp(_cc.height, targetHeight, 1f - Mathf.Exp(-heightLerp * dt));
            _cc.center = new Vector3(0f, _cc.height * 0.5f, 0f);

            if (cameraPivot == null)
                return;

            float targetEye = ducked ? crouchEyeHeight : standingEyeHeight;
            var lp = cameraPivot.localPosition;
            lp.y = Mathf.Lerp(lp.y, targetEye, 1f - Mathf.Exp(-eyeLerp * dt));
            cameraPivot.localPosition = lp;
        }

        void SampleGroundNormal(bool grounded)
        {
            if (!grounded)
            {
                _groundNormal = Vector3.up;
                return;
            }

            Vector3 origin = transform.position + Vector3.up * 0.35f;
            if (Physics.SphereCast(origin, _cc.radius * 0.85f, Vector3.down, out var hit, 0.7f, mantleMask, QueryTriggerInteraction.Ignore))
                _groundNormal = hit.normal;
            else
                _groundNormal = Vector3.up;
        }

        void LateUpdate()
        {
            var input = GameInput.Instance;
            float scale = Mathf.Max(0f, LookScale);
            float dt = Time.deltaTime;

            // Mouse is a per-frame delta and must not be scaled by delta time, or fast frames
            // would under-turn. It is already in degrees via the binding processor.
            Vector2 mouse = input != null ? input.Look : Vector2.zero;
            Vector2 delta = mouse * (mouseSensitivity * scale);

            // The stick is an absolute deflection, so it integrates over time instead. Without this
            // the turn rate tracked frame rate, which is the classic console-aim feel bug.
            Vector2 stick = input != null ? input.LookStick : Vector2.zero;

            float stickFriction = 1f;
            Vector2 assist = Vector2.zero;
            if (_aimAssist == null)
                _aimAssist = GetComponent<AimAssist>();
            if (_aimAssist != null)
                _aimAssist.Evaluate(stick, dt, out assist, out stickFriction);

            delta += StickLookDelta(stick, dt) * scale * stickFriction;
            // Assist is already in degrees this frame; ADS LookScale still braces the pull.
            delta += assist * scale;

            float yawDelta = delta.x;
            _yaw += yawDelta;
            _pitch -= delta.y;
            _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);

            // Hard reset roll — never allow Z tilt.
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            if (cameraPivot != null)
                cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

            // Slide carve: looking turns the path slightly — left stick does nothing mid-slide.
            if (_sliding && Mathf.Abs(yawDelta) > 0.0001f && slideLookInfluence > 0f)
            {
                _slideDir = Quaternion.AngleAxis(yawDelta * slideLookInfluence, Vector3.up) * _slideDir;
                _slideDir.y = 0f;
                if (_slideDir.sqrMagnitude > 0.0001f)
                    _slideDir.Normalize();
                _velocity.x = _slideDir.x * _slideSpeed;
                _velocity.z = _slideDir.z * _slideSpeed;
            }
        }

        /// <summary>
        /// Apex ALC-style stick aim:
        /// deadzone (Input System) → outer threshold → Classic/Linear response curve →
        /// base yaw/pitch → Turning Extra that only ramps in at the rim.
        /// </summary>
        Vector2 StickLookDelta(Vector2 stick, float dt)
        {
            float magnitude = Mathf.Min(1f, stick.magnitude);
            if (magnitude <= 0.0001f)
            {
                _extraHoldTime = 0f;
                return Vector2.zero;
            }

            // Outer threshold: treat max look as reached before the physical stick edge.
            float usable = Mathf.Max(0.01f, 1f - Mathf.Clamp01(gamepadOuterThreshold));
            float t = Mathf.Clamp01(magnitude / usable);

            float shaped = ApplyLookCurve(t);
            float extraBlend = TickTurningExtra(t, dt);

            Vector2 direction = stick / magnitude;
            float yawRate = gamepadTurnRate + gamepadExtraYaw * extraBlend;
            float pitchRate = gamepadPitchRate + gamepadExtraPitch * extraBlend;
            float scale = shaped * gamepadLookSensitivity * dt;

            // Horizontal: stick right → look right. Vertical: inverted (stick up → look down).
            // Signs are hardcoded — serialized invert toggles were sticking across recompiles.
            return new Vector2(direction.x * yawRate, -direction.y * pitchRate) * scale;
        }

        float ApplyLookCurve(float t)
        {
            t = Mathf.Clamp01(t);
            if (gamepadLookCurve == GamepadLookCurve.Linear || gamepadClassicExponent <= 1.001f)
                return t;

            // Classic / exponential: half-stick ≈ 0.5^2.2 ≈ 22% turn speed — Apex Classic feel.
            return Mathf.Pow(t, gamepadClassicExponent);
        }

        /// <summary>
        /// Apex Turning Extra: second gear only while the stick lives near the outer rim.
        /// Delay then ramp, so tracking mid-stick stays on the base curve.
        /// </summary>
        float TickTurningExtra(float t, float dt)
        {
            bool wantExtra = gamepadExtraYaw > 0.01f || gamepadExtraPitch > 0.01f;
            if (!wantExtra || t < gamepadExtraStart)
            {
                _extraHoldTime = 0f;
                return 0f;
            }

            _extraHoldTime += dt;
            float afterDelay = Mathf.Max(0f, _extraHoldTime - Mathf.Max(0f, gamepadRampDelay));
            float ramp = gamepadRampTime > 0.0001f
                ? Mathf.Clamp01(afterDelay / gamepadRampTime)
                : 1f;

            // Soft-gate so Extra fades in across the outer band instead of a hard cliff.
            float edge = Mathf.InverseLerp(gamepadExtraStart, 1f, t);
            return ramp * edge;
        }

        void TryStartMantle(Vector3 wish)
        {
            if (wish.sqrMagnitude < 0.25f)
                return;

            Vector3 origin = transform.position + Vector3.up * 0.3f;
            Vector3 dir = wish.normalized;
            if (!Physics.Raycast(origin, dir, out var hit, mantleReach, mantleMask, QueryTriggerInteraction.Ignore))
                return;

            // Check top clearance
            Vector3 topProbe = hit.point + Vector3.up * mantleMaxHeight + dir * 0.2f;
            if (Physics.Raycast(topProbe, Vector3.down, out var topHit, mantleMaxHeight + 0.2f, mantleMask, QueryTriggerInteraction.Ignore))
            {
                float ledgeHeight = topHit.point.y - transform.position.y;
                if (ledgeHeight > 0.35f && ledgeHeight <= mantleMaxHeight)
                {
                    _mantling = true;
                    _mantleTarget = topHit.point + Vector3.up * 0.05f;
                    _velocity = Vector3.zero;
                    _sliding = false;
                }
            }
        }

        void TickMantle()
        {
            Vector3 next = Vector3.MoveTowards(transform.position, _mantleTarget, mantleSpeed * Time.deltaTime);
            Vector3 delta = next - transform.position;
            _cc.Move(delta);
            if ((transform.position - _mantleTarget).sqrMagnitude < 0.01f)
                _mantling = false;
        }
    }
}
