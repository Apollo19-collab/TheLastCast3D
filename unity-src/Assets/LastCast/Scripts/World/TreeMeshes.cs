// Low-poly scenery, modelled on the 2D game's sprites (client/js/gfx/sprites.js)
// and using their colours: leafy trees in three leaf palettes, tiered pines,
// berry bushes, mossy boulders, stones, flower patches, grass tufts, pebbles,
// driftwood, lily pads and reeds. Every mesh is generated from a seed.

using UnityEngine;

namespace LastCast
{
    public static class TreeMeshes
    {
        static readonly Color32 Bark = MeshKit.Hex("#5b3c22");

        // [light, mid, dark, highlight], as LEAF_PALETTES in sprites.js.
        static readonly string[][] LeafPalettes =
        {
            new[] { "#7fb35a", "#4f8a3a", "#2c5a25", "#b5d98a" },
            new[] { "#8fbf5f", "#5a9440", "#30602a", "#c7e39a" },
            new[] { "#6aa34f", "#3f7a33", "#244d22", "#a7d07f" },
        };

        /// <summary>Trunk radius at the base, for walking into (Obstacles).</summary>
        public const float TrunkRadius = 0.34f;

        /// <summary>A round, leafy tree: a ring of canopy lobes, darker underneath, with light clusters on top.
        /// The canopy starts well above head height, so walking under it doesn't fill your view with leaves.</summary>
        public static Mesh LeafyTree(int variant)
        {
            var rnd = new SeededRandom((uint)(100 + variant));
            var pal = LeafPalettes[variant % LeafPalettes.Length];
            Color32 light = MeshKit.Hex(pal[0]), mid = MeshKit.Hex(pal[1]), dark = MeshKit.Hex(pal[2]), high = MeshKit.Hex(pal[3]);
            var k = new MeshKit();
            float R = rnd.Range(1.8f, 2.6f);
            float trunk = rnd.Range(3.6f, 4.6f);
            k.Cone(Vector3.zero, 0.32f, trunk + 1, 6, Bark, 0.22f);
            float cy = trunk + R * 0.75f;
            int lobes = 7 + (int)(rnd.Next() * 3);
            for (int i = 0; i < lobes; i++)
            {
                float a = i / (float)lobes * Mathf.PI * 2 + rnd.Next() * 0.4f;
                float d = R * (0.45f + rnd.Next() * 0.15f);
                float r = R * (0.48f + rnd.Next() * 0.12f);
                k.Blob(new Vector3(Mathf.Cos(a) * d, cy - r * 0.15f, Mathf.Sin(a) * d), new Vector3(r, r * 0.85f, r), i % 2 == 0 ? mid : dark, rnd, 0.16f);
            }
            k.Blob(new Vector3(0, cy + R * 0.3f, 0), Vector3.one * R * 0.62f, light, rnd, 0.15f);
            for (int i = 0; i < 4; i++)
                k.Blob(new Vector3(rnd.Range(-0.4f, 0.4f) * R, cy + R * (0.55f + rnd.Next() * 0.2f), rnd.Range(-0.4f, 0.4f) * R), Vector3.one * R * 0.2f, high, rnd, 0.1f);
            return k.Build($"tree {variant}");
        }

        /// <summary>A pine: a bare trunk up to above head height, then four stacked star-shaped tiers of needles,
        /// darkest at the bottom.</summary>
        public static Mesh Pine(int variant)
        {
            var rnd = new SeededRandom((uint)(200 + variant));
            string[] tiers = { "#1f3d2a", "#2b5236", "#386a43", "#4c8452" };
            float[] scales = { 1f, 0.78f, 0.56f, 0.34f };
            var k = new MeshKit();
            float R = rnd.Range(1.6f, 2.3f);
            float h = rnd.Range(7f, 10f);
            float bare = 2.9f; // metres of bare trunk below the lowest needles
            k.Cone(Vector3.zero, 0.3f, bare + h * 0.3f, 6, Bark, 0.2f);
            for (int t = 0; t < 4; t++)
            {
                float y = bare + h * (0.08f + t * 0.19f);
                StarCone(k, new Vector3(0, y, 0), R * scales[t] * 1.25f, h * 0.36f, 9, MeshKit.Hex(tiers[t]), t * 0.3f);
            }
            return k.Build($"pine {variant}");
        }

        /// <summary>A cone whose base is a star (points alternate full and 0.62 radius), like the 2D pine tiers.</summary>
        static void StarCone(MeshKit k, Vector3 bottom, float r, float height, int points, Color32 color, float twist)
        {
            var top = bottom + Vector3.up * height;
            int n = points * 2;
            for (int i = 0; i < n; i++)
            {
                float a0 = i / (float)n * Mathf.PI * 2 + twist, a1 = (i + 1) / (float)n * Mathf.PI * 2 + twist;
                float r0 = i % 2 == 0 ? r : r * 0.62f, r1 = (i + 1) % 2 == 0 ? r : r * 0.62f;
                var p0 = bottom + new Vector3(Mathf.Cos(a0) * r0, i % 2 == 0 ? -0.25f : 0, Mathf.Sin(a0) * r0);
                var p1 = bottom + new Vector3(Mathf.Cos(a1) * r1, (i + 1) % 2 == 0 ? -0.25f : 0, Mathf.Sin(a1) * r1);
                k.Tri(p1, p0, top, i % 2 == 0 ? color : MeshKit.Shade(color, 0.86f));
                k.Tri(p0, p1, bottom + Vector3.up * 0.1f, MeshKit.Shade(color, 0.6f)); // underside
            }
        }

        /// <summary>A bush; some have red or white berries.</summary>
        public static Mesh Bush(int variant)
        {
            var rnd = new SeededRandom((uint)(300 + variant));
            var k = new MeshKit();
            float R = rnd.Range(0.7f, 1.1f);
            for (int i = 0; i < 5; i++)
            {
                float a = rnd.Next() * Mathf.PI * 2;
                k.Blob(new Vector3(Mathf.Cos(a) * R * 0.4f, R * 0.55f, Mathf.Sin(a) * R * 0.4f), Vector3.one * R * 0.6f,
                    i % 2 == 0 ? MeshKit.Hex("#7fb35a") : MeshKit.Hex("#3f6e2e"), rnd, 0.2f);
            }
            if (variant % 5 < 2)
            {
                var berry = variant % 2 == 0 ? MeshKit.Hex("#d1495b") : MeshKit.Hex("#f4f1de");
                for (int i = 0; i < 9; i++)
                {
                    float a = rnd.Next() * Mathf.PI * 2, e = rnd.Range(0.2f, 1.1f);
                    var p = new Vector3(Mathf.Cos(a) * R * 0.85f, R * 0.55f + Mathf.Sin(e) * R * 0.5f, Mathf.Sin(a) * R * 0.85f);
                    k.Box(p, Vector3.one * 0.09f, berry);
                }
            }
            return k.Build($"bush {variant}");
        }

        /// <summary>How far a big boulder's body reaches out from its centre (metres, before scaling).</summary>
        public static float BoulderRadius(int variant) => new SeededRandom((uint)(400 + variant)).Range(0.8f, 1.7f) * 0.8f;

        /// <summary>A grey boulder, lighter on top, often with moss; or a small stone.</summary>
        public static Mesh Boulder(int variant, bool small)
        {
            var rnd = new SeededRandom((uint)((small ? 450 : 400) + variant));
            var k = new MeshKit();
            float R = small ? rnd.Range(0.3f, 0.6f) : rnd.Range(0.8f, 1.7f);
            k.Blob(new Vector3(0, R * 0.35f, 0), new Vector3(R, R * 0.7f, R * 0.85f), MeshKit.Hex("#8b8f97"), rnd, 0.28f);
            k.Blob(new Vector3(-R * 0.1f, R * 0.6f, -R * 0.05f), new Vector3(R * 0.7f, R * 0.4f, R * 0.6f), MeshKit.Hex("#b8bcc4"), rnd, 0.2f);
            if (!small && variant % 5 < 3)
                k.Blob(new Vector3(-R * 0.2f, R * 0.85f, -R * 0.25f), new Vector3(R * 0.4f, R * 0.12f, R * 0.35f), MeshKit.Hex("#6e9646"), rnd, 0.15f); // moss
            return k.Build(small ? $"stone {variant}" : $"boulder {variant}");
        }

        /// <summary>A patch of little five-petalled flowers in one colour.</summary>
        public static Mesh Flowers(int variant)
        {
            string[] colours = { "#f4f1de", "#ffd166", "#c77dff", "#ff8fab", "#90e0ef" };
            var rnd = new SeededRandom((uint)(500 + variant));
            var petal = MeshKit.Hex(colours[variant % colours.Length]);
            var centre = MeshKit.Hex("#f9c74f");
            var stem = MeshKit.Hex("#4c7a33");
            var k = new MeshKit();
            for (int i = 0; i < 6; i++)
            {
                var c = new Vector3(rnd.Range(-0.6f, 0.6f), rnd.Range(0.18f, 0.32f), rnd.Range(-0.6f, 0.6f));
                k.Box(new Vector3(c.x, c.y / 2, c.z), new Vector3(0.02f, c.y, 0.02f), stem);
                for (int p = 0; p < 5; p++)
                {
                    float a = p / 5f * Mathf.PI * 2;
                    var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    var side = new Vector3(-d.z, 0, d.x) * 0.035f;
                    var o = c + d * 0.07f;
                    k.Tri(c, o + side, o - side, petal);
                    k.Tri(c, o - side, o + side, petal);
                }
                k.Box(c + Vector3.up * 0.01f, Vector3.one * 0.03f, centre);
            }
            return k.Build($"flowers {variant}");
        }

        /// <summary>A tuft of curved grass blades.</summary>
        public static Mesh Tuft(int variant)
        {
            var rnd = new SeededRandom((uint)(600 + variant));
            var col = variant % 2 == 0 ? MeshKit.Hex("#3d6b2c") : MeshKit.Hex("#5b8f3e");
            var k = new MeshKit();
            for (int i = 0; i < 7; i++)
            {
                float a = rnd.Next() * Mathf.PI * 2, lean = rnd.Range(0.05f, 0.25f), h = rnd.Range(0.3f, 0.6f);
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var side = new Vector3(-d.z, 0, d.x) * 0.03f;
                var tip = d * lean + Vector3.up * h;
                var c = i % 2 == 0 ? col : MeshKit.Shade(col, 1.15f);
                k.Tri(-side, side, tip, c);
                k.Tri(side, -side, tip, c);
            }
            return k.Build($"tuft {variant}");
        }

        public static Mesh Pebbles(int variant)
        {
            var rnd = new SeededRandom((uint)(700 + variant));
            var k = new MeshKit();
            for (int i = 0; i < 4; i++)
            {
                byte shade = (byte)(150 + rnd.Next() * 60);
                var c = new Color32(shade, (byte)(shade - 8), (byte)(shade - 20), 255);
                k.Blob(new Vector3(rnd.Range(-0.5f, 0.5f), 0.03f, rnd.Range(-0.5f, 0.5f)), new Vector3(rnd.Range(0.08f, 0.16f), 0.05f, rnd.Range(0.06f, 0.12f)), c, rnd, 0.1f);
            }
            return k.Build($"pebbles {variant}");
        }

        /// <summary>A weathered log washed up on the beach.</summary>
        public static Mesh Driftwood(int variant)
        {
            var rnd = new SeededRandom((uint)(800 + variant));
            var k = new MeshKit();
            var wood = MeshKit.Hex("#b39c7d");
            float len = rnd.Range(2f, 2.8f);
            // A six-sided log lying along X.
            int sides = 6;
            for (int s = 0; s < sides; s++)
            {
                float a0 = s / (float)sides * Mathf.PI * 2, a1 = (s + 1) / (float)sides * Mathf.PI * 2;
                Vector3 r0 = new Vector3(0, Mathf.Sin(a0), Mathf.Cos(a0)) * 0.2f + Vector3.up * 0.15f;
                Vector3 r1 = new Vector3(0, Mathf.Sin(a1), Mathf.Cos(a1)) * 0.2f + Vector3.up * 0.15f;
                var hl = Vector3.right * len / 2;
                var c = s % 2 == 0 ? wood : MeshKit.Hex("#8a7558");
                k.Quad(r0 - hl, r0 + hl * 0.9f, r1 + hl * 0.9f, r1 - hl, c);
                k.Tri(-hl + Vector3.up * 0.15f, r0 - hl, r1 - hl, MeshKit.Hex("#d6c3a3")); // cut ends
                k.Tri(hl * 0.9f + Vector3.up * 0.15f, r1 + hl * 0.9f, r0 + hl * 0.9f, MeshKit.Hex("#d6c3a3"));
            }
            k.Cone(new Vector3(-len * 0.2f, 0.25f, 0.1f), 0.05f, 0.5f, 4, wood, 0.02f); // a snapped-off branch
            return k.Build($"driftwood {variant}");
        }

        /// <summary>A lily pad with its notch; one in five has a flower.</summary>
        public static Mesh LilyPad(int variant)
        {
            var rnd = new SeededRandom((uint)(900 + variant));
            var k = new MeshKit();
            float r = rnd.Range(0.5f, 1f);
            float rot = rnd.Next() * Mathf.PI * 2;
            int n = 12;
            Color32 inner = MeshKit.Hex("#7dbb5a"), outer = MeshKit.Hex("#3e7a35");
            for (int i = 0; i < n; i++)
            {
                float a0 = rot + 0.25f + i / (float)n * (Mathf.PI * 2 - 0.5f), a1 = rot + 0.25f + (i + 1) / (float)n * (Mathf.PI * 2 - 0.5f);
                var p0 = new Vector3(Mathf.Cos(a0) * r, 0.02f, Mathf.Sin(a0) * r);
                var p1 = new Vector3(Mathf.Cos(a1) * r, 0.02f, Mathf.Sin(a1) * r);
                k.Tri(new Vector3(0, 0.03f, 0), p1, p0, i % 2 == 0 ? inner : outer);
            }
            if (variant % 5 == 0)
            {
                var petal = variant % 2 == 0 ? MeshKit.Hex("#ffd6e5") : MeshKit.Hex("#ffffff");
                for (int i = 0; i < 8; i++)
                {
                    float a = i / 8f * Mathf.PI * 2;
                    var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    var side = new Vector3(-d.z, 0, d.x) * 0.06f;
                    var tip = d * 0.22f + Vector3.up * 0.16f;
                    k.Tri(Vector3.up * 0.05f + side, Vector3.up * 0.05f - side, tip, petal);
                    k.Tri(Vector3.up * 0.05f - side, Vector3.up * 0.05f + side, tip, petal);
                }
                k.Box(Vector3.up * 0.09f, Vector3.one * 0.06f, MeshKit.Hex("#ffd166"));
            }
            return k.Build($"lily {variant}");
        }

        /// <summary>A clump of reeds standing in the shallows; some with a cattail. Its base is 1.5 m under the surface.</summary>
        public static Mesh Reeds(int variant)
        {
            var rnd = new SeededRandom((uint)(1000 + variant));
            Color32 blade = MeshKit.Hex("#6f9c4a"), dark = MeshKit.Hex("#4c7a33"), head = MeshKit.Hex("#6b4423");
            var k = new MeshKit();
            int blades = 3 + (int)(rnd.Next() * 3);
            float h = rnd.Range(1f, 2.4f);
            for (int b = 0; b < blades; b++)
            {
                var basePos = new Vector3(rnd.Range(-0.3f, 0.3f), -1.5f, rnd.Range(-0.3f, 0.3f));
                float bh = h * rnd.Range(0.75f, 1f) + 1.5f;
                var lean = new Vector3(rnd.Range(-0.25f, 0.25f), 0, rnd.Range(-0.25f, 0.25f));
                var side = Vector3.Cross(lean.sqrMagnitude > 0 ? lean.normalized : Vector3.right, Vector3.up) * 0.04f;
                var c = b % 2 == 0 ? blade : dark;
                var tip = basePos + Vector3.up * bh + lean;
                k.Tri(basePos - side, basePos + side, tip, c);
                k.Tri(basePos + side, basePos - side, tip, c);
                if (b == 0 && variant % 3 == 0)
                    k.Cone(tip - Vector3.up * 0.45f - lean * 0.2f, 0.06f, 0.35f, 6, head, 0.06f);
            }
            return k.Build($"reeds {variant}");
        }
    }
}
