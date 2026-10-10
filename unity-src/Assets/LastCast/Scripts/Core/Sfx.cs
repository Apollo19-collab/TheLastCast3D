// Small sound effects generated in code (no audio files needed): casts,
// plops, bites, splashes, snaps and catch jingles, plus a soft ambient bed of
// wind and water.

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public static class Sfx
    {
        const int Rate = 44100;
        static AudioSource source, ambient;
        static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        public static float Volume = 0.8f;

        public static void Init(GameObject host)
        {
            source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            ambient = host.AddComponent<AudioSource>();
            ambient.clip = Make("ambient", 6f, (t, r) => Brown(r) * 0.5f * Mathf.Min(1, Mathf.Min(t, 6 - t) / 0.6f) * (0.7f + 0.3f * Mathf.Sin(t * Mathf.PI / 3)));
            ambient.loop = true;
            ambient.volume = 0.12f * Volume;
            ambient.Play();
        }

        public static void SetVolume(float v)
        {
            Volume = v;
            if (ambient) ambient.volume = 0.12f * v;
        }

        public static void Play(string name, float volume = 1)
        {
            if (!source) return;
            var clip = Clip(name);
            if (clip) source.PlayOneShot(clip, volume * Volume);
        }

        static float brown;
        static float Brown(System.Random r)
        {
            brown = Mathf.Clamp(brown + ((float)r.NextDouble() * 2 - 1) * 0.04f, -1, 1);
            return brown;
        }

        static float Env(float t, float len, float attack = 0.01f) => Mathf.Min(1, t / attack) * Mathf.Clamp01(1 - t / len);

        static AudioClip Clip(string name) => name switch
        {
            "cast" => Make(name, 0.35f, (t, r) => ((float)r.NextDouble() * 2 - 1) * Env(t, 0.35f, 0.15f) * 0.35f),
            "song" => Make(name, 2.2f, (t, r) => (Mathf.Sin(2 * Mathf.PI * (180 + 120 * Mathf.Sin(t * 2.4f)) * t) * 0.5f + Mathf.Sin(2 * Mathf.PI * (270 + 90 * Mathf.Sin(t * 1.7f)) * t) * 0.3f) * Env(t, 2.2f, 0.5f) * 0.3f),
            "travel" => Make(name, 0.6f, (t, r) => ((float)r.NextDouble() * 2 - 1) * Env(t, 0.6f, 0.25f) * 0.18f + Mathf.Sin(2 * Mathf.PI * (220 + 440 * t) * t) * Env(t, 0.6f, 0.2f) * 0.12f),
            "plop" => Make(name, 0.25f, (t, r) => Mathf.Sin(2 * Mathf.PI * (520 - 900 * t) * t) * Env(t, 0.25f) * 0.5f),
            "bite" => Make(name, 0.3f, (t, r) => Mathf.Sin(2 * Mathf.PI * 330 * t) * Env(t, 0.12f) * 0.5f + Mathf.Sin(2 * Mathf.PI * 330 * (t - 0.15f)) * (t > 0.15f ? Env(t - 0.15f, 0.12f) : 0) * 0.5f),
            "hook" => Make(name, 0.2f, (t, r) => Mathf.Sin(2 * Mathf.PI * (300 + 600 * t) * t) * Env(t, 0.2f) * 0.45f),
            "splash" => Make(name, 0.4f, (t, r) => ((float)r.NextDouble() * 2 - 1) * Env(t, 0.4f, 0.005f) * 0.3f * (1 - t * 2f)),
            "snap" => Make(name, 0.3f, (t, r) => (((float)r.NextDouble() * 2 - 1) * 0.6f + Mathf.Sin(2 * Mathf.PI * 1800 * t) * 0.4f) * Env(t, 0.12f, 0.001f)),
            "error" => Make(name, 0.2f, (t, r) => Mathf.Sign(Mathf.Sin(2 * Mathf.PI * 140 * t)) * Env(t, 0.2f) * 0.15f),
            "coin" => Arp(name, new[] { 988f, 1319f }, 0.08f),
            "catch" => Arp(name, new[] { 523f, 659f, 784f }, 0.1f),
            "trophy" => Arp(name, new[] { 523f, 659f, 784f, 1047f, 1319f }, 0.11f),
            "level" => Arp(name, new[] { 392f, 523f, 659f, 784f, 1047f }, 0.09f),
            _ => null,
        };

        static AudioClip Arp(string name, float[] notes, float step) =>
            Make(name, notes.Length * step + 0.4f, (t, r) =>
            {
                float v = 0;
                for (int i = 0; i < notes.Length; i++)
                {
                    float lt = t - i * step;
                    if (lt > 0) v += (Mathf.Sin(2 * Mathf.PI * notes[i] * lt) + 0.3f * Mathf.Sin(4 * Mathf.PI * notes[i] * lt)) * Env(lt, 0.45f) * 0.22f;
                }
                return v;
            });

        static AudioClip Make(string name, float seconds, System.Func<float, System.Random, float> f)
        {
            if (clips.TryGetValue(name, out var c)) return c;
            int n = (int)(seconds * Rate);
            var data = new float[n];
            var r = new System.Random(name.GetHashCode());
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(f(i / (float)Rate, r), -1, 1);
            c = AudioClip.Create(name, n, 1, Rate, false);
            c.SetData(data, 0);
            clips[name] = c;
            return c;
        }
    }
}
