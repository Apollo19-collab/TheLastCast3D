// Developer tool: run the game with -autoshot <folder> and it walks through a
// few views and menus, saving a screenshot of each, then quits. Nothing is
// saved to your profile in this mode.

using System.Collections;
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

            // The UI: a trophy catch card, then the menus.
            StandAt(data.spawn.x, data.spawn.y, data.spawn.x, data.spawn.y - 400, 6);
            var pike = data.Fish("pike");
            g.Hud.ShowCatch(new CatchInfo { species = pike, kg = 11.4f, points = 98, coins = 122, xp = 98, zone = "Deep Water", isNew = true, tier = "trophy", trophyName = "Old Gnasher" });
            g.Hud.Toast("star", "Level 7", "+140 coins", "New tackle may be in the shops");
            yield return new WaitForSeconds(1.2f);
            yield return Shot("6-catch-card");
            foreach (var (key, name) in new[] { ("tackle", "7-tackle"), ("index", "8-index"), ("map", "9-map"), ("pets", "10-pets"), ("zoo", "11-zoo"), ("armour", "12-armour"), ("goals", "13-goals"), ("events", "14-events") })
            {
                g.Hud.OpenForScreenshot(key);
                yield return new WaitForSeconds(key == "index" || key == "pets" || key == "zoo" ? 3 : 1);
                yield return Shot(name);
            }
            g.Hud.OpenForScreenshot("none");
            yield return VoyageTour();
            Application.Quit();
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
