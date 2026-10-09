// Chum buckets (shared/chum.js): buy them at the Bait Shop, put one down with C.
// While it's out, bobbers near it bite 15% faster, and fish you land close to
// it can be chopped into bait for your bag (rarer fish give better bait). It
// lasts 10 minutes or 30 fish.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LastCast
{
    public class Chum : MonoBehaviour
    {
        public bool Out { get; private set; }
        public Vector2 Pos { get; private set; } // world units
        public float Life { get; private set; }
        public int Fish { get; private set; }
        GameObject bucket;
        // Other players' buckets: everyone near any bucket bites faster.
        readonly Dictionary<ulong, (Vector2 pos, float life, GameObject model)> remote = new Dictionary<ulong, (Vector2, float, GameObject)>();

        ChumConfig Cfg => GameData.I.chum;

        public void Place()
        {
            var g = Game.I;
            var p = g.Profile;
            string message = null;
            var at = g.Player.WorldPos;
            if (p.chum <= 0) message = "You have no chum buckets. Buy them at the Bait Shop.";
            else if (Out) message = "You already have a chum bucket out.";
            else if (!WorldMap.I.IsWalkable(at.x, at.y)) message = "You can't put a bucket down here.";
            if (message != null) { g.Flash(message, 3); Sfx.Play("error"); return; }
            p.chum--;
            p.Save();
            Out = true;
            Pos = at;
            Life = Cfg.duration;
            Fish = 0;
            bucket = BucketModel(WorldMap.I.ToUnity(at.x, at.y, WorldMap.I.StandHeight(g.Player.transform.position)));
            g.Hud.Feed($"Chum bucket down! Land fish near it to turn them into bait. Lasts {Cfg.duration / 60:0} minutes or {Cfg.maxFish} fish.", Skin.Hex("#e07a5f"));
            if (Multiplayer.I.Online) Multiplayer.I.SendReliable("chum", new JObject { ["x"] = at.x, ["y"] = at.y, ["life"] = Cfg.duration });
            Sfx.Play("plop");
        }

        public bool Buy(int count)
        {
            var p = Game.I.Profile;
            int price = Cfg.price * count;
            if (p.chum + count > Cfg.maxOwned) { Game.I.Flash($"You can carry at most {Cfg.maxOwned} chum buckets.", 3); Sfx.Play("error"); return false; }
            if (p.coins < price) { Sfx.Play("error"); return false; }
            p.coins -= price;
            p.counters.coinsSpent += price;
            p.chum += count;
            Achievements.Check(p);
            p.Save();
            Sfx.Play("coin");
            Game.I.Hud.Feed($"You bought {count} chum bucket{(count > 1 ? "s" : "")}. Press C to put one down.", Skin.Green);
            return true;
        }

        /// <summary>Bobbers near the bucket bite faster.</summary>
        public float BiteBonus(float x, float y)
        {
            var p = new Vector2(x, y);
            if (Out && Vector2.Distance(Pos, p) <= Cfg.attractRadius) return Cfg.biteBonus;
            foreach (var r in remote.Values) if (Vector2.Distance(r.pos, p) <= Cfg.attractRadius) return Cfg.biteBonus;
            return 1;
        }

        public void AddRemote(ulong who, float x, float y, float life)
        {
            RemoveRemote(who);
            var map = WorldMap.I;
            var at = map.ToUnity(x, y);
            at.y = map.StandHeight(at);
            remote[who] = (new Vector2(x, y), life, BucketModel(at));
        }

        public void RemoveRemote(ulong who)
        {
            if (remote.TryGetValue(who, out var r) && r.model) Destroy(r.model);
            remote.Remove(who);
        }

        public void ClearRemote()
        {
            foreach (var who in remote.Keys.ToList()) RemoveRemote(who);
        }

        /// <summary>You landed a fish: if you're near your bucket it may become bait.</summary>
        public void OnCatch(string rarity)
        {
            if (!Out || Vector2.Distance(Pos, Game.I.Player.WorldPos) > Cfg.radius) return;
            Fish++;
            var p = Game.I.Profile;
            if (Cfg.table.TryGetValue(rarity, out var row) && Random.value < row.chance)
            {
                // Only bait you could buy (unlocked) comes out of the bucket.
                var options = row.bait.Where(kv => GameData.I.Item(kv.Key) is Item it && Unlocked(p, it)).ToList();
                if (options.Count > 0)
                {
                    float total = options.Sum(kv => kv.Value);
                    float roll = Random.value * total;
                    string bait = options[options.Count - 1].Key;
                    foreach (var kv in options) { roll -= kv.Value; if (roll <= 0) { bait = kv.Key; break; } }
                    int uses = Random.Range(row.uses[0], row.uses[1] + 1);
                    p.bait[bait] = (p.bait.TryGetValue(bait, out var n) ? n : 0) + uses;
                    Game.I.Hud.Feed($"Chummed! You got {uses} {GameData.I.Item(bait).name}.", Skin.Hex("#e07a5f"));
                }
            }
            if (Fish >= Cfg.maxFish) Remove("It's full");
        }

        static bool Unlocked(Profile p, Item it)
        {
            var need = Shopping.RequiredAchievement(it);
            return p.Level >= Shopping.RequiredLevel(it) && (need == null || p.achievements.ContainsKey(need.id));
        }

        void Remove(string why)
        {
            Out = false;
            if (bucket) Destroy(bucket);
            if (Multiplayer.I.Online) Multiplayer.I.SendReliable("chumgone", new JObject());
            Game.I.Hud.Feed($"{why}: your chum bucket is used up after {Fish} fish.", Skin.Hex("#e07a5f"));
        }

        void Update()
        {
            foreach (var who in remote.Keys.ToList())
            {
                var r = remote[who];
                r.life -= Time.deltaTime;
                if (r.life <= 0) RemoveRemote(who);
                else remote[who] = r;
            }
            if (!Out) return;
            Life -= Time.deltaTime;
            if (Life <= 0) Remove("Time's up");
        }

        static GameObject BucketModel(Vector3 at)
        {
            var go = new GameObject("Chum bucket");
            go.transform.position = at;
            go.AddComponent<MeshFilter>().sharedMesh = Models.Or("bucket", 0, () =>
            {
                var k = new MeshKit();
                k.Cone(Vector3.zero, 0.22f, 0.4f, 10, MeshKit.Hex("#6c757d"), 0.26f);
                k.Cone(new Vector3(0, 0.36f, 0), 0.25f, 0.02f, 10, MeshKit.Hex("#9d0208"), 0.25f); // the chum
                k.Box(new Vector3(0, 0.55f, 0), new Vector3(0.5f, 0.03f, 0.03f), MeshKit.Hex("#343a40")); // handle
                return k.Build("chum bucket");
            });
            go.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            return go;
        }
    }
}
