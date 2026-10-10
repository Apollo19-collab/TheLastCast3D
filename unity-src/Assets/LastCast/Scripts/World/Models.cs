// The Blender models (blender/lastcast_models.py → Resources/Models/<kind>.fbx).
// Each file holds one kind's variants, named <kind>_0, <kind>_1, ... Anything
// missing falls back to the game's own code-built shapes.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LastCast
{
    public static class Models
    {
        static readonly Dictionary<string, Mesh[]> kinds = new Dictionary<string, Mesh[]>();

        /// <summary>All the variants of a kind, in order (empty if there's no model for it).</summary>
        public static Mesh[] Kind(string kind)
        {
            if (kinds.TryGetValue(kind, out var list)) return list;
            list = Resources.LoadAll<Mesh>("Models/" + kind)
                .Where(m => m.name.StartsWith(kind + "_"))
                .OrderBy(m => int.TryParse(m.name.Substring(kind.Length + 1), out var n) ? n : 999)
                .ToArray();
            kinds[kind] = list;
            return list;
        }

        /// <summary>Variant i of a kind (wrapping round), or null.</summary>
        public static Mesh Get(string kind, int i = 0)
        {
            var list = Kind(kind);
            return list.Length == 0 ? null : list[((i % list.Length) + list.Length) % list.Length];
        }

        /// <summary>A model by kind and variant, or the code-built fallback.</summary>
        public static Mesh Or(string kind, int i, System.Func<Mesh> fallback) => Get(kind, i) ?? fallback();

        /// <summary>A model by its exact name within a kind's file (e.g. "pet_dragon" in pet.fbx), or null.</summary>
        public static Mesh Named(string kind, string name)
        {
            if (!named.TryGetValue(kind, out var map))
                named[kind] = map = Resources.LoadAll<Mesh>("Models/" + kind).GroupBy(m => m.name).ToDictionary(g => g.Key, g => g.First());
            return map.TryGetValue(name, out var m) ? m : null;
        }

        static readonly Dictionary<string, Dictionary<string, Mesh>> named = new Dictionary<string, Dictionary<string, Mesh>>();

        /// <summary>
        /// A copy of a model with its colourable regions painted (a shop's awning, a jacket, a hat).
        /// In the Blender models those parts are white with a region code in the alpha channel (254 = the first
        /// colour given, 253 the second...); their shading is kept.
        /// </summary>
        public static Mesh Recolour(Mesh src, params Color32[] regions)
        {
            var m = Object.Instantiate(src);
            m.name = src.name + " (coloured)";
            var cols = m.colors32;
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                int r = 254 - c.a;
                if (c.a == 255 || r < 0 || r >= regions.Length) continue;
                var t = regions[r];
                cols[i] = new Color32((byte)(t.r * c.r / 255), (byte)(t.g * c.g / 255), (byte)(t.b * c.b / 255), 255);
            }
            m.colors32 = cols;
            return m;
        }
    }
}
