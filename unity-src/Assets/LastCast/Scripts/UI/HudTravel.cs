// Fast travel on the Angler's Map: hover a place (an area's name, a shop, the Travelling Zoo, South Beach)
// or any spot on dry land, click it, and confirm (Travel, or Enter) to go there. The screen fades out
// and back in at the destination (Game.FastTravel). Not from the boat, and not with a line out.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LastCast
{
    public partial class Hud
    {
        (string name, Vector2 at)? travelPick;
        float fade; // the black of a fast travel, 0..1

        /// <summary>The named places you can travel to: where you arrive (world units, a few metres in front of each),
        /// where it is on the map, and whether it needs a pin of its own (the areas do; shops and the zoo have their markers).</summary>
        static List<(string name, Vector2 at, Vector2 marker, bool pin)> TravelPlaces()
        {
            var data = GameData.I;
            var beach = new Vector2(data.spawn.x, data.spawn.y);
            var beachArea = data.areas.Find(a => a.name == "South Beach" && a.label != null);
            var list = new List<(string, Vector2, Vector2, bool)> { ("South Beach", beach, beachArea != null ? new Vector2(beachArea.label.x, beachArea.label.y) : beach, true) };
            foreach (var a in data.areas)
                if (a.label != null && a.name != "South Beach")
                {
                    var l = new Vector2(a.label.x, a.label.y);
                    list.Add((a.name, l + new Vector2(0, 30), l, true));
                }
            foreach (var s in data.shops)
                list.Add((s.IsTackle ? s.name : "the Bait Shop", new Vector2(s.x, s.y + 60), new Vector2(s.x, s.y), false));
            var zoo = Zoo.Now.spot;
            list.Add(("the Travelling Zoo", new Vector2(zoo.x - 60, zoo.y + 80), new Vector2(zoo.x, zoo.y), false));
            return list;
        }

        /// <summary>Called from DrawMap with its layout: the travel pins, hover, the pick and the confirm bar.</summary>
        void DrawTravel(float x0, float y0, float scale, float mw, float mh, GUIStyle tag)
        {
            var map = WorldMap.I;
            Vector2 M(Vector2 w) => new Vector2(x0 + w.x * scale, y0 + w.y * scale);
            var mouse = Event.current.mousePosition;
            bool overMap = mouse.x >= x0 && mouse.x <= x0 + mw && mouse.y >= y0 && mouse.y <= y0 + mh;
            float barTop = y0 + mh + S(10);
            bool overBar = travelPick != null && mouse.y >= barTop - S(4);

            // The named places: a small pin each, highlighted under the mouse.
            (string name, Vector2 at)? hover = null;
            foreach (var (name, at, marker, pin) in TravelPlaces())
            {
                // An area's pin sits just under its name; a shop or the zoo is its own marker.
                var p = M(marker) + (pin ? new Vector2(0, S(14)) : Vector2.zero);
                bool over = (mouse - p).sqrMagnitude < S(12) * S(12) || (pin && Mathf.Abs(mouse.x - p.x) < S(70) && Mathf.Abs(mouse.y - (p.y - S(14))) < S(9));
                bool picked = travelPick != null && travelPick.Value.name == name;
                if (pin || over || picked)
                {
                    float r = over || picked ? S(7) : S(4);
                    var c = pin ? new Color(1, 0.95f, 0.84f) : Skin.Gold;
                    GUI.DrawTexture(new Rect(p.x - r, p.y - r, r * 2, r * 2), Skin.Rounded(8, pin ? c : new Color(0, 0, 0, 0), pin ? c : new Color(0, 0, 0, 0), picked || over ? Skin.Gold : Color.black, picked ? 2.5f : 1.5f));
                }
                if (over && hover == null) hover = (name, at);
            }
            // Or anywhere on dry land.
            if (hover == null && overMap && !overBar)
            {
                var w = new Vector2((mouse.x - x0) / scale, (mouse.y - y0) / scale);
                if (map.IsWalkable(w.x, w.y)) hover = ("this spot", w);
            }
            if (hover != null && !overBar)
                Shadowed(new Rect(mouse.x + S(14), mouse.y - S(22), S(320), S(20)), $"Click to travel to {hover.Value.name}", new GUIStyle(tag) { alignment = TextAnchor.MiddleLeft }, Skin.Gold);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover != null && !overBar)
            {
                travelPick = hover;
                Event.current.Use();
                Sfx.Play("hook");
            }

            // The chosen place, and the confirm bar under the map.
            if (travelPick == null) return;
            var pick = travelPick.Value;
            var pp = M(pick.at);
            float pulse = S(9 + Mathf.Sin(Time.time * 5) * 2);
            GUI.DrawTexture(new Rect(pp.x - pulse, pp.y - pulse, pulse * 2, pulse * 2), Skin.Rounded(16, new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), Skin.Gold, 2));
            float bw = S(520), bh = S(40);
            var bar = new Rect((Screen.width - bw) / 2, barTop, bw, bh);
            GUI.DrawTexture(bar, Skin.Rounded(10, Skin.PanelTop, Skin.PanelBottom, Skin.Gold, 1.5f));
            GUI.Label(new Rect(bar.x + S(14), bar.y, bw - S(220), bh), $"Travel to <b>{pick.name}</b>?", new GUIStyle(text) { alignment = TextAnchor.MiddleLeft, richText = true });
            bool go = GUI.Button(new Rect(bar.xMax - S(206), bar.y + S(5), S(110), bh - S(10)), "Travel (Enter)", buy);
            if (GUI.Button(new Rect(bar.xMax - S(90), bar.y + S(5), S(80), bh - S(10)), "Cancel", button)) travelPick = null;
            var kb = Keyboard.current;
            if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) && Event.current.type == EventType.Repaint) go = true;
            if (go && travelPick != null)
            {
                var to = travelPick.Value;
                travelPick = null;
                Game.I.FastTravel(to.at, to.name);
            }
        }

        public void CloseMenus() => Open = Menu.None;

        /// <summary>For AutoShot: a place picked on the map.</summary>
        public void PickTravel(string name, Vector2 at) => travelPick = (name, at);

        /// <summary>A fast travel's fade (called by Game.FastTravel), drawn over everything.</summary>
        public float Fade { get => fade; set => fade = value; }

        void DrawFade()
        {
            if (fade <= 0) return;
            GUI.depth = -10;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(0, 0, 0, fade), 0, 0);
        }
    }
}
