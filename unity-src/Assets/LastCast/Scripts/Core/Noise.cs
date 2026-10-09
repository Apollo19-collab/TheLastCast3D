// Deterministic noise and random numbers, ported from the web version's
// shared/noise.js so the same inputs give the same values.

using System;

namespace LastCast
{
    public static class Noise
    {
        static readonly byte[] Perm = BuildPerm();

        static byte[] BuildPerm()
        {
            // Fixed-seed shuffle of 0..255 (same as shared/noise.js).
            long s = 1234567;
            double Rnd()
            {
                s = s * 16807 % 2147483647;
                return (s - 1) / 2147483646.0;
            }
            var p = new int[256];
            for (int i = 0; i < 256; i++) p[i] = i;
            for (int i = 255; i > 0; i--)
            {
                int j = (int)Math.Floor(Rnd() * (i + 1));
                (p[i], p[j]) = (p[j], p[i]);
            }
            var perm = new byte[512];
            for (int i = 0; i < 512; i++) perm[i] = (byte)p[i & 255];
            return perm;
        }

        static float Hash(int ix, int iy) => Perm[(Perm[ix & 255] + iy) & 255] / 255f;

        static float Smooth(float t) => t * t * (3 - 2 * t);

        /// <summary>Smooth value noise in [0, 1].</summary>
        public static float Value(float x, float y)
        {
            int ix = (int)Math.Floor(x);
            int iy = (int)Math.Floor(y);
            float fx = Smooth(x - ix);
            float fy = Smooth(y - iy);
            float a = Hash(ix, iy), b = Hash(ix + 1, iy), c = Hash(ix, iy + 1), d = Hash(ix + 1, iy + 1);
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
        }

        /// <summary>Fractal noise in roughly [0, 1].</summary>
        public static float Fbm(float x, float y, int octaves = 3)
        {
            float sum = 0, amp = 0.5f, norm = 0;
            for (int i = 0; i < octaves; i++)
            {
                sum += Value(x, y) * amp;
                norm += amp;
                x = x * 2.03f + 17.1f;
                y = y * 2.03f + 9.7f;
                amp *= 0.5f;
            }
            return sum / norm;
        }

        public static float SmoothStep(float a, float b, float v)
        {
            float t = Math.Clamp((v - a) / (b - a), 0f, 1f);
            return t * t * (3 - 2 * t);
        }

        /// <summary>FNV-1a hash of some parts joined with '|' (shared/noise.js seedFrom).</summary>
        public static uint SeedFrom(params object[] parts)
        {
            uint h = 2166136261;
            foreach (char ch in string.Join("|", parts))
            {
                h ^= ch;
                h *= 16777619;
            }
            return h;
        }
    }

    /// <summary>A small seeded random generator (mulberry32, as shared/noise.js seeded()).</summary>
    public class SeededRandom
    {
        uint s;
        public SeededRandom(uint seed) { s = seed; }

        public float Next()
        {
            s += 0x6d2b79f5;
            uint t = s;
            t = (t ^ (t >> 15)) * (t | 1);
            t ^= t + (t ^ (t >> 7)) * (t | 61);
            return ((t ^ (t >> 14)) >> 0) / 4294967296f;
        }

        public float Range(float a, float b) => a + (b - a) * Next();
    }
}
