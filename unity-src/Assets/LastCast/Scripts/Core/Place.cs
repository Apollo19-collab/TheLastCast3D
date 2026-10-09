// Where you are: on the lake, or out at sea on the trawler's deck during a
// voyage. Walking, standing heights and pets ask here rather than the lake's
// map directly. Also the trees' and boulders' trunks, which you can't walk
// through.

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public static class Place
    {
        public static bool AtSea => Voyage.I != null && Voyage.I.AtSea;

        /// <summary>Is this Unity position out at sea (rather than on the lake)? The sea is far off to the west.</summary>
        public static bool IsSea(Vector3 p) => p.x < SeaWorld.Origin.x + 1000;

        public static bool IsWalkable(Vector3 p)
        {
            if (IsSea(p)) return SeaWorld.IsWalkable(p);
            var map = WorldMap.I;
            var w = map.ToWorld(p);
            return map.IsWalkable(w.x, w.y) && !Obstacles.Blocked(p);
        }

        /// <summary>Where a person (or a walking pet) stands: the ground, a dock, or the trawler's deck (the sea itself out there).</summary>
        public static float StandHeight(Vector3 p)
        {
            if (!IsSea(p)) return WorldMap.I.StandHeight(p);
            return SeaWorld.IsWalkable(p) ? SeaWorld.DeckY : 0;
        }
    }

    /// <summary>Tree trunks and boulders, in a grid of 8 m cells (Unity space).</summary>
    public static class Obstacles
    {
        const float Cell = 8;
        static readonly Dictionary<Vector2Int, List<Vector3>> cells = new Dictionary<Vector2Int, List<Vector3>>(); // x, z, radius

        static Vector2Int Key(float x, float z) => new Vector2Int(Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell));

        public static void Add(Vector3 at, float radius)
        {
            var k = Key(at.x, at.z);
            if (!cells.TryGetValue(k, out var list)) cells[k] = list = new List<Vector3>();
            list.Add(new Vector3(at.x, at.z, radius));
        }

        /// <summary>Would a person (0.3 m across the shoulders from their centre) at p be inside a trunk or boulder?</summary>
        public static bool Blocked(Vector3 p, float body = 0.3f)
        {
            var k = Key(p.x, p.z);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!cells.TryGetValue(new Vector2Int(k.x + dx, k.y + dz), out var list)) continue;
                    foreach (var o in list)
                    {
                        float r = o.z + body, ex = p.x - o.x, ez = p.z - o.y;
                        if (ex * ex + ez * ez < r * r) return true;
                    }
                }
            return false;
        }
    }
}
