using System.Collections.Generic;
using ArenaFps.Ballistics;
using ArenaFps.Core;
using UnityEngine;

namespace ArenaFps.Audio
{
    public enum Sfx
    {
        RifleShot,
        PistolShot,
        RifleShotDistant,
        DryFire,
        /// <summary>Full weapon reload one-shot. Reload duration is driven by this clip's length.</summary>
        Reload,
        MagOut,
        MagIn,
        BoltRelease,
        ImpactConcrete,
        ImpactMetal,
        ImpactWood,
        ImpactDrywall,
        ImpactFlesh,
        ImpactHeadshot,
        Ricochet,
        Hitmarker,
        HeadshotMarker,
        KillConfirm,
        BulletWhizz,
        CasingTink,
        BodyFall,
        Death,
        PlayerHurt,
        Heartbeat,
        CoverBreak,
        Footstep,
        FootstepSprint,
    }

    /// <summary>
    /// Baked bank of procedural clips, with optional authored overrides loaded from
    /// <c>Resources/Sfx/</c>. Authored clips win when present so real recordings can replace the
    /// synth without changing any call site.
    /// </summary>
    public static class SfxBank
    {
        const int Variants = 4;

        /// <summary>Resources paths (no extension) for sounds that ship as real recordings.</summary>
        static readonly Dictionary<Sfx, string> AuthoredPaths = new()
        {
            { Sfx.RifleShot, "Sfx/Gunshot" },
            { Sfx.PistolShot, "Sfx/Gunshot" },
            { Sfx.Reload, "Sfx/Reload" },
        };

        static readonly Dictionary<Sfx, AudioClip[]> Cache = new();
        static readonly Dictionary<Sfx, AudioClip> Authored = new();

        public static AudioClip Get(Sfx sfx)
        {
            if (TryGetAuthored(sfx, out var authored))
                return authored;

            if (!Cache.TryGetValue(sfx, out var bank))
            {
                bank = new AudioClip[Variants];
                for (int i = 0; i < Variants; i++)
                    bank[i] = Build(sfx, 8191 + i * 7919 + (int)sfx * 104729);
                Cache[sfx] = bank;
            }
            return bank[Random.Range(0, bank.Length)];
        }

        /// <summary>Exact clip length in seconds — used so reload gameplay matches the recording.</summary>
        public static float Duration(Sfx sfx)
        {
            var clip = Get(sfx);
            return clip != null ? clip.length : 0f;
        }

        static bool TryGetAuthored(Sfx sfx, out AudioClip clip)
        {
            if (Authored.TryGetValue(sfx, out clip))
                return clip != null;

            clip = null;
            if (!AuthoredPaths.TryGetValue(sfx, out var path))
                return false;

            clip = Resources.Load<AudioClip>(path);
            Authored[sfx] = clip;
            if (clip == null)
                Debug.LogWarning($"[SfxBank] Missing authored clip Resources/{path}");
            return clip != null;
        }

        public static Sfx ForSurface(SurfaceKind kind) => kind switch
        {
            SurfaceKind.MetalThin or SurfaceKind.MetalThick => Sfx.ImpactMetal,
            SurfaceKind.Wood => Sfx.ImpactWood,
            SurfaceKind.Drywall => Sfx.ImpactDrywall,
            SurfaceKind.Flesh => Sfx.ImpactFlesh,
            _ => Sfx.ImpactConcrete,
        };

        /// <summary>Bakes the whole bank up front so the first shot never hitches.</summary>
        public static void Prewarm()
        {
            foreach (Sfx sfx in System.Enum.GetValues(typeof(Sfx)))
                Get(sfx);
        }

        static AudioClip Build(Sfx sfx, int seed)
        {
            var rng = new System.Random(seed);
            return sfx switch
            {
                Sfx.RifleShot => Gunshot(rng, "RifleShot", 78f, 2300f, 0.5f, 1f),
                Sfx.PistolShot => Gunshot(rng, "PistolShot", 104f, 2900f, 0.4f, 0.86f),
                Sfx.RifleShotDistant => DistantGunshot(rng),
                Sfx.DryFire => DryFire(rng),
                // Fallback only — the authored Reload clip is preferred when present in Resources.
                Sfx.Reload => MagClick(rng, "Reload", 420f, 1.5f),
                Sfx.MagOut => MagClick(rng, "MagOut", 520f, 0.14f),
                Sfx.MagIn => MagClick(rng, "MagIn", 340f, 0.18f),
                Sfx.BoltRelease => MagClick(rng, "BoltRelease", 780f, 0.12f),
                Sfx.ImpactConcrete => ImpactConcrete(rng),
                Sfx.ImpactMetal => ImpactMetal(rng),
                Sfx.ImpactWood => ImpactWood(rng),
                Sfx.ImpactDrywall => ImpactDrywall(rng),
                Sfx.ImpactFlesh => ImpactFlesh(rng),
                Sfx.ImpactHeadshot => ImpactHeadshot(rng),
                Sfx.Ricochet => Ricochet(rng),
                Sfx.Hitmarker => Hitmarker(rng, 1500f, 2250f, 0.045f),
                Sfx.HeadshotMarker => Hitmarker(rng, 2100f, 3150f, 0.07f),
                Sfx.KillConfirm => KillConfirm(rng),
                Sfx.BulletWhizz => Whizz(rng),
                Sfx.CasingTink => Casing(rng),
                Sfx.BodyFall => BodyFall(rng),
                Sfx.Death => Death(rng),
                Sfx.PlayerHurt => PlayerHurt(rng),
                Sfx.Heartbeat => Heartbeat(rng),
                Sfx.CoverBreak => CoverBreak(rng),
                Sfx.Footstep => Footstep(rng, "Footstep", 0.12f, 96f, 0.5f),
                Sfx.FootstepSprint => Footstep(rng, "FootstepSprint", 0.15f, 78f, 0.85f),
                _ => Synth.ToClip("Empty", Synth.Buffer(0.05f)),
            };
        }

        static AudioClip Gunshot(System.Random rng, string name, float bodyHz, float crackHz, float tail, float scale)
        {
            var d = Synth.Buffer(0.42f);

            // Muzzle transient: the part the ear reads as "loud".
            Synth.AddNoise(d, rng, 1.15f * scale, 0.0004f, 260f);

            // Body: a downward pitch sweep, not a static tone — that is what gives weight.
            Synth.AddSine(d, bodyHz * 2.4f, 0.85f * scale, 0.001f, 34f, 0f, bodyHz * 0.8f);
            Synth.AddSine(d, bodyHz, 0.55f * scale, 0.001f, 20f);

            // Supersonic crack.
            var crack = Synth.Buffer(0.42f);
            Synth.AddNoise(crack, rng, 0.7f, 0.0002f, 70f);
            Synth.HighPass(crack, crackHz * 0.45f);
            for (int i = 0; i < d.Length; i++) d[i] += crack[i] * scale;

            // Room: discrete early reflections sell the street far better than a long tail.
            Synth.AddReflections(d,
                (0.011f, 0.34f * tail),
                (0.023f, 0.24f * tail),
                (0.041f, 0.17f * tail),
                (0.068f, 0.11f * tail));

            var air = Synth.Buffer(0.42f);
            Synth.AddNoise(air, rng, 0.3f * tail, 0.008f, 11f);
            Synth.LowPass(air, 1400f);
            for (int i = 0; i < d.Length; i++) d[i] += air[i];

            // Mechanical action, offset so it reads as a separate event.
            Synth.AddSine(d, 3100f, 0.09f, 0.0003f, 130f, 0.019f);
            Synth.AddNoise(d, rng, 0.07f, 0.0003f, 110f, 0.019f);

            Synth.HighPass(d, 42f);
            Synth.Finish(d, 1.35f);
            return Synth.ToClip(name, d);
        }

        static AudioClip DistantGunshot(System.Random rng)
        {
            var d = Synth.Buffer(0.7f);
            Synth.AddNoise(d, rng, 0.8f, 0.0015f, 90f);
            Synth.AddSine(d, 150f, 0.6f, 0.002f, 26f, 0f, 62f);
            Synth.AddReflections(d, (0.03f, 0.4f), (0.07f, 0.3f), (0.13f, 0.22f), (0.21f, 0.14f));

            var tail = Synth.Buffer(0.7f);
            Synth.AddNoise(tail, rng, 0.45f, 0.012f, 6.5f);
            Synth.LowPass(tail, 700f);
            for (int i = 0; i < d.Length; i++) d[i] += tail[i];

            // Air absorption: distance eats the high end first.
            Synth.LowPass(d, 2200f);
            Synth.HighPass(d, 55f);
            Synth.Finish(d, 1.1f, 0.85f);
            return Synth.ToClip("RifleShotDistant", d);
        }

        static AudioClip DryFire(System.Random rng)
        {
            var d = Synth.Buffer(0.09f);
            Synth.AddNoise(d, rng, 0.5f, 0.0002f, 220f);
            Synth.AddSine(d, 1900f, 0.3f, 0.0002f, 190f);
            Synth.HighPass(d, 900f);
            Synth.Finish(d, 1f, 0.5f);
            return Synth.ToClip("DryFire", d);
        }

        static AudioClip MagClick(System.Random rng, string name, float hz, float length)
        {
            var d = Synth.Buffer(length);
            Synth.AddNoise(d, rng, 0.45f, 0.0004f, 90f);
            Synth.AddSine(d, hz, 0.4f, 0.0006f, 55f, 0f, hz * 0.7f);
            Synth.AddSine(d, hz * 3.1f, 0.16f, 0.0004f, 120f);
            Synth.HighPass(d, 190f);
            Synth.Finish(d, 1.1f, 0.62f);
            return Synth.ToClip(name, d);
        }

        static AudioClip ImpactConcrete(System.Random rng)
        {
            var d = Synth.Buffer(0.2f);
            Synth.AddNoise(d, rng, 0.95f, 0.0003f, 130f);
            Synth.AddSine(d, 220f, 0.4f, 0.001f, 60f, 0f, 110f);
            var grit = Synth.Buffer(0.2f);
            Synth.AddNoise(grit, rng, 0.4f, 0.004f, 26f);
            Synth.LowPass(grit, 2600f);
            for (int i = 0; i < d.Length; i++) d[i] += grit[i];
            Synth.HighPass(d, 130f);
            Synth.Finish(d, 1.25f, 0.8f);
            return Synth.ToClip("ImpactConcrete", d);
        }

        static AudioClip ImpactMetal(System.Random rng)
        {
            var d = Synth.Buffer(0.4f);
            Synth.AddNoise(d, rng, 0.75f, 0.0002f, 200f);
            // Inharmonic partials read as struck metal; harmonic ones read as a bell.
            Synth.AddSine(d, 1870f, 0.34f, 0.0004f, 16f);
            Synth.AddSine(d, 3140f, 0.26f, 0.0004f, 21f);
            Synth.AddSine(d, 5230f, 0.18f, 0.0004f, 30f);
            Synth.AddSine(d, 7410f, 0.1f, 0.0004f, 44f);
            Synth.HighPass(d, 400f);
            Synth.Finish(d, 1.2f, 0.82f);
            return Synth.ToClip("ImpactMetal", d);
        }

        static AudioClip ImpactWood(System.Random rng)
        {
            var d = Synth.Buffer(0.22f);
            Synth.AddNoise(d, rng, 0.8f, 0.0003f, 150f);
            Synth.AddSine(d, 260f, 0.45f, 0.0008f, 42f);
            Synth.AddSine(d, 640f, 0.22f, 0.0008f, 60f);
            Synth.LowPass(d, 3400f);
            Synth.HighPass(d, 140f);
            Synth.Finish(d, 1.2f, 0.78f);
            return Synth.ToClip("ImpactWood", d);
        }

        static AudioClip ImpactDrywall(System.Random rng)
        {
            var d = Synth.Buffer(0.25f);
            Synth.AddNoise(d, rng, 0.7f, 0.0008f, 60f);
            Synth.AddSine(d, 180f, 0.3f, 0.002f, 34f);
            Synth.LowPass(d, 1900f);
            Synth.Finish(d, 1.1f, 0.62f);
            return Synth.ToClip("ImpactDrywall", d);
        }

        static AudioClip ImpactFlesh(System.Random rng)
        {
            var d = Synth.Buffer(0.26f);
            // Wet slap over a body thump. This is the sound that tells the player the shot landed.
            var wet = Synth.Buffer(0.26f);
            Synth.AddNoise(wet, rng, 1f, 0.0006f, 95f);
            Synth.LowPass(wet, 1500f);
            for (int i = 0; i < d.Length; i++) d[i] += wet[i];

            Synth.AddSine(d, 132f, 0.75f, 0.0012f, 42f, 0f, 66f);
            Synth.AddSine(d, 74f, 0.5f, 0.002f, 28f);
            Synth.AddNoise(d, rng, 0.2f, 0.0002f, 300f);
            Synth.HighPass(d, 55f);
            Synth.Finish(d, 1.4f, 0.9f);
            return Synth.ToClip("ImpactFlesh", d);
        }

        static AudioClip ImpactHeadshot(System.Random rng)
        {
            var d = Synth.Buffer(0.3f);
            Synth.AddNoise(d, rng, 1.1f, 0.0002f, 170f);
            Synth.AddSine(d, 2650f, 0.4f, 0.0003f, 42f);
            Synth.AddSine(d, 148f, 0.6f, 0.001f, 40f, 0f, 70f);
            var wet = Synth.Buffer(0.3f);
            Synth.AddNoise(wet, rng, 0.55f, 0.002f, 30f);
            Synth.LowPass(wet, 2200f);
            for (int i = 0; i < d.Length; i++) d[i] += wet[i];
            Synth.Finish(d, 1.5f, 0.95f);
            return Synth.ToClip("ImpactHeadshot", d);
        }

        static AudioClip Ricochet(System.Random rng)
        {
            var d = Synth.Buffer(0.35f);
            Synth.AddNoise(d, rng, 0.6f, 0.0002f, 240f);
            // Falling whine is the whole character of a ricochet.
            Synth.AddSine(d, 4200f, 0.42f, 0.001f, 11f, 0f, 900f);
            Synth.AddSine(d, 2700f, 0.22f, 0.001f, 13f, 0f, 620f);
            Synth.HighPass(d, 500f);
            Synth.Finish(d, 1.1f, 0.75f);
            return Synth.ToClip("Ricochet", d);
        }

        static AudioClip Footstep(System.Random rng, string name, float length, float thumpHz, float weight)
        {
            var d = Synth.Buffer(length);

            // Boot heel: a short low thump carrying the body weight.
            Synth.AddSine(d, thumpHz, 0.75f * weight, 0.0015f, 70f, 0f, thumpHz * 0.62f);
            Synth.AddSine(d, thumpHz * 2.3f, 0.22f * weight, 0.001f, 110f);

            // Grit scuff on top, band-limited so it reads as sole on concrete rather than static.
            var scuff = Synth.Buffer(length);
            Synth.AddNoise(scuff, rng, 0.5f * weight, 0.0025f, 55f);
            Synth.HighPass(scuff, 900f);
            Synth.LowPass(scuff, 5200f);
            for (int i = 0; i < d.Length; i++) d[i] += scuff[i];

            Synth.HighPass(d, 55f);
            Synth.Finish(d, 1.15f, 0.6f + weight * 0.2f);
            return Synth.ToClip(name, d);
        }

        static AudioClip CoverBreak(System.Random rng)
        {
            var d = Synth.Buffer(1.1f);

            // Structural crack first, then the load giving way underneath it.
            Synth.AddNoise(d, rng, 1f, 0.0004f, 90f);
            Synth.AddSine(d, 96f, 0.7f, 0.002f, 15f, 0f, 44f);
            Synth.AddSine(d, 210f, 0.35f, 0.001f, 24f);

            // Scattered debris settling: staggered ticks decaying over the tail.
            for (int i = 0; i < 14; i++)
            {
                float at = 0.06f + (float)rng.NextDouble() * 0.7f;
                Synth.AddNoise(d, rng, 0.13f * (1f - at * 0.8f), 0.0003f, 190f, at);
                Synth.AddSine(d, 380f + (float)rng.NextDouble() * 1400f, 0.07f, 0.0004f, 90f, at);
            }

            Synth.AddReflections(d, (0.028f, 0.3f), (0.061f, 0.2f), (0.11f, 0.12f));
            Synth.HighPass(d, 60f);
            Synth.Finish(d, 1.3f, 0.9f);
            return Synth.ToClip("CoverBreak", d);
        }

        static AudioClip Hitmarker(System.Random rng, float lowHz, float highHz, float length)
        {
            var d = Synth.Buffer(length);
            Synth.AddSine(d, lowHz, 0.6f, 0.0002f, 120f);
            Synth.AddSine(d, highHz, 0.45f, 0.0002f, 150f);
            Synth.AddNoise(d, rng, 0.12f, 0.0002f, 400f);
            Synth.HighPass(d, 700f);
            Synth.Finish(d, 1f, 0.7f);
            return Synth.ToClip("Hitmarker", d);
        }

        static AudioClip KillConfirm(System.Random rng)
        {
            var d = Synth.Buffer(0.26f);
            Synth.AddSine(d, 1560f, 0.5f, 0.0004f, 26f);
            Synth.AddSine(d, 2340f, 0.3f, 0.0004f, 30f);
            Synth.AddSine(d, 1040f, 0.45f, 0.0006f, 20f, 0.075f);
            Synth.AddSine(d, 1560f, 0.25f, 0.0006f, 24f, 0.075f);
            Synth.HighPass(d, 500f);
            Synth.Finish(d, 1.05f, 0.78f);
            return Synth.ToClip("KillConfirm", d);
        }

        static AudioClip Whizz(System.Random rng)
        {
            var d = Synth.Buffer(0.18f);
            int n = d.Length;
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n;
                // Bell envelope plus a falling centre frequency: a round passing the ear.
                float env = Mathf.Sin(u * Mathf.PI);
                env *= env;
                float f = Mathf.Lerp(2600f, 700f, u);
                phase += 2f * Mathf.PI * f / Synth.SampleRate;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                d[i] = (Mathf.Sin(phase) * 0.5f + noise * 0.5f) * env;
            }
            Synth.LowPass(d, 4200f);
            Synth.HighPass(d, 420f);
            Synth.Finish(d, 1f, 0.6f);
            return Synth.ToClip("BulletWhizz", d);
        }

        static AudioClip Casing(System.Random rng)
        {
            var d = Synth.Buffer(0.16f);
            Synth.AddSine(d, 4300f, 0.4f, 0.0002f, 46f);
            Synth.AddSine(d, 6100f, 0.28f, 0.0002f, 58f);
            Synth.AddSine(d, 8700f, 0.16f, 0.0002f, 80f);
            Synth.AddNoise(d, rng, 0.18f, 0.0002f, 260f);
            Synth.HighPass(d, 2200f);
            Synth.Finish(d, 1f, 0.45f);
            return Synth.ToClip("CasingTink", d);
        }

        static AudioClip BodyFall(System.Random rng)
        {
            var d = Synth.Buffer(0.35f);
            Synth.AddSine(d, 96f, 0.9f, 0.002f, 26f, 0f, 48f);
            var thud = Synth.Buffer(0.35f);
            Synth.AddNoise(thud, rng, 0.6f, 0.002f, 34f);
            Synth.LowPass(thud, 900f);
            for (int i = 0; i < d.Length; i++) d[i] += thud[i];
            Synth.Finish(d, 1.2f, 0.8f);
            return Synth.ToClip("BodyFall", d);
        }

        static AudioClip Death(System.Random rng)
        {
            var d = Synth.Buffer(0.75f);
            // Two formants over a noise bed reads as a voice without needing a voice.
            Synth.AddSine(d, 320f, 0.5f, 0.02f, 4.2f, 0f, 170f);
            Synth.AddSine(d, 780f, 0.22f, 0.03f, 5f, 0f, 430f);
            Synth.AddSine(d, 1450f, 0.1f, 0.03f, 6f, 0f, 800f);
            var breath = Synth.Buffer(0.75f);
            Synth.AddNoise(breath, rng, 0.3f, 0.05f, 4f);
            Synth.LowPass(breath, 2400f);
            for (int i = 0; i < d.Length; i++) d[i] += breath[i];
            Synth.HighPass(d, 120f);
            Synth.Finish(d, 1.1f, 0.72f);
            return Synth.ToClip("Death", d);
        }

        static AudioClip PlayerHurt(System.Random rng)
        {
            var d = Synth.Buffer(0.3f);
            Synth.AddSine(d, 260f, 0.5f, 0.008f, 11f, 0f, 160f);
            Synth.AddSine(d, 690f, 0.2f, 0.01f, 13f, 0f, 420f);
            var breath = Synth.Buffer(0.3f);
            Synth.AddNoise(breath, rng, 0.34f, 0.006f, 12f);
            Synth.LowPass(breath, 2000f);
            for (int i = 0; i < d.Length; i++) d[i] += breath[i];
            Synth.HighPass(d, 130f);
            Synth.Finish(d, 1.1f, 0.68f);
            return Synth.ToClip("PlayerHurt", d);
        }

        static AudioClip Heartbeat(System.Random rng)
        {
            var d = Synth.Buffer(0.6f);
            Synth.AddSine(d, 64f, 0.9f, 0.004f, 15f, 0f, 40f);
            Synth.AddSine(d, 58f, 0.6f, 0.004f, 18f, 0.16f, 36f);
            Synth.LowPass(d, 220f);
            Synth.Finish(d, 1.1f, 0.7f);
            return Synth.ToClip("Heartbeat", d);
        }
    }
}
