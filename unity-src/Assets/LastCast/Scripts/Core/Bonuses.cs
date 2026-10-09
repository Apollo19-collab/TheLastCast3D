// What your armour and your pet add to your fishing, ported from the web
// game's computeArmour (shared/armour.js) and combineBonuses (shared/pets.js).
// Armour pieces each give a small perk; wear all four of one set for its set
// effect. Your pet adds its one ability on top.

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public class Bonuses
    {
        public float coins = 1, xp = 1, bite = 1, rare = 1, tension = 1, reel = 1, weight = 1;
        public float @double, snapSave, sea = 1, legendary = 1, mythic = 1;
        public List<(string[] zones, float mult)> zoneBites = new List<(string[], float)>();
        public string set; // the full set you're wearing, if any
        public int pieces;
        // From your pet.
        public float fight = 1, hotspot = 1, baitSave, rescue, biteWindow, castRange, hold = 1;
        public Dictionary<string, float> affinity;
        public PetFind find;
        public string pet;

        /// <summary>Bites in this zone go this much faster (Reedwalker armour, some pets).</summary>
        public float ZoneBite(Zone z)
        {
            float m = 1;
            foreach (var (zones, mult) in zoneBites)
                if (System.Array.IndexOf(zones, z.kind ?? z.id) >= 0 || System.Array.IndexOf(zones, z.id) >= 0) m *= mult;
            return m;
        }

        public float AffinityFor(Species s) =>
            affinity == null ? 1 : (affinity.TryGetValue(s.family, out var a) ? a : 1) * (affinity.TryGetValue(s.id, out var b) ? b : 1);

        public static Bonuses Compute(Profile p)
        {
            var data = GameData.I;
            var b = new Bonuses();
            // Armour: add up the perks, and find a full set.
            float coins = 0, xp = 0, bite = 0, rare = 0, tension = 0, reel = 0, weight = 0;
            var sets = new Dictionary<string, int>();
            foreach (var slot in data.armourSlots)
            {
                if (!p.armour.TryGetValue(slot, out var id) || id == null) continue;
                var (set, pieceSlot) = data.Piece(id);
                if (set == null || pieceSlot != slot) continue;
                b.pieces++;
                sets[set.id] = (sets.TryGetValue(set.id, out var n) ? n : 0) + 1;
                foreach (var kv in set.perk)
                    switch (kv.Key)
                    {
                        case "coins": coins += kv.Value; break;
                        case "xp": xp += kv.Value; break;
                        case "bite": bite += kv.Value; break;
                        case "rare": rare += kv.Value; break;
                        case "tension": tension += kv.Value; break;
                        case "reel": reel += kv.Value; break;
                        case "weight": weight += kv.Value; break;
                    }
            }
            foreach (var kv in sets) if (kv.Value == data.armourSlots.Length) b.set = kv.Key;
            var e = b.set != null ? data.SetById(b.set).effect : new SetEffect();
            b.coins = 1 + coins + (e.coins ?? 0);
            b.xp = 1 + xp + (e.xp ?? 0);
            b.bite = 1 + bite + (e.bite ?? 0);
            b.rare = 1 + rare + (e.rare ?? 0);
            b.tension = 1 / (1 + tension + (e.tension ?? 0)); // multiplier on how fast tension builds
            b.reel = 1 + reel + (e.reel ?? 0);
            b.weight = 1 + weight + (e.weight ?? 0);
            b.@double = e.@double ?? 0;
            b.snapSave = e.snapSave ?? 0;
            b.sea = 1 + (e.sea ?? 0);
            b.legendary = 1 + (e.legendary ?? 0);
            b.mythic = 1 + (e.mythic ?? 0);
            if (e.zoneBite.HasValue) b.zoneBites.Add((e.zones, 1 + e.zoneBite.Value));

            // The pet's ability on top.
            var pet = data.PetById(p.pet);
            if (pet == null) return b;
            var fx = pet.fx;
            b.pet = pet.id;
            b.coins *= fx.coins ?? 1;
            b.xp *= fx.xp ?? 1;
            b.bite *= fx.bite ?? 1;
            b.rare *= fx.rare ?? 1;
            b.legendary *= fx.legendary ?? 1;
            b.mythic *= fx.mythic ?? 1;
            b.tension *= fx.tension ?? 1;
            b.reel *= fx.reel ?? 1;
            b.sea *= fx.sea ?? 1;
            b.@double = Mathf.Min(0.6f, b.@double + (fx.@double ?? 0));
            b.snapSave = Mathf.Min(0.6f, b.snapSave + (fx.snapSave ?? 0));
            if (fx.zone != null) b.zoneBites.Add((fx.zone.zones, fx.zone.bite));
            b.fight = fx.fight ?? 1;
            b.hotspot = fx.hotspot ?? 1;
            b.affinity = fx.affinity;
            b.baitSave = fx.baitSave ?? 0;
            b.rescue = fx.rescue ?? 0;
            b.find = fx.find;
            b.biteWindow = fx.biteWindow ?? 0;
            b.castRange = fx.castRange ?? 0;
            b.hold = fx.hold ?? 1;
            return b;
        }
    }
}
