// Developer tool: run the game with -autoshot <folder> and it walks through a
// few views and menus, saving a screenshot of each, then quits. Nothing is
// saved to your profile in this mode.

using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LastCast
{
    public class AutoShot : MonoBehaviour
    {
        string folder;
        public static bool Active { get; private set; }

        public static void StartIfAsked()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-autoshot");
            if (i < 0 || i + 1 >= args.Length) return;
            Active = true;
            var shot = new GameObject("AutoShot").AddComponent<AutoShot>();
            shot.folder = args[i + 1];
            Directory.CreateDirectory(shot.folder);
        }

        float slowLogged;

        void Update()
        {
            // Note slow frames in the log, with what the voyage was doing.
            if (Time.unscaledDeltaTime > 0.2f && Time.realtimeSinceStartup - slowLogged > 1)
            {
                slowLogged = Time.realtimeSinceStartup;
                var v = Voyage.I;
                Debug.Log($"[autoshot] slow frame {Time.unscaledDeltaTime:0.00}s at {Time.realtimeSinceStartup:0.0}s, voyage {v?.Phase} stop {v?.StopIndex}");
            }
        }

        IEnumerator Shot(string name)
        {
            Debug.Log($"[autoshot] {name} at {Time.realtimeSinceStartup:0.0}s, frame {Time.frameCount}");
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, name + ".png"));
            yield return new WaitForSeconds(0.6f);
        }

        /// <summary>Stand at a world point (or the nearest dry ground) and look towards another.</summary>
        void StandAt(float x, float y, float lookX, float lookY, float pitch = 4)
        {
            var map = WorldMap.I;
            for (int r = 0; r < 800 && !map.IsWalkable(x, y); r += 10) { x += Mathf.Cos(r) * 10; y += Mathf.Sin(r) * 10; }
            var p = map.ToUnity(x, y);
            var look = map.ToUnity(lookX, lookY) - p;
            Game.I.Player.Init(p, Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg);
            Game.I.Player.SetPitch(pitch);
        }

        IEnumerator Start()
        {
            var g = Game.I;
            var data = GameData.I;
            g.Profile.worldMap = true; // in memory only
            g.Profile.pets.AddRange(new[] { "dragon", "owl", "lilyfrog" });
            g.Profile.pet = "dragon";
            g.Profile.coins += 20000;
            yield return new WaitForSeconds(5);
            g.Hud.OpenForScreenshot("title");
            yield return new WaitForSeconds(0.5f);
            yield return Shot("0-title");
            g.Hud.OpenForScreenshot("none");
            yield return Shot("1-spawn");

            // A chum bucket: put down, picked up (keeping what it had left), put down somewhere else.
            g.Profile.chum += 1;
            var chum = g.Chum;
            chum.PlaceOrPickUp();
            bool placed = chum.Out;
            yield return new WaitForSeconds(1);
            float life = chum.Life;
            chum.PlaceOrPickUp();
            bool carried = chum.Carrying && !chum.Out;
            var p0 = g.Player.transform.position;
            g.Player.Teleport(p0 + g.Player.Forward * -6, g.Player.Yaw);
            chum.PlaceOrPickUp();
            Debug.Log($"[autoshot] chum: placed {placed}, picked up {carried}, placed again {chum.Out} with {chum.Life:0.0}s left (was {life:0.0}s), buckets left {g.Profile.chum}");
            chum.PickUp();

            // South Beach dock and the boat.
            StandAt(1600, 2050, data.boat.x, data.boat.y, 2);
            yield return new WaitForSeconds(1.5f);
            yield return Shot("2-dock-boat");

            // A reed bed, a lily marsh and the forest.
            foreach (var (zoneId, name) in new[] { ("weedyCove", "3-reeds"), ("marsh", "4-lilies") })
            {
                var z = data.zones.Find(q => q.id == zoneId);
                if (z?.rect == null) continue;
                float cx = z.rect.x + z.rect.w / 2, cy = z.rect.y + z.rect.h / 2;
                // Walk south from the middle of the water to the first dry ground.
                float sy = cy;
                while (sy < cy + 3000 && !WorldMap.I.IsWalkable(cx, sy)) sy += 10;
                StandAt(cx, sy + 30, cx, cy, 8);
                yield return new WaitForSeconds(1.5f);
                yield return Shot(name);
            }
            StandAt(1450, 300, 1450, 900, -2);
            yield return new WaitForSeconds(1.5f);
            yield return Shot("5-pine-point");

            // The Bait Shop's stall, a tackle shop, the zoo wagon and a signpost, from a few metres away.
            foreach (var (shopId, name) in new[] { ("bait", "5b-bait-shop"), (data.shops.Find(s => s.IsTackle)?.id, "5c-tackle-shop") })
            {
                var shop = data.ShopById(shopId);
                if (shop == null) continue;
                StandAt(shop.x + 60, shop.y + 110, shop.x, shop.y - 20, 6);
                yield return new WaitForSeconds(1f);
                yield return Shot(name);
            }
            var zoo = Zoo.Now.spot;
            StandAt(zoo.x - 70, zoo.y + 90, zoo.x, zoo.y, 4);
            yield return new WaitForSeconds(1f);
            yield return Shot("5d-zoo-wagon");
            var area = data.areas.Find(a => a.label != null);
            if (area != null)
            {
                StandAt(area.label.x + 40, area.label.y + 50, area.label.x, area.label.y, 4);
                yield return new WaitForSeconds(1f);
                yield return Shot("5e-signpost");
            }

            // The drawbridge on the East River: down, then lifted as the boat comes through.
            var bridge = data.structures.Find(s => s.type == "bridge" && Drawbridge.Crossing(s, (x, y) => 0) != null);
            if (bridge != null)
            {
                float bx = bridge.x + bridge.w / 2, by = bridge.y + bridge.h / 2;
                double interval = data.boat.interval;
                BoatSchedule.Offset = 0;
                double c = ((BoatSchedule.Now % interval) + interval) % interval;
                BoatSchedule.Offset = (interval * 0.5 - c + interval) % interval; // the boat is away
                StandAt(bx - 260, by + 120, bx, by, 2);
                yield return new WaitForSeconds(2f);
                yield return Shot("5f-bridge-down");
                c = ((BoatSchedule.Now % interval) + interval) % interval;
                BoatSchedule.Offset += (interval - data.boat.arrive + 3 - c + interval) % interval; // 3 s into its arrival
                yield return new WaitForSeconds(3.5f);
                yield return Shot("5g-bridge-up");
                BoatSchedule.Offset = 0;
            }

            // The UI: a trophy catch card, then the menus.
            StandAt(data.spawn.x, data.spawn.y, data.spawn.x, data.spawn.y - 400, 6);
            var pike = data.Fish("pike");
            g.Hud.ShowCatch(new CatchInfo { species = pike, kg = 11.4f, points = 98, coins = 122, xp = 98, zone = "Deep Water", isNew = true, tier = "trophy", trophyName = "Old Gnasher" });
            g.Hud.Toast("star", "Level 7", "+140 coins", "New tackle may be in the shops");
            yield return new WaitForSeconds(1.2f);
            yield return Shot("6-catch-card");
            foreach (var (key, name) in new[] { ("tackle", "7-tackle"), ("index", "8-index"), ("map", "9-map"), ("pets", "10-pets"), ("zoo", "11-zoo"), ("armour", "12-armour"), ("goals", "13-goals"), ("events", "14-events"), ("graphics", "14b-graphics") })
            {
                g.Hud.OpenForScreenshot(key);
                yield return new WaitForSeconds(key == "index" || key == "pets" || key == "zoo" ? 3 : 1);
                yield return Shot(name);
            }
            g.Hud.OpenForScreenshot("none");

            // Fast travel: a place picked on the map, then the trip.
            var pine = data.areas.Find(a => a.name == "Pine Point" && a.label != null) ?? data.areas.Find(a => a.label != null);
            var dest = new Vector2(pine.label.x, pine.label.y + 30);
            g.Hud.OpenForScreenshot("map");
            g.Hud.PickTravel(pine.name, dest);
            yield return new WaitForSeconds(1);
            yield return Shot("9b-map-travel");
            var before = g.Player.WorldPos;
            g.FastTravel(dest, pine.name);
            yield return new WaitForSeconds(1.5f);
            Debug.Log($"[autoshot] fast travel to {pine.name}: from {before} to {g.Player.WorldPos} ({Vector2.Distance(g.Player.WorldPos, dest) / 10:0.0} m from the pin), menu {g.MenuOpen}");
            yield return Shot("9c-after-travel");
            yield return VoyageTour();
            yield return PoseTour();
            Application.Quit();
        }

        /// <summary>
        /// The animations, frozen at a few moments: each boss (and what it grabs the rail with) in a studio far
        /// below the world, then a line of other players walking, running, hopping, charging a cast, casting and reeling.
        /// </summary>
        IEnumerator PoseTour()
        {
            var studio = new Vector3(0, -600, 0);
            var camGo = new GameObject("Pose camera");
            var cam = camGo.AddComponent<Camera>();
            cam.depth = 50;
            cam.farClipPlane = 400;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.7f, 0.82f);
            Game.I.Hud.OpenForScreenshot("hidden");
            foreach (var info in GameData.I.voyage.bosses)
            {
                var boss = new GameObject("Pose " + info.id);
                boss.transform.position = studio;
                boss.transform.rotation = Quaternion.Euler(0, 160, 0);
                var rig = LimbRig.Attach(boss, BossMeshes.Body(info), WorldBuilder.LitMaterial, 10, BossMotion.Joints(info.id));
                var grab = new GameObject("Pose grab");
                grab.transform.position = studio + new Vector3(-30, 0, 10);
                var grabRig = LimbRig.Attach(grab, BossMeshes.Grabber(info, 1), WorldBuilder.LitMaterial, 12);
                camGo.transform.position = studio + new Vector3(-14, 16, -58);
                camGo.transform.LookAt(studio + new Vector3(-8, 4, 0));
                int n = 0;
                foreach (var (t, agitation, rear) in new[] { (0f, 0f, 0f), (1.3f, 0.5f, 0f), (2.6f, 1f, 1f) })
                {
                    BossMotion.Body(rig, info.id, t, agitation, rear);
                    BossMotion.Grabber(grabRig, info.id, t * 2, agitation, 0);
                    yield return null;
                    yield return Shot($"24-pose-{info.id}-{n++}");
                }
                Destroy(boss);
                Destroy(grab);
            }

            // Other players, side on.
            var poses = new (string what, AnglerState st, Vector3 vel)[]
            {
                ("idle", new AnglerState(), Vector3.zero),
                ("walk", new AnglerState(), new Vector3(0, 0, Tuning.WalkSpeed)),
                ("run", new AnglerState { sp = true }, new Vector3(0, 0, Tuning.WalkSpeed * Tuning.SprintSpeed)),
                ("charge", new AnglerState { ch = 0.95f }, Vector3.zero),
                ("cast", new AnglerState { ls = (int)LineState.Casting }, Vector3.zero),
                ("reel", new AnglerState { ls = (int)LineState.Reeling, tn = 0.6f }, Vector3.zero),
            };
            var anglers = new List<RemoteAngler>();
            for (int i = 0; i < poses.Length; i++)
            {
                var st = poses[i].st;
                st.id = (ulong)(900 + i);
                st.n = poses[i].what;
                st.col = i;
                var at = studio + new Vector3(i * 1.6f, 0, 0);
                st.x = at.x; st.y = at.y; st.z = at.z;
                st.yaw = 90; // side on to the camera
                st.bx = at.x + 6; st.by = at.y; st.bz = at.z + 8;
                var r = RemoteAngler.Create(st.id);
                r.TestVelocity = poses[i].vel;
                r.Apply(st);
                anglers.Add(r);
            }
            camGo.transform.position = studio + new Vector3(4, 1.4f, -9);
            camGo.transform.LookAt(studio + new Vector3(4, 1.0f, 0));
            cam.fieldOfView = 55;
            for (int k = 0; k < 3; k++)
            {
                // The cast pose is the moment after letting go: restart it each time.
                if (k > 0) anglers[4].RestartCast();
                yield return new WaitForSeconds(0.12f + k * 0.21f);
                yield return Shot($"25-pose-anglers-{k}");
            }
            foreach (var r in anglers) Destroy(r.gameObject);
            Destroy(camGo);
        }

        /// <summary>The voyage boat at the dock, then a whole (shortened) voyage: stops, an event, the boss, the results.</summary>
        IEnumerator VoyageTour()
        {
            var g = Game.I;
            var data = GameData.I;
            var v = Voyage.I;
            // Wind the clock to a moment the boat is docked.
            double now = BoatSchedule.Now, c = now % data.boat.interval;
            BoatSchedule.Offset = data.boat.interval - c + 40;
            StandAt(data.boat.landing.x, data.boat.landing.y + 60, data.boat.x, data.boat.y - 60, 6);
            yield return new WaitForSeconds(1.5f);
            yield return Shot("15-boat-docked");

            Voyage.TestTiming = new VoyageTiming
            {
                stops = 3, missions = 3, missionReward = 100, outbound = 3, fishing = 12, sailing = 3, bossIntro = 6, boss = 60, results = 10,
                eventEarliest = 3, eventLength = 7, pointsBonus = 0.25f, rankBonus = new[] { 300, 150, 75 },
            };
            v.TestLaunch();
            yield return new WaitForSeconds(1.5f);
            yield return Shot("16-sea-outbound");
            while (!(v.Phase == VoyagePhase.Fishing && v.Event != null)) yield return null;
            Fishing.ForceCharge = 0.55f;
            g.Player.SetPitch(14);
            yield return new WaitForSeconds(0.8f);
            yield return Shot("17-sea-event-ghost");
            Fishing.ForceCharge = null;
            while (!(v.Phase == VoyagePhase.Fishing && v.StopIndex == 2)) yield return null;
            yield return new WaitForSeconds(4);
            yield return Shot("18-sea-sunset");
            while (v.Phase != VoyagePhase.BossIntro) yield return null;
            yield return new WaitForSeconds(4.6f);
            g.Player.Face(SeaWorld.Origin + new Vector3(0, 6, -60));
            yield return new WaitForSeconds(0.3f);
            yield return Shot("19-boss-rising");
            yield return new WaitForSeconds(2.5f);
            if (v.Boss?.weak != null)
            {
                // Look out past the weak spot, where the boss lurks.
                var w = SeaWorld.ToUnity(v.Boss.weak.x, v.Boss.weak.y);
                var dir = new Vector3(w.x - SeaWorld.Origin.x, 0, w.z - SeaWorld.Origin.z).normalized;
                g.Player.Face(SeaWorld.Origin + dir * 40 + Vector3.up * 2);
                yield return new WaitForSeconds(0.3f);
                yield return Shot("19b-boss-weak-spot");
            }
            while (v.Boss == null || v.Boss.breach == null) yield return null;
            yield return new WaitForSeconds(2.5f);
            if (v.Boss.breach != null) g.Player.Face(SeaWorld.ToUnity(v.Boss.breach.x, v.Boss.breach.y, 1));
            yield return new WaitForSeconds(0.3f);
            yield return Shot("20-boss-breach");
            while (v.Boss.grabs.Count == 0 && v.Boss.result == null) yield return null;
            yield return new WaitForSeconds(1);
            if (v.Boss.grabs.Count > 0)
            {
                var grab = v.Boss.grabs[0];
                var at = SeaWorld.ToUnity(grab.x, grab.y);
                var stand = SeaWorld.ToUnity(grab.x - 60, 600);
                g.Player.Teleport(stand, 0);
                g.Player.Face(at + Vector3.up * 2.5f);
                yield return new WaitForSeconds(0.4f);
                yield return Shot("21-boss-grab");
            }
            while (v.Boss.attackArea == null && v.Boss.result == null) yield return null;
            if (v.Boss.attackArea != null)
            {
                g.Player.Teleport(SeaWorld.ToUnity(700, 600), 90);
                g.Player.SetPitch(35);
                yield return new WaitForSeconds(1.2f);
                yield return Shot("22-boss-slam");
            }
            while (v.Phase != VoyagePhase.Results) yield return null;
            yield return new WaitForSeconds(1.5f);
            yield return Shot("23-voyage-results");
            BoatSchedule.Offset = 0;
        }
    }
}
