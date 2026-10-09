// The angler's progress: coins, XP, tackle, Fish Index, trophies and history.
// Saved as JSON in Application.persistentDataPath/profile.json.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace LastCast
{
    public class IndexEntry
    {
        public int count;
        public float bestKg;
        public int trophies;
        public long firstAt;
    }

    public class TrophyEntry
    {
        public string species, name, tier, zone;
        public float kg;
        public long at;
    }

    public class CatchEntry
    {
        public string species, zone;
        public float kg;
        public int points;
        public bool hotspot;
        public long at;
    }

    /// <summary>Lifetime stats that achievements measure (as profile.counters in the web game).</summary>
    public class Counters
    {
        public long catches, coinsEarned, coinsSpent, hotspotCatches, legendaryCatches, mythicCatches, bigFish, snaps, mapEvents, trophies, giants;
        public long voyages, seaCatches, eventCatches, bossKills;
        public float heaviest;
        public Dictionary<string, long> family = new Dictionary<string, long>();
        public Dictionary<string, long> zone = new Dictionary<string, long>();
        public Dictionary<string, long> area = new Dictionary<string, long>();

        public static void Bump(Dictionary<string, long> d, string key) => d[key] = (d.TryGetValue(key, out var n) ? n : 0) + 1;
    }

    public class Profile
    {
        public const int HistoryLimit = 50;
        public const int CabinetLimit = 100;

        public int version = 1;
        public long coins;
        public long score;
        public int catches;
        public long xp;
        public bool worldMap;
        public List<string> inventory = new List<string>();
        public Dictionary<string, int> bait = new Dictionary<string, int>();
        public Dictionary<string, string> equipped = new Dictionary<string, string>();
        public Dictionary<string, IndexEntry> index = new Dictionary<string, IndexEntry>();
        public List<TrophyEntry> trophies = new List<TrophyEntry>();
        public List<CatchEntry> history = new List<CatchEntry>();
        public int snaps, giants, trophyCount;
        public float heaviest;
        public float[] position; // Unity metres, where you last stood

        public List<string> pets = new List<string>(); // pets you've adopted
        public string pet; // the one with you
        public List<string> armourOwned = new List<string>(); // armour piece ids ("clover_head")
        public Dictionary<string, string> armour = new Dictionary<string, string>(); // slot -> piece id
        public Dictionary<string, long> achievements = new Dictionary<string, long>(); // id -> when earned
        public int chum; // chum buckets in your bag
        public Counters counters;

        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        [JsonIgnore] public int Level => Levels.For(xp);

        static string FilePath => Path.Combine(Application.persistentDataPath, "profile.json");

        public static Profile LoadOrCreate()
        {
            Profile p = null;
            try
            {
                if (File.Exists(FilePath)) p = JsonConvert.DeserializeObject<Profile>(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not read the save file, starting fresh: {e.Message}");
                File.Copy(FilePath, FilePath + ".broken", true);
            }
            p ??= new Profile();
            p.Normalize();
            return p;
        }

        /// <summary>Starter tackle, and nothing equipped that you no longer have.</summary>
        void Normalize()
        {
            var data = GameData.I;
            foreach (var kv in data.starter)
            {
                if (!data.Item(kv.Value).IsBait && !inventory.Contains(kv.Value)) inventory.Add(kv.Value);
                if (!equipped.TryGetValue(kv.Key, out var eq) || !Has(eq)) equipped[kv.Key] = kv.Value;
            }
            inventory.RemoveAll(id => data.Item(id) == null);
            pets.RemoveAll(id => data.PetById(id) == null);
            if (!pets.Contains(pet)) pet = null;
            armourOwned.RemoveAll(id => data.Piece(id).set == null);
            foreach (var slot in data.armourSlots)
                if (armour.TryGetValue(slot, out var worn) && (worn == null || !armourOwned.Contains(worn) || data.Piece(worn).slot != slot)) armour[slot] = null;
            if (counters == null) BackfillCounters();
        }

        /// <summary>Saves from before achievements existed: rebuild what we can of the counters.</summary>
        void BackfillCounters()
        {
            var data = GameData.I;
            counters = new Counters { catches = catches, coinsEarned = score, snaps = snaps, heaviest = heaviest, trophies = trophyCount, giants = giants };
            foreach (var kv in index)
            {
                var s = data.Fish(kv.Key);
                if (s == null) continue;
                counters.family[s.family] = (counters.family.TryGetValue(s.family, out var n) ? n : 0) + kv.Value.count;
                if (s.rarity == "legendary") counters.legendaryCatches += kv.Value.count;
                if (s.rarity == "mythic") counters.mythicCatches += kv.Value.count;
                if (kv.Value.bestKg >= 10) counters.bigFish++;
            }
            foreach (var h in history) if (h.hotspot) counters.hotspotCatches++;
        }

        // ---- pets and armour ------------------------------------------------------------------------

        [JsonIgnore] public Bonuses Bonuses => Bonuses.Compute(this);

        public void Save()
        {
            if (AutoShot.Active || NetTest.Active) return; // test runs leave the save alone
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                Debug.LogError($"Saving failed: {e.Message}");
            }
        }

        // ---- tackle ------------------------------------------------------------------------------

        public bool IsStarter(string id) => GameData.I.starter.ContainsValue(id);

        /// <summary>Whether you own an item (or have some of a bait; bread is free and endless).</summary>
        public bool Has(string id)
        {
            var it = GameData.I.Item(id);
            if (it == null) return false;
            if (it.IsBait) return IsStarter(id) || BaitCount(id) > 0;
            return inventory.Contains(id);
        }

        public int BaitCount(string id) => IsStarter(id) ? int.MaxValue : bait.TryGetValue(id, out var n) ? n : 0;

        [JsonIgnore] public GearStats Stats => GearStats.Compute(equipped);

        /// <summary>One bait is used each time a fish bites. Returns a message if you just ran out.</summary>
        public string UseBait()
        {
            var id = equipped["bait"];
            if (IsStarter(id)) return null;
            bait[id] = BaitCount(id) - 1;
            if (bait[id] > 0) return null;
            bait.Remove(id);
            equipped["bait"] = GameData.I.starter["bait"];
            return $"You're out of {GameData.I.Item(id).name}! Back to bread crumbs.";
        }
    }
}
