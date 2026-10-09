// The world: shoreline, ground, water zones and forests, sampled from the web
// version's map (Resources/World.bytes, see Tools~/export-data.mjs), plus the
// terrain heights the 3D world is built from.
//
// Two coordinate systems:
//   world units (x east, y south, as in the web game; 10 units = 1 m)
//   Unity metres (X east, Z north, Y up; water surface at Y = 0)

using System;
using UnityEngine;

namespace LastCast
{
    public enum Ground : byte { Grass = 0, Sand = 1, Rock = 2, Dirt = 3, Water = 255 }

    public class WorldMap
    {
        public const float UnitsPerMetre = 10f;
        public const float VertexSpacing = 2f; // metres between terrain vertices
        public const float DockHeight = 0.75f; // metres above the water

        public readonly int W, H; // samples
        public readonly float Grid; // world units per sample
        public readonly float Width, Height; // world units
        readonly short[] sdf;
        readonly byte[] ground, zone, forest;
        readonly GameData data;

        // Terrain heights (metres) on a VertexSpacing grid, shared by the mesh and the player.
        public readonly int VW, VH;
        public readonly float[] Heights;

        static WorldMap instance;
        public static WorldMap I => instance ??= new WorldMap();

        WorldMap()
        {
            data = GameData.I;
            var bytes = Resources.Load<TextAsset>("World").bytes;
            if (BitConverter.ToInt32(bytes, 0) != 0x4c435754) throw new Exception("World.bytes: bad header");
            W = BitConverter.ToInt32(bytes, 8);
            H = BitConverter.ToInt32(bytes, 12);
            Grid = BitConverter.ToInt32(bytes, 16);
            Width = data.width;
            Height = data.height;
            int n = W * H, o = 20;
            sdf = new short[n];
            Buffer.BlockCopy(bytes, o, sdf, 0, n * 2);
            o += n * 2;
            ground = new byte[n];
            Buffer.BlockCopy(bytes, o, ground, 0, n);
            o += n;
            zone = new byte[n];
            Buffer.BlockCopy(bytes, o, zone, 0, n);
            o += n;
            forest = new byte[n];
            Buffer.BlockCopy(bytes, o, forest, 0, n);

            VW = Mathf.CeilToInt(Width / UnitsPerMetre / VertexSpacing) + 1;
            VH = Mathf.CeilToInt(Height / UnitsPerMetre / VertexSpacing) + 1;
            Heights = new float[VW * VH];
            for (int j = 0; j < VH; j++)
                for (int i = 0; i < VW; i++)
                {
                    // Vertex (i, j) is at Unity (i, j) * spacing; Z runs north, world y south.
                    float x = i * VertexSpacing * UnitsPerMetre;
                    float y = Height - j * VertexSpacing * UnitsPerMetre;
                    Heights[j * VW + i] = SurfaceHeight(x, y);
                }
        }

        // ---- coordinates -----------------------------------------------------------------------

        public Vector3 ToUnity(float x, float y, float height = 0) => new Vector3(x / UnitsPerMetre, height, (Height - y) / UnitsPerMetre);
        public Vector2 ToWorld(Vector3 p) => new Vector2(p.x * UnitsPerMetre, Height - p.z * UnitsPerMetre);
        public float WidthMetres => Width / UnitsPerMetre;
        public float HeightMetres => Height / UnitsPerMetre;

        // ---- samples -----------------------------------------------------------------------------

        int Index(float x, float y)
        {
            int i = Mathf.Clamp((int)(x / Grid), 0, W - 1);
            int j = Mathf.Clamp((int)(y / Grid), 0, H - 1);
            return j * W + i;
        }

        /// <summary>Distance to the shoreline in world units (+ land, - water), interpolated.</summary>
        public float Sdf(float x, float y)
        {
            float fx = Mathf.Clamp(x / Grid - 0.5f, 0, W - 1.001f);
            float fy = Mathf.Clamp(y / Grid - 0.5f, 0, H - 1.001f);
            int i = (int)fx, j = (int)fy;
            float tx = fx - i, ty = fy - j;
            float a = sdf[j * W + i], b = sdf[j * W + i + 1], c = sdf[(j + 1) * W + i], d = sdf[(j + 1) * W + i + 1];
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        public Ground GroundAt(float x, float y) => (Ground)ground[Index(x, y)];
        public float ForestAt(float x, float y) => forest[Index(x, y)] / 255f;

        public bool InBounds(float x, float y) => x >= 0 && y >= 0 && x < Width && y < Height;
        public bool OnStructure(float x, float y) { foreach (var r in data.structures) if (r.Contains(x, y)) return true; return false; }
        public bool OnSolid(float x, float y) { foreach (var r in data.solid) if (r.Contains(x, y)) return true; return false; }
        public bool OnLand(float x, float y) => Sdf(x, y) >= 0;

        public bool IsWalkable(float x, float y) => InBounds(x, y) && !OnSolid(x, y) && (OnLand(x, y) || OnStructure(x, y));
        public bool IsWater(float x, float y) => InBounds(x, y) && !OnLand(x, y) && !OnStructure(x, y) && !OnSolid(x, y);

        /// <summary>The fishing zone at a point in the water, or null.</summary>
        public Zone ZoneAt(float x, float y)
        {
            if (!IsWater(x, y)) return null;
            int z = zone[Index(x, y)];
            if (z != 255) return data.zones[z];
            // Just off the sampled shoreline: look around for the nearest zone.
            for (int r = 1; r <= 2; r++)
                foreach (var (dx, dy) in new[] { (r, 0), (-r, 0), (0, r), (0, -r) })
                {
                    int k = Index(x + dx * Grid, y + dy * Grid);
                    if (zone[k] != 255) return data.zones[zone[k]];
                }
            return null;
        }

        // ---- heights -----------------------------------------------------------------------------

        /// <summary>Terrain height (metres) at a world point: banks rise gently, hills further inland, lake beds below.</summary>
        public float SurfaceHeight(float x, float y)
        {
            float s = Sdf(x, y);
            if (s >= 0)
            {
                float hills = Mathf.Max(0, Noise.Fbm(x / 1800f + 3, y / 1800f + 11, 3) - 0.38f) * 26f * Noise.SmoothStep(120, 1100, s);
                float bank = 1.4f * Noise.SmoothStep(0, 260, s);
                float bumps = (Noise.Value(x / 70f, y / 70f) - 0.5f) * 0.5f * Noise.SmoothStep(20, 120, s);
                return 0.18f + bank + hills + bumps;
            }
            float d = -s;
            return 0.18f - 0.6f * Noise.SmoothStep(0, 30, d) - 4.2f * Noise.SmoothStep(20, 420, d);
        }

        /// <summary>Ground height (metres) under a Unity position, from the terrain mesh grid.</summary>
        public float GroundHeight(float ux, float uz)
        {
            float fx = Mathf.Clamp(ux / VertexSpacing, 0, VW - 1.001f);
            float fz = Mathf.Clamp(uz / VertexSpacing, 0, VH - 1.001f);
            int i = (int)fx, j = (int)fz;
            float tx = fx - i, tz = fz - j;
            float a = Heights[j * VW + i], b = Heights[j * VW + i + 1], c = Heights[(j + 1) * VW + i], d = Heights[(j + 1) * VW + i + 1];
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }

        /// <summary>Where a person stands at a Unity position: the ground, or a dock's boards.</summary>
        public float StandHeight(Vector3 p)
        {
            var w = ToWorld(p);
            float g = GroundHeight(p.x, p.z);
            return OnStructure(w.x, w.y) ? Mathf.Max(g, DockHeight) : g;
        }
    }
}
