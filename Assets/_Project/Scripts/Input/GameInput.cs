using UnityEngine;
using UnityEngine.InputSystem;

namespace ArenaFps.Input
{
    /// <summary>
    /// Runtime input hub. Builds a valid InputActionAsset once (never enable orphan maps).
    /// Runs early so locomotion can ForceSprintLatch after reading the same-frame L3 click.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class GameInput : MonoBehaviour
    {
        public static GameInput Instance { get; private set; }

        [SerializeField] InputActionAsset actionAsset;

        /// <summary>How far the analog aim trigger has to travel before the sights come up.</summary>
        const float AimTriggerPoint = 0.15f;

        InputAction _move;
        InputAction _look;
        InputAction _lookStick;
        InputAction _fire;
        InputAction _aim;
        InputAction _reload;
        InputAction _sprint;
        InputAction _sprintToggle;
        InputAction _crouch;
        InputAction _jump;
        InputAction _weapon1;
        InputAction _weapon2;
        bool _ownsAsset;
        bool _sprintLatched;

        public Vector2 Move { get; private set; }

        /// <summary>Mouse delta for this frame. Already a per-frame step; do not scale by delta time.</summary>
        public Vector2 Look { get; private set; }

        /// <summary>Right-stick deflection, -1..1. An absolute axis, so it must be integrated over time.</summary>
        public Vector2 LookStick { get; private set; }

        public bool FireHeld { get; private set; }
        public bool AimHeld { get; private set; }
        public bool SprintHeld { get; private set; }
        public bool CrouchHeld { get; private set; }
        public bool CrouchPressedThisFrame { get; private set; }
        /// <summary>Shift or L3 pressed this frame — used for slide-cancel into sprint.</summary>
        public bool SprintPressedThisFrame { get; private set; }
        public bool ReloadPressedThisFrame { get; private set; }
        public bool JumpPressedThisFrame { get; private set; }
        public bool Weapon1PressedThisFrame { get; private set; }
        public bool Weapon2PressedThisFrame { get; private set; }

        /// <summary>Force the gamepad sprint latch on/off (slide-cancel pops you back into a sprint).</summary>
        public void ForceSprintLatch(bool on) => _sprintLatched = on;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            // Input lives on a child of the player rig, and DontDestroyOnLoad silently refuses
            // anything that is not a root object. Detach first so the singleton actually persists.
            if (transform.parent != null)
                transform.SetParent(null, false);
            DontDestroyOnLoad(gameObject);

            EnsureActions();
        }

        void OnEnable()
        {
            actionAsset?.Enable();
        }

        void OnDisable()
        {
            actionAsset?.Disable();
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            if (_ownsAsset && actionAsset != null)
            {
                actionAsset.Disable();
                Destroy(actionAsset);
                actionAsset = null;
            }
        }

        void Update()
        {
            if (_move == null)
                return;

            Move = _move.ReadValue<Vector2>();
            Look = _look.ReadValue<Vector2>();
            LookStick = _lookStick.ReadValue<Vector2>();
            FireHeld = _fire.IsPressed();
            // A DualSense left trigger only counts as "pressed" past the default half-pull, which
            // feels like the sights are stuck. Take the analog value at a light pull instead; a
            // mouse button still reads 1 here, so the keyboard path is unchanged.
            AimHeld = _aim.IsPressed() || _aim.ReadValue<float>() >= AimTriggerPoint;
            SprintPressedThisFrame = (_sprint != null && _sprint.WasPressedThisFrame())
                                     || (_sprintToggle != null && _sprintToggle.WasPressedThisFrame());
            SprintHeld = (_sprint != null && _sprint.IsPressed()) || TickSprintLatch();
            CrouchHeld = _crouch.IsPressed();
            CrouchPressedThisFrame = _crouch.WasPressedThisFrame();
            ReloadPressedThisFrame = _reload.WasPressedThisFrame();
            JumpPressedThisFrame = _jump.WasPressedThisFrame();
            Weapon1PressedThisFrame = _weapon1.WasPressedThisFrame();
            Weapon2PressedThisFrame = _weapon2.WasPressedThisFrame();
        }

        /// <summary>
        /// Gamepad sprint latches on a stick click instead of asking the thumb to hold the stick down
        /// while also pushing it. It releases itself the moment sprinting stops making sense — you
        /// stopped moving, you are aiming, or you are firing — so it never has to be clicked off.
        /// </summary>
        bool TickSprintLatch()
        {
            if (_sprintToggle != null && _sprintToggle.WasPressedThisFrame())
                _sprintLatched = !_sprintLatched;

            if (_sprintLatched && (Move.sqrMagnitude < 0.09f || AimHeld || FireHeld))
                _sprintLatched = false;

            return _sprintLatched;
        }

        void EnsureActions()
        {
            if (actionAsset == null)
            {
                actionAsset = BuildRuntimeAsset();
                _ownsAsset = true;
            }

            var player = actionAsset.FindActionMap("Player", throwIfNotFound: true);
            _move = player.FindAction("Move", true);
            _look = player.FindAction("Look", true);
            _lookStick = player.FindAction("LookStick", true);
            _fire = player.FindAction("Fire", true);
            _aim = player.FindAction("Aim", true);
            _reload = player.FindAction("Reload", true);
            _sprint = player.FindAction("Sprint", true);
            _sprintToggle = player.FindAction("SprintToggle", true);
            _crouch = player.FindAction("Crouch", true);
            _jump = player.FindAction("Jump", true);
            _weapon1 = player.FindAction("Weapon1", true);
            _weapon2 = player.FindAction("Weapon2", true);
        }

        static InputActionAsset BuildRuntimeAsset()
        {
            // Build entirely via JSON — avoids orphan-map / "Map must be contained in state" bugs
            // that show up with piecemeal AddActionMap + Enable on Input System 1.20.
            const string json = @"{
  ""name"": ""PlayerControls_Runtime"",
  ""maps"": [
    {
      ""name"": ""Player"",
      ""id"": ""7c9e2a1b-4d5e-4f6a-8b9c-0d1e2f3a4b5c"",
      ""actions"": [
        { ""name"": ""Move"", ""type"": ""Value"", ""id"": ""11111111-0000-0000-0000-000000000001"", ""expectedControlType"": ""Vector2"" },
        { ""name"": ""Look"", ""type"": ""Value"", ""id"": ""11111111-0000-0000-0000-000000000002"", ""expectedControlType"": ""Vector2"" },
        { ""name"": ""LookStick"", ""type"": ""Value"", ""id"": ""11111111-0000-0000-0000-00000000000b"", ""expectedControlType"": ""Vector2"" },
        { ""name"": ""Fire"", ""type"": ""Button"", ""id"": ""11111111-0000-0000-0000-000000000003"", ""expectedControlType"": ""Button"" },
        { ""name"": ""Aim"", ""type"": ""Button"", ""id"": ""11111111-0000-0000-0000-000000000004"", ""expectedControlType"": ""Button"" },
        { ""name"": ""Reload"", ""type"": ""Button"", ""id"": ""11111111-0000-0000-0000-000000000005"", ""expectedControlType"": ""Button"" },
        { ""name"": ""Sprint"", ""type"": ""Button"", ""id"": ""11111111-0000-0000-0000-000000000006"", ""expectedControlType"": ""Button"" },
        { ""name"": ""SprintToggle"", ""type"": ""Button"", ""id"": ""11111111-0000-0000-0000-00000000000c"", ""expectedControlType"": ""Button"" },
        { ""name"": ""Crouch"", ""type"": ""Button"", ""id"": ""11111111-0000-0000-0000-000000000007"", ""expectedControlType"": ""Button"" },
        { ""name"": ""Jump"", ""type"": ""Button"", ""id"": ""11111111-0000-0000-0000-000000000008"", ""expectedControlType"": ""Button"" },
        { ""name"": ""Weapon1"", ""type"": ""Button"", ""id"": ""11111111-0000-0000-0000-000000000009"", ""expectedControlType"": ""Button"" },
        { ""name"": ""Weapon2"", ""type"": ""Button"", ""id"": ""11111111-0000-0000-0000-00000000000a"", ""expectedControlType"": ""Button"" }
      ],
      ""bindings"": [
        { ""name"": ""WASD"", ""id"": ""22222222-0000-0000-0000-000000000001"", ""path"": ""2DVector"", ""action"": ""Move"", ""isComposite"": true },
        { ""name"": ""up"", ""id"": ""22222222-0000-0000-0000-000000000002"", ""path"": ""<Keyboard>/w"", ""action"": ""Move"", ""isPartOfComposite"": true },
        { ""name"": ""down"", ""id"": ""22222222-0000-0000-0000-000000000003"", ""path"": ""<Keyboard>/s"", ""action"": ""Move"", ""isPartOfComposite"": true },
        { ""name"": ""left"", ""id"": ""22222222-0000-0000-0000-000000000004"", ""path"": ""<Keyboard>/a"", ""action"": ""Move"", ""isPartOfComposite"": true },
        { ""name"": ""right"", ""id"": ""22222222-0000-0000-0000-000000000005"", ""path"": ""<Keyboard>/d"", ""action"": ""Move"", ""isPartOfComposite"": true },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000006"", ""path"": ""<Gamepad>/leftStick"", ""action"": ""Move"", ""processors"": ""StickDeadzone"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000007"", ""path"": ""<Mouse>/delta"", ""action"": ""Look"", ""processors"": ""ScaleVector2(x=0.055,y=0.055)"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000008"", ""path"": ""<Gamepad>/rightStick"", ""action"": ""LookStick"", ""processors"": ""StickDeadzone(min=0.14,max=0.98)"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000009"", ""path"": ""<Mouse>/leftButton"", ""action"": ""Fire"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-00000000000a"", ""path"": ""<Gamepad>/rightTrigger"", ""action"": ""Fire"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-00000000000b"", ""path"": ""<Mouse>/rightButton"", ""action"": ""Aim"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-00000000000c"", ""path"": ""<Gamepad>/leftTrigger"", ""action"": ""Aim"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-00000000000d"", ""path"": ""<Keyboard>/r"", ""action"": ""Reload"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-00000000000e"", ""path"": ""<Gamepad>/buttonWest"", ""action"": ""Reload"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-00000000000f"", ""path"": ""<Keyboard>/leftShift"", ""action"": ""Sprint"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000010"", ""path"": ""<Gamepad>/leftStickPress"", ""action"": ""SprintToggle"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000011"", ""path"": ""<Keyboard>/c"", ""action"": ""Crouch"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000012"", ""path"": ""<Keyboard>/leftCtrl"", ""action"": ""Crouch"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000013"", ""path"": ""<Gamepad>/buttonEast"", ""action"": ""Crouch"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000014"", ""path"": ""<Keyboard>/space"", ""action"": ""Jump"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000015"", ""path"": ""<Gamepad>/buttonSouth"", ""action"": ""Jump"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000016"", ""path"": ""<Keyboard>/1"", ""action"": ""Weapon1"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000017"", ""path"": ""<Keyboard>/2"", ""action"": ""Weapon2"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000018"", ""path"": ""<Gamepad>/dpad/up"", ""action"": ""Weapon1"" },
        { ""name"": """", ""id"": ""22222222-0000-0000-0000-000000000019"", ""path"": ""<Gamepad>/dpad/down"", ""action"": ""Weapon2"" }
      ]
    }
  ]
}";
            return InputActionAsset.FromJson(json);
        }
    }
}
