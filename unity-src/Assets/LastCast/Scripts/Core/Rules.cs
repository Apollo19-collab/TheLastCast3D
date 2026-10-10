// Game rules ported from the web version (shared/fish.js, gear.js, levels.js,
// trophies.js and server/fishing.js), so fish, tackle and progression balance
// the same way.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LastCast
{
    public static class Tuning
    {
        public const float MinCastDistance = 50; // world units
        public const float CastFlightTime = 0.6f; // seconds
        public const float BiteWindow = 1.3f;
        public const float ChargePeriod = 1.1f; // seconds for the power meter to go 0 -> 1
        public const float CoinsPerPoint = 1.25f;
        public const float HotspotBiteBoost = 1.8f;
        public const float HotspotScoreBonus = 1.25f;
        public const int MapPrice = 0; // free at the Bait Shop

        public const float WalkSpeed = 4.2f; // metres per second
        public const float SprintSpeed = 1.65f; // x walk
        public const float FishingWalk = 0.4f; // x walk, with a line out
        public const float LineSlack = 1.3f; // walk further than this x your cast range from the bobber and the line comes in

        public static readonly Dictionary<string, float> RarityFight = new Dictionary<string, float>
            { ["junk"] = 0.5f, ["common"] = 1, ["uncommon"] = 1.2f, ["rare"] = 1.45f, ["legendary"] = 1.8f, ["mythic"] = 2.6f };
        public static readonly Dictionary<string, float> HotspotRarityBoost = new Dictionary<string, float>
            { ["junk"] = 0.3f, ["common"] = 1, ["uncommon"] = 1.5f, ["rare"] = 3, ["legendary"] = 4, ["mythic"] = 4 };
        public static readonly string[] RarityOrder = { "junk", "common", "uncommon", "rare", "legendary", "mythic" };

        public static float CastDistance(float power, float range) =>
            MinCastDistance + Mathf.Clamp01(power) * (range - MinCastDistance);
    }

    public struct StrengthTier
    {
        public float max;
        public string label;
        public Color color;
    }

    /// <summary>Fish: how hard they fight, what they score, how heavy they are, which one bites.</summary>
    public static class FishRules
    {
        public static readonly StrengthTier[] Tiers =
        {
            new StrengthTier { max = 0.45f, label = "Light", color = new Color32(0x9a, 0xd1, 0xff, 255) },
            new StrengthTier { max = 0.8f, label = "Steady", color = new Color32(0x7b, 0xd3, 0x89, 255) },
            new StrengthTier { max = 1.15f, label = "Strong", color = new Color32(0xf9, 0xc7, 0x4f, 255) },
            new StrengthTier { max = 1.6f, label = "Powerful", color = new Color32(0xf8, 0x96, 0x1e, 255) },
            new StrengthTier { max = float.PositiveInfinity, label = "Monster", color = new Color32(0xff, 0x4d, 0x4d, 255) },
        };

        public static int TierOf(float difficulty)
        {
            for (int i = 0; i < Tiers.Length; i++) if (difficulty <= Tiers[i].max) return i;
            return Tiers.Length - 1;
        }

        /// <summary>How hard a hooked fish fights: rarity x size x sheer weight.</summary>
        public static float Difficulty(Species s, float kg)
        {
            float sizeFrac = Mathf.Clamp01((kg - s.minKg) / Mathf.Max(0.0001f, s.maxKg - s.minKg));
            float weight = 0.75f + 0.6f * sizeFrac + 0.08f * Mathf.Log(1 + kg, 2);
            return Mathf.Round(s.fight * Tuning.RarityFight[s.rarity] * weight * 100) / 100;
        }

        public static int Score(Species s, float kg, float multiplier = 1)
        {
            float frac = (kg - s.minKg) / Mathf.Max(0.0001f, s.maxKg - s.minKg);
            return Mathf.Max(1, Mathf.RoundToInt(s.points * (0.5f + 1.5f * frac) * multiplier));
        }

        /// <summary>A fish's weight: skewed small, the very top of the roll a giant (see Trophies).</summary>
        public static float RollKg(Species s, float weight = 1) => Trophies.KgFromRoll(s, Mathf.Pow(UnityEngine.Random.value, 2 / weight));

        static float BaitBoost(string rarity, float rareBoost)
        {
            if (rarity == "rare" || rarity == "legendary" || rarity == "mythic") return rareBoost;
            if (rarity == "uncommon") return (1 + rareBoost) / 2;
            if (rarity == "junk") return 1 / rareBoost;
            return 1;
        }

        /// <summary>Which species bites in a zone, given hotspot, tackle, armour and pet (and any extra rare odds).</summary>
        public static Species Pick(Zone zone, bool hotspot, GearStats stats, Bonuses bonus = null, float rareMult = 1)
        {
            var data = GameData.I;
            bonus ??= new Bonuses();
            float rareBoost = stats.rareBoost * bonus.rare * rareMult;
            var entries = new List<(Species s, float w)>();
            foreach (var kv in zone.fish)
            {
                var s = data.Fish(kv.Key);
                if (s == null) continue;
                float special = s.rarity == "legendary" ? bonus.legendary : s.rarity == "mythic" ? bonus.mythic : 1;
                float boost = (hotspot ? Tuning.HotspotRarityBoost[s.rarity] : 1) * BaitBoost(s.rarity, rareBoost) * stats.AffinityFor(s) * bonus.AffinityFor(s) * special;
                entries.Add((s, kv.Value * boost));
            }
            float total = entries.Sum(e => e.w);
            float roll = UnityEngine.Random.value * total;
            foreach (var e in entries)
            {
                roll -= e.w;
                if (roll <= 0) return e.s;
            }
            return entries[entries.Count - 1].s;
        }
    }

    /// <summary>Combined stats for a loadout (rod, reel, line, bait).</summary>
    public class GearStats
    {
        public float castRange, lineStrength, reelSpeed, drag, biteSpeed, rareBoost;
        public Dictionary<string, float> affinity = new Dictionary<string, float>();

        public float AffinityFor(Species s) =>
            (affinity.TryGetValue(s.family, out var a) ? a : 1) * (affinity.TryGetValue(s.id, out var b) ? b : 1);

        public static GearStats Compute(Dictionary<string, string> equipped)
        {
            var data = GameData.I;
            Item Get(string slot)
            {
                var it = equipped.TryGetValue(slot, out var id) ? data.Item(id) : null;
                return it != null && it.slot == slot ? it : data.Item(data.starter[slot]);
            }
            Item rod = Get("rod"), reel = Get("reel"), line = Get("line"), bait = Get("bait");
            var g = new GearStats
            {
                castRange = rod.range ?? 240,
                lineStrength = (rod.power ?? 1) * (line.strength ?? 1),
                reelSpeed = reel.speed ?? 1,
                drag = reel.drag ?? 1,
                biteSpeed = (bait.bite ?? 1) * (line.bite ?? 1) * (rod.bite ?? 1) * (reel.bite ?? 1),
                rareBoost = (bait.rare ?? 1) * (line.rare ?? 1) * (rod.rare ?? 1) * (reel.rare ?? 1),
            };
            foreach (var it in new[] { rod, reel, line, bait })
                foreach (var kv in it.affinity)
                    g.affinity[kv.Key] = (g.affinity.TryGetValue(kv.Key, out var m) ? m : 1) * kv.Value;
            return g;
        }
    }

    /// <summary>Tackle: who sells it and when you can buy it.</summary>
    public static class Shopping
    {
        // In the web game some tackle unlocks with an achievement. This version has
        // no achievements yet, so each of those unlocks at a matching level instead.
        static readonly Dictionary<string, int> UnlockLevels = new Dictionary<string, int>
        {
            ["bait_shop"] = 6, ["explorer"] = 8, ["trout_bum"] = 10, ["toothy"] = 10, ["weed_warrior"] = 10, ["river_rat"] = 10,
            ["dedicated"] = 12, ["deep_diver"] = 12, ["hotspot_hopper"] = 14, ["trophy_hunter"] = 15, ["seasoned"] = 18,
            ["sea_legs"] = 18, ["old_salt"] = 20, ["heavyweight"] = 22, ["collector"] = 25, ["ghost_hunter"] = 30,
            ["living_legend"] = 32, ["lake_legend"] = 40, ["myth_seeker"] = 42, ["monster_hunter"] = 45, ["mythic_hunter"] = 50,
        };

        public static int RequiredLevel(Item it)
        {
            if (it.level.HasValue) return it.level.Value;
            if (it.unlock != null && !Achievements.IsAvailable(it.unlock)) return UnlockLevels.TryGetValue(it.unlock, out var l) ? l : 20;
            return 1;
        }

        /// <summary>The achievement an item unlocks with, if you can earn it in this version.</summary>
        public static Achievement RequiredAchievement(Item it) =>
            it.unlock != null && Achievements.IsAvailable(it.unlock) ? GameData.I.achievements.Find(a => a.id == it.unlock) : null;

        /// <summary>The shop an item is sold at: its travelling tackle shop, or the Bait Shop.</summary>
        public static string ShopFor(Item it) => it.shop ?? "bait";

        public static bool IsFree(Item it) => it.price == 0;
    }

    /// <summary>Angler levels (shared/levels.js).</summary>
    public static class Levels
    {
        public const int Max = 75;
        static readonly int[] Totals = BuildTotals();

        public static int XpToNext(int level) => level >= Max ? int.MaxValue : 400 + 147 * level;

        static int[] BuildTotals()
        {
            var t = new int[Max + 1];
            for (int n = 2; n <= Max; n++) t[n] = t[n - 1] + XpToNext(n - 1);
            return t;
        }

        public static int For(long xp)
        {
            int level = 1;
            while (level < Max && xp >= Totals[level + 1]) level++;
            return level;
        }

        /// <summary>(level, xp into it, xp needed, fraction) for an XP bar.</summary>
        public static (int level, long into, int needed, float fraction) Progress(long xp)
        {
            int level = For(xp);
            if (level >= Max) return (level, 0, 0, 1);
            long into = xp - Totals[level];
            int needed = XpToNext(level);
            return (level, into, needed, (float)into / needed);
        }

        public static int LevelUpCoins(int level) => level * 20;
    }

    /// <summary>Trophy fish and named giants (shared/trophies.js).</summary>
    public static class Trophies
    {
        public const float TrophyFrac = 0.92f;
        public const float GiantRoll = 0.99334f;
        public const float GiantFight = 1.3f;

        public static readonly Dictionary<string, (string label, string icon, Color color, float points)> Tiers =
            new Dictionary<string, (string, string, Color, float)>
            {
                ["trophy"] = ("Trophy", "★", new Color32(0xff, 0xd1, 0x66, 255), 1.5f),
                ["giant"] = ("Giant", "♛", new Color32(0xff, 0x7b, 0x54, 255), 2.5f),
            };

        public static float? TrophyKg(Species s)
        {
            if (s.rarity == "junk") return null;
            return Mathf.Round((s.minKg + (s.maxKg - s.minKg) * TrophyFrac) * 100) / 100;
        }

        /// <summary>"giant", "trophy" or null.</summary>
        public static string Tier(Species s, float kg)
        {
            var t = TrophyKg(s);
            if (t == null) return null;
            if (kg > s.maxKg) return "giant";
            return kg >= t.Value ? "trophy" : null;
        }

        public static float KgFromRoll(Species s, float v)
        {
            float kg;
            if (s.rarity != "junk" && v > GiantRoll)
            {
                float f = (v - GiantRoll) / (1 - GiantRoll);
                kg = s.maxKg * (1.08f + 0.42f * f * f);
            }
            else kg = s.minKg + (s.maxKg - s.minKg) * Mathf.Min(1, v / (s.rarity == "junk" ? 1 : GiantRoll));
            return Mathf.Round(kg * 100) / 100;
        }

        static readonly string[] Titles = { "Old", "Big", "Mighty", "Grand", "Ol'", "Lady", "King", "Queen", "Captain", "Sir", "Mad", "Mother", "Father", "Little", "Granny", "Lord" };
        static readonly string[] Names =
        {
            "Gnasher", "Bertha", "Whiskers", "Goliath", "Silverjaw", "Ironscale", "Mossback", "Thunderfin", "Bruiser", "Gulper",
            "Patches", "Stubbs", "Scarlip", "Bigmouth", "Rusty", "Murk", "Hookjaw", "Duchess", "Barnaby", "Mabel", "Ezekiel",
            "Tusk", "Bramble", "Nessie", "Brutus", "Grumble", "Pebble", "Marlow", "Snag", "Copperbelly", "Ripple", "Dredge",
            "Hollow-eye", "Greybeard", "Splinter", "Cobb", "Hester", "Mudlark", "Fang", "Knuckles", "Ironsides",
        };
        static readonly string[] Epithets =
        {
            "the Unbroken", "the Untamed", "the Line-Breaker", "the Ancient", "the Leviathan", "the Uncatchable", "the Dread",
            "the Magnificent", "the Rod-Snapper", "the Deep One", "the Stormborn", "the Patient", "the Old King", "the Wise",
            "of the Depths", "the Terrible", "the Grey Ghost", "the Last", "the Hungry", "the Mountain",
        };

        /// <summary>A name for a trophy fish; the same inputs always give the same name.</summary>
        public static string Name(string tier, params object[] seedParts)
        {
            var parts = new List<object> { "trophy name", tier };
            parts.AddRange(seedParts);
            var rnd = new SeededRandom(Noise.SeedFrom(parts.ToArray()));
            string Pick(string[] list) => list[(int)Math.Floor(rnd.Next() * list.Length)];
            string name = Pick(Names);
            if (tier == "giant") return $"{name} {Pick(Epithets)}";
            return rnd.Next() < 0.55f ? $"{Pick(Titles)} {name}" : name;
        }
    }
}
