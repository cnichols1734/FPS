using ArenaFps.Audio;
using ArenaFps.Ballistics;
using ArenaFps.Combat;
using ArenaFps.Core;
using ArenaFps.Feedback;
using ArenaFps.Input;
using ArenaFps.Player;
using UnityEngine;

namespace ArenaFps.Weapons
{
    /// <summary>
    /// Hitscan rifle and pistol with the two-layer recoil a modern shooter needs: an aim punch that
    /// genuinely moves the shot, and a separate visual kick that only moves the model. Spread blooms
    /// with fire rate and movement, so holding the trigger costs accuracy without costing control.
    /// </summary>
    public sealed class WeaponController : MonoBehaviour
    {
        public enum WeaponSlot { AssaultRifle, Pistol }

        [System.Serializable]
        public struct WeaponStats
        {
            public string name;
            public float damage;
            public float roundsPerMinute;
            public float range;
            public float adsFov;
            public float adsTime;
            public float hipSpread;
            public float adsSpread;
            public float spreadPerShot;
            public float spreadRecovery;
            public float maxSpread;
            public int magSize;
            public float reloadTime;
            public bool automatic;
            public RecoilPattern recoil;
        }

        [SerializeField] Transform firePoint;
        [SerializeField] Transform viewmodel;
        [SerializeField] Camera worldCamera;
        [SerializeField] LatencyProbe latencyProbe;
        [SerializeField] DualSenseDriver dualSense;
        [SerializeField] WeaponFeedback feedback;
        [SerializeField] ViewmodelMotion motion;

        [Header("Feel")]
        [SerializeField] float sprintToFireDelay = 0.14f;
        [SerializeField] float movementSpreadScale = 1.5f;
        /// <summary>Look sensitivity multiplier at full ADS. Slower aim is what makes sights feel braced.</summary>
        [SerializeField] float adsLookScale = 0.62f;

        [SerializeField] WeaponStats ar = new()
        {
            name = "SCAR-H",
            damage = 28f,
            roundsPerMinute = 600f,
            range = 150f,
            adsFov = 50f,
            // A 7.62 battle rifle should feel like it has mass coming up.
            adsTime = 0.22f,
            hipSpread = 2.0f,
            adsSpread = 0.14f,
            spreadPerShot = 0.3f,
            spreadRecovery = 3.2f,
            maxSpread = 4.5f,
            magSize = 20,
            // Overwritten in Awake from the authored reload clip length.
            reloadTime = 2.95f,
            automatic = true,
        };

        [SerializeField] WeaponStats pistol = new()
        {
            name = "P-18",
            damage = 38f,
            roundsPerMinute = 340f,
            range = 70f,
            adsFov = 52f,
            adsTime = 0.14f,
            hipSpread = 1.5f,
            adsSpread = 0.13f,
            spreadPerShot = 0.5f,
            spreadRecovery = 5.5f,
            maxSpread = 3.6f,
            magSize = 12,
            // Overwritten in Awake from the authored reload clip length.
            reloadTime = 1.512f,
            automatic = false,
        };

        WeaponSlot _slot = WeaponSlot.AssaultRifle;
        FpsController _controller;
        ViewmodelAnimator _viewAnim;

        int _ammo;
        float _nextShotTime;
        float _reloadUntil;
        bool _reloading;

        float _ads;
        float _defaultFov = 75f;

        Vector2 _aimPunch;
        Vector2 _aimPunchTarget;
        float _recoveryHoldUntil;
        int _shotIndex;
        float _lastShotTime;

        float _spreadBloom;
        float _sprintUntil;
        bool _fireWasHeld;

        public WeaponStats Current => _slot == WeaponSlot.AssaultRifle ? ar : pistol;
        public int Ammo => _ammo;
        public bool IsAds => _ads > 0.5f;
        public float AdsProgress => _ads;
        public bool IsReloading => _reloading;

        /// <summary>Total cone half-angle in degrees. The crosshair reads this directly.</summary>
        public float SpreadDegrees
        {
            get
            {
                float baseSpread = Mathf.Lerp(Current.hipSpread, Current.adsSpread, _ads);
                float movement = _controller != null
                    ? Mathf.Clamp01(_controller.PlanarSpeed / 7.2f) * movementSpreadScale * (1f - _ads * 0.7f)
                    : 0f;
                return Mathf.Min(Current.maxSpread, baseSpread + _spreadBloom + movement);
            }
        }

        void Awake()
        {
            ar.recoil = RecoilPattern.Rifle;
            pistol.recoil = RecoilPattern.Pistol;

            _controller = GetComponent<FpsController>();

            if (worldCamera == null)
                worldCamera = GetComponentInChildren<Camera>();
            if (worldCamera != null)
                _defaultFov = worldCamera.fieldOfView;

            if (viewmodel == null)
                viewmodel = transform.Find("CameraPivot/WeaponRoot") ?? transform.Find("WeaponRoot");

            if (viewmodel != null)
            {
                var gun = ScarHViewmodelBuilder.Ensure(viewmodel);
                motion = viewmodel.GetComponent<ViewmodelMotion>() ?? viewmodel.gameObject.AddComponent<ViewmodelMotion>();
                motion.Bind(_controller);

                // FPS-authored hands already sit in camera space — keep procedural sway light.
                motion.ConfigureAuthoredFpsPose();

                var pivot = _controller != null ? _controller.CameraPivot : null;
                var sight = ScarHViewmodelBuilder.FindDeep(gun, "SightAlign");
                if (sight != null && pivot != null)
                    motion.ConfigureIronSightAds(sight, pivot);

                _viewAnim = gun != null ? gun.GetComponent<ViewmodelAnimator>() : null;
                IsolateViewmodel(viewmodel);
            }

            if (latencyProbe == null)
                latencyProbe = GetComponentInChildren<LatencyProbe>();
            if (dualSense == null)
                dualSense = GetComponentInChildren<DualSenseDriver>();
            // Resolved before anchors so WeaponFeedback rebinds against the built viewmodel even
            // when it woke first and cached a muzzle that no longer exists.
            if (feedback == null)
                feedback = GetComponent<WeaponFeedback>() ?? gameObject.AddComponent<WeaponFeedback>();

            RefreshViewmodelAnchors();

            if (GetComponent<AdsDepthOfField>() == null)
                gameObject.AddComponent<AdsDepthOfField>();

            // Prefer the authored reload clip length; fall back to the SFX recording.
            float reloadSeconds = _viewAnim != null ? _viewAnim.ReloadDuration : 0f;
            if (reloadSeconds < 0.05f)
                reloadSeconds = SfxBank.Duration(Sfx.Reload);
            if (reloadSeconds > 0.05f)
            {
                ar.reloadTime = reloadSeconds;
                pistol.reloadTime = reloadSeconds;
            }

            _ammo = Current.magSize;
        }

        /// <summary>Re-binds FirePoint under the SCAR-H after the viewmodel is built or swapped.</summary>
        public void RefreshViewmodelAnchors()
        {
            if (viewmodel == null)
                return;

            // Anchors ride the rifle bone, so a path lookup will not reach them.
            firePoint = ScarHViewmodelBuilder.FindDeep(viewmodel, "FirePoint");
            if (firePoint == null)
            {
                var fp = new GameObject("FirePoint");
                fp.transform.SetParent(viewmodel, false);
                fp.transform.localPosition = new Vector3(0f, 0.02f, 0.55f);
                firePoint = fp.transform;
            }

            if (feedback != null)
                feedback.RebindAnchors(viewmodel);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            var input = GameInput.Instance;

            TickReload();

            if (input == null)
                return;

            if (input.Weapon1PressedThisFrame) Equip(WeaponSlot.AssaultRifle);
            if (input.Weapon2PressedThisFrame) Equip(WeaponSlot.Pistol);

            if (_controller != null && _controller.IsSprinting)
                _sprintUntil = Time.time + sprintToFireDelay;

            bool wantsAds = input.AimHeld && !_reloading && (_controller == null || !_controller.IsSprinting);
            float adsSpeed = 1f / Mathf.Max(0.05f, Current.adsTime);
            _ads = Mathf.MoveTowards(_ads, wantsAds ? 1f : 0f, adsSpeed * dt);
            motion?.SetAds(_ads);

            if (_controller != null)
                _controller.LookScale = Mathf.Lerp(1f, adsLookScale, _ads);

            if (worldCamera != null)
            {
                float eased = _ads * _ads * (3f - 2f * _ads);
                worldCamera.fieldOfView = Mathf.Lerp(_defaultFov, Current.adsFov, eased);
            }

            _spreadBloom = Mathf.MoveTowards(_spreadBloom, 0f, Current.spreadRecovery * dt);
            TickRecoil(dt);

            if (input.ReloadPressedThisFrame)
                TryReload();

            bool wantsFire = Current.automatic ? input.FireHeld : WasFirePressed();
            if (wantsFire)
                TryFire();
        }

        /// <summary>
        /// The viewmodel sits centimetres in front of the camera the shot is traced from, so any
        /// collider on it would swallow every round at the muzzle. Moving it to its own layer and
        /// disabling its colliders makes that impossible rather than merely unlikely.
        /// </summary>
        static void IsolateViewmodel(Transform root)
        {
            GameLayers.ApplyRecursive(root.gameObject, GameLayers.Viewmodel);
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
        }

        void OnDisable()
        {
            // Death and weapon swaps disable this component mid-ADS; leaving the scale applied would
            // strand the player with slow aim after respawn.
            if (_controller != null)
                _controller.LookScale = 1f;
            _ads = 0f;
        }

        /// <summary>
        /// External view punch — taking fire disturbs the player's aim, same as it disturbs a bot's.
        /// Routed through the recoil channel so there is exactly one owner of camera rotation.
        /// </summary>
        public void AddViewPunch(Vector2 punch, float holdSeconds = 0.05f)
        {
            _aimPunchTarget += punch;
            _recoveryHoldUntil = Mathf.Max(_recoveryHoldUntil, Time.time + holdSeconds);
        }

        void LateUpdate()
        {
            if (worldCamera == null)
                return;
            // Assigned, never accumulated: multiplying here used to tumble the camera every frame.
            worldCamera.transform.localRotation = Quaternion.Euler(-_aimPunch.x, _aimPunch.y, 0f);
        }

        void TickRecoil(float dt)
        {
            // The view holds at full kick briefly, then recovers — an instant return feels weightless.
            if (Time.time >= _recoveryHoldUntil)
            {
                float k = 1f - Mathf.Exp(-Current.recoil.recovery * dt);
                _aimPunchTarget = Vector2.Lerp(_aimPunchTarget, Vector2.zero, k);
            }
            _aimPunch = Vector2.Lerp(_aimPunch, _aimPunchTarget, 1f - Mathf.Exp(-34f * dt));

            // A gap in fire resets the pattern, so every burst starts from a known place.
            if (Time.time - _lastShotTime > 0.42f)
                _shotIndex = 0;
        }

        void Equip(WeaponSlot slot)
        {
            if (_slot == slot || _reloading)
                return;
            _slot = slot;
            _ammo = Current.magSize;
            _ads = 0f;
            _spreadBloom = 0f;
            _shotIndex = 0;
            if (_slot == WeaponSlot.AssaultRifle)
                _viewAnim?.PlayDraw();
            else
                motion?.AddReloadShake();
            Sfx3D.Instance.Play2D(Sfx.BoltRelease, 0.5f);
            dualSense?.SetLightState(DualSenseDriver.LightState.Idle);
        }

        void TryReload()
        {
            if (_reloading || _ammo >= Current.magSize)
                return;

            bool empty = _ammo <= 0;
            float reloadSeconds = empty
                ? (_viewAnim != null ? _viewAnim.ReloadEmptyDuration : 0f)
                : (_viewAnim != null ? _viewAnim.ReloadDuration : 0f);
            if (reloadSeconds < 0.05f)
                reloadSeconds = SfxBank.Duration(Sfx.Reload);
            if (reloadSeconds > 0.05f)
            {
                ar.reloadTime = reloadSeconds;
                pistol.reloadTime = reloadSeconds;
            }

            _reloading = true;
            _reloadUntil = Time.time + Current.reloadTime;
            _ads = 0f;
            if (_slot == WeaponSlot.AssaultRifle)
                _viewAnim?.PlayReload(empty);
            else
                motion?.AddReloadShake();
            // One authored one-shot covers the whole reload. Do not layer MagOut/MagIn/Bolt on top.
            Sfx3D.Instance.Play2D(Sfx.Reload, 0.75f, 0.02f);
        }

        void TickReload()
        {
            if (!_reloading)
                return;

            if (Time.time < _reloadUntil)
                return;

            _reloading = false;
            _ammo = Current.magSize;
            _spreadBloom = 0f;
            dualSense?.SetLightState(DualSenseDriver.LightState.Idle);
        }

        void TryFire()
        {
            if (_reloading || Time.time < _nextShotTime || Time.time < _sprintUntil)
                return;

            if (_ammo <= 0)
            {
                Sfx3D.Instance.Play2D(Sfx.DryFire, 0.5f);
                dualSense?.SetLightState(DualSenseDriver.LightState.LowAmmo);
                _nextShotTime = Time.time + 0.25f;
                TryReload();
                return;
            }

            float interval = 60f / Mathf.Max(1f, Current.roundsPerMinute);
            // Advance from the scheduled time so the cyclic rate does not drift with frame rate.
            _nextShotTime = Mathf.Max(Time.time, _nextShotTime + interval);
            _lastShotTime = Time.time;
            _ammo--;

            latencyProbe?.NotifyFireInput();
            dualSense?.PulseFire();
            Sfx3D.Instance.Play2D(_slot == WeaponSlot.Pistol ? Sfx.PistolShot : Sfx.RifleShot, 0.62f, 0.05f);
            if (_slot == WeaponSlot.AssaultRifle)
                _viewAnim?.PlayFire(interval);

            var camera = worldCamera.transform;
            var origin = firePoint != null ? firePoint.position : camera.position;
            var muzzle = feedback != null ? feedback.MuzzlePosition : origin;

            feedback?.Flash();
            ImpactFx.Instance.MuzzleFlash(muzzle, camera.forward, _slot == WeaponSlot.Pistol ? 0.7f : 1f);
            if (feedback != null)
                ImpactFx.Instance.EjectCasing(feedback.EjectionPosition, camera.right, camera.up,
                    _controller != null ? _controller.Velocity : Vector3.zero);

            float spread = SpreadDegrees;
            var direction = Quaternion.Euler(
                Random.Range(-spread, spread) * 0.5f,
                Random.Range(-spread, spread) * 0.5f,
                0f) * camera.forward;

            // Trace from the camera so the reticle is the contract, then draw from the muzzle.
            Resolve(camera.position, direction, muzzle);

            ApplyRecoil();
            latencyProbe?.NotifyMuzzleFlash();

            if (_ammo <= Mathf.CeilToInt(Current.magSize * 0.2f))
                dualSense?.SetLightState(DualSenseDriver.LightState.LowAmmo);
        }

        void Resolve(Vector3 origin, Vector3 direction, Vector3 muzzle)
        {
            var ballistic = PenetrationSolver.Trace(origin, direction, Current.range, Current.damage, GameLayers.PlayerBulletMask);
            var endPoint = origin + direction * Current.range;

            if (!ballistic.didHit)
            {
                ImpactFx.Instance.Tracer(muzzle, endPoint);
                return;
            }

            endPoint = ballistic.hit.point;
            float dealt = PenetrationSolver.DamageAfter(ballistic, Current.damage);
            var kind = ballistic.surface != null ? ballistic.surface.kind : SurfaceKind.Default;

            ballistic.hit.collider.GetComponentInParent<BreakableCover>()
                ?.ApplyBallisticDamage(dealt, ballistic.hit.point, direction);

            var hitbox = ballistic.hit.collider.GetComponent<Hitbox>();
            var damageable = hitbox != null && hitbox.owner != null
                ? hitbox.owner
                : ballistic.hit.collider.GetComponentInParent<Damageable>();

            ImpactFx.Instance.Tracer(muzzle, endPoint);

            if (damageable != null)
            {
                var info = new DamageInfo
                {
                    Amount = dealt,
                    Point = ballistic.hit.point,
                    Direction = direction,
                    Normal = ballistic.hit.normal,
                    Collider = ballistic.hit.collider,
                    Attacker = gameObject,
                    FromPlayer = true,
                    Surface = kind,
                    Ricochet = ballistic.ricocheted,
                    Penetrated = ballistic.penetrated,
                    Multiplier = 1f,
                };
                damageable.ApplyDamage(info);
                damageable.GetComponent<AI.BotBrain>()?.NotifyIncomingFire();
            }
            else
            {
                ImpactFx.Instance.SurfaceImpact(ballistic.hit.point, ballistic.hit.normal, direction, kind);
                if (ballistic.ricocheted)
                    ImpactFx.Instance.Ricochet(ballistic.hit.point, ballistic.hit.normal, ballistic.continuedDirection);
                if (ballistic.penetrated && ballistic.exitPoint != Vector3.zero)
                    ImpactFx.Instance.ExitSpall(ballistic.exitPoint, ballistic.continuedDirection, kind);
            }

            var rb = ballistic.hit.collider.attachedRigidbody;
            if (rb != null && !rb.isKinematic)
                rb.AddForceAtPosition(direction * (6f + dealt * 0.12f), ballistic.hit.point, ForceMode.Impulse);
        }

        void ApplyRecoil()
        {
            var kick = Current.recoil.Sample(_shotIndex++);

            // Aiming down sights tightens the pattern, exactly as a braced stance should.
            kick *= Mathf.Lerp(1f, 0.68f, _ads);

            _aimPunchTarget += kick;
            _recoveryHoldUntil = Time.time + Current.recoil.recoveryDelay;

            _spreadBloom = Mathf.Min(Current.maxSpread, _spreadBloom + Current.spreadPerShot);
            motion?.AddRecoil(kick, _slot == WeaponSlot.Pistol ? 1.15f : 0.85f);
        }

        bool WasFirePressed()
        {
            bool held = GameInput.Instance != null && GameInput.Instance.FireHeld;
            bool pressed = held && !_fireWasHeld;
            _fireWasHeld = held;
            return pressed;
        }
    }
}
