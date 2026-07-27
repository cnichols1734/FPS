using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ArenaFps.Combat
{
    /// <summary>
    /// Plays the enemy's mocap through a three-layer Playables graph:
    ///
    ///   0  locomotion  — a seven-way directional blend (idle, walk/run forward and back, strafes)
    ///   1  action      — full-body one-shots that outrank the blend: start, stop, jump, death
    ///   2  upper body  — masked fire and reload, so the legs keep walking underneath
    ///
    /// Built in code rather than as an AnimatorController asset for the same reason the rest of the
    /// bot is: bots are assembled at runtime, and a graph needs no artist round-trip when a clip is
    /// added or renamed. It also matches how the first-person viewmodels are already driven.
    ///
    /// The blend inputs share one normalised clock. Unsynchronised clips are the classic cause of
    /// blended locomotion looking like a stumble — two cycles of different length drift apart and
    /// the character plants both feet at once.
    /// </summary>
    public sealed class SoldierClipPlayer
    {
        const int LocoSlots = 7;
        const int ActionSlots = 8;
        const int UpperSlots = 2;

        // Locomotion slots.
        const int Idle = 0, WalkF = 1, WalkB = 2, StrafeL = 3, StrafeR = 4, RunF = 5, RunB = 6;

        // Action slots.
        const int StartF = 0, StartB = 1, StopF = 2, StopB = 3, JumpF = 4, JumpB = 5, DeathStand = 6, DeathMove = 7;

        // Hysteresis on the move/stop test. A single threshold makes a bot hovering at walking pace
        // retrigger the start and stop clips against each other every few frames.
        const float MoveEnter = 0.28f;
        const float MoveExit = 0.12f;

        readonly PlayableGraph _graph;
        readonly AnimationMixerPlayable _locomotion;
        readonly AnimationMixerPlayable _action;
        readonly AnimationMixerPlayable _upper;
        readonly AnimationLayerMixerPlayable _layer;
        readonly AnimationClip[] _locoClips = new AnimationClip[LocoSlots];
        readonly AnimationClip[] _actionClips = new AnimationClip[ActionSlots];
        readonly AnimationClip[] _upperClips = new AnimationClip[UpperSlots];
        readonly AvatarMask _upperBodyMask;

        readonly float _walkClipSpeed;
        readonly float _runClipSpeed;

        float _phase;
        float _idlePhase;

        float _actionWeight;
        float _actionTime;
        float _actionLength;
        int _actionSlot = -1;
        bool _actionHolds;

        float _upperWeight;
        float _upperTime;
        float _upperLength;
        int _upperSlot = -1;

        bool _moving;
        bool _movingBack;

        public bool IsValid { get; }
        public bool IsDead { get; private set; }

        /// <summary>True while a start/stop/jump clip owns the body, so callers can hold off on gait logic.</summary>
        public bool InAction => _actionSlot >= 0;

        /// <summary>Normalised stride position, 0..1. Footstep audio keys off this so a step is heard
        /// when a boot actually lands rather than on a timer of its own.</summary>
        public float Phase => _phase;

        public SoldierClipPlayer(Animator animator, float walkClipSpeed, float runClipSpeed)
        {
            _walkClipSpeed = Mathf.Max(0.1f, walkClipSpeed);
            _runClipSpeed = Mathf.Max(0.1f, runClipSpeed);

            _graph = PlayableGraph.Create($"Soldier:{animator.name}:{animator.GetEntityId()}");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

            _locomotion = AnimationMixerPlayable.Create(_graph, LocoSlots);
            ConnectLoco(Idle, SoldierClipLibrary.Get(SoldierClip.Idle));
            ConnectLoco(WalkF, SoldierClipLibrary.Get(SoldierClip.WalkForward));
            ConnectLoco(WalkB, SoldierClipLibrary.Get(SoldierClip.WalkBack));
            ConnectLoco(StrafeL, SoldierClipLibrary.Get(SoldierClip.StrafeLeft));
            ConnectLoco(StrafeR, SoldierClipLibrary.Get(SoldierClip.StrafeRight));
            ConnectLoco(RunF, SoldierClipLibrary.Get(SoldierClip.RunForward));
            // A missing backward run is common; the backward walk covers the same direction.
            ConnectLoco(RunB, SoldierClipLibrary.GetAny(SoldierClip.RunBack, SoldierClip.WalkBack));

            _action = AnimationMixerPlayable.Create(_graph, ActionSlots);
            ConnectAction(StartF, SoldierClipLibrary.Get(SoldierClip.StartWalkForward));
            ConnectAction(StartB, SoldierClipLibrary.Get(SoldierClip.StartWalkBack));
            ConnectAction(StopF, SoldierClipLibrary.Get(SoldierClip.StopWalkForward));
            ConnectAction(StopB, SoldierClipLibrary.Get(SoldierClip.StopWalkBack));
            ConnectAction(JumpF, SoldierClipLibrary.Get(SoldierClip.JumpForward));
            ConnectAction(JumpB, SoldierClipLibrary.GetAny(SoldierClip.JumpBack, SoldierClip.JumpForward));
            ConnectAction(DeathStand, SoldierClipLibrary.GetAny(SoldierClip.DeathStanding, SoldierClip.DeathMoving));
            ConnectAction(DeathMove, SoldierClipLibrary.GetAny(SoldierClip.DeathMoving, SoldierClip.DeathStanding));

            _upper = AnimationMixerPlayable.Create(_graph, UpperSlots);
            ConnectUpper(0, SoldierClipLibrary.Get(SoldierClip.Fire));
            ConnectUpper(1, SoldierClipLibrary.Get(SoldierClip.Reload));

            _layer = AnimationLayerMixerPlayable.Create(_graph, 3);
            _graph.Connect(_locomotion, 0, _layer, 0);
            _graph.Connect(_action, 0, _layer, 1);
            _graph.Connect(_upper, 0, _layer, 2);
            _layer.SetInputWeight(0, 1f);
            _layer.SetInputWeight(1, 0f);
            _layer.SetInputWeight(2, 0f);

            _upperBodyMask = UpperBodyMask();
            _layer.SetLayerMaskFromAvatarMask(2, _upperBodyMask);

            var output = AnimationPlayableOutput.Create(_graph, "SoldierPose", animator);
            output.SetSourcePlayable(_layer);
            _graph.Play();

            IsValid = true;
        }

        void ConnectLoco(int slot, AnimationClip clip) => Connect(_locomotion, _locoClips, slot, clip);

        void ConnectAction(int slot, AnimationClip clip) => Connect(_action, _actionClips, slot, clip);

        void ConnectUpper(int slot, AnimationClip clip) => Connect(_upper, _upperClips, slot, clip);

        void Connect(AnimationMixerPlayable mixer, AnimationClip[] store, int slot, AnimationClip clip)
        {
            if (clip == null)
                return;
            store[slot] = clip;
            var playable = AnimationClipPlayable.Create(_graph, clip);
            playable.SetApplyFootIK(false);
            // Time is driven by hand below so every input stays in stride phase.
            playable.SetSpeed(0d);
            _graph.Connect(playable, 0, mixer, slot);
        }

        /// <summary>Fire and reload play from the waist up so the legs keep walking underneath.</summary>
        static AvatarMask UpperBodyMask()
        {
            var mask = new AvatarMask();
            foreach (AvatarMaskBodyPart part in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
            {
                if (part == AvatarMaskBodyPart.LastBodyPart)
                    continue;
                mask.SetHumanoidBodyPartActive(part, false);
            }
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Head, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers, true);
            mask.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFingers, true);
            return mask;
        }

        #region Public API

        public void PlayFire() => StartUpper(0);

        public void PlayReload() => StartUpper(1);

        public void PlayJump(bool backward) => StartAction(backward ? JumpB : JumpF, false);

        /// <summary>
        /// Plays a death clip and holds its final frame. Returns how long it runs so the ragdoll can
        /// take over from the pose the animation left the body in rather than from a walk cycle.
        /// Zero means no death clip was available and the caller should go straight to physics.
        /// </summary>
        public float PlayDeath(bool moving)
        {
            if (IsDead)
                return 0f;

            int slot = moving ? DeathMove : DeathStand;
            if (_actionClips[slot] == null)
                return 0f;

            IsDead = true;
            _upperSlot = -1;
            _upper.SetInputWeight(0, 0f);
            _upper.SetInputWeight(1, 0f);
            StartAction(slot, true);
            return _actionLength;
        }

        /// <summary>Clears the held death pose so a respawned bot animates again.</summary>
        public void Revive()
        {
            if (!IsDead)
                return;
            IsDead = false;
            _actionSlot = -1;
            _actionWeight = 0f;
            _actionHolds = false;
            _moving = false;
            if (_layer.IsValid())
                _layer.SetInputWeight(1, 0f);
        }

        #endregion

        void StartAction(int slot, bool holds)
        {
            if (_actionClips[slot] == null || !_action.GetInput(slot).IsValid())
                return;
            _actionSlot = slot;
            _actionTime = 0f;
            _actionLength = Mathf.Max(0.05f, _actionClips[slot].length);
            _actionHolds = holds;
            for (int i = 0; i < ActionSlots; i++)
                _action.SetInputWeight(i, i == slot ? 1f : 0f);
        }

        void StartUpper(int slot)
        {
            if (IsDead || _upperClips[slot] == null || !_upper.GetInput(slot).IsValid())
                return;
            _upperSlot = slot;
            _upperTime = 0f;
            _upperLength = Mathf.Max(0.1f, _upperClips[slot].length);
            _upper.SetInputWeight(slot, 1f);
            _upper.SetInputWeight(1 - slot, 0f);
        }

        /// <param name="moveLocal">Planar velocity in body space, normalised to [-1,1] per axis.</param>
        /// <param name="speed">Ground speed in m/s, used to keep stride length honest.</param>
        public void Tick(float dt, Vector2 moveLocal, float speed, float normalisedSpeed)
        {
            if (!_graph.IsValid())
                return;

            if (IsDead)
            {
                TickAction(dt);
                return;
            }

            float forward = Mathf.Clamp01(moveLocal.y);
            float back = Mathf.Clamp01(-moveLocal.y);
            float right = Mathf.Clamp01(moveLocal.x);
            float left = Mathf.Clamp01(-moveLocal.x);
            float travel = Mathf.Clamp01(forward + back + right + left);

            UpdateTransitions(travel, moveLocal.y);

            // A run clip only exists above a threshold; below it the walk carries the blend.
            float runBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 0.85f, normalisedSpeed));
            float runF = _locoClips[RunF] != null ? runBlend : 0f;
            float runB = _locoClips[RunB] != null ? runBlend : 0f;

            SetWeight(Idle, 1f - travel);
            SetWeight(WalkF, forward * (1f - runF));
            SetWeight(RunF, forward * runF);
            SetWeight(WalkB, back * (1f - runB));
            SetWeight(RunB, back * runB);
            SetWeight(StrafeL, left);
            SetWeight(StrafeR, right);
            Normalise();

            float authored = Mathf.Lerp(_walkClipSpeed, _runClipSpeed, runBlend);
            var reference = _locoClips[runBlend > 0.5f && _locoClips[RunF] != null ? RunF : WalkF] ?? _locoClips[Idle];
            float refLength = reference != null ? Mathf.Max(0.05f, reference.length) : 1f;

            // Advancing by distance travelled rather than wall time is what stops the feet skating.
            float cycles = speed / authored / refLength;
            _phase = Mathf.Repeat(_phase + Mathf.Clamp(cycles, 0f, 4f) * dt, 1f);
            _idlePhase = Mathf.Repeat(
                _idlePhase + dt / (_locoClips[Idle] != null ? Mathf.Max(0.05f, _locoClips[Idle].length) : 1f), 1f);

            for (int i = 0; i < LocoSlots; i++)
            {
                var input = _locomotion.GetInput(i);
                if (!input.IsValid() || _locoClips[i] == null)
                    continue;
                float t = i == Idle ? _idlePhase : _phase;
                input.SetTime(t * _locoClips[i].length);
            }

            TickAction(dt);
            TickUpper(dt);
        }

        /// <summary>
        /// Fires the start and stop clips off the movement edge.
        ///
        /// Without them the bot snaps between a static idle and a full-speed stride, which is the
        /// single most robotic thing a mocap character can do — real weight takes a step to gather
        /// and a step to shed.
        /// </summary>
        void UpdateTransitions(float travel, float forwardAxis)
        {
            bool wantsMove = _moving ? travel > MoveExit : travel > MoveEnter;
            if (wantsMove == _moving)
                return;

            bool backward = forwardAxis < -0.15f;
            _moving = wantsMove;

            if (wantsMove)
            {
                _movingBack = backward;
                StartAction(backward ? StartB : StartF, false);
            }
            else
            {
                StartAction(_movingBack ? StopB : StopF, false);
            }
        }

        void TickAction(float dt)
        {
            if (_actionSlot < 0)
            {
                _actionWeight = Mathf.MoveTowards(_actionWeight, 0f, dt * 8f);
                _layer.SetInputWeight(1, _actionWeight);
                return;
            }

            _actionTime += dt;
            var input = _action.GetInput(_actionSlot);
            if (input.IsValid())
                input.SetTime(Mathf.Min(_actionTime, _actionLength));

            float target;
            if (_actionHolds)
            {
                target = 1f;
            }
            else
            {
                // Ease in fast, ease out over the tail so the body hands back to the blend mid-stride
                // rather than popping the instant the clip runs out.
                float remaining = _actionLength - _actionTime;
                target = remaining <= 0f ? 0f : Mathf.Min(_actionTime / 0.09f, Mathf.Clamp01(remaining / 0.16f));
            }

            _actionWeight = Mathf.MoveTowards(_actionWeight, Mathf.Clamp01(target), dt * 10f);
            _layer.SetInputWeight(1, _actionWeight);

            if (_actionHolds || _actionTime < _actionLength || _actionWeight > 0.001f)
                return;

            // The start clip ends on the cycle's first frame, so handing over at phase zero is what
            // keeps the foot that was already swinging from restarting mid-air.
            if (_actionSlot is StartF or StartB)
                _phase = 0f;
            _actionSlot = -1;
        }

        void TickUpper(float dt)
        {
            if (_upperSlot < 0)
            {
                _upperWeight = Mathf.MoveTowards(_upperWeight, 0f, dt * 6f);
                _layer.SetInputWeight(2, _upperWeight);
                return;
            }

            _upperTime += dt;
            var input = _upper.GetInput(_upperSlot);
            if (input.IsValid())
                input.SetTime(Mathf.Min(_upperTime, _upperLength));

            // Ease in fast, ease out over the tail so the arms settle back onto the weapon line.
            float remaining = _upperLength - _upperTime;
            float target = remaining <= 0f ? 0f : Mathf.Min(_upperTime / 0.08f, Mathf.Clamp01(remaining / 0.18f));
            _upperWeight = Mathf.MoveTowards(_upperWeight, Mathf.Clamp01(target), dt * 12f);
            _layer.SetInputWeight(2, _upperWeight);

            if (remaining <= 0f && _upperWeight <= 0.001f)
                _upperSlot = -1;
        }

        void SetWeight(int slot, float weight)
        {
            if (_locoClips[slot] == null || !_locomotion.GetInput(slot).IsValid())
                return;
            _locomotion.SetInputWeight(slot, Mathf.Max(0f, weight));
        }

        void Normalise()
        {
            float total = 0f;
            for (int i = 0; i < LocoSlots; i++)
                total += _locomotion.GetInputWeight(i);
            if (total <= 1e-4f)
            {
                SetWeight(Idle, 1f);
                return;
            }
            for (int i = 0; i < LocoSlots; i++)
                _locomotion.SetInputWeight(i, _locomotion.GetInputWeight(i) / total);
        }

        public void Destroy()
        {
            if (_graph.IsValid())
                _graph.Destroy();
            // The mask is created per bot rather than shared, so it has to go with the graph.
            if (_upperBodyMask != null)
                Object.Destroy(_upperBodyMask);
        }
    }
}
