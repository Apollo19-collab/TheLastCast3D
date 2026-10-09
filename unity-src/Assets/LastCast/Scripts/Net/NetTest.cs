// Developer tool: test multiplayer with two copies of the game on one PC.
//
//   TheLastCast.exe -nettest host <folder>   hosts, writes the join code to <folder>\code.txt
//   TheLastCast.exe -nettest join <folder>   waits for that code, joins, says hello in chat
//   add -netvoyage to both and they then board the boat and sail a short voyage together
//
// Each takes a screenshot (host.png / join.png) once they can see each other,
// then quits. Nothing is saved to your profile in this mode.

using System.Collections;
using System.IO;
using UnityEngine;

namespace LastCast
{
    public class NetTest : MonoBehaviour
    {
        public static bool Active { get; private set; }
        string role, folder;

        public static void StartIfAsked()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-nettest");
            if (i < 0 || i + 2 >= args.Length) return;
            Active = true;
            var t = new GameObject("NetTest").AddComponent<NetTest>();
            t.role = args[i + 1];
            t.folder = args[i + 2];
            Directory.CreateDirectory(t.folder);
        }

        void Log(string s) => File.AppendAllText(Path.Combine(folder, role + ".log"), $"{System.DateTime.Now:HH:mm:ss} {s}\n");

        IEnumerator Start()
        {
            var g = Game.I;
            g.Hud.OpenForScreenshot("none");
            var map = WorldMap.I;
            var spawn = map.ToUnity(GameData.I.spawn.x, GameData.I.spawn.y);
            yield return new WaitForSeconds(2);
            var mp = Multiplayer.I;
            if (role == "host")
            {
                Multiplayer.PlayerName = "Hostie";
                g.Player.Init(spawn, 0);
                var codeFile = Path.Combine(folder, "code.txt");
                if (File.Exists(codeFile)) File.Delete(codeFile);
                var task = mp.Host("Test lake", true);
                while (!task.IsCompleted) yield return null;
                Log($"host: {task.Result} {mp.Status} code={mp.Session?.Code}");
                if (!task.Result) { Application.Quit(); yield break; }
                File.WriteAllText(codeFile, mp.Session.Code);
                // Wait for a friend, then look at them.
                float waited = 0;
                while (mp.Remotes.Count == 0 && waited < 60) { waited += Time.deltaTime; yield return null; }
                Log($"remotes: {mp.Remotes.Count}");
                yield return new WaitForSeconds(8);
                g.Hud.OpenForScreenshot("online");
                yield return new WaitForSeconds(1);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "host-online.png"));
                yield return new WaitForSeconds(1);
                g.Hud.OpenForScreenshot("none");
                yield return new WaitForSeconds(1);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "host.png"));
                yield return new WaitForSeconds(6);
                if (voyage)
                {
                    File.WriteAllText(Path.Combine(folder, "voyage.txt"), ((long)MapEvents.Now + 55).ToString());
                    yield return VoyageTest();
                }
            }
            else
            {
                Multiplayer.PlayerName = "Joiner";
                // Stand a few metres north of the spawn, facing back towards it.
                g.Player.Init(spawn + Vector3.forward * 6 + Vector3.right * 1, 180);
                var codeFile = Path.Combine(folder, "code.txt");
                float waited = 0;
                while (!File.Exists(codeFile) && waited < 60) { waited += Time.deltaTime; yield return null; }
                yield return new WaitForSeconds(0.5f);
                var code = File.ReadAllText(codeFile).Trim();
                var task = mp.Join(code);
                while (!task.IsCompleted) yield return null;
                Log($"join {code}: {task.Result} {mp.Status}");
                if (!task.Result) { Application.Quit(); yield break; }
                g.Player.Init(spawn + Vector3.forward * 6 + Vector3.right * 1, 180);
                yield return new WaitForSeconds(3);
                mp.SendReliable("chat", new Newtonsoft.Json.Linq.JObject { ["text"] = "Hello from the other window!" });
                Log($"remotes: {mp.Remotes.Count}");
                yield return new WaitForSeconds(5);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "join.png"));
                yield return new WaitForSeconds(10);
                if (voyage) yield return VoyageTest();
            }
            Log("done");
            Application.Quit();
        }

        bool voyage => System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-netvoyage") >= 0;

        /// <summary>With -netvoyage: both copies board the same boat and sail a short voyage together. The host lands a
        /// fish in the weak spot and harpoons a breach; the joiner beats off a grab. Both log the boss's health.</summary>
        IEnumerator VoyageTest()
        {
            var g = Game.I;
            var data = GameData.I;
            var v = Voyage.I;
            var file = Path.Combine(folder, "voyage.txt");
            while (!File.Exists(file)) yield return null;
            long launchAt = long.Parse(File.ReadAllText(file).Trim());
            // Shift both clocks the same way so the boat sets off at that moment.
            long t0 = (long)(System.Math.Floor(launchAt / data.boat.interval) * data.boat.interval + data.boat.board + data.boat.depart);
            BoatSchedule.Offset = t0 - launchAt;
            Voyage.TestTiming = new VoyageTiming
            {
                stops = 3, missions = 3, missionReward = 100, outbound = 3, fishing = 6, sailing = 2, bossIntro = 3, boss = 45, results = 8,
                eventEarliest = 1, eventLength = 3, pointsBonus = 0.25f, rankBonus = new[] { 300, 150, 75 },
            };
            g.Player.Teleport(WorldMap.I.ToUnity(data.boat.landing.x, data.boat.landing.y + (role == "host" ? -20 : 20)), 90);
            while (LakeBoat.I.Pose.phase != BoatPhase.Docked) yield return null;
            yield return new WaitForSeconds(1);
            Log($"boat docked, can board: {v.CanBoard}; boarding: {v.ToggleBoard()} (voyage {v.Boarded})");
            yield return new WaitForSeconds(3);
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, $"{role}-aboard.png"));
            while (!v.AtSea) yield return null;
            Log($"at sea: voyage {v.Id}, crew {v.Crew}, stops {string.Join(", ", v.Stops.ConvertAll(s => s.loc.name))}, boss {v.BossId}, missions {string.Join("; ", v.Missions.ConvertAll(m => m.text))}");
            while (v.Phase != VoyagePhase.Boss) yield return null;
            yield return new WaitForSeconds(1);
            Log($"boss: {v.Boss.info.name} hp {v.Boss.hp}/{v.Boss.max}, crew {v.Crew}");
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, $"{role}-boss.png"));
            if (role == "host")
            {
                yield return new WaitForSeconds(2);
                v.OnCatch(new CatchInfo { species = data.Fish("cod"), kg = 6, points = 50, hotspot = true });
                Log($"host landed a 50-point fish in the weak spot: hp {v.Boss.hp}");
                while (v.Boss.breach == null && v.Boss.result == null) yield return null;
                if (v.Boss.breach != null)
                {
                    v.OnLand(v.Boss.breach.x, v.Boss.breach.y);
                    Log($"host harpooned the breach: hp {v.Boss.hp}");
                }
            }
            else
            {
                while (v.Boss.grabs.Count == 0 && v.Boss.result == null) yield return null;
                if (v.Boss.grabs.Count > 0)
                {
                    var grab = v.Boss.grabs[0];
                    g.Player.Teleport(SeaWorld.ToUnity(grab.x, grab.y < 600 ? grab.y + 20 : grab.y - 20), 0);
                    yield return new WaitForSeconds(0.5f);
                    int strikes = 0;
                    while (v.Boss.grabs.Contains(grab) && strikes < 60)
                    {
                        if (v.TryStrike()) strikes++;
                        yield return new WaitForSeconds(0.3f);
                    }
                    Log($"joiner struck the grab {strikes} times; cleared: {!v.Boss.grabs.Contains(grab)}; hp {v.Boss.hp}");
                }
            }
            float next = 0;
            while (v.Phase == VoyagePhase.Boss)
            {
                if (Time.time >= next)
                {
                    next = Time.time + 3;
                    Log($"boss clock {v.Boss.clock:0.0}: hp {v.Boss.hp}/{v.Boss.max}, phase {v.Boss.phase}");
                }
                yield return null;
            }
            yield return new WaitForSeconds(1);
            var r = v.Results;
            Log($"results: boss {r?.bossResult}, rank {r?.rank}, bonus {r?.bonus}, xp {r?.xp}, pool {r?.pool}, crew: " +
                string.Join(", ", r?.ranking.ConvertAll(s => $"{s.name} pts {s.points} dmg {s.damage} contrib {s.contribution} share {s.share}") ?? new System.Collections.Generic.List<string>()));
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, $"{role}-results.png"));
            yield return new WaitForSeconds(2);
        }
    }
}
