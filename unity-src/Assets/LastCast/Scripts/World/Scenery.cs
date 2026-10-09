// Scenery placed by the same rules as the 2D game's terrain decor
// (client/js/gfx/terrain.js decorFor): every 256-unit chunk scatters 90
// candidates and each becomes a tree, pine, bush, tuft, flowers, pebbles,
// driftwood, boulder, stone or lily pad depending on the ground, the forest
// density and the water zone. Reeds stand in reed beds, current streaks drift
// down the rivers, wooden signposts name each area, the voyage boat comes and
// goes (Boat.cs) and the Travelling Zoo moves every 15 minutes. Tree trunks and
// boulders are solid (Obstacles).
//
// Everything small is drawn with GPU instancing, chunk by chunk, out to a
// distance that suits its size.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LastCast
{
    public class Scenery : MonoBehaviour
    {
        class Batch
        {
            public Mesh mesh;
            public Material material;
            public float distance, chunk;
            public bool shadows;
            public readonly Dictionary<Vector2Int, List<Matrix4x4>> cells = new Dictionary<Vector2Int, List<Matrix4x4>>();
        }

        readonly Dictionary<string, List<Batch>> kinds = new Dictionary<string, List<Batch>>();
        readonly List<Batch> batches = new List<Batch>();
        WorldMap map;
        GameData data;
        Material lilyMaterial, streakMaterial;
        Mesh streakMesh;
        GameObject zoo;
        int zooSpot = -1;

        class Streak { public float x, z, minX, phase, len; }
        readonly List<Streak> streaks = new List<Streak>();
        readonly List<Matrix4x4> streakMatrices = new List<Matrix4x4>();

        public void Build()
        {
            map = WorldMap.I;
            data = GameData.I;
            lilyMaterial = new Material(WorldBuilder.LitMaterial) { enableInstancing = true };
            lilyMaterial.SetFloat("_Float", 1);
            streakMaterial = new Material(RodView.Unlit) { enableInstancing = true };

            Kind("tree", 6, i => Models.Or("tree", i, () => TreeMeshes.LeafyTree(i)), WorldBuilder.TreeMaterial, 430, true);
            Kind("pine", 4, i => Models.Or("pine", i, () => TreeMeshes.Pine(i)), WorldBuilder.TreeMaterial, 430, true);
            Kind("bush", 5, i => Models.Or("bush", i, () => TreeMeshes.Bush(i)), WorldBuilder.TreeMaterial, 200, true);
            Kind("boulder", 5, i => Models.Or("boulder", i, () => TreeMeshes.Boulder(i, false)), WorldBuilder.LitMaterial, 260, true);
            Kind("stone", 4, i => Models.Or("stone", i, () => TreeMeshes.Boulder(i, true)), WorldBuilder.LitMaterial, 110, false);
            Kind("flowers", 5, i => Models.Or("flowers", i, () => TreeMeshes.Flowers(i)), WorldBuilder.TreeMaterial, 70, false);
            Kind("tuft", 4, i => Models.Or("tuft", i, () => TreeMeshes.Tuft(i)), WorldBuilder.TreeMaterial, 60, false);
            Kind("pebbles", 3, i => Models.Or("pebbles", i, () => TreeMeshes.Pebbles(i)), WorldBuilder.LitMaterial, 60, false);
            Kind("driftwood", 2, i => Models.Or("driftwood", i, () => TreeMeshes.Driftwood(i)), WorldBuilder.LitMaterial, 150, true);
            Kind("lily", 6, i => Models.Or("lily", i, () => TreeMeshes.LilyPad(i)), lilyMaterial, 170, false);
            Kind("reeds", 6, i => Models.Or("reeds", i, () => TreeMeshes.Reeds(i)), WorldBuilder.TreeMaterial, 170, false);

            PlaceDecor();
            PlaceWaterLife();
            PlaceSignposts();
            BuildBoat();
            streakMesh = FlatQuad(new Color32(230, 250, 250, 72));
        }

        /// <summary>How far a big boulder reaches out from its centre (metres, before scaling), for walking into it.</summary>
        static float BoulderRadius(int variant)
        {
            var m = Models.Get("boulder", variant);
            return m ? Mathf.Max(m.bounds.extents.x, m.bounds.extents.z) * 0.8f : TreeMeshes.BoulderRadius(variant);
        }

        void Kind(string name, int variants, System.Func<int, Mesh> make, Material mat, float distance, bool shadows)
        {
            var list = new List<Batch>();
            for (int i = 0; i < variants; i++)
            {
                var b = new Batch { mesh = make(i), material = mat, distance = distance, chunk = Mathf.Clamp(distance / 3, 24, 128), shadows = shadows };
                list.Add(b);
                batches.Add(b);
            }
            kinds[name] = list;
        }

        void Add(string kind, int variant, Vector3 p, float yaw, float scale)
        {
            var list = kinds[kind];
            var b = list[variant % list.Count];
            var key = new Vector2Int(Mathf.FloorToInt(p.x / b.chunk), Mathf.FloorToInt(p.z / b.chunk));
            if (!b.cells.TryGetValue(key, out var cell)) b.cells[key] = cell = new List<Matrix4x4>();
            cell.Add(Matrix4x4.TRS(p, Quaternion.Euler(0, yaw, 0), Vector3.one * scale));
        }

        // ---- the 2D game's decor rules -----------------------------------------------------------

        void PlaceDecor()
        {
            const float Chunk = 256; // world units, as in the 2D terrain
            var structures = data.structures;
            var keepClear = new List<Vector2> { new Vector2(data.spawn.x, data.spawn.y) };
            foreach (var s in data.shops) keepClear.Add(new Vector2(s.x, s.y));
            foreach (var z in data.zoo.spots) keepClear.Add(new Vector2(z.x, z.y));
            bool NearStructure(float x, float y, float m)
            {
                foreach (var s in structures) if (x > s.x - m && x < s.x + s.w + m && y > s.y - m && y < s.y + s.h + m) return true;
                foreach (var c in keepClear) if ((c - new Vector2(x, y)).sqrMagnitude < 150 * 150) return true;
                return false;
            }

            for (int cy = 0; cy * Chunk < map.Height; cy++)
                for (int cx = 0; cx * Chunk < map.Width; cx++)
                {
                    var rnd = new SeededRandom(Noise.SeedFrom("decor", cx, cy));
                    float x0 = cx * Chunk, y0 = cy * Chunk;
                    for (int n = 0; n < 90; n++)
                    {
                        float x = x0 + rnd.Next() * Chunk, y = y0 + rnd.Next() * Chunk;
                        int variant = (int)(rnd.Next() * 100);
                        float scale = 0.85f + rnd.Next() * 0.3f, yaw = rnd.Next() * 360;
                        if (!map.InBounds(x, y)) continue;
                        float s = map.Sdf(x, y);
                        if (s < -5)
                        {
                            var z = map.ZoneAt(x, y);
                            if (z?.decor == "lilies" && rnd.Next() < 0.55f) Add("lily", variant, map.ToUnity(x, y, 0.02f), yaw, scale);
                            continue;
                        }
                        if (s < 5) continue;
                        var ground = map.GroundAt(x, y);
                        float roll = rnd.Next();
                        var p = map.ToUnity(x, y);
                        p.y = map.GroundHeight(p.x, p.z) - 0.05f;
                        switch (ground)
                        {
                            case Ground.Grass:
                            {
                                float forest = map.ForestAt(x, y);
                                bool pineCountry = (y < 1150 && x > 700 && x < 2300) || Noise.Value(x / 900 + 2, y / 900 + 5) > 0.62f;
                                // A little sparser than the 2D map, so there's room to walk between the trees.
                                if (s > 55 && !NearStructure(x, y, 60) && roll < 0.06f + 0.5f * forest)
                                {
                                    Add(pineCountry || rnd.Next() < 0.15f ? "pine" : "tree", variant, p, yaw, scale);
                                    Obstacles.Add(p, TreeMeshes.TrunkRadius * scale);
                                }
                                else if (s > 18 && roll < 0.25f + 0.35f * forest) Add("bush", variant, p, yaw, scale);
                                else if (roll < 0.62f + 0.25f * forest) Add("tuft", variant, p, yaw, scale);
                                else Add("flowers", variant, p, yaw, scale);
                                break;
                            }
                            case Ground.Dirt:
                                if (roll < 0.12f) Add("pebbles", variant, p, yaw, 0.6f + rnd.Next() * 0.3f);
                                break;
                            case Ground.Sand:
                                if (s < 30 && roll < 0.06f) Add("driftwood", variant, p, yaw, scale);
                                else if (roll < 0.18f) Add("pebbles", variant, p, yaw, scale);
                                break;
                            case Ground.Rock:
                                if (roll < 0.35f)
                                {
                                    Add("boulder", variant, p, yaw, scale);
                                    Obstacles.Add(p, BoulderRadius(variant % kinds["boulder"].Count) * scale);
                                }
                                else if (roll < 0.8f) Add("stone", variant, p, yaw, scale);
                                break;
                        }
                    }
                }
        }

        /// <summary>Reeds in the reed beds and current streaks on the rivers (the 2D game's "dynamic" extras).</summary>
        void PlaceWaterLife()
        {
            const float Chunk = 256;
            for (int cy = 0; cy * Chunk < map.Height; cy++)
                for (int cx = 0; cx * Chunk < map.Width; cx++)
                {
                    var rnd = new SeededRandom(Noise.SeedFrom("dynamic", cx, cy));
                    float x0 = cx * Chunk, y0 = cy * Chunk;
                    for (int i = 0; i < 74; i++)
                    {
                        float x = x0 + rnd.Next() * Chunk, y = y0 + rnd.Next() * Chunk;
                        float roll = rnd.Next();
                        if (!map.InBounds(x, y)) continue;
                        float s = map.Sdf(x, y);
                        if (s >= -2) continue;
                        var z = map.ZoneAt(x, y);
                        if (z?.decor == "reeds" && s > -160)
                        {
                            // Denser than the 2D map's single stems: each reed spot is a small clump.
                            for (int r = 0; r < 3; r++)
                            {
                                var p = map.ToUnity(x + rnd.Range(-25, 25), y + rnd.Range(-25, 25));
                                Add("reeds", (int)(roll * 100) + r, p, rnd.Next() * 360, rnd.Range(0.8f, 1.2f));
                            }
                        }
                        if (z?.decor == "current" && i % 2 == 0)
                        {
                            var p = map.ToUnity(x, y);
                            streaks.Add(new Streak { x = p.x, z = p.z, minX = x0 / WorldMap.UnitsPerMetre, phase = rnd.Next() * 100, len = rnd.Range(1.4f, 3.6f) });
                        }
                    }
                }
        }

        // ---- signposts, the boat and the zoo --------------------------------------------------------

        void PlaceSignposts()
        {
            foreach (var a in data.areas)
            {
                if (a.label == null) continue;
                // Find dry ground near the label point.
                Vector2 at = new Vector2(a.label.x, a.label.y);
                for (int r = 0; r < 600 && !map.IsWalkable(at.x, at.y); r += 20)
                    at = new Vector2(a.label.x + Mathf.Cos(r) * r, a.label.y + Mathf.Sin(r) * r);
                if (!map.IsWalkable(at.x, at.y)) continue;
                Signpost.Create(transform, map.ToUnity(at.x, at.y), a.name);
            }
        }

        void BuildBoat()
        {
            if (data.boat != null) LakeBoat.Create(transform);
        }

        void UpdateZoo()
        {
            var cfg = data.zoo;
            if (cfg == null || cfg.spots.Count == 0) return;
            long period = (long)(System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() / cfg.interval);
            int spot = (int)(period % cfg.spots.Count);
            if (spot == zooSpot) return;
            zooSpot = spot;
            if (zoo) Destroy(zoo);
            var s = cfg.spots[spot];
            var p = map.ToUnity(s.x, s.y);
            p.y = map.GroundHeight(p.x, p.z);
            zoo = ZooWagon.Create(transform, p);
        }

        // ---- drawing --------------------------------------------------------------------------------

        static Mesh FlatQuad(Color32 c)
        {
            var k = new MeshKit();
            k.Quad(new Vector3(0, 0, -0.5f), new Vector3(0, 0, 0.5f), new Vector3(1, 0, 0.5f), new Vector3(1, 0, -0.5f), c);
            return k.Build("streak");
        }

        void Update()
        {
            UpdateZoo();
            var cam = Camera.main;
            if (!cam || !SystemInfo.supportsInstancing) return;
            var c = cam.transform.position;
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);
            foreach (var b in batches)
            {
                int reach = Mathf.CeilToInt(b.distance / b.chunk);
                int ccx = Mathf.FloorToInt(c.x / b.chunk), ccz = Mathf.FloorToInt(c.z / b.chunk);
                for (int dz = -reach; dz <= reach; dz++)
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        var key = new Vector2Int(ccx + dx, ccz + dz);
                        if (!b.cells.TryGetValue(key, out var list)) continue;
                        var centre = new Vector3((key.x + 0.5f) * b.chunk, c.y, (key.y + 0.5f) * b.chunk);
                        float lim = b.distance + b.chunk * 0.71f;
                        if ((centre - c).sqrMagnitude > lim * lim) continue;
                        var bounds = new Bounds(new Vector3(centre.x, 8, centre.z), new Vector3(b.chunk + 8, 50, b.chunk + 8));
                        if (!GeometryUtility.TestPlanesAABB(planes, bounds)) continue;
                        // Culling bounds must be given, or Unity culls the whole batch.
                        var rp = new RenderParams(b.material)
                        {
                            shadowCastingMode = b.shadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                            receiveShadows = true,
                            worldBounds = bounds,
                        };
                        for (int i = 0; i < list.Count; i += 1023)
                            Graphics.RenderMeshInstanced(rp, b.mesh, 0, list, Mathf.Min(1023, list.Count - i), i);
                    }
            }

            // River current: streaks drifting west, into the lake (4.5 m/s, as in the 2D game).
            streakMatrices.Clear();
            float t = Time.time * 4.5f;
            const float Span = 25.6f;
            foreach (var s in streaks)
            {
                if (Mathf.Abs(s.x - c.x) > 120 || Mathf.Abs(s.z - c.z) > 120) continue;
                float x = s.minX + Mathf.Repeat(s.x - s.minX - t - s.phase, Span);
                var w = map.ToWorld(new Vector3(x, 0, s.z));
                if (map.Sdf(w.x, w.y) > -40) continue;
                streakMatrices.Add(Matrix4x4.TRS(new Vector3(x, 0.12f, s.z), Quaternion.Euler(0, Mathf.Sin(x * 0.5f) * 4, 0), new Vector3(s.len, 1, 0.14f)));
            }
            if (streakMatrices.Count > 0)
            {
                var rp = new RenderParams(streakMaterial) { shadowCastingMode = ShadowCastingMode.Off, worldBounds = new Bounds(c, new Vector3(260, 20, 260)) };
                for (int i = 0; i < streakMatrices.Count; i += 1023)
                    Graphics.RenderMeshInstanced(rp, streakMesh, 0, streakMatrices, Mathf.Min(1023, streakMatrices.Count - i), i);
            }
        }
    }

    /// <summary>A gentle bob and roll for things floating on the water.</summary>
    public class Bobbing : MonoBehaviour
    {
        Vector3 basePos;
        Quaternion baseRot;
        float seed;

        void Start()
        {
            basePos = transform.position;
            baseRot = transform.rotation;
            seed = Random.value * 10;
        }

        void Update()
        {
            float t = Time.time + seed;
            transform.position = basePos + Vector3.up * Mathf.Sin(t * 0.9f) * 0.08f;
            transform.rotation = baseRot * Quaternion.Euler(Mathf.Sin(t * 0.7f) * 1.2f, 0, Mathf.Sin(t * 0.55f) * 1.8f);
        }
    }
}
