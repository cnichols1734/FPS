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
    /// Hitscan SCAR-H and ACR with the two-layer recoil a modern shooter needs: an aim punch that
    /// genuinely moves the shot, and a separate visual kick that only moves the model. Spread blooms
    /// with fire rate and movement, so holding the trigger costs accuracy without costing control.
    /// </summary>
    public sealed class WeaponController : MonoBehaviour
    {
        public enum WeaponSlot { AssaultRifle, Carbine }

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

        [SerializeField] WeaponStats carbine = new()
        {
            name = "ACR",
            damage = 22f,
            roundsPerMinute = 720f,
            range = 120f,
            adsFov = 48f,
            adsTime = 0.16f,
            hipSpread = 1.7f,
            adsSpread = 0.12f,
            spreadPerShot = 0.22f,
            spreadRecovery = 4.0f,
            maxSpread = 3.8f,
            magSize = 30,
            // Overwritten from the ACR reload clip length.
            reloadTime = 3.3f,
            automatic = true,
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

        public WeaponStats Current => _slot == WeaponSlot.AssaultRifle ? ar : carbine;
        public WeaponSlot Slot => _slot;
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
            carbine.recoil = RecoilPattern.Carbine;

            _controller = GetComponent<FpsController>();

            if (worldCamera == null)
                worldCamera = GetComponentInChildren<Camera>();
            if (worldCamera != null)
                _defaultFov = worldCamera.fieldOfView;

            if (viewmodel == null)
                viewmodel = transform.Find("CameraPivot/WeaponRoot") ?? transform.Find("WeaponRoot");

            if (viewmodel != null)
            {
                motion = viewmodel.GetComponent<ViewmodelMotion>() ?? viewmodel.gameObject.AddComponent<ViewmodelMotion>();
                motion.Bind(_controller);
                // Zero WeaponRoot before seating — both packs own their hip pocket on the wrapper.
                motion.ConfigureAuthoredFpsPose();
                BuildActiveViewmodel();
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

            SyncReloadTimesFromClips();
            _ammo = Current.magSize;
        }

        /// <summary>Re-binds FirePoint after the viewmodel is built or swapped.</summary>
        public void RefreshViewmodelAnchors()
        {
            if (viewmodel == null)
                return;

            firePoint = FindAnchor(viewmodel, "FirePoint");
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

        Transform BuildActiveViewmodel()
        {
            if (viewmodel == null)
                return null;

            motion?.ClearAuthoredHipFraming();

            Transform gun = _slot == WeaponSlot.AssaultRifle
                ? ScarHViewmodelBuilder.Ensure(viewmodel)
                : AcrViewmodelBuilder.Ensure(viewmodel);

            var pivot = _controller != null ? _controller.CameraPivot : null;
            var sight = FindAnchor(gun, "SightAlign");
            if (sight != null && pivot != null)
            {
                // Holo: tight relief, zero vertical bias, then screen-calibrate the glowing
                // reticle onto the HUD / hitscan centre. Irons keep ghost-ring drop.
                if (_slot == WeaponSlot.Carbine)
                {
                    // ADS first on pure Head_Cam, then layer the hip pocket/zoom so aiming
                    // still lands on the old perfect seating.
                    motion?.ConfigureIronSightAds(sight, pivot, 0.16f, 0f);
                    float prevFov = worldCamera != null ? worldCamera.fieldOfView : 0f;
                    if (worldCamera != null)
                        worldCamera.fieldOfView = carbine.adsFov;
                    motion?.CalibrateAdsToViewportCenter(pivot, () =>
                        AcrViewmodelBuilder.TryMeasureReticleWorld(gun, out var point)
                            ? point
                            : sight.position);
                    if (worldCamera != null)
                        worldCamera.fieldOfView = prevFov;
                    motion?.ConfigureAuthoredHipFraming(
                        gun, AcrViewmodelBuilder.HipPocket, AcrViewmodelBuilder.HipZoomScale);
                }
                else
                {
                    motion?.ConfigureIronSightAds(sight, pivot);
                }
            }

            _viewAnim = gun != null ? gun.GetComponent<ViewmodelAnimator>() : null;
            return gun;
        }

        void SyncReloadTimesFromClips()
        {
            float reloadSeconds = _viewAnim != null ? _viewAnim.ReloadDuration : 0f;
            if (reloadSeconds < 0.05f)
                reloadSeconds = SfxBank.Duration(Sfx.Reload);
            if (reloadSeconds <= 0.05f)
                return;

            if (_slot == WeaponSlot.AssaultRifle)
                ar.reloadTime = reloadSeconds;
            else
                carbine.reloadTime = reloadSeconds;
        }

        static Transform FindAnchor(Transform root, string name)
        {
            if (root == null)
                return null;
            return ScarHViewmodelBuilder.FindDeep(root, name) ?? AcrViewmodelBuilder.FindDeep(root, name);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            var input = GameInput.Instance;

            TickReload();

            if (input == null)
                return;

            if (input.Weapon1PressedThisFrame) Equip(WeaponSlot.AssaultRifle);
            if (input.Weapon2PressedThisFrame) Equip(WeaponSlot.Carbine);

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
            // ACR ADS recovers slower so the climb sticks like COD instead of springing back down.
            if (Time.time >= _recoveryHoldUntil)
            {
                float recovery = Current.recoil.recovery;
                if (_slot == WeaponSlot.Carbine)
                    recovery *= Mathf.Lerp(1f, 0.55f, _ads);
                float k = 1f - Mathf.Exp(-recovery * dt);
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

            BuildActiveViewmodel();
            RefreshViewmodelAnchors();
            SyncReloadTimesFromClips();
            _viewAnim?.PlayDraw();

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
                if (_slot == WeaponSlot.AssaultRifle)
                    ar.reloadTime = reloadSeconds;
                else
                    carbine.reloadTime = reloadSeconds;
            }

            _reloading = true;
            _reloadUntil = Time.time + Current.reloadTime;
            _ads = 0f;
            _viewAnim?.PlayReload(empty);
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
            Sfx3D.Instance.Play2D(Sfx.RifleShot, 0.62f, 0.05f);
            // ACR fire clip has baked climb that fights a COD-style ADS pattern. Hip plays it;
            // ADS uses procedural kick only so the optic climbs in a learnable way.
            bool muteFireAnim = _slot == WeaponSlot.Carbine && _ads > 0.55f;
            if (!muteFireAnim)
                _viewAnim?.PlayFire(interval);

            var camera = worldCamera.transform;
            var origin = firePoint != null ? firePoint.position : camera.position;
            var muzzle = feedback != null ? feedback.MuzzlePosition : origin;

            feedback?.Flash();
            ImpactFx.Instance.MuzzleFlash(muzzle, camera.forward, _slot == WeaponSlot.Carbine ? 0.85f : 1f);
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

            // COD-style ADS: clear climb you pull against, not a trampoline and not a frozen optic.
            // Authored fire clip stays muted above; this is the feel layer.
            // ACR ADS: full pattern weight — climb + lateral walk + gun punch, not pitch-only.
            float adsRecoil = _slot == WeaponSlot.Carbine ? 0.78f : 0.68f;
            kick *= Mathf.Lerp(1f, adsRecoil, _ads);
            if (_slot == WeaponSlot.Carbine)
            {
                kick.x *= Mathf.Lerp(1f, 0.7f, _ads);  // vertical
                kick.y *= Mathf.Lerp(1f, 0.95f, _ads); // keep the horizontal walk alive
            }

            _aimPunchTarget += kick;
            float hold = Current.recoil.recoveryDelay;
            if (_slot == WeaponSlot.Carbine)
                hold *= Mathf.Lerp(1f, 1.45f, _ads);
            _recoveryHoldUntil = Time.time + hold;

            _spreadBloom = Mathf.Min(Current.maxSpread, _spreadBloom + Current.spreadPerShot);

            float viewPunch = _slot == WeaponSlot.Carbine
                ? Mathf.Lerp(0.75f, 0.55f, _ads)
                : 0.85f;
            var viewKick = kick;
            if (_slot == WeaponSlot.Carbine)
                viewKick *= Mathf.Lerp(1f, 0.85f, _ads);
            motion?.AddRecoil(viewKick, viewPunch);
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
