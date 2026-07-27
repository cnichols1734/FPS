using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ArenaFps.Combat
{
    public enum SoldierClip
    {
        Idle,

        WalkForward,
        WalkBack,
        StrafeLeft,
        StrafeRight,
        RunForward,
        RunBack,

        StartWalkForward,
        StartWalkBack,
        StopWalkForward,
        StopWalkBack,

        JumpForward,
        JumpBack,

        Fire,
        Reload,

        DeathStanding,
        DeathMoving,

        Count,
    }

    /// <summary>
    /// Resolves downloaded mocap into the roles the animator asks for.
    ///
    /// Clips are matched by name rather than by a hand-authored asset list so that dropping a new
    /// Mixamo export into the folder is the entire integration step. Anything unmatched is ignored
    /// and any missing role degrades gracefully, so a partial clip set still runs.
    /// </summary>
    public static class SoldierClipLibrary
    {
        public const string ResourceFolder = "Animations/Soldier";

        static AnimationClip[] _clips;
        static bool _loaded;

        public static bool Loaded => _loaded;

        /// <summary>True when there is at least enough mocap to drive locomotion.</summary>
        public static bool HasLocomotion
        {
            get
            {
                Load();
                return _clips[(int)SoldierClip.Idle] != null && _clips[(int)SoldierClip.WalkForward] != null;
            }
        }

        public static AnimationClip Get(SoldierClip role)
        {
            Load();
            return _clips[(int)role];
        }

        /// <summary>First non-null role in preference order, so a missing clip falls back instead of popping.</summary>
        public static AnimationClip GetAny(params SoldierClip[] roles)
        {
            Load();
            foreach (var role in roles)
            {
                var clip = _clips[(int)role];
                if (clip != null)
                    return clip;
            }
            return null;
        }

        public static void Reload()
        {
            _loaded = false;
            _clips = null;
            Load();
        }

        static void Load()
        {
            if (_loaded)
                return;
            _loaded = true;
            _clips = new AnimationClip[(int)SoldierClip.Count];

            var found = Resources.LoadAll<AnimationClip>(ResourceFolder);
            if (found == null || found.Length == 0)
                return;

            foreach (var clip in found)
            {
                // Mixamo exports carry a "mixamo.com" take alongside the real clip on some rigs.
                if (clip == null || clip.name.Contains("mixamo.com"))
                    continue;

                var role = Classify(clip.name);
                if (role == SoldierClip.Count)
                    continue;
                // First match wins so a folder with duplicates stays deterministic.
                if (_clips[(int)role] == null)
                    _clips[(int)role] = clip;
            }

            var summary = new StringBuilder("[Soldier] Mocap loaded:");
            int count = 0;
            for (int i = 0; i < _clips.Length; i++)
            {
                if (_clips[i] == null)
                    continue;
                summary.Append(' ').Append((SoldierClip)i);
                count++;
            }
            summary.Append("  (").Append(count).Append('/').Append((int)SoldierClip.Count).Append(')');
            Debug.Log(summary.ToString());
        }

        /// <summary>
        /// Maps a filename onto a role by whole-word tokens, tested most-specific first.
        ///
        /// Order is the whole design. "start walking backwards" contains every token that the plain
        /// backward walk does, so the transition rules have to win before the loops are considered;
        /// likewise "walking to dying" is a death, not a walk. Matching whole tokens rather than
        /// substrings keeps a word like "bright" from registering as a right strafe.
        /// </summary>
        public static SoldierClip Classify(string rawName)
        {
            var t = Tokenise(rawName);
            if (t.Count == 0)
                return SoldierClip.Count;

            bool back = Has(t, "back", "backward", "backwards", "backpedal", "reverse");
            bool walk = Has(t, "walk", "walking", "walks");
            bool run = Has(t, "run", "running", "sprint", "sprinting", "jog", "jogging");

            // Death first: a death clip is usually named after the action it interrupts.
            if (Has(t, "death", "dying", "die", "dies", "killed", "collapse"))
                return walk || run || Has(t, "moving", "forward") ? SoldierClip.DeathMoving : SoldierClip.DeathStanding;

            if (Has(t, "reload", "reloading"))
                return SoldierClip.Reload;

            if (Has(t, "fire", "firing", "shoot", "shooting", "shot"))
                return SoldierClip.Fire;

            if (Has(t, "jump", "jumping", "hop", "vault"))
                return back ? SoldierClip.JumpBack : SoldierClip.JumpForward;

            // Transitions before loops — they share every locomotion token with the cycle they enter.
            if (walk || run)
            {
                if (Has(t, "start", "starting", "begin"))
                    return back ? SoldierClip.StartWalkBack : SoldierClip.StartWalkForward;
                if (Has(t, "stop", "stopping", "end", "halt"))
                    return back ? SoldierClip.StopWalkBack : SoldierClip.StopWalkForward;
            }

            if (Has(t, "idle", "aiming", "stance"))
                return SoldierClip.Idle;

            // A sideways clip is identified by its direction word, whichever gait carries it.
            if (Has(t, "strafe", "strafing", "sidestep") || walk || run)
            {
                if (Has(t, "left"))
                    return SoldierClip.StrafeLeft;
                if (Has(t, "right"))
                    return SoldierClip.StrafeRight;
            }

            if (run)
                return back ? SoldierClip.RunBack : SoldierClip.RunForward;
            if (walk)
                return back ? SoldierClip.WalkBack : SoldierClip.WalkForward;

            return SoldierClip.Count;
        }

        static bool Has(List<string> tokens, params string[] any)
        {
            foreach (var token in tokens)
            {
                foreach (var candidate in any)
                {
                    if (token == candidate)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Lower-cased alphanumeric words. Mixamo mixes spaces, underscores and camel case.</summary>
        static List<string> Tokenise(string value)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(value))
                return tokens;

            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool boundary = !char.IsLetterOrDigit(c);
                // A capital following a lower-case letter starts a new word in "StrafeLeft".
                if (!boundary && i > 0 && char.IsUpper(c) && char.IsLower(value[i - 1]) && sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }

                if (boundary)
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }
                    continue;
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            if (sb.Length > 0)
                tokens.Add(sb.ToString());
            return tokens;
        }
    }
}
