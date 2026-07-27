using ArenaFps.Input;
using ArenaFps.Player;
using UnityEngine;

namespace ArenaFps.Weapons
{
    /// <summary>
    /// Every procedural motion applied to the weapon: aim sway, look lag, walk bob, landing
    /// compression, sprint carry, ADS pose and recoil kick, all resolved through critically damped
    /// springs. A weapon welded rigidly to the camera is the single biggest tell that a shooter is
    /// not finished — this is where the sense of holding something heavy comes from.
    /// </summary>
    public sealed class ViewmodelMotion : MonoBehaviour
    {
        // Offsets are WeaponRoot local space, so x is right of the eye, y below it and z downrange.
        // These defaults suit a gun whose own pivot sits on the buttplate; for the authored SCAR-H
        // pack the builder owns the hip pocket and ConfigureAuthoredFpsPose zeroes them out.
        [Header("Poses")]
        [SerializeField] Vector3 hipOffset = new Vector3(0.145f, -0.105f, 0.06f);
        [SerializeField] Vector3 adsOffset = new Vector3(0f, -0.004f, -0.04f);
        [SerializeField] Vector3 adsTilt = Vector3.zero;
        [SerializeField] Vector3 sprintOffset = new Vector3(0.1f, -0.2f, 0.02f);
        [SerializeField] Vector3 sprintTilt = new Vector3(14f, -18f, -12f);
        [SerializeField] Vector3 slideOffset = new Vector3(0.06f, -0.15f, 0.05f);
        [SerializeField] Vector3 slideTilt = new Vector3(8f, -6f, -4f);
        [Tooltip("Eye relief: how far in front of the camera the ghost ring sits at full ADS. " +
                 "Too close and the aperture swallows the screen or clips the near plane.")]
        [SerializeField] float adsSightDistance = 0.18f;
        [Tooltip("Drops the sight line a hair below the reticle so the receiver reads as under it.")]
        [SerializeField] float adsVerticalBias = 0.004f;

        [Header("Look response")]
        [SerializeField] float swayPosition = 0.016f;
        [SerializeField] float swayRotation = 2.6f;
        [SerializeField] float swayMax = 0.05f;

        [Header("Bob")]
        [SerializeField] float bobFrequency = 8.6f;
        [SerializeField] float bobHorizontal = 0.021f;
        [SerializeField] float bobVertical = 0.014f;
        [SerializeField] float bobRoll = 1.5f;

        [Header("Springs")]
        [SerializeField] float positionStiffness = 150f;
        [SerializeField] float positionDamping = 17f;
        [SerializeField] float rotationStiffness = 210f;
        [SerializeField] float rotationDamping = 21f;

        [Header("Idle")]
        [SerializeField] float breathAmplitude = 0.0035f;
        [SerializeField] float breathFrequency = 1.15f;

        FpsController _controller;

        Vector3 _positionOffset;
        Vector3 _positionVelocity;
        Vector3 _rotationOffset;
        Vector3 _rotationVelocity;

        Vector3 _swayTarget;
        Vector3 _swayRotationTarget;
        float _bobPhase;
        float _bobAmount;
        float _breathPhase;
        float _ads;
        float _sprint;
        float _slide;
        float _landPunch;

        // Optional gun-root zoom that lives only at hip (ACR). ADS solves at scale 1, then this
        // blends the authored hip zoom back in when you drop the sight.
        Transform _hipFrame;
        Vector3 _hipFrameBasePos;
        float _hipFrameScale = 1f;

        public void Bind(FpsController controller)
        {
            if (_controller != null)
                _controller.Landed -= OnLanded;
            _controller = controller;
            if (_controller != null)
                _controller.Landed += OnLanded;
        }

        void Awake()
        {
            // Start the springs already at the hip pose. Left at zero they begin at the camera
            // and visibly swing the gun out of the player's face on the first spawn.
            _positionOffset = hipOffset;
            transform.localPosition = hipOffset;
            _breathPhase = Random.Range(0f, Mathf.PI * 2f);
        }

        void OnDestroy()
        {
            if (_controller != null)
                _controller.Landed -= OnLanded;
        }

        void OnLanded(float impactSpeed)
        {
            _landPunch = Mathf.Clamp(impactSpeed / 14f, 0.08f, 1f);
            _positionVelocity += new Vector3(0f, -_landPunch * 1.5f, -_landPunch * 0.35f);
            _rotationVelocity += new Vector3(-_landPunch * 130f, 0f, _landPunch * 26f);
        }

        /// <summary>Weapon-side recoil: visual only, never touches where the bullets go.</summary>
        public void AddRecoil(Vector2 kick, float punch)
        {
            _positionVelocity += new Vector3(
                -kick.y * 0.012f,
                kick.x * 0.006f,
                -punch * 0.55f);

            _rotationVelocity += new Vector3(
                -kick.x * 46f,
                kick.y * 22f,
                -kick.y * 30f);
        }

        public void AddReloadShake()
        {
            _positionVelocity += new Vector3(0f, -0.22f, -0.05f);
            _rotationVelocity += new Vector3(-34f, 12f, 18f);
        }

        public void SetAds(float value) => _ads = Mathf.Clamp01(value);

        /// <summary>
        /// Authored FPS packs (hands + gun baked for a camera) already own their hip pose.
        /// Keep WeaponRoot near identity so sway/bob rides on top instead of fighting the clip.
        /// </summary>
        public void ConfigureAuthoredFpsPose()
        {
            // ScarHViewmodelBuilder owns the camera pocket on its wrapper. WeaponRoot stays put
            // so we do not stack a second translation and shove the gun downrange.
            ClearAuthoredHipFraming();
            hipOffset = Vector3.zero;
            adsOffset = new Vector3(0f, 0.01f, -0.06f);
            adsTilt = Vector3.zero;
            sprintOffset = new Vector3(0.04f, -0.08f, 0.02f);
            sprintTilt = new Vector3(10f, -12f, -8f);
            _positionOffset = hipOffset;
            transform.localPosition = hipOffset;
        }

        /// <summary>
        /// ACR hip pocket/zoom after ADS has been solved on the pure Head_Cam pose. WeaponRoot
        /// carries the drop; the gun root carries hip-only scale that eases out as ADS rises.
        /// </summary>
        public void ConfigureAuthoredHipFraming(Transform gunRoot, Vector3 hipPocket, float hipZoomScale)
        {
            hipOffset = hipPocket;
            _positionOffset = hipOffset;
            transform.localPosition = hipOffset;

            _hipFrame = gunRoot;
            _hipFrameBasePos = gunRoot != null ? gunRoot.localPosition : Vector3.zero;
            _hipFrameScale = Mathf.Max(1e-3f, hipZoomScale);
            ApplyHipFrame(0f);
        }

        public void ClearAuthoredHipFraming()
        {
            if (_hipFrame != null)
            {
                _hipFrame.localPosition = _hipFrameBasePos;
                _hipFrame.localScale = Vector3.one;
            }

            _hipFrame = null;
            _hipFrameScale = 1f;
            // Scar (and ADS calibration) expect WeaponRoot identity under the gun wrapper.
            hipOffset = Vector3.zero;
            _positionOffset = hipOffset;
            transform.localPosition = hipOffset;
        }

        void ApplyHipFrame(float ads)
        {
            if (_hipFrame == null)
                return;

            float a = Smooth(ads);
            _hipFrame.localPosition = _hipFrameBasePos;
            _hipFrame.localScale = Vector3.one * Mathf.Lerp(_hipFrameScale, 1f, a);
        }

        /// <summary>
        /// Solves the ADS pose so the rear ghost ring lands on the camera centreline at the
        /// configured eye relief, whatever pivot the imported gun happens to have.
        /// </summary>
        public void ConfigureIronSightAds(Transform sightAlign, Transform cameraPivot)
            => ConfigureIronSightAds(sightAlign, cameraPivot, adsSightDistance, adsVerticalBias);

        public void ConfigureIronSightAds(Transform sightAlign, Transform cameraPivot, float eyeRelief)
            => ConfigureIronSightAds(sightAlign, cameraPivot, eyeRelief, adsVerticalBias);

        /// <param name="eyeRelief">
        /// Distance in front of the eye to park the sight plane. Iron sights want ~0.18;
        /// holos want a tighter picture (~0.16) so the window fills the centre.
        /// </param>
        /// <param name="verticalBias">
        /// Drops the sight below screen centre for iron-sight silhouette. Pass 0 for optics so
        /// the hologram reticle lands on the HUD crosshair.
        /// </param>
        public void ConfigureIronSightAds(Transform sightAlign, Transform cameraPivot,
                                          float eyeRelief, float verticalBias)
        {
            if (sightAlign == null)
                return;

            var eye = ResolveEye(cameraPivot);
            if (eye == null)
                return;

            float relief = Mathf.Max(0.05f, eyeRelief);

            // Exact camera centreline — HUD crosshair and hitscan both live here.
            Vector3 desired = eye.TransformPoint(new Vector3(0f, -verticalBias, relief));

            // adsOffset is a localPosition, so it has to be expressed in WeaponRoot's parent
            // space — which is not necessarily the eye's space.
            var parent = transform.parent;
            if (parent != null)
                desired = parent.InverseTransformPoint(desired);

            // Sight measured against WeaponRoot's own origin. At the ADS pose the weapon's
            // local rotation is adsTilt (zero), so parent-space sight is simply offset + this.
            Vector3 sightInRoot = transform.InverseTransformPoint(sightAlign.position);

            adsOffset = desired - sightInRoot;
            adsTilt = Vector3.zero;
        }

        /// <summary>
        /// Fine-tunes <see cref="adsOffset"/> so a measured world point (optic reticle) lands on
        /// the camera viewport centre — the same pixel the HUD crosshair and hitscan use.
        /// </summary>
        public void CalibrateAdsToViewportCenter(Transform cameraPivot, System.Func<Vector3> measureWorldPoint)
            => CalibrateAdsToViewportCenter(cameraPivot, measureWorldPoint, Vector2.zero);

        /// <param name="viewportBias">
        /// Extra target offset in viewport space. Use when the glowing reticle is a hair off the
        /// measured mesh UV centre (texture packing quirk).
        /// </param>
        public void CalibrateAdsToViewportCenter(Transform cameraPivot, System.Func<Vector3> measureWorldPoint,
                                                 Vector2 viewportBias)
        {
            if (measureWorldPoint == null)
                return;

            var eyeCam = ResolveEyeCamera(cameraPivot);
            if (eyeCam == null)
                return;

            var eye = eyeCam.transform;
            var parent = transform.parent;
            var savedPos = transform.localPosition;
            var savedRot = transform.localRotation;
            var target = new Vector2(0.5f, 0.5f) + viewportBias;

            for (int i = 0; i < 5; i++)
            {
                transform.localPosition = adsOffset;
                transform.localRotation = Quaternion.identity;

                Vector3 world = measureWorldPoint();
                Vector3 vp = eyeCam.WorldToViewportPoint(world);
                if (vp.z <= 0.01f)
                    break;

                var err = new Vector2(vp.x - target.x, vp.y - target.y);
                if (err.sqrMagnitude < 1e-8f)
                    break;

                float dist = Mathf.Max(0.05f, Vector3.Dot(world - eye.position, eye.forward));
                float halfH = dist * Mathf.Tan(eyeCam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                float halfW = halfH * eyeCam.aspect;
                Vector3 worldNudge = eye.right * (-err.x * 2f * halfW) + eye.up * (-err.y * 2f * halfH);
                Vector3 parentNudge = parent != null ? parent.InverseTransformVector(worldNudge) : worldNudge;
                adsOffset += parentNudge;
            }

            transform.localPosition = savedPos;
            transform.localRotation = savedRot;
        }

        /// <summary>
        /// The supplied pivot may be the yaw/pitch pivot or the camera itself depending on how
        /// the player was assembled, and the ADS solve only works off the real eye.
        /// </summary>
        static Transform ResolveEye(Transform cameraPivot)
        {
            var cam = ResolveEyeCamera(cameraPivot);
            return cam != null ? cam.transform : cameraPivot;
        }

        static Camera ResolveEyeCamera(Transform cameraPivot)
        {
            if (cameraPivot != null)
            {
                var own = cameraPivot.GetComponent<Camera>();
                if (own != null)
                    return own;

                var child = cameraPivot.GetComponentInChildren<Camera>();
                if (child != null)
                    return child;
            }

            return Camera.main;
        }

        void LateUpdate()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (dt <= 0f)
                return;

            GatherSway(dt);
            GatherBob(dt);

            bool sprinting = _controller != null && _controller.IsSprinting && _ads < 0.15f;
            bool sliding = _controller != null && _controller.IsSliding;
            _sprint = Mathf.MoveTowards(_sprint, sprinting ? 1f : 0f, dt * 6f);
            _slide = Mathf.MoveTowards(_slide, sliding ? 1f : 0f, dt * 10f);
            _landPunch = Mathf.MoveTowards(_landPunch, 0f, dt * 3.5f);

            // ADS suppresses sway, bob and carry pose — the sight has to sit still to be usable.
            float looseness = Mathf.Lerp(1f, 0.12f, _ads);
            // Slides kill stride bob — the gun should ride low and steady through the carve.
            looseness *= Mathf.Lerp(1f, 0.2f, _slide);

            var basePose = Vector3.Lerp(hipOffset, adsOffset, Smooth(_ads));
            basePose = Vector3.Lerp(basePose, sprintOffset, _sprint * (1f - _ads));
            basePose = Vector3.Lerp(basePose, slideOffset, _slide * (1f - _ads));

            var targetPosition = basePose + (_swayTarget + BobOffset() + BreathOffset()) * looseness;
            var targetRotation = (_swayRotationTarget + BobRotation()) * looseness
                                 + adsTilt * Smooth(_ads)
                                 + sprintTilt * _sprint * (1f - _ads)
                                 + slideTilt * _slide * (1f - _ads);

            Spring(ref _positionOffset, ref _positionVelocity, targetPosition, positionStiffness, positionDamping, dt);
            Spring(ref _rotationOffset, ref _rotationVelocity, targetRotation, rotationStiffness, rotationDamping, dt);

            transform.localPosition = _positionOffset;
            transform.localRotation = Quaternion.Euler(_rotationOffset);
            ApplyHipFrame(_ads);
        }

        void GatherSway(float dt)
        {
            var input = GameInput.Instance;
            var look = input != null ? input.Look : Vector2.zero;

            // Framerate-independent: look is a per-frame delta, so normalise before scaling.
            float scale = dt > 0f ? Mathf.Min(1f, 1f / (dt * 60f)) : 1f;
            var lag = new Vector3(-look.x, look.y, 0f) * (swayPosition * scale);
            _swayTarget = Vector3.ClampMagnitude(lag, swayMax);
            _swayRotationTarget = new Vector3(look.y, -look.x, look.x * 0.6f) * (swayRotation * scale);
        }

        void GatherBob(float dt)
        {
            float speed = _controller != null ? _controller.PlanarSpeed : 0f;
            bool grounded = _controller == null || _controller.IsGrounded;
            float normalised = grounded ? Mathf.Clamp01(speed / 7.2f) : 0f;

            _bobPhase += dt * bobFrequency * (0.55f + normalised);
            _breathPhase += dt * breathFrequency;
            _bobAmount = Mathf.Lerp(_bobAmount, normalised, 1f - Mathf.Exp(-8f * dt));
        }

        Vector3 BobOffset()
        {
            // Figure-eight: horizontal at the stride rate, vertical at twice it.
            float x = Mathf.Sin(_bobPhase) * bobHorizontal;
            float y = -Mathf.Abs(Mathf.Cos(_bobPhase)) * bobVertical;
            return new Vector3(x, y, 0f) * _bobAmount;
        }

        Vector3 BobRotation() => new Vector3(
            Mathf.Cos(_bobPhase * 2f) * bobRoll * 0.4f,
            Mathf.Sin(_bobPhase) * bobRoll * 0.5f,
            -Mathf.Sin(_bobPhase) * bobRoll) * _bobAmount;

        Vector3 BreathOffset() => new Vector3(
            Mathf.Sin(_breathPhase * 0.77f) * breathAmplitude,
            Mathf.Sin(_breathPhase) * breathAmplitude,
            0f) * (1f - _bobAmount);

        static float Smooth(float t) => t * t * (3f - 2f * t);

        static void Spring(ref Vector3 value, ref Vector3 velocity, Vector3 target, float stiffness, float damping, float dt)
        {
            var accel = (target - value) * stiffness - velocity * damping;
            velocity += accel * dt;
            value += velocity * dt;
        }
    }
}
