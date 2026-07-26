using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ArenaFps.Weapons
{
    /// <summary>
    /// Drives the SCAR-H viewmodel clips without an AnimatorController asset.
    /// Clips are sliced on import from the Sketchfab showcase take and loaded as sub-assets.
    /// </summary>
    public sealed class ViewmodelAnimator : MonoBehaviour
    {
        public enum ClipId { Idle, Fire, Reload, ReloadEmpty, Draw, Holster }

        [SerializeField] float fireFade = 0.03f;
        [SerializeField] float reloadFade = 0.08f;
        [SerializeField] float idleFade = 0.12f;

        Animator _animator;
        PlayableGraph _graph;
        AnimationMixerPlayable _mixer;
        readonly Dictionary<ClipId, AnimationClip> _clips = new();
        readonly Dictionary<ClipId, int> _slotOf = new();
        AnimationClipPlayable[] _slots;
        int _activeSlot = -1;
        float _oneshotUntil;
        bool _ready;
        Coroutine _fadeRoutine;

        public float ReloadDuration => DurationOf(ClipId.Reload);
        public float ReloadEmptyDuration
        {
            get
            {
                float empty = DurationOf(ClipId.ReloadEmpty);
                return empty > 0.05f ? empty : ReloadDuration;
            }
        }

        float DurationOf(ClipId id) =>
            _clips.TryGetValue(id, out var clip) && clip != null ? clip.length : 0f;

        /// <summary>
        /// Stamps the idle pose onto the hierarchy immediately, before the playable graph gets its
        /// first evaluation. Without it the viewmodel's first rendered frame is the imported rest
        /// pose, which has the arms spread wide.
        /// </summary>
        public void SampleIdleImmediate(GameObject root)
        {
            if (root == null)
                return;

            AnimationClip clip = null;
            if (_clips.TryGetValue(ClipId.Idle, out var idle) && idle != null)
                clip = idle;
            else if (_clips.TryGetValue(ClipId.Draw, out var draw) && draw != null)
                clip = draw;

            if (clip == null)
                return;

            clip.SampleAnimation(root, 0f);
        }

        public void Bind(Animator animator, string resourcePath)
        {
            _animator = animator;
            LoadClips(resourcePath);
            BuildGraph();
            if (_clips.ContainsKey(ClipId.Draw))
                PlayOneShot(ClipId.Draw, 0f);
            else
                Play(ClipId.Idle, 0f, loop: true);
        }

        void LoadClips(string resourcePath)
        {
            _clips.Clear();
            var all = Resources.LoadAll<AnimationClip>(resourcePath);

            foreach (var clip in all)
            {
                if (clip == null || clip.name.StartsWith("__preview__", System.StringComparison.Ordinal))
                    continue;

                var id = Resolve(clip.name);
                if (id == null)
                    continue;

                // Prefer explicitly sliced ScarH_* clips over raw showcase takes.
                if (!_clips.ContainsKey(id.Value)
                    || clip.name.IndexOf("ScarH_", System.StringComparison.Ordinal) >= 0)
                    _clips[id.Value] = clip;
            }

            if (!_clips.ContainsKey(ClipId.Idle))
            {
                foreach (var clip in all)
                {
                    if (clip != null && clip.length > 2f)
                    {
                        _clips[ClipId.Idle] = clip;
                        break;
                    }
                }
            }
        }

        static ClipId? Resolve(string name)
        {
            if (Contains(name, "ScarH_Idle")) return ClipId.Idle;
            if (Contains(name, "ScarH_Fire")) return ClipId.Fire;
            if (Contains(name, "ScarH_ReloadEmpty")) return ClipId.ReloadEmpty;
            if (Contains(name, "ScarH_Reload")) return ClipId.Reload;
            if (Contains(name, "ScarH_Draw")) return ClipId.Draw;
            if (Contains(name, "ScarH_Holster")) return ClipId.Holster;
            return null;
        }

        static bool Contains(string name, string token) =>
            name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;

        void BuildGraph()
        {
            TearDownGraph();
            if (_animator == null || _clips.Count == 0)
                return;

            _graph = PlayableGraph.Create("ScarH_Viewmodel");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

            _slotOf.Clear();
            var ordered = new List<ClipId>(_clips.Keys);
            ordered.Sort();
            _slots = new AnimationClipPlayable[ordered.Count];
            _mixer = AnimationMixerPlayable.Create(_graph, ordered.Count);

            for (int i = 0; i < ordered.Count; i++)
            {
                var id = ordered[i];
                _slotOf[id] = i;
                _slots[i] = AnimationClipPlayable.Create(_graph, _clips[id]);
                _slots[i].SetApplyFootIK(false);
                _slots[i].Pause();
                _graph.Connect(_slots[i], 0, _mixer, i);
                _mixer.SetInputWeight(i, 0f);
            }

            var output = AnimationPlayableOutput.Create(_graph, "Viewmodel", _animator);
            output.SetSourcePlayable(_mixer);
            _graph.Play();
            _ready = true;
        }

        void TearDownGraph()
        {
            if (_fadeRoutine != null)
            {
                StopCoroutine(_fadeRoutine);
                _fadeRoutine = null;
            }

            if (_graph.IsValid())
                _graph.Destroy();
            _ready = false;
            _activeSlot = -1;
        }

        void OnDestroy() => TearDownGraph();

        void Update()
        {
            if (!_ready || _oneshotUntil <= 0f)
                return;

            if (Time.time >= _oneshotUntil)
            {
                _oneshotUntil = 0f;
                Play(ClipId.Idle, idleFade, loop: true);
            }
        }

        public void PlayDraw() => PlayOneShot(ClipId.Draw, idleFade);

        /// <summary>
        /// Retriggers the shot animation. When the cyclic rate is faster than the clip, the clip is
        /// sped up to fit so each round gets a whole kick instead of the same opening frames.
        /// </summary>
        public void PlayFire(float shotInterval = 0f)
        {
            float speed = 1f;
            if (shotInterval > 1e-3f && _clips.TryGetValue(ClipId.Fire, out var fire) && fire != null && fire.length > shotInterval)
                speed = Mathf.Clamp(fire.length / shotInterval, 1f, 4f);
            PlayOneShot(ClipId.Fire, fireFade, speed);
        }

        public void PlayReload(bool empty = false) =>
            PlayOneShot(empty && _clips.ContainsKey(ClipId.ReloadEmpty) ? ClipId.ReloadEmpty : ClipId.Reload, reloadFade);
        public void PlayHolster() => PlayOneShot(ClipId.Holster, idleFade);
        public void PlayIdle() => Play(ClipId.Idle, idleFade, loop: true);

        void PlayOneShot(ClipId id, float fade, float speed = 1f)
        {
            if (!_clips.TryGetValue(id, out var clip) || clip == null)
            {
                if (id != ClipId.Idle)
                    Play(ClipId.Idle, fade, loop: true);
                return;
            }

            Play(id, fade, loop: false, speed);
            _oneshotUntil = Time.time + Mathf.Max(0.05f, clip.length / Mathf.Max(0.01f, speed) - fade);
        }

        void Play(ClipId id, float fade, bool loop, float speed = 1f)
        {
            if (!_ready || !_slotOf.TryGetValue(id, out int index))
                return;

            var playable = _slots[index];
            playable.SetTime(0);
            playable.SetSpeed(speed);
            playable.SetDuration(loop ? double.MaxValue : _clips[id].length);
            playable.Play();

            if (_fadeRoutine != null)
                StopCoroutine(_fadeRoutine);

            if (fade <= 1e-4f || _activeSlot < 0)
            {
                for (int i = 0; i < _slots.Length; i++)
                    _mixer.SetInputWeight(i, i == index ? 1f : 0f);
            }
            else
            {
                _fadeRoutine = StartCoroutine(CrossFade(index, fade));
            }

            _activeSlot = index;
        }

        System.Collections.IEnumerator CrossFade(int to, float duration)
        {
            int count = _slots.Length;
            var from = new float[count];
            for (int i = 0; i < count; i++)
                from[i] = _mixer.GetInputWeight(i);

            float t = 0f;
            while (t < duration)
            {
                if (!_graph.IsValid())
                    yield break;
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / duration);
                for (int i = 0; i < count; i++)
                {
                    float target = i == to ? 1f : 0f;
                    _mixer.SetInputWeight(i, Mathf.Lerp(from[i], target, u));
                }

                yield return null;
            }

            if (_graph.IsValid())
            {
                for (int i = 0; i < count; i++)
                    _mixer.SetInputWeight(i, i == to ? 1f : 0f);
            }

            _fadeRoutine = null;
        }
    }
}
