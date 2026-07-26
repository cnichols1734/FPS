using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;

namespace ArenaFps.Input
{
    /// <summary>
    /// DualSense haptics + lightbar on macOS USB HID.
    /// Bluetooth: input works; rumble/lightbar do not (Unity limitation) — we warn once.
    /// </summary>
    public sealed class DualSenseDriver : MonoBehaviour
    {
        public enum LightState
        {
            Idle,
            LowAmmo,
            Hit,
            Dead
        }

        [SerializeField] float fireRumbleHigh = 0.55f;
        [SerializeField] float fireRumbleLow = 0.25f;
        [SerializeField] float fireRumbleDuration = 0.05f;
        [SerializeField] float hitRumbleHigh = 0.8f;
        [SerializeField] float hitRumbleLow = 0.6f;
        [SerializeField] float hitRumbleDuration = 0.18f;

        DualSenseGamepadHID _pad;
        float _rumbleTimer;
        float _rumbleHigh;
        float _rumbleLow;
        bool _warnedBluetooth;
        LightState _state = LightState.Idle;

        public bool IsConnected => _pad != null;
        public bool HapticsAvailable { get; private set; }

        void OnEnable()
        {
            InputSystem.onDeviceChange += OnDeviceChange;
            TryBind();
        }

        void OnDisable()
        {
            InputSystem.onDeviceChange -= OnDeviceChange;
            StopRumble();
        }

        void Update()
        {
            if (_pad == null)
                return;

            if (_rumbleTimer > 0f)
            {
                _rumbleTimer -= Time.unscaledDeltaTime;
                if (_rumbleTimer <= 0f)
                    StopRumble();
            }
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device is DualSenseGamepadHID || device is Gamepad)
                TryBind();
        }

        void TryBind()
        {
            // In Input System 1.20, DualSenseGamepadHID.current is typed as DualShockGamepad.
            _pad = DualSenseGamepadHID.current as DualSenseGamepadHID;
            if (_pad == null)
            {
                foreach (var g in Gamepad.all)
                {
                    if (g is DualSenseGamepadHID ds)
                    {
                        _pad = ds;
                        break;
                    }
                }
            }

            HapticsAvailable = _pad != null;
            if (_pad == null)
                return;

            // Unity docs: rumble/lightbar over Bluetooth unsupported on macOS.
            // Heuristic: USB devices usually expose a more complete description.
            var desc = _pad.description;
            var viaBluetooth = desc.interfaceName != null &&
                               desc.interfaceName.ToLowerInvariant().Contains("bluetooth");
            if (viaBluetooth && !_warnedBluetooth)
            {
                _warnedBluetooth = true;
                HapticsAvailable = false;
                Debug.LogWarning("[DualSense] Connected over Bluetooth — rumble/lightbar unavailable. Use USB-C for haptics.");
            }

            ApplyLightbar(_state);
        }

        public void SetLightState(LightState state)
        {
            _state = state;
            ApplyLightbar(state);
        }

        public void PulseFire()
        {
            Pulse(fireRumbleLow, fireRumbleHigh, fireRumbleDuration);
        }

        public void PulseHit()
        {
            SetLightState(LightState.Hit);
            Pulse(hitRumbleLow, hitRumbleHigh, hitRumbleDuration);
        }

        public void Pulse(float low, float high, float duration)
        {
            if (_pad == null || !HapticsAvailable)
                return;

            _rumbleLow = Mathf.Clamp01(low);
            _rumbleHigh = Mathf.Clamp01(high);
            _rumbleTimer = Mathf.Max(0.01f, duration);
            _pad.SetMotorSpeeds(_rumbleLow, _rumbleHigh);
        }

        void StopRumble()
        {
            _rumbleTimer = 0f;
            if (_pad != null)
                _pad.SetMotorSpeeds(0f, 0f);
        }

        void ApplyLightbar(LightState state)
        {
            if (_pad == null || !HapticsAvailable)
                return;

            Color c = state switch
            {
                LightState.LowAmmo => new Color(1f, 0.55f, 0.1f),
                LightState.Hit => new Color(1f, 0.1f, 0.1f),
                LightState.Dead => new Color(0.15f, 0f, 0f),
                _ => new Color(0.85f, 0.9f, 1f)
            };
            _pad.SetLightBarColor(c);
        }
    }
}
