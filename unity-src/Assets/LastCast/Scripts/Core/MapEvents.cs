// Map events (shared/events.js, server/events.js): every 10 minutes something
// happens at one of the lakes or ponds, on the same clock and seeds as the web
// game. Three kinds take turns:
//
//   Feeding Shoal  bites much faster and rarer fish; a bonus for every fish at the end
//   Great Haul     land the goal in time for a big reward (16 fish on your own)
//   Golden Tide    fill the golden meter with points to start a Golden Rush of double points
//
// Goals are sized for everyone online (just you, when you're on your own), and
// everyone who helps is paid at the end, as in the web game.

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public class MapEvent
    {
        public long id;
        public string type;
        public double start, end; // seconds since the epoch
        public EventPlace place;
        public float radius;
        public MapEventType T => GameData.I.events.types[type];
        public bool Contains(float x, float y) => Vector2.Distance(new Vector2(place.x, place.y), new Vector2(x, y)) <= radius;
    }

    public class EventMods { public float bite = 1, rare = 1, points = 1; }

    public class MapEvents : MonoBehaviour
    {
        static readonly string[] Types = { "shoal", "haul", "tide" };

        public MapEvent Active { get; private set; }
        public float Progress { get; private set; }
        public float Goal { get; private set; }
        public float Rush { get; private set; }
        public int Rushes { get; private set; }
        public int Fish { get; private set; }
        long lastId = -1;
        GameObject marker;

        static EventsConfig Cfg => GameData.I.events;
        public static double Now => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        // ---- the schedule ---------------------------------------------------------------------------

        public static MapEvent ForSlot(long slot)
        {
            var type = Types[((slot % 3) + 3) % 3];
            var rnd = new SeededRandom(Noise.SeedFrom("map event place", slot));
            var place = Cfg.places[(int)System.Math.Floor(rnd.Next() * Cfg.places.Count)];
            double start = slot * Cfg.slot + Cfg.lead;
            return new MapEvent { id = slot, type = type, start = start, end = start + Cfg.types[type].duration, place = place, radius = Cfg.types[type].radius };
        }

        /// <summary>The event on now (or null), and the next few.</summary>
        public static (MapEvent current, List<MapEvent> upcoming) Around(double now, int count = 6)
        {
            long slot = (long)System.Math.Floor(now / Cfg.slot);
            var here = ForSlot(slot);
            var current = now >= here.start && now < here.end ? here : null;
            var upcoming = new List<MapEvent>();
            for (long s = here.start > now ? slot : slot + 1; upcoming.Count < count; s++) upcoming.Add(ForSlot(s));
            return (current, upcoming);
        }

        /// <summary>What an event is about, for one angler (the web game's text talks about groups).</summary>
        public static string SoloDesc(string type)
        {
            var t = Cfg.tuning;
            return type switch
            {
                "shoal" => $"A huge shoal is feeding: bites come {Mathf.RoundToInt((t.shoal.bite - 1) * 100)}% faster and rare fish are more likely. Every fish you land in it earns {t.shoal.coinsPerFish:0} coins at the end.",
                "haul" => $"Land {t.haul.goalBase + t.haul.goalPerAngler:0} fish in the ring before time runs out for {t.haul.coins:0} coins, {t.haul.coinsPerFish:0} more a fish and {t.haul.xp:0} XP.",
                _ => $"Every catch in the ring fills the golden meter ({t.tide.meterBase + t.tide.meterPerAngler:0} points). Fill it for a {t.tide.rush:0}-second Golden Rush: double points and better odds.",
            };
        }

        // ---- the live event ------------------------------------------------------------------------
        //
        // On your own, or as the host, this machine runs the event: it keeps the shared progress,
        // who helped and how much, and pays everyone at the end. Players who joined a hosted game
        // send their catches to the host and show what the host sends back.

        readonly Dictionary<ulong, int> helpers = new Dictionary<ulong, int>(); // player -> fish landed in the event
        public int AnglersIn { get; private set; } = 1; // lines in the Feeding Shoal right now
        public int Helpers => helpers.Count;
        float sendTimer;

        static Multiplayer Net => Multiplayer.I;
        static bool Following => Net != null && Net.IsClient; // the host runs the event
        static ulong Me => Net != null && Net.Online ? Net.MyId : 0;

        void Update()
        {
            if (Following) return;
            double now = Now;
            var a = Active;
            if (a != null && Rush > 0)
            {
                Rush -= Time.deltaTime;
                if (Rush <= 0)
                {
                    Rush = 0;
                    Game.I.Hud.Feed("The Golden Rush is over. Fill the meter again for another!", Skin.Gold);
                    SendState();
                }
            }
            if (a != null && a.type == "shoal") AnglersIn = CountAnglersIn(a);
            if (a != null && now >= a.end) Finish(false);
            if (Active == null)
            {
                var (current, _) = Around(now, 0);
                if (current != null && current.id != lastId) Begin(current);
            }
            sendTimer -= Time.deltaTime;
            if (Active != null && sendTimer <= 0) SendState();
        }

        /// <summary>Lines in the event's water: yours and everyone else's.</summary>
        int CountAnglersIn(MapEvent a)
        {
            int n = 0;
            var f = Game.I.Fishing;
            var map = WorldMap.I;
            if (f.State != LineState.Idle && f.State != LineState.Casting)
            {
                var w = map.ToWorld(f.BobberPos);
                if (a.Contains(w.x, w.y)) n++;
            }
            if (Net != null && Net.Online)
                foreach (var r in Net.Remotes.Values)
                {
                    if (!r || r.State == null) continue;
                    var ls = (LineState)r.State.ls;
                    if (ls == LineState.Idle || ls == LineState.Casting) continue;
                    var w = map.ToWorld(new Vector3(r.State.bx, 0, r.State.bz));
                    if (a.Contains(w.x, w.y)) n++;
                }
            return Mathf.Max(1, n);
        }

        int PlayersOnline => Net != null && Net.Online ? 1 + Net.Remotes.Count : 1;

        void Begin(MapEvent ev)
        {
            lastId = ev.id;
            Active = ev;
            Progress = 0;
            Rush = 0;
            Rushes = 0;
            Fish = 0;
            helpers.Clear();
            var t = Cfg.tuning;
            int anglers = PlayersOnline;
            Goal = ev.type == "haul" ? t.haul.goalBase + t.haul.goalPerAngler * Mathf.Min(t.haul.maxAnglers, anglers)
                : ev.type == "tide" ? t.tide.meterBase + t.tide.meterPerAngler * Mathf.Min(t.tide.maxAnglers, anglers) : 0;
            Announce(ev);
            SendState();
        }

        void Announce(MapEvent ev)
        {
            if (marker) Destroy(marker);
            marker = EventMarker.Create(ev);
            var T = ev.T;
            Game.I.Banner(T.name, $"at {ev.place.name}: {Direction(ev)}", Skin.Hex(T.color));
            Game.I.Hud.Feed($"{T.name} has started at {ev.place.name} ({Direction(ev)}). {Desc(ev.type)}", Skin.Hex(T.color));
            Sfx.Play("level");
        }

        /// <summary>The event's description: for one angler, or the web game's co-op wording when others are online.</summary>
        static string Desc(string type) => Net != null && Net.Online ? Cfg.types[type].desc : SoloDesc(type);

        public static string Direction(MapEvent ev)
        {
            var me = Game.I.Player.WorldPos;
            var d = new Vector2(ev.place.x, ev.place.y) - me;
            float m = d.magnitude / WorldMap.UnitsPerMetre;
            if (m < 30) return "right here";
            string[] names = { "east", "south-east", "south", "south-west", "west", "north-west", "north", "north-east" };
            return $"{m:0} m {names[(Mathf.RoundToInt(Mathf.Atan2(d.y, d.x) / (Mathf.PI / 4)) + 8) % 8]}";
        }

        /// <summary>Fishing bonuses for a bobber at (x, y), or null.</summary>
        public EventMods ModsAt(float x, float y)
        {
            var a = Active;
            if (a == null || !a.Contains(x, y)) return null;
            var t = Cfg.tuning;
            return a.type switch
            {
                // The Feeding Shoal gets faster with every angler in it.
                "shoal" => new EventMods { bite = Mathf.Min(t.shoal.maxBite, t.shoal.bite + t.shoal.perAngler * (AnglersIn - 1)), rare = t.shoal.rare },
                "haul" => new EventMods { bite = t.haul.bite },
                "tide" when Rush > 0 => new EventMods { points = t.tide.points, rare = t.tide.rare },
                _ => null,
            };
        }

        /// <summary>You landed a fish.</summary>
        public void OnCatch(float x, float y, int points)
        {
            var a = Active;
            if (a == null || !a.Contains(x, y)) return;
            if (Following)
            {
                Fish++; // shown locally; the host keeps the real count
                Net.SendReliable("evcatch", new Newtonsoft.Json.Linq.JObject { ["x"] = x, ["y"] = y, ["points"] = points });
                return;
            }
            Count(Me, points);
        }

        /// <summary>The host hears that another player landed a fish.</summary>
        public void OnRemoteCatch(ulong from, float x, float y, int points)
        {
            var a = Active;
            if (a == null || !a.Contains(x, y)) return;
            Count(from, points);
        }

        void Count(ulong who, int points)
        {
            var a = Active;
            helpers[who] = (helpers.TryGetValue(who, out var n) ? n : 0) + 1;
            if (who == Me) Fish++;
            if (a.type == "haul")
            {
                Progress++;
                if (Progress >= Goal) { Finish(true); return; }
            }
            else if (a.type == "tide" && Rush <= 0)
            {
                Progress += points;
                if (Progress >= Goal)
                {
                    Progress = 0;
                    Rushes++;
                    Rush = Cfg.tuning.tide.rush;
                    Game.I.Banner("GOLDEN RUSH!", $"Double points for {Rush:0} seconds!", Skin.Gold);
                    Sfx.Play("trophy");
                }
            }
            SendState();
        }

        /// <summary>End the event (early when the Great Haul is done) and pay everyone who helped.</summary>
        void Finish(bool completed)
        {
            var a = Active;
            Active = null;
            Rush = 0;
            if (marker) Destroy(marker);
            if (a == null) return;
            var T = a.T;
            var t = Cfg.tuning;
            int group = helpers.Count;
            bool success = a.type != "haul" || completed;
            foreach (var kv in helpers)
            {
                int fish = kv.Value, coins, xp = 0;
                string why;
                if (a.type == "shoal")
                {
                    coins = Mathf.RoundToInt(Mathf.Min(fish, t.shoal.maxFish) * t.shoal.coinsPerFish * Mathf.Min(group, t.shoal.maxGroup));
                    why = group > 1 ? $"{fish} fish × {group} anglers in the shoal" : $"{fish} fish in the shoal";
                }
                else if (a.type == "haul")
                {
                    if (completed)
                    {
                        coins = Mathf.RoundToInt(t.haul.coins + Mathf.Min(fish, t.haul.maxFish) * t.haul.coinsPerFish);
                        xp = Mathf.RoundToInt(t.haul.xp);
                        why = $"Goal reached! You landed {fish} of the {Goal:0}.";
                    }
                    else
                    {
                        coins = Mathf.RoundToInt(Mathf.Min(fish, t.haul.maxFish) * t.haul.failCoinsPerFish);
                        why = $"{fish} fish (goal missed: {Progress:0} of {Goal:0})";
                    }
                }
                else
                {
                    coins = Mathf.RoundToInt(t.tide.coins + Rushes * t.tide.coinsPerRush);
                    why = $"{Rushes} Golden Rush{(Rushes == 1 ? "" : "es")}";
                }
                if (kv.Key == Me) Reward(a.type, coins, xp, why);
                else Net?.SendReliable("evreward", new Newtonsoft.Json.Linq.JObject { ["type"] = a.type, ["coins"] = coins, ["xp"] = xp, ["why"] = why }, kv.Key);
            }
            if (group == 0) Game.I.Hud.Feed($"The {T.name} at {a.place.name} is over.", Skin.Dim);
            else if (Net != null && Net.Online)
                Game.I.Hud.Feed($"The {T.name} is over: {group} angler{(group > 1 ? "s" : "")} took part{(a.type == "haul" ? success ? ", goal reached!" : ", goal missed." : ".")}", Skin.Hex(T.color));
            helpers.Clear();
            SendDone(a.id);
        }

        static void Reward(string type, int coins, int xp, string why)
        {
            var T = Cfg.types[type];
            var p = Game.I.Profile;
            int before = p.Level;
            p.coins += coins;
            p.counters.coinsEarned += coins;
            p.counters.mapEvents++;
            p.xp += xp;
            for (int l = before + 1; l <= p.Level; l++) p.coins += Levels.LevelUpCoins(l);
            Achievements.Check(p);
            p.Save();
            Game.I.Hud.Toast("calendar", T.name, $"+{coins:N0} coins{(xp > 0 ? $", +{xp} XP" : "")}", why);
            Sfx.Play("coin");
        }

        // ---- keeping everyone in step ------------------------------------------------------------------

        public void SendState(ulong? to = null)
        {
            sendTimer = 1;
            if (Net == null || !Net.IsHost || Active == null) return;
            Net.SendReliable("event", new Newtonsoft.Json.Linq.JObject
            {
                ["id"] = Active.id, ["progress"] = Progress, ["goal"] = Goal, ["rush"] = Rush, ["rushes"] = Rushes,
                ["in"] = AnglersIn, ["helpers"] = helpers.Count,
            }, to);
        }

        void SendDone(long id)
        {
            if (Net != null && Net.IsHost) Net.SendReliable("event", new Newtonsoft.Json.Linq.JObject { ["id"] = id, ["done"] = true });
        }

        /// <summary>A player in someone else's game: show the host's event.</summary>
        public void ApplyState(Newtonsoft.Json.Linq.JObject m)
        {
            long id = (long)m["id"];
            if (m["done"] != null)
            {
                if (Active != null && Active.id == id)
                {
                    Active = null;
                    if (marker) Destroy(marker);
                }
                return;
            }
            if (Active == null || Active.id != id)
            {
                Active = ForSlot(id);
                lastId = id;
                Fish = 0;
                Announce(Active);
            }
            float rushBefore = Rush;
            Progress = (float)m["progress"];
            Goal = (float)m["goal"];
            Rush = (float)m["rush"];
            Rushes = (int)m["rushes"];
            AnglersIn = (int)m["in"];
            if (rushBefore <= 0 && Rush > 0)
            {
                Game.I.Banner("GOLDEN RUSH!", $"Double points for {Rush:0} seconds!", Skin.Gold);
                Sfx.Play("trophy");
            }
        }

        public void ApplyReward(Newtonsoft.Json.Linq.JObject m) => Reward((string)m["type"], (int)m["coins"], (int)m["xp"], (string)m["why"]);

        /// <summary>Back on your own: forget the host's event (your own schedule takes over again).</summary>
        public void OnLeftGame()
        {
            if (Active != null && Following) { }
            Active = null;
            Rush = 0;
            helpers.Clear();
            lastId = -1;
            if (marker) Destroy(marker);
        }

        void LateUpdate()
        {
            // Following the host: run the Golden Rush clock locally between updates.
            if (Following && Rush > 0) Rush = Mathf.Max(0, Rush - Time.deltaTime);
        }
    }

    /// <summary>Where an event is: a tall beam of light you can see from far away, and a ring on the water.</summary>
    public static class EventMarker
    {
        public static GameObject Create(MapEvent ev)
        {
            var map = WorldMap.I;
            var c = Skin.Hex(ev.T.color);
            Color32 col = c;
            var go = new GameObject($"Event: {ev.T.name}");
            go.transform.position = map.ToUnity(ev.place.x, ev.place.y, 0.1f);
            float r = ev.radius / WorldMap.UnitsPerMetre;
            var k = new MeshKit();
            // The beam: a tall, faint column.
            int n = 12;
            var beam = new Color32(col.r, col.g, col.b, 60);
            for (int i = 0; i < n; i++)
            {
                float a0 = i / (float)n * Mathf.PI * 2, a1 = (i + 1) / (float)n * Mathf.PI * 2;
                var p0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * 2.5f;
                var p1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * 2.5f;
                k.Quad(p0, p0 * 0.4f + Vector3.up * 120, p1 * 0.4f + Vector3.up * 120, p1, beam);
            }
            // The ring around the event's water.
            var ring = new Color32(col.r, col.g, col.b, 150);
            int m = 64;
            for (int i = 0; i < m; i++)
            {
                if (i % 2 == 1) continue;
                float a0 = i / (float)m * Mathf.PI * 2, a1 = (i + 1) / (float)m * Mathf.PI * 2;
                var p0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                var p1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                k.Quad(p0 * (r - 0.4f), p0 * r, p1 * r, p1 * (r - 0.4f), ring);
            }
            go.AddComponent<MeshFilter>().sharedMesh = k.Build("event marker");
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = RodView.Unlit;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.AddComponent<Spin>();
            return go;
        }

        class Spin : MonoBehaviour
        {
            void Update() => transform.rotation = Quaternion.Euler(0, Time.time * 6, 0);
        }
    }
}
