// Boat voyages, ported from the web game (server/boat.js, server/voyage.js,
// shared/voyage.js):
//
//   board at the dock -> outbound -> [fishing at stop 1..3, sailing between] -> boss -> results -> home
//
// Board the boat (E) while it's docked at South Beach. When it has sailed out of
// sight you're on a trawler at sea. It visits 3 of 8 locations, each with its
// own fish and a special event part-way through. The crew shares three
// missions. Then a boss surfaces: land fish to hurt it (double from its glowing
// weak spot), harpoon it when it breaches (cast into the golden ring), beat off
// whatever grabs the rail (E) and get out of the red when it slams the deck.
// Everyone is paid at the end: points, missions, rank, the boss and a share of
// the prize pool.
//
// In multiplayer, everyone aboard the same boat is on the same voyage: the
// stops, events, missions and the boss's moves all come from the departure time
// (its seed), and catches, harpoons and strikes are shared so the boss's health
// and the missions stay in step.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LastCast
{
    public enum VoyagePhase { None, Outbound, Fishing, Sailing, BossIntro, Boss, Results }

    public class VoyageStop { public SeaLocation loc; public string time; public float eventAt; }
    public class VoyageMission { public MissionDef def; public string text; public int goal, progress; public bool done; }

    public class VoyageScore
    {
        public ulong id;
        public string name;
        public int points, catches, harpoons, grabHits, dazed, share;
        public float damage, contribution;
    }

    /// <summary>A hotspot at sea, or the boss's weak spot (sea units).</summary>
    public class SeaSpot { public float x, y, r; public bool weak; }

    public class BossGrab { public int id, hp, max; public float x, y, until; public readonly Dictionary<ulong, int> hits = new Dictionary<ulong, int>(); }
    public class BossBreach { public float x, y, r, until; public bool struck; public int strikes; }

    public class BossFight
    {
        public BossInfo info;
        public float hp, max, clock;
        public int phase;
        public string attack, attackArea; // an attack being telegraphed, and the deck area a slam will hit
        public float attackAt; // when it lands (fight clock)
        public int attackSeed; // which attack of the fight it is (for where it comes from)
        public string lastAttack, lastArea; // the attack that last landed, its deck area, when, and its seed: its animation plays on
        public float landedAt = -99;
        public int lastSeed;
        public string effect; // an attack's lingering effect (ink, whirlpool)
        public float effectUntil;
        public BossBreach breach;
        public readonly List<BossGrab> grabs = new List<BossGrab>();
        public SeaSpot weak;
        public float nextBreachAt, nextGrabAt, nextAttackAt, weakUntil, lastStrike = -99;
        public int breaches, waves, attacks, delays, weakMoves;
        public string result; // null while fighting, then "won" or "lost"
        public float Fraction => max > 0 ? hp / max : 0;
    }

    public class VoyageResults
    {
        public int rank, bonus, xp, crewTotal, pool;
        public int fromPoints, fromMissions, fromRank, fromBoss, fromPool;
        public List<(string text, bool done)> missions;
        public string bossName, bossResult, mvp;
        public List<VoyageScore> ranking;
        public ulong me;
    }

    public class Voyage : MonoBehaviour
    {
        public static Voyage I { get; private set; }
        static VoyageData V => GameData.I.voyage;

        /// <summary>For testing: shorter timings (AutoShot), used by the next launch.</summary>
        public static VoyageTiming TestTiming;
        /// <summary>For testing (AutoShot -attacks): the boss to meet, and the attack it uses next.</summary>
        public static string TestBoss;
        public static string TestAttack;

        public VoyageTiming T { get; private set; }
        /// <summary>The voyage you're booked on while standing aboard the docked boat (its departure time), or 0.</summary>
        public long Boarded { get; private set; }
        /// <summary>The voyage you're on (its departure time, also its seed), or 0 on the lake.</summary>
        public long Id { get; private set; }
        public bool AtSea => Id != 0;
        public VoyagePhase Phase { get; private set; }
        public float PhaseLeft { get; private set; }
        public int StopIndex { get; private set; } = -1;
        public readonly List<VoyageStop> Stops = new List<VoyageStop>();
        public VoyageStop Stop => StopIndex >= 0 && StopIndex < Stops.Count ? Stops[StopIndex] : null;
        public VoyageStop NextStop => Phase == VoyagePhase.Outbound ? Stops[0] : Phase == VoyagePhase.Sailing ? Stops[StopIndex + 1] : null;
        public SeaEvent Event { get; private set; }
        public float EventLeft { get; private set; }
        public readonly List<VoyageMission> Missions = new List<VoyageMission>();
        public readonly Dictionary<ulong, VoyageScore> Scores = new Dictionary<ulong, VoyageScore>();
        public int CrewTotal { get; private set; }
        public int Crew { get; private set; } = 1;
        public string BossId { get; private set; }
        public BossInfo BossInfo => V.Boss(BossId);
        public BossFight Boss { get; private set; }
        public VoyageResults Results { get; private set; }
        public float Dazed { get; private set; }
        public readonly List<SeaSpot> Hotspots = new List<SeaSpot>();
        public ulong Me { get; private set; }

        int slot;
        double resultsEnd, bossStart;
        readonly HashSet<string> species = new HashSet<string>();
        Zone eventZone;
        SeaSpot[] spots;
        int[] spotK;
        double[] spotEnd;
        BoatPhase? lastBoat;
        bool lastCall;

        public static Voyage Create(GameObject host) => I = host.AddComponent<Voyage>();

        /// <summary>A boss's, an event's or the water's colour, lightened so text in it reads on the dark panels.</summary>
        static Color Tint(string hex) => Color.Lerp(Skin.Hex(hex), Color.white, 0.35f);

        static double Now => BoatSchedule.Now;
        static PlayerController Player => Game.I.Player;
        static Fishing Line => Game.I.Fishing;

        // ---- what fishing asks ------------------------------------------------------------------------

        /// <summary>The water you're fishing right now (with the special event's fish while it's on).</summary>
        public Zone Zone => Phase switch
        {
            VoyagePhase.Fishing => Event != null ? eventZone : Stop.loc.zone,
            VoyagePhase.Boss => BossInfo.zone,
            _ => V.openSea,
        };

        /// <summary>A special event's (or a boss attack's) changes to everyone's fishing.</summary>
        public SeaMods Mods => Phase == VoyagePhase.Fishing ? Event?.mods
            : Phase == VoyagePhase.Boss && Boss?.effect != null ? V.Attack(Boss.effect).mods : null;

        public bool EventOn => Phase == VoyagePhase.Fishing && Event != null;

        /// <summary>Why you can't cast just now, or null.</summary>
        public string CastBlocked
        {
            get
            {
                if (Boarded != 0 && !AtSea) return "You're aboard the boat. The fishing starts out at sea!";
                if (!AtSea) return null;
                if (Dazed > 0) return "You're dazed! Wait a moment.";
                return Phase switch
                {
                    VoyagePhase.Outbound => "The boat is heading out to sea. Get ready!",
                    VoyagePhase.Sailing => $"Lines in! Sailing to {NextStop.loc.name}...",
                    VoyagePhase.BossIntro => "Lines in! Something huge is rising from the deep...",
                    VoyagePhase.Results => "The voyage is over. Heading home!",
                    _ => null,
                };
            }
        }

        public SeaSpot HotspotAt(float x, float y) => Hotspots.FirstOrDefault(h => Vector2.Distance(new Vector2(h.x, h.y), new Vector2(x, y)) <= h.r);

        public Vector2 MySeaPos => SeaWorld.ToSea(Player.transform.position);

        // ---- boarding at the dock ------------------------------------------------------------------------

        /// <summary>Can you board (or step off) right here, right now?</summary>
        public bool CanBoard => !AtSea && Boarded == 0 && LakeBoat.I && LakeBoat.I.Pose.phase == BoatPhase.Docked && LakeBoat.InReach(Player.WorldPos);
        public bool CanStepOff => !AtSea && Boarded != 0 && LakeBoat.I && LakeBoat.I.Pose.phase == BoatPhase.Docked;

        /// <summary>E near the boat: board it, or step back off while it's still docked.</summary>
        public bool ToggleBoard()
        {
            if (AtSea || !LakeBoat.I) return false;
            var pose = LakeBoat.I.Pose;
            if (Boarded != 0)
            {
                if (pose.phase != BoatPhase.Docked)
                {
                    Game.I.Flash("Too late to get off: you're going to sea!");
                    return true;
                }
                Boarded = 0;
                Player.Anchor = null;
                Player.Teleport(LandingPos(), 0);
                Game.I.Hud.Feed("You stepped back onto the dock.", Skin.Dim);
                return true;
            }
            if (!CanBoard) return false;
            Line.Cancel(null);
            Boarded = BoatSchedule.LaunchTime(Now);
            slot = Multiplayer.I.Online ? (int)(Multiplayer.I.MyId % 16) : 0;
            float facing = BoatSchedule.Yaw(pose.h) + (slot % 2 == 1 ? 90 : -90); // out over the rail
            Player.Teleport(LakeBoat.I.SlotPosition(slot), facing);
            Player.Anchor = () => LakeBoat.I.SlotPosition(slot);
            Game.I.Hud.Feed($"You're aboard! The boat sails in {Clock(pose.left)}. Press E to step off before then.", Skin.Accent);
            Game.I.Banner("ALL ABOARD!", $"The boat sails in {Clock(pose.left)}", Skin.Accent);
            Sfx.Play("level");
            return true;
        }

        static Vector3 LandingPos()
        {
            var b = GameData.I.boat;
            var map = WorldMap.I;
            for (int i = 0; i < 20; i++)
            {
                float x = b.landing.x + Random.Range(-15f, 15f), y = b.landing.y + Random.Range(-60f, 60f);
                if (map.IsWalkable(x, y)) return map.ToUnity(x, y);
            }
            return map.ToUnity(GameData.I.spawn.x, GameData.I.spawn.y);
        }

        /// <summary>The lake hears about the boat: arriving, docked, last call.</summary>
        void LakeNews()
        {
            if (!LakeBoat.I) return;
            var pose = LakeBoat.I.Pose;
            if (lastBoat == null) { lastBoat = pose.phase; lastCall = pose.left <= 30; return; }
            if (pose.phase != lastBoat)
            {
                lastBoat = pose.phase;
                lastCall = false;
                if (!AtSea)
                {
                    if (pose.phase == BoatPhase.Arriving) Game.I.Hud.Feed("The voyage boat is coming in up the East River. It docks at South Beach in half a minute.", Skin.Accent);
                    if (pose.phase == BoatPhase.Docked)
                    {
                        Game.I.Hud.Feed($"The boat has docked at the South Beach dock! Walk up and press E to board. It sails in {Clock(pose.left)}.", Skin.Gold);
                        Game.I.Hud.Toast("calendar", "Boat voyage", "The boat is in at South Beach", "Board it (E) for a voyage out to sea");
                    }
                    if (pose.phase == BoatPhase.Departing && Boarded == 0) Game.I.Hud.Feed("The boat has sailed. The next one docks in 15 minutes.", Skin.Dim);
                }
            }
            if (pose.phase == BoatPhase.Docked && !lastCall && pose.left <= 30)
            {
                lastCall = true;
                if (!AtSea) Game.I.Hud.Feed(Boarded != 0 ? "Last call: we sail in 30 seconds!" : "Last call! The boat sails in 30 seconds.", Skin.Gold);
            }
        }

        // ---- the voyage --------------------------------------------------------------------------------------

        static List<T> Shuffled<T>(IEnumerable<T> list, SeededRandom rng)
        {
            var a = list.ToList();
            for (int i = a.Count - 1; i > 0; i--)
            {
                int j = Mathf.FloorToInt(rng.Next() * (i + 1));
                (a[i], a[j]) = (a[j], a[i]);
            }
            return a;
        }

        int CrewAboard() => 1 + Multiplayer.I.Remotes.Values.Count(r => r && r.State != null && r.State.vy == Id);

        void Launch(long id)
        {
            Id = id;
            Boarded = 0;
            T = TestTiming ?? V.timing;
            Me = Multiplayer.I.Online ? Multiplayer.I.MyId : 0;
            Phase = VoyagePhase.None;
            StopIndex = -1;
            Results = null;
            Boss = null;
            Event = null;
            Dazed = 0;
            CrewTotal = 0;
            Scores.Clear();
            species.Clear();
            Missions.Clear();
            Stops.Clear();

            var rng = new SeededRandom(Noise.SeedFrom("voyage", id));
            foreach (var (loc, i) in Shuffled(V.locations, rng).Take(T.stops).Select((l, i) => (l, i)))
                Stops.Add(new VoyageStop
                {
                    loc = loc,
                    time = V.times[Mathf.Min(i, V.times.Length - 1)],
                    eventAt = T.eventEarliest + rng.Next() * Mathf.Max(0, T.fishing - T.eventLength - T.eventEarliest - 5),
                });
            Crew = CrewAboard();
            foreach (var m in Shuffled(V.missions, rng).Take(T.missions))
            {
                int goal = Mathf.Max((int)m.min, Mathf.RoundToInt(m.perAngler * Crew));
                Missions.Add(new VoyageMission { def = m, goal = goal, text = m.text.Replace("{n}", goal.ToString()) });
            }
            BossId = TestBoss ?? Shuffled(V.bosses, rng)[0].id;
            Score(Me, Multiplayer.PlayerName);

            int n = V.hotspots.count;
            spots = new SeaSpot[n];
            spotK = Enumerable.Repeat(-1, n).ToArray();
            spotEnd = new double[n];
            Hotspots.Clear();

            Player.Anchor = null;
            Line.Cancel(null);
            slot = Multiplayer.I.Online ? (int)(Multiplayer.I.MyId % 20) : 0;
            var deck = DeckSlot(slot);
            Player.Teleport(SeaWorld.ToUnity(deck.x, deck.y), slot % 2 == 1 ? 180 : 0); // looking out over your rail
            SeaWorld.I.Enter(Stops[0].time);
            Game.I.Hud.Feed($"The boat sails out to sea! Stops: {string.Join(", ", Stops.Select(s => s.loc.name))}. Crew missions: {string.Join("; ", Missions.Select(m => m.text))}.", Skin.Accent);
            Game.I.Banner("OUT TO SEA!", Crew > 1 ? $"A crew of {Crew}. Three stops, then something big..." : "Three stops, then something big...", Skin.Accent);
        }

        /// <summary>Where passenger i stands on the trawler's deck (shared/voyage.js deckSlot).</summary>
        static Vector2 DeckSlot(int i)
        {
            const int perRail = 10;
            int rail = i % 2, k = (i / 2) % perRail, extra = i / (perRail * 2);
            return new Vector2(700 + k * 40 + extra * 12, rail == 1 ? 638 : 562);
        }

        /// <summary>For testing (AutoShot): go to sea right now.</summary>
        public void TestLaunch() => Launch((long)System.Math.Floor(Now));

        float StopSpan => T.fishing + T.sailing;
        double BossIntroAt => T.outbound + (Stops.Count - 1) * StopSpan + T.fishing;

        (VoyagePhase phase, int stop, float left) Compute(double now)
        {
            if (Results != null) return (VoyagePhase.Results, StopIndex, (float)(resultsEnd - now));
            double t = now - Id;
            if (t < T.outbound) return (VoyagePhase.Outbound, -1, (float)(T.outbound - t));
            for (int i = 0; i < Stops.Count; i++)
            {
                double start = T.outbound + i * StopSpan;
                if (t < start + T.fishing) return (VoyagePhase.Fishing, i, (float)(start + T.fishing - t));
                if (i < Stops.Count - 1 && t < start + StopSpan) return (VoyagePhase.Sailing, i, (float)(start + StopSpan - t));
            }
            double bossAt = BossIntroAt + T.bossIntro;
            if (t < bossAt) return (VoyagePhase.BossIntro, Stops.Count - 1, (float)(bossAt - t));
            return (VoyagePhase.Boss, Stops.Count - 1, (float)(bossAt + T.boss - t));
        }

        void Update()
        {
            if (Game.I == null || Player == null) return;
            LakeNews();
            double now = Now;
            if (Boarded != 0 && !AtSea && now >= Boarded) Launch(Boarded);
            if (!AtSea) return;
            Dazed = Mathf.Max(0, Dazed - Time.deltaTime);
            var (phase, stop, left) = Compute(now);
            PhaseLeft = left;
            if (phase != Phase || stop != StopIndex) Enter(phase, stop, now);
            if (Phase == VoyagePhase.Fishing) UpdateEvent();
            if (Phase == VoyagePhase.Boss) UpdateBoss(now);
            if (Phase == VoyagePhase.Results && now >= resultsEnd)
            {
                GoHome();
                return;
            }
            UpdateHotspots(now);
        }

        void Enter(VoyagePhase phase, int stop, double now)
        {
            Phase = phase;
            StopIndex = stop;
            switch (phase)
            {
                case VoyagePhase.Outbound:
                    Game.I.Flash("The boat is heading out to sea. Get ready!", 4);
                    break;
                case VoyagePhase.Fishing:
                {
                    Event = null;
                    var s = Stop;
                    var ev = V.Event(s.loc.@event);
                    SeaWorld.I.SetStop(s.loc, s.time);
                    Game.I.Banner(s.loc.name, $"Stop {stop + 1} of {Stops.Count} · {s.time} · {s.loc.desc}", Tint(s.loc.water[1]));
                    Game.I.Hud.Feed($"Stop {stop + 1} of {Stops.Count}: {s.loc.name} ({s.time}). {s.loc.desc} Lines out! Its special event, {ev.name}, starts some time during the stop.", Skin.Accent);
                    Sfx.Play("plop");
                    break;
                }
                case VoyagePhase.Sailing:
                    EndEvent();
                    LinesIn($"Lines in! Sailing to {NextStop.loc.name}...");
                    Game.I.Hud.Feed($"Lines in! Sailing on to {NextStop.loc.name}.", Skin.Dim);
                    break;
                case VoyagePhase.BossIntro:
                {
                    EndEvent();
                    var b = BossInfo;
                    LinesIn("Lines in! Something huge is rising from the deep...");
                    Game.I.Banner($"{b.name} rises!", b.desc, Tint(b.color));
                    Game.I.Hud.Feed($"Lines in! {b.name} is rising from the deep. {b.desc}", Tint(b.color));
                    Sfx.Play("splash");
                    break;
                }
                case VoyagePhase.Boss:
                    StartBoss(now);
                    break;
            }
        }

        void LinesIn(string message)
        {
            if (Line.State != LineState.Idle) Line.Cancel(message);
            else Game.I.Flash(message, 3);
        }

        void UpdateEvent()
        {
            var s = Stop;
            float elapsed = T.fishing - PhaseLeft;
            bool on = elapsed >= s.eventAt && elapsed < s.eventAt + T.eventLength;
            EventLeft = on ? s.eventAt + T.eventLength - elapsed : 0;
            if (on && Event == null)
            {
                Event = V.Event(s.loc.@event);
                eventZone = WithExtraFish(s.loc.eventZone, Event.mods.extraFish);
                Game.I.Banner(Event.name, Event.desc, Tint(Event.color));
                Game.I.Hud.Feed($"Special event: {Event.name}! {Event.desc}", Tint(Event.color));
                Sfx.Play("level");
            }
            else if (!on && Event != null) EndEvent();
        }

        static Zone WithExtraFish(Zone z, Dictionary<string, float> extra)
        {
            if (extra == null) return z;
            var copy = new Zone { id = z.id, name = z.name, kind = z.kind, biteRate = z.biteRate, fish = new Dictionary<string, float>(z.fish) };
            foreach (var kv in extra) copy.fish[kv.Key] = (copy.fish.TryGetValue(kv.Key, out var w) ? w : 0) + kv.Value;
            return copy;
        }

        void EndEvent()
        {
            if (Event == null) return;
            Game.I.Hud.Feed($"{Event.name} is over.", Skin.Dim);
            Event = null;
            EventLeft = 0;
        }

        /// <summary>The hotspots at sea: two at a time, each living 30-60 s. Where and when come from the voyage's seed,
        /// so the whole crew sees the same ones. In the boss fight the weak spot replaces them.</summary>
        void UpdateHotspots(double now)
        {
            Hotspots.Clear();
            if (Phase == VoyagePhase.Boss)
            {
                if (Boss?.weak != null && Boss.result == null) Hotspots.Add(Boss.weak);
                return;
            }
            if (Phase == VoyagePhase.BossIntro || Phase == VoyagePhase.Results) return;
            var cfg = V.hotspots;
            double t = now - Id;
            for (int j = 0; j < spots.Length; j++)
            {
                while (t >= spotEnd[j])
                {
                    spotK[j]++;
                    var rng = new SeededRandom(Noise.SeedFrom("sea hotspot", Id, j, spotK[j]));
                    double start = spotK[j] == 0 ? 0 : spotEnd[j];
                    spotEnd[j] = start + rng.Range(cfg.minLife, cfg.maxLife);
                    float x = 0, y = 0;
                    for (int tries = 0; tries < 12; tries++)
                    {
                        x = cfg.area.x + rng.Next() * cfg.area.w;
                        y = cfg.area.y + rng.Next() * cfg.area.h;
                        if (!SeaWorld.OnBoat(x, y, cfg.radius)) break;
                    }
                    spots[j] = new SeaSpot { x = x, y = y, r = cfg.radius };
                }
                Hotspots.Add(spots[j]);
            }
        }

        // ---- catches, missions and scores ---------------------------------------------------------------

        VoyageScore Score(ulong who, string name = null)
        {
            if (!Scores.TryGetValue(who, out var s)) Scores[who] = s = new VoyageScore { id = who, name = name ?? RemoteName(who) };
            else if (name != null) s.name = name;
            return s;
        }

        static string RemoteName(ulong id) =>
            Multiplayer.I.Remotes.TryGetValue(id, out var r) && r && r.State != null ? r.State.n : "An angler";

        /// <summary>You landed a fish at sea (after the usual rewards).</summary>
        public void OnCatch(CatchInfo c)
        {
            if (!AtSea || Phase == VoyagePhase.Results) return;
            bool ev = EventOn;
            Record(Me, Multiplayer.PlayerName, c.species, c.kg, c.points, ev);
            float dmg = 0;
            if (Phase == VoyagePhase.Boss && Boss != null && Boss.result == null)
            {
                dmg = c.points * (c.hotspot ? 2 : 1);
                DamageBoss(Me, dmg, c.hotspot, false);
            }
            Send("vcatch", new JObject { ["sp"] = c.species.id, ["kg"] = c.kg, ["pts"] = c.points, ["ev"] = ev, ["dmg"] = dmg, ["weak"] = c.hotspot });
        }

        void Record(ulong who, string name, Species s, float kg, int points, bool ev)
        {
            var sc = Score(who, name);
            sc.points += points;
            sc.catches++;
            CrewTotal += points;
            species.Add(s.id);
            Bump("haul");
            if (s.rarity is "rare" or "legendary" or "mythic") Bump("rare");
            if (ev) Bump("event");
            if (kg >= 10) Bump("heavy");
            Bump("variety");
            if (s.rarity is "legendary" or "mythic") Bump("legend");
        }

        void Bump(string id)
        {
            var m = Missions.Find(x => x.def.id == id);
            if (m == null || m.done) return;
            m.progress = id == "variety" ? species.Count : m.progress + 1;
            if (m.progress < m.goal) return;
            m.progress = m.goal;
            m.done = true;
            Game.I.Hud.Feed($"Crew mission complete: {m.text}! (+{T.missionReward} coins each at the end)", Skin.Green);
            Game.I.Hud.Toast("medal", "Crew mission", m.text, $"+{T.missionReward} coins at the end of the voyage");
            Sfx.Play("level");
        }

        // ---- the boss fight ---------------------------------------------------------------------------------

        static BossTuning B => V.boss;

        /// <summary>A random-looking but shared stream of numbers for one part of the fight.</summary>
        SeededRandom Stream(string what, int n) => new SeededRandom(Noise.SeedFrom("boss", Id, what, n));

        static float Between(SeededRandom rng, float[] range) => range[0] + rng.Next() * (range[1] - range[0]);

        void StartBoss(double now)
        {
            var info = BossInfo;
            Crew = Mathf.Max(Crew, CrewAboard());
            bossStart = Id + BossIntroAt + T.bossIntro;
            Boss = new BossFight
            {
                info = info,
                max = Mathf.Round((B.@base + B.perAngler * Mathf.Max(1, Crew)) * info.hp),
                nextBreachAt = B.breach.first,
                nextGrabAt = B.grab.first,
                clock = (float)(now - bossStart),
            };
            Boss.hp = Boss.max;
            Boss.nextAttackAt = AttackDelay();
            MoveWeakSpot();
            Game.I.Banner(info.name, $"Defeat it within {Mathf.RoundToInt(T.boss / 60)} minutes!", Tint(info.color));
            Game.I.Hud.Feed($"{info.name} attacks! Land fish to hurt it (double from its glowing red weak spot). When it breaches, cast into the golden ring to harpoon it. If something grabs the rail, walk over and mash E. Get out of the red when it slams!", Tint(info.color));
            Sfx.Play("splash");
        }

        float AttackDelay()
        {
            var rng = Stream("delay", Boss.delays++);
            return Boss.clock + Between(rng, B.phases[Boss.phase].attackEvery);
        }

        /// <summary>A random spot in the water within casting reach of the deck.</summary>
        static Vector2 SpotOffDeck(SeededRandom rng)
        {
            var sb = V.seaBoat;
            float side = rng.Next() < 0.5f ? -1 : 1;
            float x = sb.x - 220 + rng.Next() * 440;
            float y = sb.y + side * (sb.beam / 2 + 90 + rng.Next() * 130);
            return new Vector2(x, y);
        }

        void MoveWeakSpot()
        {
            var p = SpotOffDeck(Stream("weak", Boss.weakMoves++));
            Boss.weak = new SeaSpot { x = p.x, y = p.y, r = B.weakSpotRadius, weak = true };
            Boss.weakUntil = Boss.clock + B.phases[Boss.phase].weakSpotMove;
        }

        static int PhaseFor(float fraction)
        {
            int phase = 0;
            for (int i = 0; i < B.phases.Count; i++) if (fraction <= B.phases[i].at) phase = i;
            return phase;
        }

        void UpdateBoss(double now)
        {
            var b = Boss;
            if (b == null || b.result != null) return;
            b.clock = (float)(now - bossStart);
            if (b.clock >= T.boss)
            {
                EndBoss(false, now);
                return;
            }
            if (b.clock >= b.weakUntil) MoveWeakSpot();

            int phase = PhaseFor(b.Fraction);
            if (phase > b.phase)
            {
                b.phase = phase;
                b.nextAttackAt = Mathf.Min(b.nextAttackAt, AttackDelay());
                MoveWeakSpot();
                Game.I.Banner(B.phases[phase].name, $"{b.info.name} is {B.phases[phase].name.ToLower()}! It attacks more often now.", Tint(b.info.color));
                Game.I.Hud.Feed($"{b.info.name} is {B.phases[phase].name.ToLower()}!", Tint(b.info.color));
            }
            if (b.effect != null && b.clock >= b.effectUntil) b.effect = null;
            UpdateBreach(b);
            UpdateGrabs(b, now);
            if (b.result != null) return;

            if (b.attack != null)
            {
                if (b.clock >= b.attackAt) LandAttack(b);
                return;
            }
            if (b.clock < b.nextAttackAt) return;
            b.attackSeed = b.attacks;
            var rng = Stream("attack", b.attacks++);
            string id = b.info.attacks[Mathf.FloorToInt(rng.Next() * b.info.attacks.Length) % b.info.attacks.Length];
            if (TestAttack != null) id = TestAttack;
            var a = V.Attack(id);
            b.attack = id;
            b.attackAt = b.clock + B.warning;
            var (name, desc) = BossMoves.Describe(b.info, a, null);
            if (a.area)
            {
                var area = V.deckAreas[Mathf.FloorToInt(rng.Next() * V.deckAreas.Count) % V.deckAreas.Count];
                b.attackArea = area.id;
                (name, desc) = BossMoves.Describe(b.info, a, area.name);
            }
            Game.I.Banner($"{name}!", desc, Skin.Red);
            Game.I.Hud.Feed($"{b.info.name} is about to use {name}! {desc}", Skin.Red);
            Sfx.Play("error");
        }

        void LandAttack(BossFight b)
        {
            var a = V.Attack(b.attack);
            string area = b.attackArea;
            b.lastAttack = b.attack;
            b.lastArea = area;
            b.landedAt = b.clock;
            b.lastSeed = b.attackSeed;
            b.attack = null;
            b.attackArea = null;
            b.nextAttackAt = AttackDelay();
            if (a.tension.HasValue) Line.AddTension(a.tension.Value);
            if (a.mods != null)
            {
                b.effect = a.id;
                b.effectUntil = b.clock + (a.seconds ?? 10);
            }
            if (a.area)
            {
                var me = MySeaPos;
                if (V.DeckArea(area)?.Contains(me.x, me.y) == true)
                {
                    float stun = B.slam.TryGetValue("stun", out var s) ? s : 5;
                    if (Line.State != LineState.Idle) Line.Cancel(null);
                    Dazed = stun;
                    Score(Me).dazed++;
                    Game.I.Banner("Dazed!", $"The {b.info.slam} caught you! No fishing for {stun:0} seconds.", Skin.Red);
                    Sfx.Play("snap");
                }
                else Game.I.Hud.Feed($"The {b.info.slam} crashed down on {V.DeckArea(area).name}. You got clear!", Skin.Green);
                SeaWorld.I.Shake(0.6f);
            }
            else
            {
                var (name, desc) = BossMoves.Describe(b.info, a, null);
                Game.I.Hud.Feed($"{name}! {desc}", Skin.Red);
            }
        }

        // -- breaches: cast into the ring to harpoon it --

        void UpdateBreach(BossFight b)
        {
            var cfg = B.breach;
            if (b.breach != null)
            {
                if (b.clock >= b.breach.until)
                {
                    Game.I.Hud.Feed($"{b.info.name} dove back under. {b.breach.strikes} harpoon{(b.breach.strikes == 1 ? "" : "s")} struck it.", Skin.Dim);
                    b.breach = null;
                }
                return;
            }
            if (b.clock < b.nextBreachAt) return;
            var rng = Stream("breach", b.breaches++);
            var p = SpotOffDeck(rng);
            b.breach = new BossBreach { x = p.x, y = p.y, r = cfg.radius, until = b.clock + cfg.window };
            b.nextBreachAt = b.breach.until + Between(rng, cfg.every);
            Game.I.Banner("It's breaching!", $"Cast into the golden ring to harpoon {b.info.name}! ({cfg.window:0} s)", Skin.Gold);
            Sfx.Play("splash");
        }

        /// <summary>Your bobber landed: inside a breach ring, that's a harpoon strike (once per breach).</summary>
        public void OnLand(float x, float y)
        {
            var b = Boss;
            if (Phase != VoyagePhase.Boss || b == null || b.result != null || b.breach == null || b.breach.struck) return;
            if (Vector2.Distance(new Vector2(x, y), new Vector2(b.breach.x, b.breach.y)) > b.breach.r) return;
            b.breach.struck = true;
            b.breach.strikes++;
            Score(Me).harpoons++;
            Game.I.Banner("Harpooned!", $"-{B.breach.damage:0} to {b.info.name}", Skin.Gold);
            Sfx.Play("hook");
            DamageBoss(Me, B.breach.damage, false, true);
            Send("vdmg", new JObject { ["dmg"] = B.breach.damage });
        }

        // -- grabs: walk over and mash E --

        void UpdateGrabs(BossFight b, double now)
        {
            var cfg = B.grab;
            foreach (var g in b.grabs.ToList())
            {
                if (b.clock < g.until) continue;
                // It held on: the boss heals and the boat lurches.
                b.grabs.Remove(g);
                float before = b.hp;
                b.hp = Mathf.Min(b.max, b.hp + Mathf.Round(b.max * cfg.heal));
                Line.AddTension(cfg.lurch);
                SeaWorld.I.Shake(0.5f);
                Game.I.Banner("It held on!", $"The {b.info.grab} wasn't beaten off: {b.info.name} healed {b.hp - before:0} and the boat lurched.", Skin.Red);
            }
            if (b.grabs.Count > 0 || b.clock < b.nextGrabAt) return;
            int wave = b.waves++;
            var rng = Stream("grab", wave);
            b.nextGrabAt = b.clock + cfg.time * 0.5f + Between(rng, cfg.every);
            int count = b.phase >= 1 && Crew >= 3 ? 2 : 1;
            int hits = Mathf.Max(3, Mathf.RoundToInt((cfg.hits + cfg.hitsPerAngler * Crew) / count));
            float[] rails = rng.Next() < 0.5f ? new[] { 566f, 634f } : new[] { 634f, 566f };
            for (int i = 0; i < count; i++)
                b.grabs.Add(new BossGrab { id = wave * 4 + i, x = 720 + rng.Next() * 340, y = rails[i], hp = hits, max = hits, until = b.clock + cfg.time });
            Game.I.Banner($"{b.info.grab}!", $"It's grabbed the rail! Walk over and mash E to beat it off within {cfg.time:0} s.", Tint(b.info.color));
            Sfx.Play("splash");
        }

        /// <summary>The grab within reach of you, if any.</summary>
        public BossGrab GrabInReach
        {
            get
            {
                if (Phase != VoyagePhase.Boss || Boss == null || Boss.result != null) return null;
                var me = MySeaPos;
                return Boss.grabs.Where(g => Vector2.Distance(me, new Vector2(g.x, g.y)) <= B.grab.range)
                    .OrderBy(g => Vector2.Distance(me, new Vector2(g.x, g.y))).FirstOrDefault();
            }
        }

        /// <summary>E at sea: strike whatever has grabbed the rail.</summary>
        public bool TryStrike()
        {
            var g = GrabInReach;
            if (g == null || Dazed > 0 || Line.State != LineState.Idle) return false;
            if (Boss.clock - Boss.lastStrike < B.grab.cooldown) return true;
            Boss.lastStrike = Boss.clock;
            Sfx.Play("hook", 0.7f);
            Send("vstrike", new JObject { ["g"] = g.id });
            ApplyStrike(g, Me);
            return true;
        }

        void ApplyStrike(BossGrab g, ulong who)
        {
            var b = Boss;
            var cfg = B.grab;
            g.hp--;
            g.hits[who] = (g.hits.TryGetValue(who, out var n) ? n : 0) + 1;
            var s = Score(who);
            s.grabHits++;
            s.contribution += B.grabHitValue;
            if (g.hp > 0) return;
            // Beaten off: it hurts the boss, credited to everyone who struck it.
            b.grabs.Remove(g);
            var names = new List<string>();
            foreach (var kv in g.hits)
            {
                var sc = Score(kv.Key);
                names.Add(kv.Key == Me ? "you" : sc.name);
                float share = Mathf.Round(cfg.damage * kv.Value / g.max);
                sc.damage += share;
                sc.contribution += share;
            }
            b.hp = Mathf.Max(0, b.hp - cfg.damage);
            Game.I.Hud.Feed($"The {b.info.grab} was beaten off by {string.Join(", ", names)}! -{cfg.damage:0} to {b.info.name}.", Skin.Green);
            Sfx.Play("catch", 0.6f);
            if (b.hp <= 0) EndBoss(true, Now);
        }

        void DamageBoss(ulong who, float damage, bool weak, bool harpoon)
        {
            var b = Boss;
            if (b == null || b.result != null) return;
            b.hp = Mathf.Max(0, b.hp - damage);
            var s = Score(who);
            s.damage += damage;
            s.contribution += damage;
            if (who == Me && !harpoon) Game.I.Flash($"-{damage:0} to {b.info.name}{(weak ? " (weak spot, double damage!)" : "")}", 2.5f);
            else if (who != Me && harpoon) Game.I.Hud.Feed($"{s.name} harpooned {b.info.name}! -{damage:0}", Skin.Gold);
            if (b.hp <= 0) EndBoss(true, Now);
        }

        void EndBoss(bool won, double now)
        {
            var b = Boss;
            if (b == null || b.result != null) return;
            b.result = won ? "won" : "lost";
            b.attack = null;
            b.breach = null;
            b.grabs.Clear();
            b.effect = null;
            Dazed = 0;
            if (Line.State != LineState.Idle) Line.Cancel(null);
            var col = Tint(b.info.color);
            if (won) Game.I.Banner($"{b.info.name} is defeated!", "The crew shares the prize pool.", Skin.Gold);
            else Game.I.Banner($"{b.info.name} escaped", "It slipped back into the deep. The crew still gets a share of the prize.", col);
            Sfx.Play(won ? "trophy" : "error");
            PayOut();
            resultsEnd = now + T.results;
        }

        static int BossPool(BossInfo info, int crew, bool won)
        {
            float full = (B.pool.@base + B.pool.perAngler * Mathf.Max(1, crew)) * info.hp;
            return Mathf.RoundToInt(won ? full : full * B.pool.lose);
        }

        /// <summary>Split a prize pool by contribution: an even share of part of it for everyone who helped, the rest in proportion.</summary>
        static int[] SplitPool(int pool, IList<float> contributions)
        {
            float total = contributions.Sum(c => Mathf.Max(0, c));
            int helpers = contributions.Count(c => c > 0);
            if (total <= 0 || helpers == 0) return contributions.Select(_ => 0).ToArray();
            return contributions.Select(c => c > 0 ? Mathf.FloorToInt(pool * B.pool.even / helpers + pool * (1 - B.pool.even) * (c / total)) : 0).ToArray();
        }

        /// <summary>End of the voyage: rank the crew and pay your bonuses.</summary>
        void PayOut()
        {
            var p = Game.I.Profile;
            var ranking = Scores.Values.OrderByDescending(s => s.points).ThenByDescending(s => s.catches).ToList();
            int missionsDone = Missions.Count(m => m.done);
            bool fought = Boss?.result != null, won = Boss?.result == "won";
            var mvp = fought ? ranking.OrderByDescending(s => s.contribution).FirstOrDefault() : null;
            int pool = fought ? BossPool(BossInfo, ranking.Count, won) : 0;
            var shares = SplitPool(pool, ranking.Select(r => r.contribution).ToList());
            for (int i = 0; i < ranking.Count; i++) ranking[i].share = shares[i];
            var mine = Score(Me);
            int rank = ranking.IndexOf(mine);
            var r = new VoyageResults
            {
                rank = rank + 1,
                me = Me,
                crewTotal = CrewTotal,
                pool = pool,
                fromPoints = Mathf.RoundToInt(mine.points * T.pointsBonus),
                fromMissions = missionsDone * T.missionReward,
                fromRank = mine.points > 0 && rank < T.rankBonus.Length ? T.rankBonus[rank] : 0,
                fromBoss = (won ? B.win.coins : fought ? B.lose.coins : 0) + (won && mvp == mine && mine.contribution > 0 ? B.win.mvpCoins : 0),
                fromPool = mine.share,
                missions = Missions.Select(m => (m.text, m.done)).ToList(),
                bossName = BossInfo.name,
                bossResult = Boss?.result,
                mvp = mvp != null && mvp.contribution > 0 ? (mvp == mine ? "You" : mvp.name) : null,
                ranking = ranking,
            };
            r.bonus = r.fromPoints + r.fromMissions + r.fromRank + r.fromBoss + r.fromPool;
            r.xp = V.xp + Mathf.RoundToInt(mine.points * T.pointsBonus) + (won ? B.win.xp : fought ? B.lose.xp : 0);
            Results = r;

            p.coins += r.bonus;
            p.counters.coinsEarned += r.bonus;
            p.counters.voyages++;
            if (won) p.counters.bossKills++;
            GainXp(p, r.xp);
            Achievements.Check(p);
            Game.I.Hud.Feed($"Voyage complete! You came {Ordinal(r.rank)} of {ranking.Count}: +{r.bonus:N0} coins and +{r.xp:N0} XP.", Skin.Gold);
            if (!AutoShot.Active && !NetTest.Active) p.Save();
        }

        static void GainXp(Profile p, long xp)
        {
            int before = p.Level;
            p.xp += xp;
            int after = p.Level;
            if (after <= before) return;
            int coins = 0;
            for (int l = before + 1; l <= after; l++) coins += Levels.LevelUpCoins(l);
            p.coins += coins;
            p.counters.coinsEarned += coins;
            Game.I.Hud.Toast("star", $"Level {after}", $"+{coins} coins", "New tackle may be in the shops");
            if (Multiplayer.I.Online) Multiplayer.I.SendReliable("levelup", new JObject { ["level"] = after });
            Sfx.Play("level");
        }

        public static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

        static string Clock(double seconds)
        {
            int s = Mathf.Max(0, Mathf.CeilToInt((float)seconds));
            return $"{s / 60}:{s % 60:00}";
        }

        void GoHome()
        {
            Id = 0;
            Phase = VoyagePhase.None;
            StopIndex = -1;
            Boss = null;
            Results = null;
            Event = null;
            Dazed = 0;
            Hotspots.Clear();
            Line.Cancel(null);
            SeaWorld.I.Leave();
            Player.Teleport(LandingPos(), 0);
            Game.I.Hud.Feed("You're back at the South Beach dock. The next boat docks on the quarter hour.", Skin.Accent);
            Game.I.SaveNow();
        }

        // ---- multiplayer -------------------------------------------------------------------------------------

        void Send(string type, JObject body)
        {
            if (!Multiplayer.I.Online) return;
            body["vy"] = Id;
            Multiplayer.I.SendReliable(type, body);
        }

        /// <summary>Another angler on the same voyage landed a fish, harpooned the boss or struck a grab.</summary>
        public void OnNet(string type, JObject m, ulong from)
        {
            if (!AtSea || m["vy"] == null || m["vy"].Value<long>() != Id) return;
            switch (type)
            {
                case "vcatch":
                {
                    var s = GameData.I.Fish((string)m["sp"]);
                    if (s == null || Phase == VoyagePhase.Results) break;
                    Record(from, RemoteName(from), s, (float)m["kg"], (int)m["pts"], (bool)m["ev"]);
                    float dmg = (float)m["dmg"];
                    if (dmg > 0 && Phase == VoyagePhase.Boss) DamageBoss(from, dmg, (bool)m["weak"], false);
                    break;
                }
                case "vdmg":
                    if (Phase != VoyagePhase.Boss || Boss == null || Boss.result != null) break;
                    if (Boss.breach != null) Boss.breach.strikes++;
                    Score(from).harpoons++;
                    DamageBoss(from, (float)m["dmg"], false, true);
                    break;
                case "vstrike":
                {
                    var g = Boss?.grabs.Find(x => x.id == (int)m["g"]);
                    if (g != null && Boss.result == null) ApplyStrike(g, from);
                    break;
                }
            }
        }
    }
}
