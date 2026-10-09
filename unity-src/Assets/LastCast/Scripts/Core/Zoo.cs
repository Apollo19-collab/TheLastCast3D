// The Travelling Zoo (shared/pets.js zooAt): it parks at one of six spots
// around the lake and brings three new animals every 15 minutes. It follows
// the clock with the same seeds as the web game, so both games' zoos match.
//
// Walk up to the wagon and press E to adopt. Own as many pets as you like;
// one comes with you at a time.

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LastCast
{
    public static class Zoo
    {
        static readonly string[] RarityOrder = { "common", "uncommon", "rare", "epic", "legendary", "mythic" };

        public class Stock
        {
            public long period;
            public List<string> pets = new List<string>();
            public ZooSpot spot;
            public double left; // seconds until it moves on
        }

        /// <summary>A Lehmer generator, as seededRng in pets.js.</summary>
        static System.Func<double> Rng(long seed)
        {
            long s = seed % 2147483646 + 1;
            return () =>
            {
                s = s * 16807 % 2147483647;
                return (s - 1) / 2147483646.0;
            };
        }

        public static Stock At(double nowSec)
        {
            var data = GameData.I;
            var cfg = data.zoo;
            long period = (long)System.Math.Floor(nowSec / cfg.interval);
            var rng = Rng(period * 7919 + 13);
            rng();
            var stock = new Stock { period = period };
            float total = RarityOrder.Sum(r => data.petRarities[r].weight);
            while (stock.pets.Count < cfg.stockSize)
            {
                double roll = rng() * total;
                string rarity = "common";
                foreach (var r in RarityOrder)
                {
                    roll -= data.petRarities[r].weight;
                    if (roll <= 0) { rarity = r; break; }
                }
                var options = data.pets.Where(p => p.rarity == rarity && !stock.pets.Contains(p.id)).ToList();
                stock.pets.Add(options[(int)System.Math.Floor(rng() * options.Count)].id);
            }
            stock.spot = cfg.spots[(int)(period % cfg.spots.Count)];
            stock.left = (period + 1) * cfg.interval - nowSec;
            return stock;
        }

        public static Stock Now => At(System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

        public static int Price(Pet p) => GameData.I.petRarities[p.rarity].price;

        /// <summary>Whether you're standing at the wagon.</summary>
        public static bool AtWagon(Vector2 worldPos)
        {
            var z = Now;
            return Vector2.Distance(worldPos, new Vector2(z.spot.x, z.spot.y)) <= GameData.I.zoo.range;
        }

        public static string Blocked(Profile p, Pet pet)
        {
            if (p.pets.Contains(pet.id)) return "Adopted";
            if (!Now.pets.Contains(pet.id)) return "Not in stock";
            int price = Price(pet);
            if (p.coins < price) return $"Need {price - p.coins:N0} more";
            return null;
        }

        public static bool Adopt(Profile p, Pet pet)
        {
            if (Blocked(p, pet) != null) { Sfx.Play("error"); return false; }
            int price = Price(pet);
            p.coins -= price;
            p.counters.coinsSpent += price;
            p.pets.Add(pet.id);
            p.pet = pet.id;
            Achievements.Check(p);
            p.Save();
            Sfx.Play("level");
            Game.I.Hud.Toast("paw", "New pet", pet.name, $"{pet.ability}: {pet.desc}");
            if (Multiplayer.I.Online) Multiplayer.I.SendReliable("pet", new Newtonsoft.Json.Linq.JObject { ["name"] = pet.name });
            Game.I.Hud.Feed($"You adopted a {pet.name}! {pet.ability}: {pet.desc}", Skin.Hex(GameData.I.petRarities[pet.rarity].color));
            return true;
        }

        /// <summary>Take a pet along, or send it home if it's already with you.</summary>
        public static void Choose(Profile p, string petId)
        {
            if (!p.pets.Contains(petId)) return;
            p.pet = p.pet == petId ? null : petId;
            p.Save();
        }
    }
}
