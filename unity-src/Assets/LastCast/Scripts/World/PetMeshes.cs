// Low-poly models of the 30 pets, built from a handful of body plans
// (four-legged, bird, frog, crab, snail, turtle, octopus, fish, serpent, moth)
// in each pet's own colours. Each model faces +Z, about half a metre tall.

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public static class PetMeshes
    {
        public enum Plan { Quad, Bird, Frog, Crab, Hermit, Snail, Turtle, Octopus, Fish, Seal, Serpent, Moth }

        /// <summary>How a pet moves: on the ground, flying, or floating in the air like a spirit.</summary>
        public enum Motion { Walk, Fly, Float }

        public class Look
        {
            public Plan plan;
            public string body, accent, extra;
            public float size = 1;
            public Motion motion = Motion.Walk;
            public string feature; // "horn", "antlers", "wings", "mane", "fan", "mask", "tall", "upright", "flat-tail", "spots"
        }

        static Look L(Plan plan, string body, string accent, string extra = null, float size = 1, Motion motion = Motion.Walk, string feature = null) =>
            new Look { plan = plan, body = body, accent = accent, extra = extra ?? accent, size = size, motion = motion, feature = feature };

        public static readonly Dictionary<string, Look> Looks = new Dictionary<string, Look>
        {
            ["lilyfrog"] = L(Plan.Frog, "#6ab04c", "#f6e58d"),
            ["duckling"] = L(Plan.Bird, "#ffd60a", "#f77f00", size: 0.7f),
            ["sandcrab"] = L(Plan.Crab, "#e76f51", "#f4a261"),
            ["snail"] = L(Plan.Snail, "#d4c4a8", "#c08552"),
            ["fieldmouse"] = L(Plan.Quad, "#9e9e9e", "#ffafcc", size: 0.6f),
            ["otterpup"] = L(Plan.Quad, "#7b5b3a", "#c8a27c", size: 0.9f, feature: "long"),
            ["flamingo"] = L(Plan.Bird, "#ff8fab", "#1b1b1b", size: 1.1f, feature: "tall"),
            ["hermit"] = L(Plan.Hermit, "#e76f51", "#e9c46a"),
            ["turtle"] = L(Plan.Turtle, "#90a955", "#4f772d"),
            ["kingfisher"] = L(Plan.Bird, "#1e88e5", "#f77f00", size: 0.8f, motion: Motion.Fly),
            ["raccoon"] = L(Plan.Quad, "#8d8d8d", "#2b2b2b", "#e0e0e0", feature: "mask"),
            ["owl"] = L(Plan.Bird, "#a47148", "#f1e3c8", size: 1f, motion: Motion.Fly, feature: "round"),
            ["beaver"] = L(Plan.Quad, "#6f4518", "#3b2410", feature: "flat-tail"),
            ["penguin"] = L(Plan.Bird, "#1b1b1b", "#f1f1f1", "#f77f00", feature: "upright"),
            ["seal"] = L(Plan.Seal, "#9aa5b1", "#6c757d"),
            ["snowfox"] = L(Plan.Quad, "#f1f1f1", "#cfd8dc", feature: "bushy"),
            ["eagle"] = L(Plan.Bird, "#5c3d2e", "#f1f1f1", "#ffb703", size: 1.2f, motion: Motion.Fly),
            ["grizzly"] = L(Plan.Quad, "#6d4c41", "#4e342e", size: 1.3f),
            ["octopus"] = L(Plan.Octopus, "#e56b6f", "#b5838d", motion: Motion.Float),
            ["parrot"] = L(Plan.Bird, "#e63946", "#1e88e5", "#ffd166", motion: Motion.Fly),
            ["dragon"] = L(Plan.Quad, "#2a9d8f", "#e9c46a", "#e76f51", size: 1.1f, motion: Motion.Fly, feature: "wings"),
            ["peacock"] = L(Plan.Bird, "#1e88e5", "#2a9d8f", "#ffd166", size: 1.1f, feature: "fan"),
            ["kraken"] = L(Plan.Octopus, "#6a0dad", "#c77dff", motion: Motion.Float),
            ["unicorn"] = L(Plan.Quad, "#f8f9fa", "#ff8fab", "#ffd166", size: 1.2f, feature: "horn"),
            ["spiritkoi"] = L(Plan.Fish, "#fdfcdc", "#f77f00", size: 1.1f, motion: Motion.Float),
            ["phoenix"] = L(Plan.Bird, "#ff7b00", "#ffba08", "#d00000", size: 1.1f, motion: Motion.Fly, feature: "fan"),
            ["wyrmling"] = L(Plan.Serpent, "#3a86a8", "#90e0ef", size: 1.1f, motion: Motion.Float),
            ["qilin"] = L(Plan.Quad, "#ffd166", "#e76f51", "#2a9d8f", size: 1.2f, feature: "antlers"),
            ["moonmoth"] = L(Plan.Moth, "#bde0fe", "#a2d2ff", "#f1f1f1", motion: Motion.Fly),
            ["starwhale"] = L(Plan.Fish, "#3a0ca3", "#bde0fe", size: 1.4f, motion: Motion.Float, feature: "spots"),
        };

        static readonly Dictionary<string, Mesh> cache = new Dictionary<string, Mesh>();

        public static Look LookOf(string petId) => Looks.TryGetValue(petId, out var l) ? l : L(Plan.Quad, "#a0a0a0", "#707070");

        public static Mesh Build(string petId)
        {
            if (cache.TryGetValue(petId, out var m)) return m;
            var model = Models.Named("pet", "pet_" + petId); // the Blender pet, if there is one
            if (model) return cache[petId] = model;
            var look = LookOf(petId);
            var rnd = new SeededRandom(Noise.SeedFrom("pet", petId));
            Color32 body = MeshKit.Hex(look.body), accent = MeshKit.Hex(look.accent), extra = MeshKit.Hex(look.extra);
            Color32 eye = new Color32(20, 20, 20, 255);
            var k = new MeshKit();
            float s = 0.25f * look.size;
            void Eyes(Vector3 head, float r, float spread = 0.5f)
            {
                k.Box(head + new Vector3(r * spread, r * 0.25f, r * 0.8f), Vector3.one * r * 0.28f, eye);
                k.Box(head + new Vector3(-r * spread, r * 0.25f, r * 0.8f), Vector3.one * r * 0.28f, eye);
            }
            switch (look.plan)
            {
                case Plan.Quad:
                {
                    float len = look.feature == "long" ? 1.5f : 1.1f;
                    var torso = new Vector3(0, s * 1.4f, 0);
                    k.Blob(torso, new Vector3(s * 0.8f, s * 0.75f, s * len), body, rnd, 0.08f);
                    var head = torso + new Vector3(0, s * 0.6f, s * (len + 0.25f));
                    k.Blob(head, Vector3.one * s * 0.6f, body, rnd, 0.08f);
                    k.Blob(head + new Vector3(0, -s * 0.15f, s * 0.45f), new Vector3(s * 0.3f, s * 0.25f, s * 0.3f), accent, rnd, 0.05f); // snout
                    Eyes(head, s * 0.6f);
                    foreach (int side in new[] { -1, 1 })
                        k.Cone(head + new Vector3(side * s * 0.35f, s * 0.4f, 0), s * 0.18f, s * 0.35f, 4, look.feature == "mask" ? extra : body);
                    foreach (var (x, z) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                        k.Box(new Vector3(x * s * 0.45f, s * 0.45f, z * s * 0.6f * len), new Vector3(s * 0.25f, s * 0.9f, s * 0.25f), accent);
                    // Tail.
                    if (look.feature == "flat-tail") k.Box(torso + new Vector3(0, -s * 0.4f, -s * (len + 0.5f)), new Vector3(s * 0.6f, s * 0.12f, s * 0.9f), accent);
                    else if (look.feature == "bushy") k.Blob(torso + new Vector3(0, 0, -s * (len + 0.5f)), new Vector3(s * 0.35f, s * 0.35f, s * 0.7f), body, rnd, 0.1f);
                    else k.Cone(torso + new Vector3(0, 0, -s * len * 0.9f), s * 0.12f, s * 0.8f, 4, look.feature == "mask" ? extra : accent);
                    if (look.feature == "mask") k.Box(head + new Vector3(0, s * 0.15f, s * 0.5f), new Vector3(s * 0.9f, s * 0.2f, s * 0.15f), accent);
                    if (look.feature == "horn") k.Cone(head + new Vector3(0, s * 0.45f, s * 0.2f), s * 0.09f, s * 0.8f, 5, extra);
                    if (look.feature == "horn") k.Blob(torso + new Vector3(0, s * 0.8f, s * 0.6f), new Vector3(s * 0.2f, s * 0.35f, s * 0.6f), accent, rnd, 0.15f); // mane
                    if (look.feature == "antlers")
                        foreach (int side in new[] { -1, 1 })
                        {
                            k.Cone(head + new Vector3(side * s * 0.2f, s * 0.45f, 0), s * 0.06f, s * 0.6f, 4, extra);
                            k.Cone(head + new Vector3(side * s * 0.35f, s * 0.75f, 0), s * 0.05f, s * 0.35f, 4, extra);
                        }
                    if (look.feature == "wings")
                    {
                        foreach (int side in new[] { -1, 1 })
                        {
                            var root = torso + new Vector3(side * s * 0.5f, s * 0.5f, 0);
                            var tip = root + new Vector3(side * s * 1.6f, s * 0.8f, -s * 0.3f);
                            var back = root + new Vector3(side * s * 0.4f, 0, -s * 0.9f);
                            k.Tri(root, tip, back, extra);
                            k.Tri(root, back, tip, extra);
                        }
                        k.Cone(head + new Vector3(s * 0.2f, s * 0.4f, -s * 0.1f), s * 0.07f, s * 0.4f, 4, accent);
                        k.Cone(head + new Vector3(-s * 0.2f, s * 0.4f, -s * 0.1f), s * 0.07f, s * 0.4f, 4, accent);
                    }
                    break;
                }
                case Plan.Bird:
                {
                    bool upright = look.feature == "upright", tall = look.feature == "tall";
                    float legH = tall ? s * 2.2f : s * 0.4f;
                    var torso = new Vector3(0, legH + s * 0.7f, 0);
                    k.Blob(torso, upright ? new Vector3(s * 0.65f, s * 1.1f, s * 0.6f) : new Vector3(s * 0.65f, s * 0.6f, s * 0.95f), body, rnd, 0.06f);
                    k.Blob(torso + new Vector3(0, -s * 0.05f, s * 0.2f), upright ? new Vector3(s * 0.5f, s * 0.9f, s * 0.5f) : new Vector3(s * 0.5f, s * 0.45f, s * 0.7f), accent, rnd, 0.05f); // belly
                    var head = torso + new Vector3(0, upright ? s * 1.1f : s * 0.75f, upright ? s * 0.1f : s * 0.7f);
                    if (tall) head = torso + new Vector3(0, s * 1.7f, s * 0.6f);
                    if (tall) k.Box(torso + new Vector3(0, s * 0.9f, s * 0.4f), new Vector3(s * 0.15f, s * 1.6f, s * 0.15f), body); // neck
                    k.Blob(head, Vector3.one * s * (look.feature == "round" ? 0.6f : 0.45f), body, rnd, 0.06f);
                    var beak = MeshKit.Hex(look.extra ?? "#f77f00");
                    k.Box(head + new Vector3(0, -s * 0.05f, s * 0.5f), new Vector3(s * 0.18f, s * 0.15f, s * 0.35f), beak);
                    Eyes(head, s * 0.45f);
                    foreach (int side in new[] { -1, 1 })
                    {
                        var root = torso + new Vector3(side * s * 0.55f, s * 0.2f, s * 0.2f);
                        var tip = root + new Vector3(side * s * 0.3f, -s * 0.2f, -s * 1.1f);
                        var low = root + new Vector3(side * s * 0.1f, -s * 0.5f, -s * 0.3f);
                        k.Tri(root, tip, low, MeshKit.Shade(body, 0.85f));
                        k.Tri(root, low, tip, MeshKit.Shade(body, 0.85f));
                        k.Box(new Vector3(side * s * 0.25f, legH / 2, 0), new Vector3(s * 0.08f, legH, s * 0.08f), beak);
                    }
                    if (look.feature == "fan")
                        for (int f = 0; f < 7; f++)
                        {
                            float a = (f / 6f - 0.5f) * 2.2f;
                            var b0 = torso + new Vector3(0, s * 0.2f, -s * 0.8f);
                            var dir = new Vector3(Mathf.Sin(a), Mathf.Cos(a) * 0.9f + 0.4f, -0.4f);
                            var c = f % 2 == 0 ? accent : extra;
                            k.Tri(b0, b0 + dir * s * 2f + new Vector3(0.15f * s, 0, 0), b0 + dir * s * 2f - new Vector3(0.15f * s, 0, 0), c);
                            k.Tri(b0, b0 + dir * s * 2f - new Vector3(0.15f * s, 0, 0), b0 + dir * s * 2f + new Vector3(0.15f * s, 0, 0), c);
                        }
                    else k.Tri(torso + new Vector3(0, s * 0.1f, -s * 0.8f), torso + new Vector3(s * 0.3f, s * 0.3f, -s * 1.3f), torso + new Vector3(-s * 0.3f, s * 0.3f, -s * 1.3f), body);
                    break;
                }
                case Plan.Frog:
                {
                    var torso = new Vector3(0, s * 0.5f, 0);
                    k.Blob(torso, new Vector3(s * 0.8f, s * 0.5f, s * 0.85f), body, rnd, 0.08f);
                    k.Blob(torso + new Vector3(0, -s * 0.15f, s * 0.2f), new Vector3(s * 0.6f, s * 0.3f, s * 0.6f), accent, rnd, 0.05f);
                    foreach (int side in new[] { -1, 1 })
                    {
                        k.Blob(torso + new Vector3(side * s * 0.4f, s * 0.45f, s * 0.45f), Vector3.one * s * 0.22f, body, rnd, 0.05f);
                        k.Box(torso + new Vector3(side * s * 0.4f, s * 0.55f, s * 0.6f), Vector3.one * s * 0.12f, eye);
                        k.Blob(new Vector3(side * s * 0.75f, s * 0.2f, -s * 0.3f), new Vector3(s * 0.25f, s * 0.2f, s * 0.5f), body, rnd, 0.05f);
                    }
                    break;
                }
                case Plan.Crab:
                case Plan.Hermit:
                {
                    var torso = new Vector3(0, s * 0.4f, 0);
                    k.Blob(torso, new Vector3(s * 0.85f, s * 0.35f, s * 0.6f), body, rnd, 0.06f);
                    foreach (int side in new[] { -1, 1 })
                    {
                        k.Blob(torso + new Vector3(side * s * 0.9f, s * 0.1f, s * 0.6f), new Vector3(s * 0.3f, s * 0.22f, s * 0.35f), accent, rnd, 0.1f);
                        for (int l = 0; l < 3; l++) k.Box(torso + new Vector3(side * s * 0.75f, -s * 0.2f, (l - 1) * s * 0.3f), new Vector3(s * 0.5f, s * 0.06f, s * 0.06f), body);
                        k.Box(torso + new Vector3(side * s * 0.2f, s * 0.4f, s * 0.45f), Vector3.one * s * 0.12f, eye);
                    }
                    if (look.plan == Plan.Hermit)
                    {
                        k.Cone(torso + new Vector3(0, s * 0.1f, -s * 0.3f), s * 0.6f, s * 1.1f, 7, accent, 0, 0.3f);
                        k.Cone(torso + new Vector3(0, s * 0.9f, -s * 0.3f), s * 0.3f, s * 0.6f, 7, MeshKit.Shade(accent, 0.85f), 0, 0.6f);
                    }
                    break;
                }
                case Plan.Snail:
                {
                    k.Blob(new Vector3(0, s * 0.2f, s * 0.2f), new Vector3(s * 0.35f, s * 0.2f, s * 1f), body, rnd, 0.05f);
                    k.Blob(new Vector3(0, s * 0.8f, -s * 0.2f), new Vector3(s * 0.45f, s * 0.6f, s * 0.6f), accent, rnd, 0.06f);
                    k.Blob(new Vector3(s * 0.12f, s * 0.85f, -s * 0.2f), new Vector3(s * 0.35f, s * 0.4f, s * 0.4f), MeshKit.Shade(accent, 0.8f), rnd, 0.06f);
                    foreach (int side in new[] { -1, 1 })
                    {
                        k.Box(new Vector3(side * s * 0.12f, s * 0.55f, s * 1f), new Vector3(s * 0.05f, s * 0.6f, s * 0.05f), body);
                        k.Box(new Vector3(side * s * 0.12f, s * 0.85f, s * 1f), Vector3.one * s * 0.1f, eye);
                    }
                    break;
                }
                case Plan.Turtle:
                {
                    k.Blob(new Vector3(0, s * 0.55f, 0), new Vector3(s * 0.9f, s * 0.5f, s * 1f), accent, rnd, 0.12f);
                    k.Blob(new Vector3(0, s * 0.35f, s * 1.05f), Vector3.one * s * 0.3f, body, rnd, 0.05f);
                    Eyes(new Vector3(0, s * 0.35f, s * 1.05f), s * 0.3f);
                    foreach (var (x, z) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                        k.Blob(new Vector3(x * s * 0.7f, s * 0.2f, z * s * 0.6f), new Vector3(s * 0.25f, s * 0.15f, s * 0.3f), body, rnd, 0.05f);
                    break;
                }
                case Plan.Octopus:
                {
                    var head = new Vector3(0, s * 1.4f, 0);
                    k.Blob(head, new Vector3(s * 0.7f, s * 0.85f, s * 0.7f), body, rnd, 0.08f);
                    Eyes(head + new Vector3(0, -s * 0.2f, 0), s * 0.7f, 0.4f);
                    for (int t = 0; t < 8; t++)
                    {
                        float a = t / 8f * Mathf.PI * 2;
                        var root = head + new Vector3(Mathf.Cos(a) * s * 0.4f, -s * 0.6f, Mathf.Sin(a) * s * 0.4f);
                        var tip = root + new Vector3(Mathf.Cos(a) * s * 0.7f, -s * 0.8f, Mathf.Sin(a) * s * 0.7f);
                        var side = new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a)) * s * 0.15f;
                        var c = t % 2 == 0 ? body : accent;
                        k.Tri(root - side, root + side, tip, c);
                        k.Tri(root + side, root - side, tip, c);
                    }
                    break;
                }
                case Plan.Fish:
                case Plan.Seal:
                {
                    bool seal = look.plan == Plan.Seal;
                    var torso = new Vector3(0, s * (seal ? 0.6f : 1.2f), 0);
                    k.Blob(torso, new Vector3(s * 0.55f, s * 0.6f, s * 1.4f), body, rnd, 0.06f);
                    k.Blob(torso + new Vector3(0, -s * 0.2f, s * 0.1f), new Vector3(s * 0.45f, s * 0.35f, s * 1.1f), accent, rnd, 0.05f);
                    Eyes(torso + new Vector3(0, s * 0.1f, s * 0.9f), s * 0.5f, 0.8f);
                    var tail = torso + new Vector3(0, 0, -s * 1.3f);
                    if (seal)
                    {
                        k.Tri(tail, tail + new Vector3(s * 0.6f, -s * 0.2f, -s * 0.5f), tail + new Vector3(-s * 0.6f, -s * 0.2f, -s * 0.5f), accent);
                        k.Tri(tail, tail + new Vector3(-s * 0.6f, -s * 0.2f, -s * 0.5f), tail + new Vector3(s * 0.6f, -s * 0.2f, -s * 0.5f), accent);
                    }
                    else
                    {
                        k.Tri(tail, tail + new Vector3(0, s * 0.7f, -s * 0.6f), tail + new Vector3(0, -s * 0.7f, -s * 0.6f), accent);
                        k.Tri(tail, tail + new Vector3(0, -s * 0.7f, -s * 0.6f), tail + new Vector3(0, s * 0.7f, -s * 0.6f), accent);
                    }
                    foreach (int side in new[] { -1, 1 })
                    {
                        var fin = torso + new Vector3(side * s * 0.5f, -s * 0.2f, s * 0.3f);
                        k.Tri(fin, fin + new Vector3(side * s * 0.5f, -s * 0.2f, -s * 0.3f), fin + new Vector3(0, 0, -s * 0.5f), accent);
                        k.Tri(fin, fin + new Vector3(0, 0, -s * 0.5f), fin + new Vector3(side * s * 0.5f, -s * 0.2f, -s * 0.3f), accent);
                    }
                    if (look.feature == "spots")
                        for (int i = 0; i < 8; i++)
                            k.Box(torso + new Vector3(rnd.Range(-0.4f, 0.4f) * s, s * 0.5f, rnd.Range(-1f, 1f) * s), Vector3.one * s * 0.1f, accent);
                    break;
                }
                case Plan.Serpent:
                {
                    for (int i = 0; i < 7; i++)
                    {
                        float t = i / 6f;
                        var p = new Vector3(Mathf.Sin(t * 5) * s * 0.5f, s * (1.2f + Mathf.Sin(t * 3) * 0.3f), s * (1.5f - t * 3f));
                        k.Blob(p, Vector3.one * s * (0.45f - t * 0.25f), i % 2 == 0 ? body : MeshKit.Shade(body, 0.9f), rnd, 0.05f);
                    }
                    var head = new Vector3(0, s * 1.3f, s * 1.9f);
                    k.Blob(head, new Vector3(s * 0.4f, s * 0.35f, s * 0.55f), body, rnd, 0.06f);
                    Eyes(head, s * 0.4f);
                    k.Cone(head + new Vector3(s * 0.15f, s * 0.25f, -s * 0.2f), s * 0.07f, s * 0.4f, 4, accent);
                    k.Cone(head + new Vector3(-s * 0.15f, s * 0.25f, -s * 0.2f), s * 0.07f, s * 0.4f, 4, accent);
                    break;
                }
                case Plan.Moth:
                {
                    var torso = new Vector3(0, s * 1.2f, 0);
                    k.Blob(torso, new Vector3(s * 0.25f, s * 0.25f, s * 0.7f), MeshKit.Shade(body, 0.7f), rnd, 0.05f);
                    foreach (int side in new[] { -1, 1 })
                        foreach (var (z, size) in new[] { (0.15f, 1.2f), (-0.35f, 0.8f) })
                        {
                            var root = torso + new Vector3(side * s * 0.15f, 0, z * s);
                            var a = root + new Vector3(side * s * size, s * 0.3f, s * 0.4f * size);
                            var b = root + new Vector3(side * s * size * 0.9f, s * 0.2f, -s * 0.6f * size);
                            k.Tri(root, a, b, size > 1 ? body : accent);
                            k.Tri(root, b, a, size > 1 ? body : accent);
                        }
                    k.Box(torso + new Vector3(s * 0.1f, s * 0.25f, s * 0.75f), new Vector3(s * 0.04f, s * 0.04f, s * 0.4f), extra);
                    k.Box(torso + new Vector3(-s * 0.1f, s * 0.25f, s * 0.75f), new Vector3(s * 0.04f, s * 0.04f, s * 0.4f), extra);
                    break;
                }
            }
            m = k.Build(petId);
            cache[petId] = m;
            return m;
        }
    }
}
