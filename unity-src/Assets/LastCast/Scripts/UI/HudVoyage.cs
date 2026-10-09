// The HUD for boat voyages (the web game's voyage panel, boss bar and results):
//
//   top right     the voyage: this stop, time left, the special event, crew missions, points
//   top centre    the boss's health, phase and clock, and what it's about to do
//   centre        "E: board the boat", "E: strike the Tentacle!", "Get out of the red!"
//   bottom right  a sea chart instead of the minimap
//   middle        the results when it's over

using System.Linq;
using UnityEngine;

namespace LastCast
{
    public partial class Hud
    {
        static Voyage Vy => Voyage.I;

        /// <summary>The boat on the player card, on the lake.</summary>
        void BoatCardLine(System.Collections.Generic.List<(string, string, Color)> lines)
        {
            var v = Vy;
            if (v == null || v.AtSea || !LakeBoat.I) return;
            var pose = LakeBoat.I.Pose;
            if (v.Boarded != 0) lines.Add(("boat", pose.phase == BoatPhase.Docked ? $"Aboard! Sailing in {Clock(pose.left)} (E to step off)" : "Aboard! Heading out to sea...", Skin.Accent));
            else if (pose.phase == BoatPhase.Docked) lines.Add(("boat", $"Boat at South Beach dock: board within {Clock(pose.left)}", Skin.Gold));
            else if (pose.phase == BoatPhase.Arriving) lines.Add(("boat", "The voyage boat is coming in now!", Skin.Gold));
            else lines.Add(("boat", $"Next voyage boat docks in {Clock(pose.nextDock)}", Skin.Dim));
        }

        /// <summary>The prompt under the crosshair for the boat and the boss, or null.</summary>
        (string text, Color colour)? VoyagePrompt()
        {
            var v = Vy;
            if (v == null) return null;
            var f = Game.I.Fishing;
            if (!v.AtSea)
            {
                if (f.State != LineState.Idle || f.Charge >= 0 || !LakeBoat.I) return null;
                if (v.CanBoard) return ($"E: board the boat for a voyage (sails in {Clock(LakeBoat.I.Pose.left)})", Skin.Gold);
                if (v.CanStepOff) return ("E: step back off the boat", Skin.Text);
                return null;
            }
            var b = v.Boss;
            if (b != null && b.result == null && b.attackArea != null)
            {
                var me = v.MySeaPos;
                if (GameData.I.voyage.DeckArea(b.attackArea).Contains(me.x, me.y)) return ("GET OUT OF THE RED!", Skin.Red);
            }
            if (v.Dazed > 0) return ($"Dazed! {Mathf.CeilToInt(v.Dazed)}", Skin.Red);
            var g = v.GrabInReach;
            if (g != null && f.State == LineState.Idle) return ($"E: strike the {b.info.grab}! ({g.hp} to go)", Skin.Gold);
            if (g == null && b != null && b.result == null && b.grabs.Count > 0 && f.State == LineState.Idle) return ($"The {b.info.grab} has grabbed the rail! Go and beat it off (E)", Skin.Hex(b.info.color));
            return null;
        }

        // ---- the voyage panel (top right) --------------------------------------------------------------

        void DrawVoyagePanel(float top)
        {
            var v = Vy;
            var V = GameData.I.voyage;
            if (MenuOpen || v.Phase == VoyagePhase.None) return;
            var stop = v.Stop ?? v.Stops[0];
            var accent = Color.Lerp(Skin.Hex(stop.loc.water[1]), Color.white, 0.45f); // the stop's water, light enough to read
            int rows = 3 + v.Missions.Count + (v.Phase == VoyagePhase.Fishing ? 1 : 0);
            var r = new Rect(Screen.width - S(304), top, S(290), S(34) + rows * S(20) + S(12));
            GUI.Box(r, GUIContent.none, Box(Skin.Rounded(14, Skin.PanelTop, Skin.PanelBottom, accent, 1.5f, 0, accent), 16));
            float x = r.x + S(12), w = r.width - S(24), y = r.y + S(8);
            Icon(new Rect(x, y + S(1), S(16), S(16)), "boat");
            string head = v.Phase switch
            {
                VoyagePhase.Outbound => "SAILING OUT",
                VoyagePhase.Sailing => "UNDER WAY",
                VoyagePhase.BossIntro or VoyagePhase.Boss => "BOSS FIGHT",
                VoyagePhase.Results => "VOYAGE OVER",
                _ => $"STOP {v.StopIndex + 1} OF {v.Stops.Count}",
            };
            Label(new Rect(x + S(22), y, w, S(18)), head, new GUIStyle(small) { fontStyle = FontStyle.Bold }, accent);
            Label(new Rect(x, y, w, S(18)), Clock(v.PhaseLeft), smallDim, null, TextAnchor.UpperRight);
            y += S(22);
            string where = v.Phase switch
            {
                VoyagePhase.Outbound => $"Heading for {v.NextStop.loc.name}",
                VoyagePhase.Sailing => $"Next: {v.NextStop.loc.name} ({v.NextStop.time})",
                VoyagePhase.BossIntro or VoyagePhase.Boss => v.BossInfo.name,
                VoyagePhase.Results => "Heading home",
                _ => $"{stop.loc.name} · {stop.time}",
            };
            Label(new Rect(x, y, w, S(18)), where, new GUIStyle(small) { fontStyle = FontStyle.Bold });
            y += S(20);
            if (v.Phase == VoyagePhase.Fishing)
            {
                var ev = V.Event(stop.loc.@event);
                if (v.Event != null)
                {
                    var ec = Skin.Hex(v.Event.color);
                    Label(new Rect(x, y, w, S(18)), $"{v.Event.name}!", new GUIStyle(small) { fontStyle = FontStyle.Bold }, ec);
                    Label(new Rect(x, y, w, S(18)), Clock(v.EventLeft), small, ec, TextAnchor.UpperRight);
                    Bar(new Rect(x, y + S(17), w, S(3)), v.EventLeft / v.T.eventLength, Color.Lerp(ec, Color.white, 0.2f), ec);
                }
                else
                {
                    bool done = v.T.fishing - v.PhaseLeft > stop.eventAt;
                    Label(new Rect(x, y, w, S(18)), done ? $"{ev.name} is over" : $"Coming up: {ev.name}", smallDim);
                }
                y += S(20);
            }
            // Crew missions.
            foreach (var m in v.Missions)
            {
                Label(new Rect(x, y, w - S(44), S(18)), (m.done ? "✓ " : "") + m.text, new GUIStyle(small) { fontSize = Mathf.RoundToInt(S(11)), clipping = TextClipping.Clip, wordWrap = false }, m.done ? Skin.Green : (Color?)null);
                Label(new Rect(x, y, w, S(18)), m.done ? "done" : $"{m.progress}/{m.goal}", smallDim, m.done ? Skin.Green : (Color?)null, TextAnchor.UpperRight);
                y += S(20);
            }
            var mine = v.Scores.TryGetValue(v.Me, out var s) ? s : null;
            int rank = v.Scores.Values.OrderByDescending(q => q.points).ThenByDescending(q => q.catches).ToList().IndexOf(mine) + 1;
            Label(new Rect(x, y, w, S(18)), $"You: {mine?.points ?? 0} pts" + (v.Scores.Count > 1 ? $" · {Voyage.Ordinal(rank)} of {v.Scores.Count}" : "") + $" · Crew {v.CrewTotal}", smallDim);
        }

        // ---- the boss bar (top centre) ---------------------------------------------------------------------

        void DrawBossBar()
        {
            var v = Vy;
            var b = v.Boss;
            if (MenuOpen || v.Phase != VoyagePhase.Boss || b == null || b.result != null) return;
            var V = GameData.I.voyage;
            var col = Color.Lerp(Skin.Hex(b.info.color), Color.white, 0.25f);
            float W = Screen.width, w = Mathf.Min(S(460), W - S(560));
            var r = new Rect(W / 2 - w / 2, S(88), w, S(58));
            GUI.Box(r, GUIContent.none, Box(Skin.Rounded(14, Skin.PanelTop, Skin.PanelBottom, col, 1.5f, 0, col), 16));
            float x = r.x + S(12), iw = r.width - S(24);
            Icon(new Rect(x, r.y + S(7), S(18), S(18)), "boss");
            Label(new Rect(x + S(24), r.y + S(6), iw, S(20)), $"{b.info.name.ToUpper()}  <color=#{ColorUtility.ToHtmlStringRGB(Skin.Dim)}>· {V.boss.phases[b.phase].name}</color>", new GUIStyle(bold) { fontSize = Mathf.RoundToInt(S(13)) }, col);
            Label(new Rect(x, r.y + S(6), iw, S(20)), $"{b.hp:0} / {b.max:0} · {Clock(v.T.boss - b.clock)}", smallDim, null, TextAnchor.UpperRight);
            Bar(new Rect(x, r.y + S(30), iw, S(12)), b.Fraction, Color.Lerp(col, Skin.Red, 0.4f), col);
            // Phase marks on the bar.
            foreach (var ph in V.boss.phases.Skip(1))
                GUI.DrawTexture(new Rect(x + iw * ph.at - S(1), r.y + S(28), S(2), S(16)), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(1, 1, 1, 0.6f), 0, 0);

            // What it's doing: an attack coming, its effect, a breach, grabs.
            var notes = new System.Collections.Generic.List<(string, Color)>();
            if (b.attack != null)
            {
                var a = V.Attack(b.attack);
                string name = a.area ? b.info.slam : a.name;
                string what = a.area ? $"get away from {V.DeckArea(b.attackArea).name}!" : a.desc;
                notes.Add(($"⚠ {name} in {Mathf.CeilToInt(b.attackAt - b.clock)}: {what}", Skin.Red));
            }
            if (b.effect != null)
            {
                var a = V.Attack(b.effect);
                notes.Add(($"{a.name}: {a.desc.Split(':').Last().Trim()} ({Mathf.CeilToInt(b.effectUntil - b.clock)} s)", Skin.Hex("#9ad1ff")));
            }
            if (b.breach != null)
                notes.Add((b.breach.struck ? $"Harpooned! It dives in {Mathf.CeilToInt(b.breach.until - b.clock)} s" : $"BREACHING! Cast into the golden ring to harpoon it ({Mathf.CeilToInt(b.breach.until - b.clock)} s)", Skin.Gold));
            foreach (var g in b.grabs)
                notes.Add(($"{b.info.grab} on the rail: {g.hp} hits to go, {Mathf.CeilToInt(g.until - b.clock)} s (E)", Color.Lerp(col, Color.white, 0.45f)));
            float y = r.yMax + S(4);
            var st = new GUIStyle(bold) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(S(13)) };
            foreach (var (text, c) in notes)
            {
                float tw = Mathf.Min(st.CalcSize(new GUIContent(text)).x + S(28), W - S(40));
                var nr = new Rect(W / 2 - tw / 2, y, tw, S(26));
                GUI.Box(nr, GUIContent.none, pill);
                Label(nr, text, new GUIStyle(st) { clipping = TextClipping.Clip }, c);
                y += S(29);
            }
        }

        // ---- results ------------------------------------------------------------------------------------------

        void DrawResults()
        {
            var v = Vy;
            var res = v.Results;
            if (res == null || v.Phase != VoyagePhase.Results || (Open != Menu.None && Open != Menu.Title)) return;
            float W = Screen.width, H = Screen.height;
            float w = Mathf.Min(S(560), W - S(40));
            int rankRows = Mathf.Min(8, res.ranking.Count);
            float h = S(150) + S(22) * 5 + S(22) * res.missions.Count + S(24) * rankRows + S(40);
            var r = new Rect(W / 2 - w / 2, Mathf.Max(S(60), H / 2 - h / 2 - S(20)), w, h);
            GUI.Box(r, GUIContent.none, window);
            float x = r.x + S(20), iw = r.width - S(40), y = r.y + S(14);
            bool won = res.bossResult == "won";
            Label(new Rect(r.x, y, r.width, S(28)), "VOYAGE COMPLETE", new GUIStyle(title) { alignment = TextAnchor.MiddleCenter }, Skin.Gold);
            y += S(30);
            string boss = res.bossResult == null ? "" : won ? $"{res.bossName} defeated!" : $"{res.bossName} escaped.";
            if (res.mvp != null) boss += $" MVP: {res.mvp}.";
            Label(new Rect(r.x, y, r.width, S(20)), boss, new GUIStyle(bold) { alignment = TextAnchor.MiddleCenter }, won ? Skin.Green : Skin.Hex("#ffb4a2"));
            y += S(26);
            Label(new Rect(r.x, y, r.width, S(22)), $"You came {Voyage.Ordinal(res.rank)} of {res.ranking.Count}: <b>+{res.bonus:N0} coins</b> and <b>+{res.xp:N0} XP</b>", new GUIStyle(text) { alignment = TextAnchor.MiddleCenter });
            y += S(30);
            foreach (var (label, value) in new[]
            {
                ("Your points (25%)", res.fromPoints), ("Crew missions", res.fromMissions), ("Rank bonus", res.fromRank),
                (res.bossResult == null ? "Boss" : won ? "Boss defeated" : "Boss fought", res.fromBoss), ($"Prize pool share (of {res.pool:N0})", res.fromPool),
            })
            {
                Label(new Rect(x, y, iw, S(20)), label, small);
                Label(new Rect(x, y, iw, S(20)), $"+{value:N0}", small, value > 0 ? Skin.Gold : Skin.Dim, TextAnchor.UpperRight);
                y += S(22);
            }
            y += S(6);
            foreach (var (t, done) in res.missions)
            {
                Label(new Rect(x, y, iw, S(20)), (done ? "✓ " : "✗ ") + t, small, done ? Skin.Green : Skin.Dim);
                y += S(22);
            }
            y += S(6);
            Label(new Rect(x, y, iw, S(18)), "CREW", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
            Label(new Rect(x, y, iw, S(18)), "points · fish · damage · share", smallDim, null, TextAnchor.UpperRight);
            y += S(20);
            for (int i = 0; i < rankRows; i++)
            {
                var s = res.ranking[i];
                bool me = s.id == res.me;
                if (me) GUI.Box(new Rect(x - S(6), y - S(1), iw + S(12), S(22)), GUIContent.none, Box(Skin.Rounded(8, new Color(76 / 255f, 201 / 255f, 240 / 255f, 0.14f), new Color(76 / 255f, 201 / 255f, 240 / 255f, 0.14f), new Color(0, 0, 0, 0)), 9));
                Label(new Rect(x, y, iw, S(20)), $"{i + 1}. {(me ? "You" : s.name)}", me ? new GUIStyle(small) { fontStyle = FontStyle.Bold } : small, i == 0 ? Skin.Gold : (Color?)null);
                Label(new Rect(x, y, iw, S(20)), $"{s.points} · {s.catches} · {s.damage:0} · {s.share:N0}", small, null, TextAnchor.UpperRight);
                y += S(24);
            }
            Label(new Rect(r.x, r.yMax - S(30), r.width, S(20)), $"Heading home in {Mathf.CeilToInt(v.PhaseLeft)} s...", new GUIStyle(smallDim) { alignment = TextAnchor.MiddleCenter });
        }

        // ---- the sea chart (bottom right, at sea) ------------------------------------------------------------

        void DrawSeaMinimap()
        {
            var v = Vy;
            var V = GameData.I.voyage;
            float mw = S(200), mh = S(150);
            var r = new Rect(Screen.width - mw - S(14), Screen.height - mh - S(16), mw, mh);
            // Show the middle of the sea world, around the boat.
            float x0 = 250, y0 = 150, sw = 1200, sh = 900;
            Vector2 M(float x, float y) => new Vector2(r.x + (x - x0) / sw * mw, r.y + (y - y0) / sh * mh);
            var stop = v.Stop;
            var water = stop != null ? Skin.Hex(stop.loc.water[0]) : Skin.Hex("#123a66");
            GUI.DrawTexture(r, Skin.Rounded(8, Color.Lerp(water, Color.white, 0.08f), water, new Color(0, 0, 0, 0)));
            GUI.BeginClip(r);
            var off = new Vector2(r.x, r.y);
            void Dot(Vector2 p, float size, Color c, Color? edge = null) =>
                GUI.DrawTexture(new Rect(p.x - off.x - size / 2, p.y - off.y - size / 2, size, size), Skin.Rounded(Mathf.Max(2, Mathf.RoundToInt(size / 2)), c, c, edge ?? new Color(0, 0, 0, 0), edge.HasValue ? 1 : 0));
            // The trawler.
            var sb = V.seaBoat;
            var a = M(sb.x - sb.length / 2, sb.y - sb.beam / 2);
            var bb = M(sb.x + sb.length / 2, sb.y + sb.beam / 2);
            GUI.DrawTexture(new Rect(a.x - off.x, a.y - off.y, bb.x - a.x, bb.y - a.y), Skin.Rounded(3, Skin.Hex("#b5865a"), Skin.Hex("#8a5a33"), new Color(1, 1, 1, 0.6f), 1));
            foreach (var h in v.Hotspots)
                Dot(M(h.x, h.y), h.weak ? S(10 + Mathf.Sin(Time.time * 5) * 1.5f) : S(6), h.weak ? Skin.Red : Skin.Hex("#ffe066"));
            var b = v.Boss;
            if (b != null && b.result == null)
            {
                if (b.breach != null) Dot(M(b.breach.x, b.breach.y), S(12 + Mathf.Sin(Time.time * 6) * 2), new Color(0, 0, 0, 0), Skin.Gold);
                foreach (var g in b.grabs) Dot(M(g.x, g.y), S(7), Skin.Hex(b.info.color), Color.white);
            }
            foreach (var rem in Multiplayer.I.Remotes.Values)
                if (rem && rem.State != null && rem.State.vy == v.Id)
                    Dot(M(SeaWorld.ToSea(new Vector3(rem.State.x, 0, rem.State.z)).x, SeaWorld.ToSea(new Vector3(rem.State.x, 0, rem.State.z)).y), S(6), RemoteColour(rem.State.col), Color.white);
            var me = M(v.MySeaPos.x, v.MySeaPos.y);
            float yaw = Game.I.Player.Yaw * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Sin(yaw), -Mathf.Cos(yaw));
            for (int i = 1; i <= 3; i++)
            {
                var q = me + dir * S(4 + i * 3);
                GUI.DrawTexture(new Rect(q.x - off.x - S(1.5f), q.y - off.y - S(1.5f), S(3), S(3)), Texture2D.whiteTexture);
            }
            Dot(me, S(8), Skin.Gold, Color.white);
            GUI.EndClip();
            GUI.DrawTexture(r, Skin.Rounded(8, new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), new Color(1, 1, 1, 0.35f), 2));
            Label(new Rect(r.x + S(6), r.y + S(3), r.width, S(16)), stop?.loc.name ?? "Open Sea", new GUIStyle(smallDim) { fontSize = Mathf.RoundToInt(S(10)), fontStyle = FontStyle.Bold }, new Color(1, 1, 1, 0.75f));
        }
    }
}
