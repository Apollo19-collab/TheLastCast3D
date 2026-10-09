// Buying things: what each shop sells, what you can afford, and the purchase.
// The Bait Shop on South Beach sells the regular tackle, bait and the Angler's
// Map; the three travelling tackle shops sell their own stock (as in the web game).

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LastCast
{
    public static class Store
    {
        public const int BulkPacks = 5;
        public const float BulkDiscount = 0.1f;

        /// <summary>Everything a shop sells, cheapest first within each slot.</summary>
        public static List<Item> Stock(Shop shop)
        {
            var slots = new[] { "rod", "reel", "line", "bait" };
            return GameData.I.items
                .Where(it => it.price > 0 && Shopping.ShopFor(it) == shop.id)
                .OrderBy(it => System.Array.IndexOf(slots, it.slot)).ThenBy(it => Shopping.RequiredLevel(it)).ThenBy(it => it.price)
                .ToList();
        }

        public static int PackPrice(Item it, int packs) =>
            packs >= BulkPacks ? Mathf.RoundToInt(it.price * packs * (1 - BulkDiscount)) : it.price * packs;

        /// <summary>Why you can't buy this right now, or null if you can.</summary>
        public static string Blocked(Profile p, Item it, int packs = 1)
        {
            if (!it.IsBait && p.inventory.Contains(it.id)) return "Owned";
            int level = Shopping.RequiredLevel(it);
            if (p.Level < level) return $"Level {level}";
            var need = Shopping.RequiredAchievement(it);
            if (need != null && !p.achievements.ContainsKey(need.id)) return $"Needs \"{need.name}\"";
            int price = it.IsBait ? PackPrice(it, packs) : it.price;
            if (p.coins < price) return $"Need {price - p.coins:N0} more";
            return null;
        }

        public static bool Buy(Profile p, Item it, int packs = 1)
        {
            if (Blocked(p, it, packs) != null) { Sfx.Play("error"); return false; }
            if (it.IsBait)
            {
                int price = PackPrice(it, packs);
                p.coins -= price;
                p.counters.coinsSpent += price;
                int uses = (it.pack ?? 1) * packs;
                p.bait[it.id] = (p.bait.TryGetValue(it.id, out var n) ? n : 0) + uses;
                Game.I.Hud.Feed($"You bought {uses} {it.name} ({p.bait[it.id]} in your bag).", Skin.Green);
            }
            else
            {
                p.coins -= it.price;
                p.counters.coinsSpent += it.price;
                p.inventory.Add(it.id);
                Game.I.Hud.Feed($"You bought the {it.name}!", Skin.Green);
            }
            p.equipped[it.slot] = it.id;
            Achievements.Check(p);
            p.Save();
            Sfx.Play("coin");
            return true;
        }

        public static bool BuyMap(Profile p)
        {
            if (p.worldMap || p.coins < Tuning.MapPrice) { Sfx.Play("error"); return false; }
            p.coins -= Tuning.MapPrice;
            p.counters.coinsSpent += Tuning.MapPrice;
            p.worldMap = true;
            Achievements.Check(p);
            p.Save();
            Sfx.Play("coin");
            Game.I.Hud.Feed("You bought the Angler's Map! Press M to see the whole world.", Skin.Green);
            Game.I.Hud.Toast("map", "New item", "The Angler's Map", "Press M to open it");
            return true;
        }

        // ---- armour (bought anywhere, from your level) ------------------------------------------

        public static string ArmourBlocked(Profile p, ArmourSet set, string slot)
        {
            if (p.armourOwned.Contains($"{set.id}_{slot}")) return "Owned";
            if (p.Level < set.level) return $"Level {set.level}";
            if (p.coins < set.price) return $"Need {set.price - p.coins:N0} more";
            return null;
        }

        public static void BuyArmour(Profile p, ArmourSet set, string slot)
        {
            if (ArmourBlocked(p, set, slot) != null) { Sfx.Play("error"); return; }
            string before = p.Bonuses.set;
            p.coins -= set.price;
            p.counters.coinsSpent += set.price;
            p.armourOwned.Add($"{set.id}_{slot}");
            p.armour[slot] = $"{set.id}_{slot}";
            Game.I.Hud.Feed($"You bought the {set.pieces[slot]}!", Skin.Green);
            SetBonusCheck(p, before);
            Achievements.Check(p);
            p.Save();
            Sfx.Play("coin");
        }

        /// <summary>Put on an owned piece, or take it off if it's already worn.</summary>
        public static void WearArmour(Profile p, string pieceId)
        {
            if (!p.armourOwned.Contains(pieceId)) return;
            var (set, slot) = GameData.I.Piece(pieceId);
            string before = p.Bonuses.set;
            p.armour.TryGetValue(slot, out var worn);
            p.armour[slot] = worn == pieceId ? null : pieceId;
            SetBonusCheck(p, before);
            p.Save();
        }

        static void SetBonusCheck(Profile p, string before)
        {
            var now = p.Bonuses.set;
            if (now == null || now == before) return;
            var set = GameData.I.SetById(now);
            Game.I.Hud.Toast("shield", "Set bonus", $"{set.name}: {set.effect.name}", set.effect.desc);
            Sfx.Play("level");
        }

        /// <summary>One line describing what an item does.</summary>
        public static string StatsLine(Item it)
        {
            var parts = new List<string>();
            if (it.slot == "rod") parts.Add($"Cast {it.range / 10:0.#} m · Power ×{it.power}");
            if (it.slot == "reel") parts.Add($"Reel ×{it.speed}");
            if (it.slot == "line") parts.Add($"Strength ×{it.strength}");
            if (it.drag.HasValue) parts.Add($"Drag ×{it.drag}");
            if (it.bite.HasValue) parts.Add($"Bites ×{it.bite}");
            if (it.rare.HasValue) parts.Add($"Rare odds ×{it.rare}");
            var aff = AffinityText(it.affinity);
            if (aff != "") parts.Add($"Favours {aff}");
            if (it.pack.HasValue) parts.Add($"{it.pack} per pack");
            return string.Join(" · ", parts);
        }

        public static string AffinityText(Dictionary<string, float> affinity)
        {
            if (affinity == null || affinity.Count == 0) return "";
            var data = GameData.I;
            var keys = affinity.Keys.ToList();
            var rarity = data.Fish(keys[0])?.rarity;
            if (rarity != null && keys.Count > 3 && keys.All(k => data.Fish(k)?.rarity == rarity))
                return $"{data.Rarity(rarity).label} fish ×{affinity[keys[0]]}";
            return string.Join(", ", affinity.Select(kv => $"{(data.families.TryGetValue(kv.Key, out var f) ? f : data.Fish(kv.Key)?.name ?? kv.Key)} ×{kv.Value}"));
        }
    }
}
