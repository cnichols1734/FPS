using UnityEngine;

namespace ArenaFps.Core
{
    /// <summary>
    /// Sample-level DSP helpers for the procedural audio bank. Everything here is offline —
    /// clips are baked once at first use, never per shot.
    /// </summary>
    public static class Synth
    {
        public const int SampleRate = 44100;

        public static float[] Buffer(float seconds) => new float[Mathf.CeilToInt(SampleRate * seconds)];

        public static float Decay(float t, float rate) => Mathf.Exp(-t * rate);

        /// <summary>Fast attack, exponential release. Avoids the click of a hard start.</summary>
        public static float Env(float t, float attack, float rate)
        {
            float a = attack <= 0f ? 1f : Mathf.Clamp01(t / attack);
            return a * Mathf.Exp(-t * rate);
        }

        public static void AddNoise(float[] data, System.Random rng, float gain, float attack, float rate, float startSeconds = 0f)
        {
            int start = Mathf.Clamp(Mathf.RoundToInt(startSeconds * SampleRate), 0, data.Length);
            for (int i = start; i < data.Length; i++)
            {
                float t = (i - start) / (float)SampleRate;
                data[i] += (float)(rng.NextDouble() * 2.0 - 1.0) * gain * Env(t, attack, rate);
            }
        }

        public static void AddSine(float[] data, float freq, float gain, float attack, float rate, float startSeconds = 0f, float freqEnd = -1f)
        {
            int start = Mathf.Clamp(Mathf.RoundToInt(startSeconds * SampleRate), 0, data.Length);
            float phase = 0f;
            int span = Mathf.Max(1, data.Length - start);
            for (int i = start; i < data.Length; i++)
            {
                int local = i - start;
                float t = local / (float)SampleRate;
                float f = freqEnd > 0f ? Mathf.Lerp(freq, freqEnd, local / (float)span) : freq;
                phase += 2f * Mathf.PI * f / SampleRate;
                data[i] += Mathf.Sin(phase) * gain * Env(t, attack, rate);
            }
        }

        /// <summary>One-pole lowpass. Cheap, and enough to sell distance and material.</summary>
        public static void LowPass(float[] data, float cutoffHz)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * cutoffHz / SampleRate);
            float y = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                y += a * (data[i] - y);
                data[i] = y;
            }
        }

        public static void HighPass(float[] data, float cutoffHz)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * cutoffHz / SampleRate);
            float y = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                y += a * (data[i] - y);
                data[i] -= y;
            }
        }

        /// <summary>
        /// Discrete early reflections. This is what separates "a click" from "a gunshot in a street" —
        /// far more convincing than a longer noise tail.
        /// </summary>
        public static void AddReflections(float[] data, params (float delaySeconds, float gain)[] taps)
        {
            var dry = (float[])data.Clone();
            foreach (var (delaySeconds, gain) in taps)
            {
                int offset = Mathf.RoundToInt(delaySeconds * SampleRate);
                if (offset <= 0 || offset >= data.Length)
                    continue;
                for (int i = offset; i < data.Length; i++)
                    data[i] += dry[i - offset] * gain;
            }
        }

        /// <summary>Soft-knee saturation, then peak normalise. Keeps layered shots loud but unclipped.</summary>
        public static void Finish(float[] data, float drive = 1.2f, float peak = 0.92f)
        {
            float max = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (float)System.Math.Tanh(data[i] * drive);
                float abs = Mathf.Abs(data[i]);
                if (abs > max) max = abs;
            }
            if (max <= 0.0001f) return;
            float scale = peak / max;
            for (int i = 0; i < data.Length; i++)
                data[i] *= scale;
        }

        public static AudioClip ToClip(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
