// Achievements (shared/achievements.js): long-term goals that pay coins, and
// some unlock tackle. Progress comes from the profile's lifetime counters.
//
// A few goals need things this version doesn't have yet (duels and the Hall of
// Records). Those are left out, and tackle they unlock opens at a matching
// level instead (Shopping.UnlockLevels).

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LastCast
{
    public static class Achievements
    {
        static readonly HashSet<string> MissingCounters = new HashSet<string>
            { "duels", "duelsWon", "recordsSet", "weeklyWins" };

        static List<Achievement> available;

        /// <summary>The achievements you can earn in this version.</summary>
        public static List<Achievement> Available => available ??= GameData.I.achievements.Where(CanEarn).ToList();

        public static bool IsAvailable(string id) => Available.Any(a => a.id == id);

        static IEnumerable<string> Paths(JToken metric) =>
            metric.Type == JTokenType.String ? new[] { metric.Value<string>() }
            : (metric["sum"] ?? metric["min"]).Values<string>();

        static bool CanEarn(Achievement a)
        {
            var data = GameData.I;
            var sea = new HashSet<string>(data.voyage.SeaSpecies());
            var catchable = data.species.Where(s => sea.Contains(s.id) || data.zones.Any(z => z.fish.ContainsKey(s.id))).ToList();
            foreach (var path in Paths(a.metric))
            {
                var parts = path.Split('.');
                if (MissingCounters.Contains(parts[0])) return false;
                if (parts[0] == "index" && !catchable.Any(s => s.id == parts[1])) return false;
                if (path == "species" && a.goal > catchable.Count) return false;
                if (parts[0] == "rarity" && parts[1] != "mythicPet" && a.goal > catchable.Count(s => s.rarity == parts[1])) return false;
                if (parts[0] == "zone" && !data.zones.Any(z => (z.kind ?? z.id) == parts[1])) return false;
            }
            return true;
        }

        static long Read(Profile p, string path)
        {
            var data = GameData.I;
            var c = p.counters;
            if (path == "species") return p.index.Count;
            if (path == "score") return p.score;
            if (path == "level") return p.Level;
            if (path == "pets") return p.pets.Count;
            if (path == "trophySpecies") return p.index.Values.Count(e => e.trophies > 0);
            if (path == "rarity.mythicPet") return p.pets.Count(id => data.PetById(id)?.rarity == "mythic");
            var parts = path.Split('.');
            if (parts[0] == "index") return p.index.TryGetValue(parts[1], out var e) ? e.count : 0;
            if (parts[0] == "rarity") return p.index.Keys.Count(id => data.Fish(id)?.rarity == parts[1]);
            if (parts.Length == 2)
            {
                var group = parts[0] switch { "family" => c.family, "zone" => c.zone, "area" => c.area, _ => null };
                return group != null && group.TryGetValue(parts[1], out var n) ? n : 0;
            }
            return path switch
            {
                "catches" => c.catches,
                "coinsEarned" => c.coinsEarned,
                "coinsSpent" => c.coinsSpent,
                "hotspotCatches" => c.hotspotCatches,
                "legendaryCatches" => c.legendaryCatches,
                "mythicCatches" => c.mythicCatches,
                "bigFish" => c.bigFish,
                "heaviest" => Mathf.FloorToInt(c.heaviest),
                "snaps" => c.snaps,
                "mapEvents" => c.mapEvents,
                "trophies" => c.trophies,
                "giants" => c.giants,
                "voyages" => c.voyages,
                "seaCatches" => c.seaCatches,
                "eventCatches" => c.eventCatches,
                "bossKills" => c.bossKills,
                _ => 0,
            };
        }

        public static long Value(Profile p, Achievement a)
        {
            var m = a.metric;
            if (m.Type == JTokenType.String) return Read(p, m.Value<string>());
            if (m["sum"] != null) return m["sum"].Values<string>().Sum(path => Read(p, path));
            if (m["min"] != null) return m["min"].Values<string>().Min(path => Read(p, path));
            return 0;
        }

        public static (long value, float goal, bool done, float fraction) Progress(Profile p, Achievement a)
        {
            long v = Value(p, a);
            bool done = p.achievements.ContainsKey(a.id) || v >= a.goal;
            return (System.Math.Min(v, (long)a.goal), a.goal, done, Mathf.Clamp01(v / a.goal));
        }

        /// <summary>Award anything newly reached: coins, a toast, and any tackle it unlocks.</summary>
        public static void Check(Profile p)
        {
            foreach (var a in Available)
            {
                if (p.achievements.ContainsKey(a.id) || Value(p, a) < a.goal) continue;
                p.achievements[a.id] = Profile.Now;
                p.coins += a.coins;
                p.counters.coinsEarned += a.coins;
                var unlocks = GameData.I.items.Where(it => it.unlock == a.id).Select(it => it.name).ToList();
                Game.I?.Hud?.Toast("medal", "Achievement unlocked", a.name, $"+{a.coins:N0} coins" + (unlocks.Count > 0 ? $" · Unlocked: {string.Join(", ", unlocks)}" : ""));
                Game.I?.Hud?.Feed($"You earned the \"{a.name}\" achievement! +{a.coins:N0} coins", Skin.Gold);
                if (Multiplayer.I != null && Multiplayer.I.Online) Multiplayer.I.SendReliable("achievement", new Newtonsoft.Json.Linq.JObject { ["name"] = a.name });
                Sfx.Play("level");
            }
        }
    }
}
