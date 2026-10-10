// The HUD and menus, laid out and styled like the 2D game (client/index.html,
// client/css/style.css):
//
//   top-left      player card: gold level badge, XP bar, coin / score / fish chips, location, bait
//   top-centre    compass, and the status pill (what to do next)
//   top-right     recent catches (where the 2D game lists the anglers)
//   centre        crosshair, and the prompt pill under it ("E: open the Bait Shop")
//   bottom-left   activity feed, fading away on its own
//   bottom-centre the dock: icon buttons for the menus
//   bottom-right  minimap, with toasts above it
//
// Menus (one window with tabs): G Tackle, I Fish Index, K Trophies, H History,
// M Map (once you own the Angler's Map), O / Esc Options. E opens a shop's counter.
// Voyages have their own panels (HudVoyage.cs).

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LastCast
{
    public enum Menu { None, Tackle, Armour, Pets, Index, Goals, Trophies, History, Events, Map, Online, Options, Shop, Zoo, Title }

    public partial class Hud : MonoBehaviour
    {
        public Menu Open { get; private set; } = Menu.Title; // the title screen first
        public bool MenuOpen => Open != Menu.None || chatOpen;

        void Start()
        {
            if (AutoShot.Active) Open = Menu.None;
        }

        class FeedItem { public string text; public Color tint; public float at; }
        class ToastItem { public string icon, title, name, detail; public float at; }

        string flash;
        float flashUntil;
        string bannerTitle, bannerSub;
        Color bannerColor;
        float bannerAt = -99;
        CatchInfo lastCatch;
        float catchAt = -99, catchFor;
        readonly List<FeedItem> feed = new List<FeedItem>();
        readonly List<ToastItem> toasts = new List<ToastItem>();
        Shop shop;
        string tackleSlot = "rod";
        Vector2 scroll;
        Texture2D mapTexture;

        static readonly string[] Slots = { "rod", "reel", "line", "bait" };
        static readonly Dictionary<string, string> SlotNames = new Dictionary<string, string>
            { ["rod"] = "Rods", ["reel"] = "Reels", ["line"] = "Lines", ["bait"] = "Bait & lures" };
        static readonly (Menu menu, string label, string icon, string key)[] Tabs =
        {
            (Menu.Tackle, "Tackle", "rod", "G"), (Menu.Armour, "Armour", "shield", "R"), (Menu.Pets, "Pets", "paw", "P"),
            (Menu.Index, "Index", "book", "I"), (Menu.Goals, "Goals", "medal", "T"), (Menu.Trophies, "Trophies", "trophy", "K"),
            (Menu.History, "History", "scroll", "H"), (Menu.Events, "Events", "calendar", "V"), (Menu.Map, "Map", "map", "M"),
            (Menu.Online, "Online", "globe", "N"), (Menu.Options, "Options", "gear", "O"),
        };
        string armourSet;

        Profile P => Game.I.Profile;

        // ---- messages ------------------------------------------------------------------------------

        public void Flash(string text, float seconds)
        {
            flash = text;
            flashUntil = Time.time + seconds;
        }

        public void Feed(string text, Color tint)
        {
            feed.Insert(0, new FeedItem { text = text, tint = tint, at = Time.time });
            if (feed.Count > 6) feed.RemoveAt(feed.Count - 1);
        }

        public void Toast(string icon, string title, string name, string detail = null)
        {
            toasts.Insert(0, new ToastItem { icon = icon, title = title, name = name, detail = detail, at = Time.time });
            if (toasts.Count > 3) toasts.RemoveAt(toasts.Count - 1);
        }

        public void Banner(string title, string sub, Color color)
        {
            bannerTitle = title;
            bannerSub = sub;
            bannerColor = color;
            bannerAt = Time.time;
        }

        public void ShowCatch(CatchInfo c)
        {
            lastCatch = c;
            catchAt = Time.time;
            catchFor = c.tier != null || c.species.rarity == "legendary" || c.species.rarity == "mythic" ? 5.5f : 3.4f;
            FishIcons.Get(c.species.id, urgent: true);
            var rc = GameData.I.Rarity(c.species.rarity).Color;
            if (c.tier != null)
            {
                var t = Trophies.Tiers[c.tier];
                Feed($"{(c.tier == "giant" ? "♛" : "★")} You landed a {t.label.ToUpper()}: \"{c.trophyName}\", a {c.kg} kg {c.species.name} (+{c.points})", t.color);
            }
            else Feed($"You caught a {c.kg} kg {c.species.name} (+{c.points}) in the {c.zone}{(c.hotspot ? ", hotspot" : "")}", rc);
            if (c.isNew) Toast("book", "New species", c.species.name, "Added to your Fish Index");
            if (c.levelUp > 0) Toast("star", $"Level {c.levelUp}", $"+{Levels.LevelUpCoins(c.levelUp)} coins", "New tackle may be in the shops");
        }

        // ---- menus ----------------------------------------------------------------------------------

        bool hideAll; // AutoShot's animation poses: no HUD at all

        /// <summary>For AutoShot: open a menu by name.</summary>
        public void OpenForScreenshot(string key)
        {
            Open = key switch
            {
                "tackle" => Menu.Tackle, "index" => Menu.Index, "map" => Menu.Map, "trophies" => Menu.Trophies, "armour" => Menu.Armour,
                "pets" => Menu.Pets, "zoo" => Menu.Zoo, "goals" => Menu.Goals, "events" => Menu.Events, "online" => Menu.Online, "title" => Menu.Title, "graphics" => Menu.Options, _ => Menu.None,
            };
            scroll = Vector2.zero;
            hideAll = key == "hidden";
            if (key == "graphics") optionsPage = 1;
        }

        public void OpenShop(Shop s)
        {
            shop = s;
            Show(Menu.Shop);
        }

        public void OpenZoo() => Show(Menu.Zoo);

        void Show(Menu m)
        {
            if (m == Menu.Map && !P.worldMap)
            {
                Flash("You need the Angler's Map to see the whole world. Pick one up free at the Bait Shop on South Beach.", 4);
                Sfx.Play("error");
                return;
            }
            Open = Open == m ? Menu.None : m;
            scroll = Vector2.zero;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || AutoShot.Active || NetTest.Active || !Application.isFocused) return;
            if (Open == Menu.Title || chatOpen) return; // the title screen and the chat line take the keyboard
            if ((kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) && Open == Menu.None)
            {
                chatOpen = true;
                chatFocus = true;
                chatText = "";
                return;
            }
            if (kb.escapeKey.wasPressedThisFrame) { Open = MenuOpen ? Menu.None : Menu.Options; return; }
            if (Game.I.Fishing.State == LineState.Reeling) return; // keep your hands on the rod
            if (kb.gKey.wasPressedThisFrame) Show(Menu.Tackle);
            else if (kb.iKey.wasPressedThisFrame) Show(Menu.Index);
            else if (kb.kKey.wasPressedThisFrame) Show(Menu.Trophies);
            else if (kb.hKey.wasPressedThisFrame) Show(Menu.History);
            else if (kb.mKey.wasPressedThisFrame) Show(Menu.Map);
            else if (kb.oKey.wasPressedThisFrame || kb.f1Key.wasPressedThisFrame) Show(Menu.Options);
            else if (kb.rKey.wasPressedThisFrame && Game.I.Fishing.State == LineState.Idle) Show(Menu.Armour);
            else if (kb.pKey.wasPressedThisFrame) Show(Menu.Pets);
            else if (kb.tKey.wasPressedThisFrame) Show(Menu.Goals);
            else if (kb.vKey.wasPressedThisFrame) Show(Menu.Events);
            else if (kb.nKey.wasPressedThisFrame) Show(Menu.Online);
            // Walking away from the counter (or the wagon) closes the shop.
            if (Open == Menu.Shop && Game.I.NearShop() != shop) Open = Menu.None;
            if (Open == Menu.Zoo && !Zoo.AtWagon(Game.I.Player.WorldPos)) Open = Menu.None;
        }

        // ---- styles -----------------------------------------------------------------------------------

        float ui = 1;
        GUIStyle panel, window, pill, row, chip, text, dim, bold, small, smallDim, heavy, title, banner, button, buy, tabOn, tabOff, plaque;
        float S(float v) => v * ui;

        static GUIStyle Box(Texture2D bg, int border, int padH = 12, int padV = 10) =>
            new GUIStyle { normal = { background = bg }, border = new RectOffset(border, border, border, border), padding = new RectOffset(padH, padH, padV, padV) };

        void Styles()
        {
            float want = Mathf.Clamp(Screen.height / 900f, 0.8f, 2.2f);
            if (panel != null && Mathf.Approximately(want, ui)) return;
            ui = want;
            int F(float px) => Mathf.RoundToInt(px * ui);
            var clear = new Color(0, 0, 0, 0);
            panel = Box(Skin.Rounded(14, Skin.PanelTop, Skin.PanelBottom, Skin.Border), 16);
            panel.padding = new RectOffset(F(12), F(12), F(10), F(10));
            window = Box(Skin.Rounded(18, new Color(20 / 255f, 38 / 255f, 50 / 255f, 0.98f), new Color(12 / 255f, 24 / 255f, 32 / 255f, 0.98f), Skin.Border), 20);
            window.padding = new RectOffset(0, 0, 0, F(10));
            pill = Box(Skin.Rounded(16, Skin.PanelTop, Skin.PanelBottom, Skin.Border), 17);
            row = Box(Skin.Rounded(12, new Color(1, 1, 1, 0.055f), new Color(1, 1, 1, 0.04f), clear), 13);
            row.padding = new RectOffset(F(12), F(12), F(8), F(8));
            row.margin = new RectOffset(0, 0, F(3), F(3));
            chip = Box(Skin.Rounded(10, new Color(1, 1, 1, 0.07f), new Color(1, 1, 1, 0.05f), clear), 11);
            plaque = Box(Skin.Rounded(10, Skin.Hex("#5b3a1e"), Skin.Hex("#3b2513"), Skin.Gold, 2), 12);

            text = new GUIStyle { font = Skin.Regular, fontSize = F(14), richText = true, wordWrap = true, normal = { textColor = Skin.Text } };
            dim = new GUIStyle(text) { normal = { textColor = Skin.Dim } };
            bold = new GUIStyle(text) { fontStyle = FontStyle.Bold };
            small = new GUIStyle(text) { fontSize = F(12) };
            smallDim = new GUIStyle(small) { normal = { textColor = Skin.Dim } };
            heavy = new GUIStyle(text) { font = Skin.Heavy, fontSize = F(16), fontStyle = FontStyle.Bold };
            title = new GUIStyle(heavy) { fontSize = F(20) };
            banner = new GUIStyle(heavy) { fontSize = F(44), alignment = TextAnchor.MiddleCenter, wordWrap = false };

            button = Box(Skin.Rounded(9, new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.07f), clear), 10, F(12), F(6));
            button.font = Skin.Regular;
            button.fontSize = F(13);
            button.fontStyle = FontStyle.Bold;
            button.alignment = TextAnchor.MiddleCenter;
            button.normal.textColor = Skin.Text;
            button.hover.background = Skin.Rounded(9, new Color(1, 1, 1, 0.2f), new Color(1, 1, 1, 0.12f), clear);
            button.hover.textColor = Color.white;
            button.margin = new RectOffset(F(3), F(3), F(3), F(3));
            buy = new GUIStyle(button) { normal = { background = Skin.Rounded(9, Skin.Accent, Skin.Accent2, clear), textColor = Color.white } };
            buy.hover.background = Skin.Rounded(9, Skin.Hex("#7ad8f5"), Skin.Hex("#4a9cc0"), clear);
            tabOff = new GUIStyle(button) { padding = new RectOffset(F(9), F(9), F(6), F(6)), normal = { background = null, textColor = Skin.Dim }, hover = { background = Skin.Rounded(9, new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.06f), clear), textColor = Color.white } };
            tabOn = new GUIStyle(tabOff) { normal = { background = Skin.Rounded(9, new Color(76 / 255f, 201 / 255f, 240 / 255f, 0.12f), new Color(76 / 255f, 201 / 255f, 240 / 255f, 0.1f), clear), textColor = Color.white } };
        }

        static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
        static string Num(long n) => n.ToString("N0");

        void Label(Rect r, string s, GUIStyle style, Color? colour = null, TextAnchor? align = null)
        {
            var st = style;
            if (colour.HasValue || align.HasValue)
            {
                st = new GUIStyle(style);
                if (colour.HasValue) st.normal.textColor = colour.Value;
                if (align.HasValue) st.alignment = align.Value;
            }
            GUI.Label(r, s, st);
        }

        /// <summary>Text with a soft drop shadow, for things drawn straight over the world.</summary>
        void Shadowed(Rect r, string s, GUIStyle style, Color colour, float offset = 2)
        {
            var sh = new GUIStyle(style) { normal = { textColor = new Color(0, 0, 0, 0.55f) } };
            string plain = System.Text.RegularExpressions.Regex.Replace(s, "<color=[^>]+>|</color>", "");
            GUI.Label(new Rect(r.x, r.y + S(offset), r.width, r.height), plain, sh);
            Label(r, s, style, colour);
        }

        void Bar(Rect r, float frac, Color left, Color right)
        {
            GUI.DrawTexture(r, Skin.BarFill(new Color(0, 0, 0, 0.45f), new Color(0, 0, 0, 0.45f)));
            frac = Mathf.Clamp01(frac);
            if (frac > 0) GUI.DrawTexture(new Rect(r.x, r.y, Mathf.Max(r.height, r.width * frac), r.height), Skin.BarFill(left, right));
        }

        void Icon(Rect r, string name, Color? tint = null)
        {
            var old = GUI.color;
            if (tint.HasValue) GUI.color = tint.Value;
            GUI.DrawTexture(r, Skin.Icon(name), ScaleMode.ScaleToFit);
            GUI.color = old;
        }

        // ---- drawing ------------------------------------------------------------------------------------

        void OnGUI()
        {
            if (Game.I == null || Game.I.Player == null || hideAll) return;
            Styles();
            GUI.depth = 0;
            if (Open == Menu.Title)
            {
                DrawTitle();
                return;
            }
            if (Open != Menu.Map)
            {
                bool atSea = Vy != null && Vy.AtSea;
                DrawPlayerCard();
                float below = S(14);
                if (Multiplayer.I != null && Multiplayer.I.Online) below = DrawAnglers();
                else if (!atSea) DrawCatchLog();
                if (atSea) DrawVoyagePanel(below);
                DrawCompassAndStatus();
                if (atSea) DrawBossBar();
                DrawCentre();
                DrawFeed();
                DrawChat();
                if (atSea) DrawSeaMinimap();
                else DrawMinimap();
                DrawToasts();
                DrawCatchCard();
                DrawBanner();
                if (atSea) DrawResults();
            }
            DrawDock();
            if (GraphicsOptions.ShowFps) DrawFps();
            if (Open != Menu.Map) travelPick = null;
            switch (Open)
            {
                case Menu.Map: DrawMap(); break;
                case Menu.Shop: Window(DrawShop); break;
                case Menu.Zoo: Window(DrawZoo); break;
                case Menu.None: break;
                default: Window(Open switch
                {
                    Menu.Tackle => DrawTackle,
                    Menu.Armour => DrawArmour,
                    Menu.Pets => DrawPets,
                    Menu.Goals => DrawGoals,
                    Menu.Events => DrawEvents,
                    Menu.Online => DrawOnline,
                    Menu.Index => DrawIndex,
                    Menu.Trophies => DrawTrophies,
                    Menu.History => DrawHistory,
                    _ => (System.Action)DrawOptions,
                }); break;
            }
            DrawFade();
        }

        // ---- player card ----------------------------------------------------------------------------------

        void DrawPlayerCard()
        {
            var lines = CardLines();
            var r = new Rect(S(14), S(14), S(262), S(196) + lines.Count * S(19));
            GUI.Box(r, GUIContent.none, panel);
            float x = r.x + S(12), y = r.y + S(12);
            var prog = Levels.Progress(P.xp);
            // Level badge, with its little "LV" tag.
            GUI.DrawTexture(new Rect(x, y, S(42), S(42)), Skin.Badge());
            Label(new Rect(x, y, S(42), S(40)), prog.level.ToString(), heavy, Skin.Hex("#2b1d00"), TextAnchor.MiddleCenter);
            var tag = new Rect(x + S(11), y + S(35), S(20), S(12));
            GUI.DrawTexture(tag, Skin.Rounded(5, Skin.Hex("#1b2b36"), Skin.Hex("#1b2b36"), new Color(1, 0.82f, 0.4f, 0.6f)));
            Label(tag, "LV", new GUIStyle(smallDim) { fontSize = Mathf.RoundToInt(S(8)), fontStyle = FontStyle.Bold }, Color.white, TextAnchor.MiddleCenter);
            Label(new Rect(x + S(52), y + S(2), S(190), S(24)), Multiplayer.PlayerName, heavy);
            var mp = Multiplayer.I;
            Label(new Rect(x + S(52), y + S(24), S(190), S(18)), mp != null && mp.Online ? $"Online · {mp.Session?.Name}" : "Single player · N to go online", smallDim, mp != null && mp.Online ? Skin.Accent : (Color?)null);
            // XP bar.
            y += S(56);
            Bar(new Rect(x, y + S(4), S(150), S(7)), prog.fraction, Skin.Hex("#f8961e"), Skin.Gold);
            Label(new Rect(x + S(158), y - S(2), S(90), S(18)), prog.level >= Levels.Max ? "MAX" : $"{Num(prog.into)} / {Num(prog.needed)} XP", smallDim);
            // Coins, score and fish.
            y += S(20);
            var stats = new[] { ("coin", P.coins), ("star", P.score), ("fish", (long)P.catches) };
            for (int i = 0; i < 3; i++)
            {
                var c = new Rect(x + i * S(80), y, S(74), S(28));
                GUI.Box(c, GUIContent.none, chip);
                Icon(new Rect(c.x + S(6), c.y + S(6), S(16), S(16)), stats[i].Item1);
                Label(new Rect(c.x + S(24), c.y, c.width - S(26), c.height), Num(stats[i].Item2), new GUIStyle(bold) { fontSize = Mathf.RoundToInt(S(13)) }, null, TextAnchor.MiddleLeft);
            }
            // Where you are, and your bait.
            y += S(36);
            var w = Game.I.Player.WorldPos;
            string here;
            if (Vy != null && Vy.AtSea) here = Vy.Phase == VoyagePhase.Fishing ? $"{Vy.Stop.loc.name} (at sea)" : "At sea, aboard the trawler";
            else if (Vy != null && Vy.Boarded != 0) here = "Aboard the voyage boat";
            else here = GameData.I.AreaAt(w.x, w.y)?.name ?? "The wilds";
            Icon(new Rect(x, y + S(1), S(14), S(14)), "pin");
            Label(new Rect(x + S(18), y - S(1), S(220), S(18)), here, new GUIStyle(small) { fontStyle = FontStyle.Bold }, Skin.Location);
            y += S(19);
            var bait = GameData.I.Item(P.equipped["bait"]);
            bool starter = P.IsStarter(bait.id);
            int left = P.BaitCount(bait.id);
            Icon(new Rect(x, y + S(1), S(14), S(14)), "worm");
            Label(new Rect(x + S(18), y - S(1), S(220), S(18)), starter ? $"{bait.name} (free)" : $"{bait.name} ×{left}", small, !starter && left < 10 ? Skin.Hex("#ffb4a2") : Skin.Dim);
            foreach (var (icon, line, colour) in lines)
            {
                y += S(19);
                Icon(new Rect(x, y + S(1), S(14), S(14)), icon);
                Label(new Rect(x + S(18), y - S(1), S(225), S(18)), line, small, colour);
            }
        }

        /// <summary>Extra lines on the player card: your pet, your chum bucket, a live map event.</summary>
        List<(string icon, string text, Color colour)> CardLines()
        {
            var lines = new List<(string, string, Color)>();
            var pet = GameData.I.PetById(P.pet);
            if (pet != null) lines.Add(("paw", $"{pet.name} · {pet.ability}", Skin.Hex(GameData.I.petRarities[pet.rarity].color)));
            var chum = Game.I.Chum;
            if (chum.Out) lines.Add(("bucket", $"Chum: {Clock(chum.Life)} left · {chum.Fish}/{GameData.I.chum.maxFish} fish", Skin.Hex("#e07a5f")));
            else if (chum.Carrying) lines.Add(("bucket", $"Chum bucket in hand · {Clock(chum.Life)} (C)", Skin.Hex("#e07a5f")));
            else if (P.chum > 0) lines.Add(("bucket", $"{P.chum} chum bucket{(P.chum > 1 ? "s" : "")} (C to put one down)", Skin.Dim));
            var ev = Game.I.Events.Active;
            if (ev != null && (Vy == null || !Vy.AtSea)) lines.Add(("calendar", $"{ev.T.name} at {ev.place.name} now! (V)", Skin.Gold));
            BoatCardLine(lines);
            return lines;
        }

        static string Clock(double seconds)
        {
            int s = Mathf.Max(0, Mathf.CeilToInt((float)seconds));
            return $"{s / 60}:{s % 60:00}";
        }

        void DrawCatchLog()
        {
            var list = P.history.Take(6).ToList();
            float h = S(34) + Mathf.Max(1, list.Count) * S(22);
            var r = new Rect(Screen.width - S(250), S(14), S(236), h);
            GUI.Box(r, GUIContent.none, panel);
            Label(new Rect(r.x + S(12), r.y + S(8), r.width, S(16)), "RECENT CATCHES", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(S(11)) });
            if (list.Count == 0) Label(new Rect(r.x + S(12), r.y + S(30), r.width - S(20), S(20)), "Nothing yet. Go cast a line!", smallDim);
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                var s = GameData.I.Fish(c.species);
                float y = r.y + S(30) + i * S(22);
                Label(new Rect(r.x + S(8), y, S(16), S(20)), (i + 1).ToString(), smallDim, i == 0 ? Skin.Gold : (Color?)null, TextAnchor.MiddleRight);
                var dot = new Rect(r.x + S(30), y + S(6), S(9), S(9));
                GUI.DrawTexture(dot, Skin.Rounded(5, GameData.I.Rarity(s?.rarity ?? "common").Color, GameData.I.Rarity(s?.rarity ?? "common").Color, new Color(0, 0, 0, 0)));
                Label(new Rect(r.x + S(46), y, S(120), S(20)), s?.name ?? c.species, small, null, TextAnchor.MiddleLeft);
                Label(new Rect(r.x + S(150), y, S(76), S(20)), $"{c.kg} kg", smallDim, null, TextAnchor.MiddleRight);
            }
        }

        // ---- compass, status, centre --------------------------------------------------------------------

        void DrawCompassAndStatus()
        {
            float W = Screen.width;
            var r = new Rect(W / 2 - S(150), S(12), S(300), S(30));
            GUI.Box(r, GUIContent.none, pill);
            float heading = Game.I.Player.Yaw;
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            GUI.BeginGroup(new Rect(r.x + S(14), r.y, r.width - S(28), r.height));
            float cw = r.width - S(28);
            for (int i = -8; i <= 16; i++)
            {
                float deg = i * 45;
                float dx = Mathf.DeltaAngle(heading, deg) * S(1.35f);
                if (Mathf.Abs(dx) > cw / 2 + S(20)) continue;
                bool cardinal = i % 2 == 0;
                var name = names[((i % 8) + 8) % 8];
                Label(new Rect(cw / 2 + dx - S(20), 0, S(40), r.height), name, cardinal ? new GUIStyle(bold) { fontSize = Mathf.RoundToInt(S(13)) } : smallDim,
                    name == "N" ? Skin.Red : (Color?)null, TextAnchor.MiddleCenter);
            }
            GUI.EndGroup();
            GUI.DrawTexture(new Rect(W / 2 - S(1), r.y + S(2), S(2), S(6)), Texture2D.whiteTexture);

            // The status pill: a message, or what to do next.
            var f = Game.I.Fishing;
            var blocked = Vy?.CastBlocked;
            string status = Time.time < flashUntil ? flash : f.State switch
            {
                LineState.Idle when f.Charge >= 0 => $"Power {Mathf.RoundToInt(f.Charge * 100)}%. Release when the ghost bobber is where you want it.",
                LineState.Idle when blocked != null => blocked,
                LineState.Idle when Vy != null && Vy.AtSea => "Hold the left mouse button to cast over the rail.",
                LineState.Idle => "Look at the water and hold the left mouse button to cast.",
                LineState.Casting => "Casting...",
                LineState.Waiting => "Waiting for a bite... (right-click to reel in)",
                LineState.Bite => "A BITE! Click to hook it!",
                _ => f.Pulling ? "It's pulling! Ease off or the line will snap!" : "Hold to reel in. Let go when the fish pulls.",
            };
            DrawEventPanel();
            if (string.IsNullOrEmpty(status) || MenuOpen) return;
            var st = new GUIStyle(bold) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            float tw = Mathf.Min(st.CalcSize(new GUIContent(status)).x + S(34), W - S(560));
            var sr = new Rect(W / 2 - tw / 2, S(48), tw, S(32));
            GUI.Box(sr, GUIContent.none, pill);
            Color c = f.State == LineState.Bite ? Skin.Gold : f.Pulling ? Skin.Red : Skin.Text;
            Label(sr, status, new GUIStyle(st) { wordWrap = true, clipping = TextClipping.Clip }, c);
        }

        /// <summary>A live map event: what it is, where, time left and progress (top right, under the catch log).</summary>
        void DrawEventPanel()
        {
            var events = Game.I.Events;
            var ev = events.Active;
            if (ev == null || MenuOpen || (Vy != null && Vy.AtSea)) return;
            var T = ev.T;
            var accent = Skin.Hex(T.color);
            int rowsAbove = Multiplayer.I != null && Multiplayer.I.Online ? 1 + Multiplayer.I.Remotes.Count : Mathf.Max(1, Mathf.Min(6, P.history.Count));
            var r = new Rect(Screen.width - S(250), S(14) + S(34) + rowsAbove * S(22) + S(10), S(236), S(82));
            GUI.Box(r, GUIContent.none, Box(Skin.Rounded(14, Skin.PanelTop, Skin.PanelBottom, accent, 1.5f, 0, accent), 16));
            Icon(new Rect(r.x + S(12), r.y + S(10), S(18), S(18)), "calendar");
            Label(new Rect(r.x + S(36), r.y + S(8), r.width - S(46), S(20)), T.name.ToUpper(), new GUIStyle(small) { fontStyle = FontStyle.Bold }, accent);
            Label(new Rect(r.x + S(12), r.y + S(8), r.width - S(24), S(20)), Clock(ev.end - MapEvents.Now), smallDim, null, TextAnchor.UpperRight);
            Label(new Rect(r.x + S(12), r.y + S(28), r.width - S(24), S(18)), $"{ev.place.name} · {MapEvents.Direction(ev)}", small);
            string detail;
            float frac;
            if (ev.type == "haul") { detail = $"{events.Progress:0} / {events.Goal:0} fish"; frac = events.Progress / events.Goal; }
            else if (ev.type == "tide" && events.Rush > 0) { detail = $"GOLDEN RUSH! {events.Rush:0}s of double points"; frac = events.Rush / GameData.I.events.tuning.tide.rush; }
            else if (ev.type == "tide") { detail = $"Golden meter {events.Progress:0} / {events.Goal:0}"; frac = events.Progress / events.Goal; }
            else { detail = $"{events.Fish} fish landed in the shoal"; frac = (float)((MapEvents.Now - ev.start) / (ev.end - ev.start)); }
            Label(new Rect(r.x + S(12), r.y + S(46), r.width - S(24), S(18)), detail, smallDim);
            Bar(new Rect(r.x + S(12), r.y + S(66), r.width - S(24), S(6)), frac, Color.Lerp(accent, Color.white, 0.2f), accent);
        }

        void DrawCentre()
        {
            float W = Screen.width, H = Screen.height;
            var f = Game.I.Fishing;
            var pl = Game.I.Player;
            if (!MenuOpen)
            {
                GUI.DrawTexture(new Rect(W / 2 - S(3), H / 2 - S(3), S(6), S(6)), Skin.Rounded(3, new Color(1, 1, 1, 0.9f), new Color(1, 1, 1, 0.9f), new Color(0, 0, 0, 0.5f), 1.5f));
            }
            // The prompt pill under the crosshair.
            string prompt = null;
            Color promptColour = Skin.Text;
            var near = Game.I.NearShop();
            var voyagePrompt = VoyagePrompt();
            if (f.State == LineState.Bite) { prompt = "!!!  Click to hook!"; promptColour = Skin.Gold; }
            else if (voyagePrompt != null) (prompt, promptColour) = voyagePrompt.Value;
            else if (f.State == LineState.Idle && f.Charge < 0 && near != null) prompt = $"E: open {(near.IsTackle ? near.name : "the Bait Shop")}";
            else if (f.State == LineState.Idle && f.Charge < 0 && Zoo.AtWagon(pl.WorldPos)) prompt = "E: visit the Travelling Zoo";
            else if (f.State == LineState.Idle && f.Charge < 0 && Game.I.Chum.CanPickUp) prompt = "C: pick up your chum bucket";
            if (prompt != null && !MenuOpen)
            {
                var st = new GUIStyle(bold) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
                float tw = st.CalcSize(new GUIContent(prompt)).x + S(28);
                var r = new Rect(W / 2 - tw / 2, H / 2 + S(30), tw, S(28));
                GUI.Box(r, GUIContent.none, pill);
                Label(r, prompt, st, promptColour);
            }
            // The power meter, or the fight, just above the dock.
            float y = H - S(140);
            if (f.Charge >= 0)
            {
                var r = new Rect(W / 2 - S(170), y, S(340), S(46));
                GUI.Box(r, GUIContent.none, panel);
                Label(new Rect(r.x + S(12), r.y + S(4), S(200), S(18)), "CAST POWER", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
                Bar(new Rect(r.x + S(12), r.y + S(26), r.width - S(24), S(10)), f.Charge, Skin.Green, Color.Lerp(Skin.Green, Skin.Gold, f.Charge));
            }
            else if (f.State == LineState.Reeling)
            {
                var tier = FishRules.Tiers[f.TierIndex];
                var r = new Rect(W / 2 - S(190), y - S(26), S(380), S(72));
                GUI.Box(r, GUIContent.none, panel);
                Label(new Rect(r.x + S(12), r.y + S(4), S(260), S(18)), f.GiantOn ? "GIANT" : tier.label.ToUpper(), new GUIStyle(small) { font = Skin.Heavy }, f.GiantOn ? Trophies.Tiers["giant"].color : tier.color);
                Label(new Rect(r.x + S(12), r.y + S(4), r.width - S(24), S(18)), "tension", smallDim, null, TextAnchor.UpperRight);
                Bar(new Rect(r.x + S(12), r.y + S(24), r.width - S(24), S(10)), f.Tension, Color.Lerp(Skin.Green, Skin.Red, f.Tension * 0.7f), Color.Lerp(Skin.Green, Skin.Red, f.Tension));
                Label(new Rect(r.x + S(12), r.y + S(36), r.width - S(24), S(18)), "line in", smallDim, null, TextAnchor.UpperRight);
                Bar(new Rect(r.x + S(12), r.y + S(54), r.width - S(24), S(8)), f.Progress, Skin.Accent2, Skin.Accent);
            }
        }

        // ---- feed, toasts, catch card, banner --------------------------------------------------------------

        void DrawFeed()
        {
            float y = Screen.height - S(18);
            for (int i = 0; i < feed.Count; i++)
            {
                var item = feed[i];
                float age = Time.time - item.at;
                if (age > 16) continue;
                float a = age < 0.6f ? age / 0.6f : age > 13.6f ? (16 - age) / 2.4f : 1;
                var st = new GUIStyle(small) { wordWrap = true };
                float w = Mathf.Min(S(340), Screen.width * 0.5f - S(260));
                float h = st.CalcHeight(new GUIContent(item.text), w - S(22)) + S(10);
                y -= h + S(4);
                var r = new Rect(S(14), y, w, h);
                var old = GUI.color;
                GUI.color = new Color(1, 1, 1, a);
                GUI.Box(r, GUIContent.none, Box(Skin.Rounded(10, new Color(10 / 255f, 22 / 255f, 30 / 255f, 0.75f), new Color(10 / 255f, 22 / 255f, 30 / 255f, 0.75f), new Color(0, 0, 0, 0)), 11));
                GUI.DrawTexture(new Rect(r.x, r.y + S(3), S(3), r.height - S(6)), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(item.tint.r, item.tint.g, item.tint.b, 0.75f * a), 0, 0);
                Label(new Rect(r.x + S(12), r.y + S(4), w - S(18), h), item.text, st, new Color(item.tint.r, item.tint.g, item.tint.b, a));
                GUI.color = old;
            }
        }

        void DrawToasts()
        {
            float y = Screen.height - S(236);
            foreach (var t in toasts)
            {
                float age = Time.time - t.at;
                if (age > 7) continue;
                float a = age > 6.5f ? (7 - age) / 0.5f : 1;
                float slide = age < 0.4f ? (1 - age / 0.4f) * S(40) : 0;
                var r = new Rect(Screen.width - S(274) + slide, y - S(64), S(260), S(60));
                y -= S(70);
                var old = GUI.color;
                GUI.color = new Color(1, 1, 1, a * (age < 0.4f ? age / 0.4f : 1));
                GUI.Box(r, GUIContent.none, Box(Skin.Rounded(12, new Color(20 / 255f, 30 / 255f, 20 / 255f, 0.95f), new Color(20 / 255f, 30 / 255f, 20 / 255f, 0.95f), Skin.Gold), 13));
                Icon(new Rect(r.x + S(10), r.y + S(14), S(32), S(32)), t.icon);
                Label(new Rect(r.x + S(50), r.y + S(5), r.width - S(56), S(16)), t.title.ToUpper(), new GUIStyle(small) { fontSize = Mathf.RoundToInt(S(10)), fontStyle = FontStyle.Bold }, Skin.Gold);
                Label(new Rect(r.x + S(50), r.y + S(19), r.width - S(56), S(20)), t.name, new GUIStyle(bold) { wordWrap = false });
                if (t.detail != null) Label(new Rect(r.x + S(50), r.y + S(38), r.width - S(56), S(18)), t.detail, smallDim);
                GUI.color = old;
            }
        }

        void DrawCatchCard()
        {
            var c = lastCatch;
            if (c == null) return;
            float age = Time.time - catchAt;
            if (age > catchFor) return;
            var rarity = GameData.I.Rarity(c.species.rarity);
            Color rc = rarity.Color;
            bool special = c.species.rarity == "legendary" || c.species.rarity == "mythic";
            // Just the words: the fish itself is the one in your hands, flapping (RodView).
            float w = S(340), h = S(c.tier != null ? 112 : 86);
            var r = new Rect(Screen.width / 2 - w / 2, Screen.height * 0.16f, w, h);
            // Pop in with a little overshoot and spin, as the 2D card does.
            float t = Mathf.Clamp01(age / 0.55f);
            float pop = t < 1 ? 1 + 2.2f * Mathf.Pow(t - 1, 3) + 1.2f * Mathf.Pow(t - 1, 2) : 1;
            float fade = age > catchFor - 0.4f ? (catchFor - age) / 0.4f : 1;
            var oldM = GUI.matrix;
            var oldC = GUI.color;
            GUIUtility.ScaleAroundPivot(Vector2.one * Mathf.Lerp(0.4f, 1, pop), r.center);
            GUIUtility.RotateAroundPivot((1 - pop) * -6, r.center);
            GUI.color = new Color(1, 1, 1, fade * Mathf.Clamp01(t * 2));
            GUI.DrawTexture(r, Skin.CatchCard(rc, special ? 3 : 2));
            if (c.species.rarity is "rare" or "legendary" or "mythic")
            {
                GUI.BeginGroup(new Rect(r.x + S(3), r.y + S(3), r.width - S(6), r.height - S(6)));
                var m = GUI.matrix;
                var centre = new Vector2(r.width / 2, r.height * 0.5f);
                GUIUtility.RotateAroundPivot(Time.time * 30, centre);
                GUI.DrawTexture(new Rect(centre.x - w, centre.y - w, w * 2, w * 2), Skin.Rays(rc));
                GUI.matrix = m;
                GUI.EndGroup();
            }
            string head = c.tier == "giant" ? "A GIANT!" : c.tier == "trophy" ? "TROPHY FISH!" : c.isNew ? "NEW SPECIES!" : "YOU CAUGHT";
            Label(new Rect(r.x, r.y + S(12), r.width, S(18)), head, new GUIStyle(small) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter }, rc);
            Color nameColour = c.species.rarity == "mythic" ? Color.HSVToRGB(Mathf.Repeat(Time.time * 0.25f, 1), 0.55f, 1) : Skin.Text;
            Label(new Rect(r.x, r.y + S(30), r.width, S(30)), c.species.name, new GUIStyle(title) { fontSize = Mathf.RoundToInt(S(22)), alignment = TextAnchor.MiddleCenter, wordWrap = false }, nameColour);
            Label(new Rect(r.x, r.y + S(58), r.width, S(20)), $"<b><color={Hex(rc)}>{rarity.label}</color></b> · {c.kg} kg · +{c.points} pts · +{c.coins} coins{(c.hotspot ? " · hotspot bonus" : "")}",
                new GUIStyle(small) { alignment = TextAnchor.MiddleCenter }, new Color(1, 1, 1, 0.85f));
            if (c.tier != null)
            {
                var tt = Trophies.Tiers[c.tier];
                Label(new Rect(r.x, r.y + S(82), r.width, S(22)), $"{(c.tier == "giant" ? "♛" : "★")} \"{c.trophyName}\" · {tt.label} · points ×{tt.points}", new GUIStyle(bold) { alignment = TextAnchor.MiddleCenter }, tt.color);
            }
            GUI.matrix = oldM;
            GUI.color = oldC;
        }

        void DrawBanner()
        {
            float age = Time.time - bannerAt;
            if (age > 3.2f) return;
            float a = age < 0.3f ? age / 0.3f : age > 2.6f ? (3.2f - age) / 0.6f : 1;
            float scale = age < 0.3f ? Mathf.Lerp(1.3f, 1, age / 0.3f) : 1;
            var r = new Rect(0, Screen.height * 0.26f, Screen.width, S(56));
            var oldM = GUI.matrix;
            GUIUtility.ScaleAroundPivot(Vector2.one * scale, r.center);
            var glow = new Color(bannerColor.r, bannerColor.g, bannerColor.b, 0.18f * a);
            foreach (var o in new[] { new Vector2(-2, 0), new Vector2(2, 0), new Vector2(0, -2), new Vector2(0, 2) })
                Label(new Rect(r.x + S(o.x * 2), r.y + S(o.y * 2), r.width, r.height), bannerTitle.ToUpper(), banner, glow);
            Label(new Rect(r.x, r.y + S(3), r.width, r.height), bannerTitle.ToUpper(), banner, new Color(0, 0, 0, 0.45f * a));
            Label(r, bannerTitle.ToUpper(), banner, new Color(bannerColor.r, bannerColor.g, bannerColor.b, a));
            GUI.matrix = oldM;
            Shadowed(new Rect(0, r.yMax, Screen.width, S(26)), bannerSub, new GUIStyle(bold) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(S(15)) }, new Color(1, 1, 1, a));
        }

        // ---- dock -------------------------------------------------------------------------------------------

        void DrawDock()
        {
            float bw = S(58), pad = S(6);
            float w = Tabs.Length * bw + pad * 2;
            var r = new Rect(Screen.width / 2 - w / 2, Screen.height - S(14) - S(56), w, S(56));
            GUI.Box(r, GUIContent.none, Box(Skin.Rounded(18, Skin.PanelTop, Skin.PanelBottom, Skin.Border), 19));
            for (int i = 0; i < Tabs.Length; i++)
            {
                var tab = Tabs[i];
                var b = new Rect(r.x + pad + i * bw, r.y + S(4), bw - S(4), S(48));
                bool on = Open == tab.menu;
                if (on) GUI.DrawTexture(b, Skin.Rounded(12, new Color(1, 1, 1, 0.12f), new Color(1, 1, 1, 0.08f), new Color(0, 0, 0, 0)));
                bool locked = tab.menu == Menu.Map && !P.worldMap;
                if (GUI.Button(b, GUIContent.none, GUIStyle.none)) Show(tab.menu);
                Icon(new Rect(b.x + b.width / 2 - S(12), b.y + S(5), S(24), S(24)), tab.icon, locked ? new Color(1, 1, 1, 0.35f) : (Color?)null);
                Label(new Rect(b.x, b.y + S(29), b.width, S(14)), tab.label, new GUIStyle(smallDim) { fontSize = Mathf.RoundToInt(S(10)), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter },
                    on ? Color.white : (Color?)null);
                Label(new Rect(b.xMax - S(14), b.y + S(1), S(12), S(12)), tab.key, new GUIStyle(smallDim) { fontSize = Mathf.RoundToInt(S(8)) }, new Color(1, 1, 1, 0.35f));
            }
        }

        // ---- minimap ------------------------------------------------------------------------------------------

        void DrawMinimap()
        {
            var map = WorldMap.I;
            mapTexture ??= BuildMapTexture();
            float mw = S(200), mh = S(150);
            var r = new Rect(Screen.width - mw - S(14), Screen.height - mh - S(16), mw, mh);
            const float SpanM = 360f; // metres across
            var me = Game.I.Player.WorldPos;
            float spanX = SpanM * WorldMap.UnitsPerMetre, spanY = spanX * 0.75f;
            float x0 = Mathf.Clamp(me.x - spanX / 2, 0, map.Width - spanX), y0 = Mathf.Clamp(me.y - spanY / 2, 0, map.Height - spanY);
            // Texture coordinates start at the bottom.
            var uv = new Rect(x0 / map.Width, 1 - (y0 + spanY) / map.Height, spanX / map.Width, spanY / map.Height);
            GUI.DrawTexture(r, Skin.Rounded(8, Skin.PanelBottom, Skin.PanelBottom, new Color(0, 0, 0, 0)));
            GUI.BeginGroup(r);
            GUI.DrawTextureWithTexCoords(new Rect(0, 0, mw, mh), mapTexture, uv);
            Vector2 M(float x, float y) => new Vector2((x - x0) / spanX * mw, (y - y0) / spanY * mh);
            foreach (var h in Game.I.Hotspots)
            {
                var p = M(h.x, h.y);
                GUI.DrawTexture(new Rect(p.x - S(2.5f), p.y - S(2.5f), S(5), S(5)), Skin.Rounded(3, Skin.Hex("#ffe066"), Skin.Hex("#ffe066"), new Color(0, 0, 0, 0)));
            }
            foreach (var s in GameData.I.shops)
            {
                var p = M(s.x, s.y);
                GUI.DrawTexture(new Rect(p.x - S(3), p.y - S(3), S(6), S(6)), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Skin.Hex(s.minimap ?? "#ffd166"), 0, 0);
            }
            var zoo = Zoo.Now.spot;
            var zp = M(zoo.x, zoo.y);
            GUI.DrawTexture(new Rect(zp.x - S(4), zp.y - S(4), S(8), S(8)), Skin.Rounded(4, Skin.Hex("#c77dff"), Skin.Hex("#c77dff"), Color.white, 1));
            var live = Game.I.Events.Active;
            if (live != null)
            {
                var ep = M(live.place.x, live.place.y);
                float pulse = S(5 + Mathf.Sin(Time.time * 4) * 1.5f);
                Icon(new Rect(ep.x - pulse, ep.y - pulse, pulse * 2, pulse * 2), "star", Skin.Hex(live.T.color));
            }
            // Other anglers on the lake, in their colours, pointing where they face (pinned to the edge when off the map).
            foreach (var (st, at) in RemotesOnLake())
            {
                var rp = M(at.x, at.y);
                rp = new Vector2(Mathf.Clamp(rp.x, S(5), mw - S(5)), Mathf.Clamp(rp.y, S(5), mh - S(5)));
                var c = RemoteColour(st.col);
                var rd = new Vector2(Mathf.Sin(st.yaw * Mathf.Deg2Rad), -Mathf.Cos(st.yaw * Mathf.Deg2Rad));
                for (int i = 1; i <= 2; i++)
                {
                    var q = rp + rd * S(3 + i * 3);
                    GUI.DrawTexture(new Rect(q.x - S(1.25f), q.y - S(1.25f), S(2.5f), S(2.5f)), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, c, 0, 0);
                }
                GUI.DrawTexture(new Rect(rp.x - S(4.5f), rp.y - S(4.5f), S(9), S(9)), Skin.Rounded(5, c, c, Color.white, 1.5f));
            }
            var mp = M(me.x, me.y);
            float yaw = Game.I.Player.Yaw * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Sin(yaw), -Mathf.Cos(yaw));
            for (int i = 1; i <= 3; i++)
            {
                var q = mp + dir * S(4 + i * 3);
                GUI.DrawTexture(new Rect(q.x - S(1.5f), q.y - S(1.5f), S(3), S(3)), Texture2D.whiteTexture);
            }
            GUI.DrawTexture(new Rect(mp.x - S(4), mp.y - S(4), S(8), S(8)), Skin.Rounded(4, Skin.Gold, Skin.Gold, Color.white, 2));
            GUI.EndGroup();
            GUI.DrawTexture(r, Skin.Rounded(8, new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), new Color(1, 1, 1, 0.35f), 2));
        }

        // ---- the menu window ---------------------------------------------------------------------------------------

        void Window(System.Action body)
        {
            float w = Mathf.Min(S(880), Screen.width - S(32)), h = Mathf.Min(Screen.height * 0.8f, Screen.height - S(120));
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(6 / 255f, 14 / 255f, 20 / 255f, 0.6f), 0, 0);
            var r = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2 - S(30), w, h);
            GUI.Box(r, GUIContent.none, window);
            GUILayout.BeginArea(r);
            // Tabs (or the shop's name), and the close button.
            GUILayout.BeginHorizontal(GUILayout.Height(S(44)));
            GUILayout.Space(S(8));
            if (Open == Menu.Shop || Open == Menu.Zoo)
                GUILayout.Label(Open == Menu.Zoo ? "The Travelling Zoo" : shop.name, new GUIStyle(title) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset((int)S(8), 0, (int)S(8), 0) }, GUILayout.Height(S(44)));
            else
                foreach (var tab in Tabs)
                {
                    bool on = Open == tab.menu;
                    if (GUILayout.Button(tab.label, on ? tabOn : tabOff, GUILayout.Height(S(34)))) Show(tab.menu);
                    if (on)
                    {
                        var last = GUILayoutUtility.GetLastRect();
                        GUI.DrawTexture(new Rect(last.x + S(4), last.yMax - S(3), last.width - S(8), S(3)), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Skin.Accent, 0, 0);
                    }
                }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("✕  Close", button, GUILayout.Height(S(30)))) Open = Menu.None;
            GUILayout.Space(S(8));
            GUILayout.EndHorizontal();
            var line = GUILayoutUtility.GetLastRect();
            GUI.DrawTexture(new Rect(0, line.yMax + S(2), r.width, 1), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Skin.Border, 0, 0);
            GUILayout.Space(S(8));
            scroll = GUILayout.BeginScrollView(scroll, GUIStyle.none, GUI.skin.verticalScrollbar);
            GUILayout.BeginHorizontal();
            GUILayout.Space(S(16));
            GUILayout.BeginVertical();
            body();
            GUILayout.EndVertical();
            GUILayout.Space(S(16));
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void Note(string s) => GUILayout.Label(s, new GUIStyle(text) { normal = { textColor = new Color(1, 1, 1, 0.85f) }, margin = new RectOffset(0, 0, 0, (int)S(10)) });

        // ---- tackle ---------------------------------------------------------------------------------------------

        void DrawTackle()
        {
            var stats = P.Stats;
            Note($"<b>{Num(P.coins)} coins</b>. Mix and match: equip any rod, reel, line and bait you own.");
            // The loadout: one card per slot (click to see that slot).
            GUILayout.BeginHorizontal();
            foreach (var s in Slots)
            {
                var it = GameData.I.Item(P.equipped[s]);
                bool on = tackleSlot == s;
                var st = new GUIStyle(row) { normal = { background = on ? Skin.Rounded(12, new Color(76 / 255f, 201 / 255f, 240 / 255f, 0.16f), new Color(76 / 255f, 201 / 255f, 240 / 255f, 0.1f), Skin.Accent, 1.5f) : row.normal.background } };
                GUILayout.BeginVertical(st, GUILayout.Height(S(58)));
                GUILayout.Label(SlotNames[s].Split(' ')[0].ToUpper(), new GUIStyle(smallDim) { fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(S(10)) });
                GUILayout.Label($"<b>{it.name}</b>{(it.IsBait && !P.IsStarter(it.id) ? $"  ×{P.BaitCount(it.id)}" : "")}", small);
                GUILayout.EndVertical();
                if (Event.current.type == EventType.MouseUp && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition)) { tackleSlot = s; Event.current.Use(); }
            }
            GUILayout.EndHorizontal();
            var aff = Store.AffinityText(stats.affinity);
            GUILayout.Label($"Cast {stats.castRange / 10:0.#} m · Line strength ×{stats.lineStrength:0.##} · Reel ×{stats.reelSpeed:0.##} · Drag ×{stats.drag:0.##} · Bites ×{stats.biteSpeed:0.##} · Rare odds ×{stats.rareBoost:0.##}" + (aff != "" ? $"\nSpecialties: {aff}" : ""),
                new GUIStyle(smallDim) { margin = new RectOffset(0, 0, (int)S(6), (int)S(8)) });

            // Where the travelling tackle shops are.
            GUILayout.BeginVertical(row);
            GUILayout.Label("<b>Travelling tackle shops</b>: walk there to buy their gear", small);
            var me = Game.I.Player.WorldPos;
            foreach (var s in GameData.I.shops.Where(s => s.IsTackle))
            {
                var stock = Store.Stock(s);
                float d = Vector2.Distance(me, new Vector2(s.x, s.y)) / 1000f;
                GUILayout.Label($"<color={Hex(Skin.Hex(s.awning[0]))}>▌</color><b>{s.name}</b> · {stock.Count} items, level {stock.Min(Shopping.RequiredLevel)}-{stock.Max(Shopping.RequiredLevel)} · <color=#9ad1ff>{(d < 0.1f ? "you are here" : $"{d * 100:0} m {Direction(me, new Vector2(s.x, s.y))}")}</color>", small);
            }
            GUILayout.EndVertical();

            GUILayout.BeginHorizontal();
            foreach (var s in Slots)
                if (GUILayout.Button(SlotNames[s], tackleSlot == s ? tabOn : button)) tackleSlot = s;
            GUILayout.EndHorizontal();
            foreach (var it in GameData.I.items.Where(i => i.slot == tackleSlot).OrderBy(Shopping.RequiredLevel).ThenBy(i => i.price))
                ItemRow(it, () =>
                {
                    if (P.equipped[it.slot] == it.id) GUILayout.Label("Equipped", new GUIStyle(bold) { alignment = TextAnchor.MiddleCenter }, GUILayout.Width(S(170)));
                    else if (P.Has(it.id)) { if (GUILayout.Button("Equip", button, GUILayout.Width(S(170)))) { P.equipped[it.slot] = it.id; P.Save(); } }
                    else
                    {
                        var where = GameData.I.ShopById(Shopping.ShopFor(it));
                        int lvl = Shopping.RequiredLevel(it);
                        GUILayout.Label($"<b>{(where?.IsTackle == true ? where.name : "Bait Shop")}</b>\n{Num(it.price)}c{(lvl > 1 ? $" · level {lvl}" : "")}",
                            new GUIStyle(smallDim) { alignment = TextAnchor.MiddleCenter }, GUILayout.Width(S(170)));
                    }
                });
        }

        static string Direction(Vector2 from, Vector2 to)
        {
            var d = to - from; // world: y south
            string[] names = { "east", "south-east", "south", "south-west", "west", "north-west", "north", "north-east" };
            return names[(Mathf.RoundToInt(Mathf.Atan2(d.y, d.x) / (Mathf.PI / 4)) + 8) % 8];
        }

        void ItemRow(Item it, System.Action action)
        {
            GUILayout.BeginHorizontal(row);
            GUILayout.BeginVertical();
            bool owned = P.Has(it.id);
            string count = it.IsBait ? P.IsStarter(it.id) ? "  · free, endless" : $"  · {P.BaitCount(it.id)} left" : "";
            GUILayout.Label($"<b>{it.name}</b><color=#ffffffaa>{count}</color>", text);
            GUILayout.Label(it.desc, smallDim);
            GUILayout.Label(Store.StatsLine(it), new GUIStyle(small) { normal = { textColor = new Color(1, 1, 1, 0.8f) } });
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(S(174)));
            GUILayout.FlexibleSpace();
            action();
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            if (owned) GUI.DrawTexture(new Rect(GUILayoutUtility.GetLastRect().x, GUILayoutUtility.GetLastRect().y + S(6), S(3), GUILayoutUtility.GetLastRect().height - S(12)),
                Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Skin.Green, 0, 0);
        }

        // ---- shop ---------------------------------------------------------------------------------------------------

        void DrawShop()
        {
            var accent = Skin.Hex(shop.awning?[0] ?? "#e63946");
            GUILayout.BeginVertical(new GUIStyle(row) { normal = { background = Skin.Rounded(12, new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.04f), new Color(0, 0, 0, 0), 1, 0, accent) } });
            if (shop.IsTackle) GUILayout.Label($"<i>{shop.keeper}: \"{shop.greeting}\"</i>", text);
            else GUILayout.Label("<i>Bait, chum and tackle for every angler. Better tackle unlocks as you level up.</i>", text);
            GUILayout.Label($"<color=#ffd166><b>{Num(P.coins)} coins</b></color> · Level {P.Level}", small);
            GUILayout.EndVertical();
            if (!shop.IsTackle)
            {
                GUILayout.Space(S(6));
                GUILayout.BeginHorizontal(row);
                GUILayout.Box(Skin.Icon("map"), GUIStyle.none, GUILayout.Width(S(40)), GUILayout.Height(S(40)));
                GUILayout.BeginVertical();
                GUILayout.Label("<b>Angler's Map</b>", text);
                GUILayout.Label("A hand-drawn map of the whole country: every lake and pond, the shops and the hotspots. Press M to open it, and click a place on it to travel there.", smallDim);
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                if (P.worldMap) GUILayout.Label("Owned · press M", new GUIStyle(bold) { normal = { textColor = Skin.Green }, alignment = TextAnchor.MiddleCenter }, GUILayout.Width(S(170)));
                else
                {
                    GUI.enabled = P.coins >= Tuning.MapPrice;
                    if (GUILayout.Button(Tuning.MapPrice > 0 ? $"Buy · {Tuning.MapPrice}c" : "Take one · free", buy, GUILayout.Width(S(170)))) Store.BuyMap(P);
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }
            if (!shop.IsTackle)
            {
                var cfg = GameData.I.chum;
                GUILayout.BeginHorizontal(row);
                GUILayout.Box(Skin.Icon("bucket"), GUIStyle.none, GUILayout.Width(S(40)), GUILayout.Height(S(40)));
                GUILayout.BeginVertical();
                GUILayout.Label($"<b>Chum Bucket</b><color=#ffffffaa>  · {P.chum} in your bag</color>", text);
                GUILayout.Label("Put it down near the water (C). Fish you land close to it can turn into bait (rarer fish give better bait), and bites nearby are 15% faster.", smallDim);
                GUILayout.Label($"Lasts {cfg.duration / 60:0} minutes or {cfg.maxFish} fish · {cfg.price}c each · carry up to {cfg.maxOwned}", new GUIStyle(small) { normal = { textColor = new Color(1, 1, 1, 0.8f) } });
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                GUILayout.BeginVertical(GUILayout.Width(S(174)));
                foreach (int n in new[] { 1, 5 })
                {
                    GUI.enabled = P.coins >= cfg.price * n && P.chum + n <= cfg.maxOwned;
                    if (GUILayout.Button($"Buy {n} · {cfg.price * n}c", buy)) Game.I.Chum.Buy(n);
                    GUI.enabled = true;
                }
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
            string lastSlot = null;
            foreach (var it in Store.Stock(shop))
            {
                if (it.slot != lastSlot)
                {
                    lastSlot = it.slot;
                    GUILayout.Label(SlotNames[it.slot].ToUpper(), new GUIStyle(smallDim) { fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, (int)S(12), (int)S(2)) });
                }
                ItemRow(it, () =>
                {
                    if (it.IsBait)
                        foreach (int packs in new[] { 1, Store.BulkPacks })
                        {
                            var why = Store.Blocked(P, it, packs);
                            GUI.enabled = why == null;
                            if (GUILayout.Button(why ?? $"Buy {it.pack * packs} · {Num(Store.PackPrice(it, packs))}c", why == null ? buy : button)) Store.Buy(P, it, packs);
                            GUI.enabled = true;
                        }
                    else if (P.equipped[it.slot] == it.id) GUILayout.Label("Equipped", new GUIStyle(bold) { normal = { textColor = Skin.Green }, alignment = TextAnchor.MiddleCenter });
                    else if (P.Has(it.id)) { if (GUILayout.Button("Equip", button)) { P.equipped[it.slot] = it.id; P.Save(); } }
                    else
                    {
                        var why = Store.Blocked(P, it);
                        GUI.enabled = why == null;
                        if (GUILayout.Button(why ?? $"Buy · {Num(it.price)}c", why == null ? buy : button)) Store.Buy(P, it);
                        GUI.enabled = true;
                    }
                });
            }
        }

        // ---- armour -----------------------------------------------------------------------------------------------

        static string BonusText(Bonuses b)
        {
            string Pct(float v) => $"{Mathf.RoundToInt((v - 1) * 100)}%";
            var parts = new List<string>();
            if (b.coins > 1.001f) parts.Add($"+{Pct(b.coins)} coins");
            if (b.xp > 1.001f) parts.Add($"+{Pct(b.xp)} XP");
            if (b.bite > 1.001f) parts.Add($"bites +{Pct(b.bite)}");
            if (b.rare > 1.001f) parts.Add($"rare odds +{Pct(b.rare)}");
            if (b.tension < 0.999f) parts.Add($"tension {Mathf.RoundToInt((1 - b.tension) * 100)}% slower");
            if (b.reel > 1.001f) parts.Add($"reel +{Pct(b.reel)}");
            if (b.weight > 1.001f) parts.Add($"fish {Pct(b.weight)} bigger");
            if (b.@double > 0) parts.Add($"{Mathf.RoundToInt(b.@double * 100)}% Double Catch");
            return parts.Count > 0 ? string.Join(" · ", parts) : "none yet";
        }

        string tryOn, tryOnNext; // armour with a piece you're pointing at swapped in, for the preview

        void DrawArmour()
        {
            var data = GameData.I;
            var b = P.Bonuses;
            Note($"<b>Level {P.Level}</b> · {Num(P.coins)} coins. Armour doesn't change your tackle: it adds bonuses of its own. Wear all four pieces of a set for its set effect. Higher levels unlock better sets.");
            // Your angler in what you're wearing; point at a piece below to try it on.
            var ev = Event.current;
            if (ev.type == EventType.Repaint) { tryOn = tryOnNext == AnglerMesh.GearOf(P) ? null : tryOnNext; tryOnNext = null; }
            GUILayout.BeginHorizontal();
            var pr = GUILayoutUtility.GetRect(S(210), S(280), GUILayout.Width(S(210)), GUILayout.Height(S(280)));
            GUI.DrawTexture(pr, Skin.Rounded(12, new Color(1, 1, 1, 0.07f), new Color(1, 1, 1, 0.02f), Skin.Border, 1));
            float drag = 0;
            if (ev.type == EventType.MouseDrag && pr.Contains(ev.mousePosition)) { drag = -ev.delta.x * 0.9f; ev.Use(); }
            var mp = Multiplayer.I;
            var preview = AnglerPreview.Get(tryOn ?? AnglerMesh.GearOf(P), mp != null && mp.Online ? (int)(mp.MyId % 10) : 0, drag);
            if (preview) GUI.DrawTexture(new Rect(pr.x + S(4), pr.y + S(4), pr.width - S(8), pr.height - S(26)), preview, ScaleMode.ScaleToFit);
            Label(new Rect(pr.x, pr.yMax - S(22), pr.width, S(18)), tryOn != null ? "Trying it on" : "Drag to turn", smallDim, tryOn != null ? Skin.Gold : (Color?)null, TextAnchor.MiddleCenter);
            GUILayout.Space(S(10));
            GUILayout.BeginVertical(row);
            GUILayout.Label($"<b>Wearing:</b> {string.Join(" · ", data.armourSlots.Select(sl => P.armour.TryGetValue(sl, out var id) && id != null ? data.SetById(data.Piece(id).set.id).pieces[sl] : $"no {data.armourSlotLabels[sl].ToLower()}"))}", small);
            GUILayout.Label($"<b>Bonuses:</b> {BonusText(b)}", small);
            if (b.set != null) GUILayout.Label($"<color=#ffd166><b>Set effect: {data.SetById(b.set).effect.name}</b> · {data.SetById(b.set).effect.desc}</color>", small);
            GUILayout.Label("Every piece looks different: point at one below to see it on you. Other anglers see what you wear.", smallDim);
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            foreach (var set in data.sets)
            {
                bool locked = P.Level < set.level;
                GUILayout.BeginVertical(row);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<b>{set.name}</b>  <color=#ffffffaa>{(locked ? $"unlocks at level {set.level}" : $"level {set.level}")} · {Num(set.price)}c a piece</color>", text);
                GUILayout.FlexibleSpace();
                int owned = data.armourSlots.Count(sl => P.armourOwned.Contains($"{set.id}_{sl}"));
                GUILayout.Label($"{owned}/4", smallDim, GUILayout.Width(S(40)));
                GUILayout.EndHorizontal();
                var perk = string.Join(", ", set.perk.Select(kv => $"+{Mathf.RoundToInt(kv.Value * 100)}% {(kv.Key == "tension" ? "steadier line" : kv.Key == "weight" ? "fish size" : kv.Key)}"));
                GUILayout.Label($"Each piece: {perk} · <color=#ffd166>Full set, {set.effect.name}:</color> {set.effect.desc}", smallDim);
                GUILayout.BeginHorizontal();
                foreach (var sl in data.armourSlots)
                {
                    var id = $"{set.id}_{sl}";
                    bool has = P.armourOwned.Contains(id);
                    bool worn = P.armour.TryGetValue(sl, out var w) && w == id;
                    GUILayout.BeginVertical(GUILayout.Width(S(170)));
                    GUILayout.Label(set.pieces[sl], new GUIStyle(small) { wordWrap = false, clipping = TextClipping.Clip });
                    if (worn) { if (GUILayout.Button("Worn · take off", tabOn)) Store.WearArmour(P, id); }
                    else if (has) { if (GUILayout.Button("Wear", button)) Store.WearArmour(P, id); }
                    else
                    {
                        var why = Store.ArmourBlocked(P, set, sl);
                        GUI.enabled = why == null;
                        if (GUILayout.Button(why ?? $"Buy · {Num(set.price)}c", why == null ? buy : button)) Store.BuyArmour(P, set, sl);
                        GUI.enabled = true;
                    }
                    GUILayout.EndVertical();
                    if (ev.type == EventType.Repaint && GUILayoutUtility.GetLastRect().Contains(ev.mousePosition))
                        tryOnNext = AnglerMesh.With(AnglerMesh.GearOf(P), sl, set.id);
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
        }

        // ---- pets and the zoo ----------------------------------------------------------------------------------

        void PetCard(Pet pet, float w, System.Action action, bool known = true)
        {
            var colour = Skin.Hex(GameData.I.petRarities[pet.rarity].color);
            var bg = known ? Skin.Rounded(12, new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.04f), colour, 1.5f) : Skin.Rounded(12, new Color(1, 1, 1, 0.03f), new Color(1, 1, 1, 0.02f), new Color(1, 1, 1, 0.2f));
            GUILayout.BeginVertical(new GUIStyle(row) { normal = { background = bg }, margin = new RectOffset(0, (int)S(6), (int)S(3), (int)S(3)) }, GUILayout.Width(w), GUILayout.MinHeight(S(210)));
            var ir = GUILayoutUtility.GetRect(w - S(24), S(70));
            var icon = FishIcons.GetPet(pet.id);
            if (icon)
            {
                var old = GUI.color;
                if (!known) GUI.color = new Color(0, 0, 0, 0.6f);
                GUI.DrawTexture(ir, icon, ScaleMode.ScaleToFit);
                GUI.color = old;
            }
            GUILayout.Label(known ? $"<b>{pet.name}</b>" : "<b>???</b>", text);
            GUILayout.Label(GameData.I.petRarities[pet.rarity].label.ToUpper(), new GUIStyle(small) { fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(S(10)), normal = { textColor = colour } });
            if (known)
            {
                GUILayout.Label($"<b>{pet.ability}</b>", new GUIStyle(small) { normal = { textColor = Skin.Gold } });
                GUILayout.Label(pet.desc, smallDim);
            }
            GUILayout.FlexibleSpace();
            action?.Invoke();
            GUILayout.EndVertical();
        }

        void PetGrid(List<Pet> pets, System.Action<Pet, float> card)
        {
            float width = Mathf.Min(S(880), Screen.width - S(32)) - S(48);
            int cols = Mathf.Max(1, (int)(width / S(185)));
            float cw = width / cols - S(6);
            for (int i = 0; i < pets.Count; i += cols)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(pets.Count, i + cols); j++) card(pets[j], cw);
                GUILayout.EndHorizontal();
            }
        }

        void ZooLine()
        {
            var z = Zoo.Now;
            var me = Game.I.Player.WorldPos;
            float d = Vector2.Distance(me, new Vector2(z.spot.x, z.spot.y)) / WorldMap.UnitsPerMetre;
            var names = z.pets.Select(id => GameData.I.PetById(id)).Select(pt => $"<color={Hex(Skin.Hex(GameData.I.petRarities[pt.rarity].color))}>{pt.name}</color>");
            GUILayout.BeginVertical(new GUIStyle(row) { normal = { background = Skin.Rounded(12, new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.04f), new Color(0, 0, 0, 0), 1, 0, Skin.Hex("#9d4edd")) } });
            GUILayout.Label($"<b>The Travelling Zoo</b> is at {z.spot.area} ({(d < 10 ? "right here" : $"{d:0} m {Direction(me, new Vector2(z.spot.x, z.spot.y))}")}) and moves on in <b>{Clock(z.left)}</b>.", small);
            GUILayout.Label($"Today's animals: {string.Join(", ", names)}", small);
            GUILayout.EndVertical();
        }

        void DrawPets()
        {
            var data = GameData.I;
            Note($"<b>{P.pets.Count} / {data.pets.Count}</b> pets. Each has one ability; take one along at a time. Adopt more at the Travelling Zoo (it moves every 15 minutes and brings three new animals).");
            ZooLine();
            if (P.pets.Count > 0)
            {
                GUILayout.Label("YOUR PETS", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, (int)S(10), (int)S(2)) });
                PetGrid(P.pets.Select(data.PetById).ToList(), (pet, w) => PetCard(pet, w, () =>
                {
                    bool with = P.pet == pet.id;
                    if (GUILayout.Button(with ? "With you · send home" : "Take along", with ? tabOn : button)) Zoo.Choose(P, pet.id);
                }));
            }
            GUILayout.Label("COLLECTION", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, (int)S(10), (int)S(2)) });
            var rest = data.pets.Where(pt => !P.pets.Contains(pt.id)).ToList();
            PetGrid(rest, (pet, w) => PetCard(pet, w, () => GUILayout.Label($"{Num(Zoo.Price(pet))}c at the zoo", new GUIStyle(smallDim) { alignment = TextAnchor.MiddleCenter }), known: true));
        }

        void DrawZoo()
        {
            var data = GameData.I;
            GUILayout.BeginVertical(new GUIStyle(row) { normal = { background = Skin.Rounded(12, new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.04f), new Color(0, 0, 0, 0), 1, 0, Skin.Hex("#9d4edd")) } });
            GUILayout.Label("<i>\"Roll up, roll up! Three fine animals today, each with a talent for fishing. Tomorrow? Who knows!\"</i>", text);
            GUILayout.Label($"<color=#ffd166><b>{Num(P.coins)} coins</b></color> · moving on in {Clock(Zoo.Now.left)}", small);
            GUILayout.EndVertical();
            GUILayout.Space(S(6));
            PetGrid(Zoo.Now.pets.Select(data.PetById).ToList(), (pet, w) => PetCard(pet, w, () =>
            {
                var why = Zoo.Blocked(P, pet);
                GUI.enabled = why == null;
                if (GUILayout.Button(why ?? $"Adopt · {Num(Zoo.Price(pet))}c", why == null ? buy : button)) Zoo.Adopt(P, pet);
                GUI.enabled = true;
            }));
        }

        // ---- achievements -----------------------------------------------------------------------------------

        void DrawGoals()
        {
            var list = Achievements.Available;
            int earned = list.Count(a => P.achievements.ContainsKey(a.id));
            Note($"<b>{earned} / {list.Count}</b> achievements earned. Many unlock new tackle in the shops.");
            foreach (var a in list.OrderBy(a => P.achievements.ContainsKey(a.id)).ThenByDescending(a => Achievements.Progress(P, a).fraction))
            {
                var pr = Achievements.Progress(P, a);
                var unlocks = GameData.I.items.Where(it => it.unlock == a.id).Select(it => it.name).ToList();
                GUILayout.BeginHorizontal(row);
                GUILayout.Box(Skin.Icon("medal"), GUIStyle.none, GUILayout.Width(S(30)), GUILayout.Height(S(30)));
                GUILayout.Space(S(6));
                GUILayout.BeginVertical();
                GUILayout.Label(pr.done ? $"<color=#7bd389><b>✓ {a.name}</b></color>" : $"<b>{a.name}</b>", text);
                GUILayout.Label(a.desc + (unlocks.Count > 0 ? $" <color=#9ad1ff>· Unlocks {string.Join(", ", unlocks)}</color>" : ""), smallDim);
                var bar = GUILayoutUtility.GetRect(S(300), S(6));
                Bar(bar, pr.fraction, pr.done ? Skin.Hex("#f9c74f") : Skin.Accent2, pr.done ? Skin.Gold : Skin.Green);
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                GUILayout.BeginVertical(GUILayout.Width(S(110)));
                GUILayout.Label($"<b>+{Num(a.coins)}c</b>", new GUIStyle(text) { alignment = TextAnchor.MiddleRight, normal = { textColor = Skin.Gold } });
                GUILayout.Label($"{Num(pr.value)} / {Num((long)pr.goal)}{(a.unit != null ? $" {a.unit}" : "")}", new GUIStyle(smallDim) { alignment = TextAnchor.MiddleRight });
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
        }

        // ---- map events -------------------------------------------------------------------------------------

        void DrawEvents()
        {
            var cfg = GameData.I.events;
            double now = MapEvents.Now;
            var (current, upcoming) = MapEvents.Around(now, 6);
            Note("Map events happen at a lake or pond every 10 minutes, on the same clock as the web game. Head over and fish in the ring of light for bonuses and a reward.");
            void Card(MapEvent ev, string when, string extra)
            {
                var T = ev.T;
                var accent = Skin.Hex(T.color);
                GUILayout.BeginVertical(new GUIStyle(row) { normal = { background = Skin.Rounded(12, new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.04f), new Color(0, 0, 0, 0), 1, 0, accent) } });
                GUILayout.Label($"<color={Hex(accent)}><b>{T.name.ToUpper()}</b></color> · {ev.place.name}", text);
                GUILayout.Label(when, small);
                GUILayout.Label(extra ?? MapEvents.SoloDesc(ev.type), smallDim);
                GUILayout.EndVertical();
            }
            var live = Game.I.Events.Active;
            if (live != null)
            {
                GUILayout.Label("HAPPENING NOW", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
                var e = Game.I.Events;
                string progress = live.type switch
                {
                    "haul" => $"{e.Progress:0} / {e.Goal:0} fish landed. Hit the goal for {cfg.tuning.haul.coins:0} coins + {cfg.tuning.haul.coinsPerFish:0} a fish and {cfg.tuning.haul.xp:0} XP.",
                    "tide" => e.Rush > 0 ? $"GOLDEN RUSH: double points for {e.Rush:0} more seconds!" : $"Golden meter: {e.Progress:0} / {e.Goal:0} points. Fill it for a Golden Rush.",
                    _ => $"{e.Fish} fish landed in the shoal so far ({cfg.tuning.shoal.coinsPerFish:0} coins each at the end).",
                };
                Card(live, $"{Clock(live.end - now)} left · {MapEvents.Direction(live)}", progress);
            }
            GUILayout.Label("COMING UP", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, (int)S(8), (int)S(2)) });
            foreach (var ev in upcoming)
                Card(ev, $"in {Clock(ev.start - now)} · lasts {ev.T.duration / 60:0} min · {MapEvents.Direction(ev)}", null);
        }

        // ---- multiplayer: the title screen, the Online tab and chat -------------------------------------------------

        string titleView = "main"; // "main", "host" or "join"
        string joinCode = "", gameName = "", chatText = "";
        bool privateGame, chatOpen, chatFocus;
        List<Unity.Services.Multiplayer.ISessionInfo> openGames;
        bool loadingGames;
        float copiedAt = -99;

        async void RefreshGames()
        {
            if (loadingGames) return;
            loadingGames = true;
            openGames = await Multiplayer.I.Browse();
            loadingGames = false;
        }

        void DrawTitle()
        {
            float W = Screen.width, H = Screen.height;
            GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(6 / 255f, 14 / 255f, 20 / 255f, 0.55f), 0, 0);
            float w = Mathf.Min(S(520), W - S(32));
            float h = titleView == "join" ? Mathf.Min(S(600), H - S(60)) : S(420);
            var r = new Rect((W - w) / 2, (H - h) / 2, w, h);
            GUI.Box(r, GUIContent.none, window);
            var big = new GUIStyle(banner) { fontSize = Mathf.RoundToInt(S(40)) };
            Label(new Rect(r.x, r.y + S(22), r.width, S(50)), "THE LAST CAST", big, Skin.Gold);
            Label(new Rect(r.x, r.y + S(68), r.width, S(20)), "A cosy fishing game, now with friends", new GUIStyle(smallDim) { alignment = TextAnchor.MiddleCenter });
            GUILayout.BeginArea(new Rect(r.x + S(28), r.y + S(100), r.width - S(56), r.height - S(116)));
            GUILayout.Label("YOUR NAME", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
            var field = new GUIStyle(GUI.skin.textField) { font = Skin.Regular, fontSize = Mathf.RoundToInt(S(16)), padding = new RectOffset(10, 10, 6, 6) };
            Multiplayer.PlayerName = GUILayout.TextField(Multiplayer.PlayerName, 16, field, GUILayout.Height(S(34)));
            GUILayout.Space(S(10));
            DrawOnlineControls(field, title: true);
            GUILayout.EndArea();
            DrawUpdateStrip(r);
        }

        /// <summary>Host / join controls, used on the title screen and in the Online tab when you're not in a game.</summary>
        void DrawOnlineControls(GUIStyle field, bool title)
        {
            var mp = Multiplayer.I;
            bool busy = mp.Mode == NetMode.Busy;
            GUI.enabled = !busy;
            if (titleView == "main")
            {
                if (title && GUILayout.Button("Play solo", buy, GUILayout.Height(S(40)))) Open = Menu.None;
                if (GUILayout.Button("Host a game", title ? button : buy, GUILayout.Height(S(40)))) titleView = "host";
                if (GUILayout.Button("Join a game", button, GUILayout.Height(S(40)))) { titleView = "join"; RefreshGames(); }
            }
            else if (titleView == "host")
            {
                GUILayout.Label("GAME NAME", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
                gameName = GUILayout.TextField(gameName == "" ? $"{Multiplayer.PlayerName}'s lake" : gameName, 32, field, GUILayout.Height(S(34)));
                privateGame = GUILayout.Toggle(privateGame, "  Private: only people with the join code can find it", new GUIStyle(GUI.skin.toggle) { font = Skin.Regular, fontSize = Mathf.RoundToInt(S(13)), normal = { textColor = Skin.Text }, onNormal = { textColor = Skin.Text } });
                GUILayout.Label($"Up to {Multiplayer.MaxPlayers} anglers. Your progress stays yours; everyone shares the lake, the hotspots and the map events.", smallDim);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Back", button, GUILayout.Height(S(38)), GUILayout.Width(S(100)))) titleView = "main";
                if (GUILayout.Button(busy ? "Opening..." : "Host", buy, GUILayout.Height(S(38))))
                    StartHost();
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("JOIN WITH A CODE", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
                GUILayout.BeginHorizontal();
                joinCode = GUILayout.TextField(joinCode, 12, field, GUILayout.Height(S(34))).ToUpperInvariant();
                GUI.enabled = !busy && joinCode.Trim().Length >= 4;
                if (GUILayout.Button(busy ? "Joining..." : "Join", buy, GUILayout.Height(S(34)), GUILayout.Width(S(110)))) StartJoin(joinCode, null);
                GUI.enabled = !busy;
                GUILayout.EndHorizontal();
                GUILayout.Space(S(8));
                GUILayout.BeginHorizontal();
                GUILayout.Label("OPEN GAMES", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(loadingGames ? "Loading..." : "Refresh", button, GUILayout.Width(S(100)))) RefreshGames();
                GUILayout.EndHorizontal();
                gamesScroll = GUILayout.BeginScrollView(gamesScroll, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(S(title ? 170 : 220)));
                if (openGames == null || openGames.Count == 0)
                    GUILayout.Label(loadingGames ? "Looking for games..." : "No open games right now. Ask a friend for their join code, or host your own!", smallDim);
                else
                    foreach (var g in openGames)
                    {
                        GUILayout.BeginHorizontal(row);
                        GUILayout.Label($"<b>{g.Name}</b>\n<color=#ffffffaa>{g.MaxPlayers - g.AvailableSlots} / {g.MaxPlayers} anglers</color>", small);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Join", buy, GUILayout.Width(S(90)))) StartJoin(null, g.Id);
                        GUILayout.EndHorizontal();
                    }
                GUILayout.EndScrollView();
                if (GUILayout.Button("Back", button, GUILayout.Height(S(34)), GUILayout.Width(S(100)))) titleView = "main";
            }
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(mp.Status)) GUILayout.Label(mp.Status, new GUIStyle(small) { normal = { textColor = mp.Status.StartsWith("Couldn't") ? Skin.Red : Skin.Accent } });
        }

        Vector2 gamesScroll;

        async void StartHost()
        {
            Multiplayer.PlayerName = Multiplayer.PlayerName;
            if (await Multiplayer.I.Host(gameName, privateGame)) { titleView = "main"; Open = Menu.Online; }
        }

        async void StartJoin(string code, string id)
        {
            if (await Multiplayer.I.Join(code, id)) { titleView = "main"; Open = Menu.None; }
        }

        void DrawOnline()
        {
            var mp = Multiplayer.I;
            if (!mp.Online)
            {
                Note("Fish with friends: host your lake and give them the join code, or join someone else's game. Up to 8 anglers; everyone keeps their own progress.");
                var field = new GUIStyle(GUI.skin.textField) { font = Skin.Regular, fontSize = Mathf.RoundToInt(S(16)), padding = new RectOffset(10, 10, 6, 6) };
                GUILayout.BeginVertical(row);
                GUILayout.Label("YOUR NAME", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
                Multiplayer.PlayerName = GUILayout.TextField(Multiplayer.PlayerName, 16, field, GUILayout.Height(S(34)));
                GUILayout.EndVertical();
                DrawOnlineControls(field, title: false);
                return;
            }
            var s = mp.Session;
            GUILayout.BeginVertical(new GUIStyle(row) { normal = { background = Skin.Rounded(12, new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.04f), new Color(0, 0, 0, 0), 1, 0, Skin.Accent) } });
            GUILayout.Label($"<b>{s?.Name}</b>  <color=#ffffffaa>{(mp.IsHost ? "you're the host" : "hosted by a friend")}{(s != null && s.IsPrivate ? " · private" : "")}</color>", text);
            GUILayout.BeginHorizontal();
            GUILayout.Label("JOIN CODE", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold }, GUILayout.Width(S(90)));
            GUILayout.Label($"<b>{s?.Code}</b>", new GUIStyle(title) { fontSize = Mathf.RoundToInt(S(28)), normal = { textColor = Skin.Gold } });
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Time.time - copiedAt < 2 ? "Copied!" : "Copy code", button, GUILayout.Width(S(130)), GUILayout.Height(S(34))))
            {
                GUIUtility.systemCopyBuffer = s?.Code ?? "";
                copiedAt = Time.time;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Friends join from the title screen or the Online tab: Join a game, then type this code.", smallDim);
            GUILayout.EndVertical();

            GUILayout.Label($"ANGLERS · {1 + mp.Remotes.Count} / {Multiplayer.MaxPlayers}", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, (int)S(10), (int)S(2)) });
            void PlayerRow(string name, int level, int catches, float best, Color colour, string note)
            {
                GUILayout.BeginHorizontal(row);
                GUILayout.Label($"<color={Hex(colour)}>●</color>  <b>{name}</b>  <color=#ffffffaa>Lv {level}{note}</color>", text);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{catches} fish{(best > 0 ? $" · best {best} kg" : "")}", smallDim);
                GUILayout.EndHorizontal();
            }
            PlayerRow(Multiplayer.PlayerName, P.Level, mp.SessionCatches, mp.SessionBest, Skin.Gold, mp.IsHost ? " · host · you" : " · you");
            foreach (var r in mp.Remotes.Values)
                if (r && r.State != null) PlayerRow(r.State.n, r.State.lv, r.State.c, r.State.best, RemoteColour(r.State.col), r.State.id == 0 ? " · host" : "");
            GUILayout.Space(S(10));
            GUILayout.Label("Press Enter to chat. Catches, trophies and level-ups show up for everyone.", smallDim);
            if (GUILayout.Button(mp.IsHost ? "Close the game (everyone goes back to fishing alone)" : "Leave the game", button, GUILayout.Height(S(36)))) _ = mp.Leave();
        }

        static readonly string[] AnglerColours = { "#e4572e", "#f3a712", "#a8c686", "#669bbc", "#c77dff", "#ff8fab", "#43aa8b", "#f9c74f", "#90be6d", "#577590" };
        static Color RemoteColour(int i) => Skin.Hex(AnglerColours[i % AnglerColours.Length]);

        /// <summary>Top right when you're in a game: everyone fishing (where the 2D game lists the anglers).
        /// Returns where the panel ends, for the voyage panel to go under it.</summary>
        float DrawAnglers()
        {
            var mp = Multiplayer.I;
            var rows = new List<(string name, int lv, int c, Color col, bool me)> { (Multiplayer.PlayerName, P.Level, mp.SessionCatches, Skin.Gold, true) };
            foreach (var r in mp.Remotes.Values) if (r && r.State != null) rows.Add((r.State.n, r.State.lv, r.State.c, RemoteColour(r.State.col), false));
            rows = rows.OrderByDescending(x => x.c).ToList();
            var rect = new Rect(Screen.width - S(250), S(14), S(236), S(34) + rows.Count * S(22));
            GUI.Box(rect, GUIContent.none, panel);
            Label(new Rect(rect.x + S(12), rect.y + S(8), rect.width, S(16)), $"ANGLERS · {rows.Count}/{Multiplayer.MaxPlayers}", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(S(11)) });
            for (int i = 0; i < rows.Count; i++)
            {
                var x = rows[i];
                float y = rect.y + S(30) + i * S(22);
                if (x.me) GUI.Box(new Rect(rect.x + S(4), y, rect.width - S(8), S(21)), GUIContent.none, Box(Skin.Rounded(8, new Color(76 / 255f, 201 / 255f, 240 / 255f, 0.12f), new Color(76 / 255f, 201 / 255f, 240 / 255f, 0.12f), new Color(0, 0, 0, 0)), 9));
                Label(new Rect(rect.x + S(8), y, S(16), S(20)), (i + 1).ToString(), smallDim, i == 0 ? Skin.Gold : (Color?)null, TextAnchor.MiddleRight);
                GUI.DrawTexture(new Rect(rect.x + S(30), y + S(6), S(9), S(9)), Skin.Rounded(5, x.col, x.col, new Color(0, 0, 0, 0)));
                Label(new Rect(rect.x + S(46), y, S(120), S(20)), x.name, x.me ? new GUIStyle(small) { fontStyle = FontStyle.Bold } : small, null, TextAnchor.MiddleLeft);
                Label(new Rect(rect.x + S(150), y, S(76), S(20)), $"Lv {x.lv} · {x.c}", smallDim, null, TextAnchor.MiddleRight);
            }
            return rect.yMax + S(10);
        }

        /// <summary>The chat line above the feed (Enter to open and send, Esc to cancel).</summary>
        void DrawChat()
        {
            if (!chatOpen) return;
            var r = new Rect(S(14), Screen.height - S(60), Mathf.Min(S(420), Screen.width * 0.5f), S(36));
            GUI.Box(r, GUIContent.none, pill);
            var e = Event.current;
            if (e.type == UnityEngine.EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                SendChat();
                e.Use();
                return;
            }
            if (e.type == UnityEngine.EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                chatOpen = false;
                e.Use();
                return;
            }
            GUI.SetNextControlName("chat");
            var st = new GUIStyle(text) { alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
            chatText = GUI.TextField(new Rect(r.x + S(14), r.y, r.width - S(28), r.height), chatText, 120, st);
            if (chatFocus) { GUI.FocusControl("chat"); chatFocus = false; }
            if (chatText.Length == 0) Label(new Rect(r.x + S(14), r.y, r.width, r.height), "Say something... (Enter to send)", new GUIStyle(smallDim) { alignment = TextAnchor.MiddleLeft });
        }

        void SendChat()
        {
            var t = chatText.Trim();
            chatOpen = false;
            chatText = "";
            if (t.Length == 0) return;
            Feed($"{Multiplayer.PlayerName}: {t}", Color.white);
            if (Multiplayer.I.Online) Multiplayer.I.SendReliable("chat", new Newtonsoft.Json.Linq.JObject { ["text"] = t });
            else Feed("(You're fishing on your own: host or join a game in the Online tab to chat.)", Skin.Dim);
        }

        // ---- fish index ------------------------------------------------------------------------------------------

        static readonly Dictionary<string, string> WildNouns = new Dictionary<string, string>
            { ["willowPond"] = "Pond", ["frogPond"] = "Pool", ["crystalPond"] = "Tarn", ["blackBog"] = "Mire", ["millPond"] = "Millpond", ["wildLake"] = "Lake" };

        void DrawIndex()
        {
            var data = GameData.I;
            var seaIds = new HashSet<string>(data.voyage.SeaSpecies());
            List<Species> Sorted(IEnumerable<Species> list) => list.OrderBy(s => System.Array.IndexOf(Tuning.RarityOrder, s.rarity)).ThenBy(s => s.name).ToList();
            var lake = Sorted(data.species.Where(s => data.zones.Any(z => z.fish.ContainsKey(s.id))));
            var sea = Sorted(data.species.Where(s => seaIds.Contains(s.id) && !lake.Contains(s)));
            int found = lake.Concat(sea).Count(s => P.index.ContainsKey(s.id));
            Note($"<b>{found} / {lake.Count + sea.Count}</b> species discovered. Sea fish are caught on boat voyages: board the boat at the South Beach dock when it comes in.");
            float width = Mathf.Min(S(880), Screen.width - S(32)) - S(48);
            int cols = Mathf.Max(1, (int)(width / S(180)));
            float cw = width / cols - S(6);
            foreach (var (heading, list) in new[] { ("THE LAKE AND THE WILDS", lake), ("AT SEA", sea) })
            {
                GUILayout.Label($"{heading} · {list.Count(s => P.index.ContainsKey(s.id))}/{list.Count}", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, (int)S(8), (int)S(4)) });
                for (int i = 0; i < list.Count; i += cols)
                {
                    GUILayout.BeginHorizontal();
                    for (int j = i; j < Mathf.Min(list.Count, i + cols); j++) IndexCard(list[j], cw);
                    GUILayout.EndHorizontal();
                }
            }
        }

        /// <summary>Where a sea species bites: its locations (legendaries during the location's event), and the bosses' waters.</summary>
        static IEnumerable<string> SeaPlaces(Species s)
        {
            var V = GameData.I.voyage;
            foreach (var l in V.locations)
            {
                if (l.legendary == s.id) yield return $"{l.name} (during {V.Event(l.@event).name})";
                else if (l.zone.fish.ContainsKey(s.id) || l.mythic == s.id) yield return l.name;
            }
            foreach (var e in V.events)
                if (e.mods.extraFish != null && e.mods.extraFish.ContainsKey(s.id)) yield return $"anywhere during {e.name}";
            foreach (var b in V.bosses)
                if (b.zone.fish.ContainsKey(s.id)) yield return $"{b.name}'s waters";
        }

        void IndexCard(Species s, float w)
        {
            var data = GameData.I;
            P.index.TryGetValue(s.id, out var e);
            var rc = data.Rarity(s.rarity).Color;
            var bg = e != null ? Skin.Rounded(12, new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.04f), rc, 1.5f) : Skin.Rounded(12, new Color(1, 1, 1, 0.03f), new Color(1, 1, 1, 0.02f), new Color(1, 1, 1, 0.2f));
            GUILayout.BeginVertical(new GUIStyle(row) { normal = { background = bg } }, GUILayout.Width(w), GUILayout.MinHeight(S(200)));
            var icon = FishIcons.Get(s.id);
            var ir = GUILayoutUtility.GetRect(w - S(24), S(64));
            if (icon)
            {
                var old = GUI.color;
                if (e == null) GUI.color = new Color(0, 0, 0, 0.6f); // a silhouette until you catch one
                GUI.DrawTexture(ir, icon, ScaleMode.ScaleToFit);
                GUI.color = old;
            }
            GUILayout.Label(e != null ? $"<b>{s.name}</b>" : "<b>???</b>", text);
            GUILayout.Label(data.Rarity(s.rarity).label.ToUpper(), new GUIStyle(small) { fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(S(10)), normal = { textColor = rc } });
            GUILayout.Label(e != null ? $"Caught {e.count}× · Best {e.bestKg} kg" : "Not caught yet", smallDim);
            var trophy = Trophies.TrophyKg(s);
            if (trophy != null) GUILayout.Label($"★ Trophy {trophy} kg+{(e?.trophies > 0 ? $" · {e.trophies} landed" : "")}", new GUIStyle(small) { normal = { textColor = e?.trophies > 0 ? Skin.Gold : new Color(1, 1, 1, 0.55f) } });
            var zones = data.zones.Where(z => z.fish.ContainsKey(s.id)).ToList();
            var where = zones.Where(z => !z.id.StartsWith("wild")).Select(z => z.name)
                .Concat(zones.Where(z => z.id.StartsWith("wild")).Select(z => z.kind).Distinct().Select(k => $"any {(WildNouns.TryGetValue(k, out var n) ? n : "pond")} in the wilds"))
                .Concat(SeaPlaces(s)).Distinct();
            GUILayout.Label($"Found in: {string.Join(", ", where)}", new GUIStyle(smallDim) { fontSize = Mathf.RoundToInt(S(11)) });
            GUILayout.EndVertical();
        }

        // ---- trophies --------------------------------------------------------------------------------------------

        void DrawTrophies()
        {
            Note("A <b>trophy</b> is one of the heaviest of its kind (the Fish Index shows each trophy weight). About 1 fish in 300 is a <b>giant</b>, bigger than its kind should ever grow, and fights much harder. Every trophy gets a name.");
            GUILayout.BeginHorizontal();
            foreach (var (icon, n, label) in new[] { ("trophy", P.trophyCount.ToString("N0"), "TROPHIES"), ("crown", P.giants.ToString("N0"), "GIANTS"), ("fish", $"{P.heaviest} kg", "HEAVIEST"), ("book", P.trophies.Count.ToString(), "ON THE WALL") })
            {
                GUILayout.BeginVertical(row);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Box(Skin.Icon(icon), GUIStyle.none, GUILayout.Width(S(20)), GUILayout.Height(S(20)));
                GUILayout.Label($"<b>{n}</b>", new GUIStyle(heavy) { wordWrap = false });
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Label(label, new GUIStyle(smallDim) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(S(10)) });
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            if (P.trophies.Count == 0) { Note("Your cabinet is empty. Land a trophy-sized fish to put it on the wall."); return; }
            float width = Mathf.Min(S(880), Screen.width - S(32)) - S(48);
            int cols = Mathf.Max(1, (int)(width / S(176)));
            float cw = width / cols - S(6);
            var serif = new GUIStyle(text) { font = SerifFont, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            for (int i = 0; i < P.trophies.Count; i += cols)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(P.trophies.Count, i + cols); j++)
                {
                    var t = P.trophies[j];
                    var s = GameData.I.Fish(t.species);
                    var tier = Trophies.Tiers[t.tier];
                    var bg = Skin.Rounded(10, t.tier == "giant" ? Skin.Hex("#6b2d1d") : Skin.Hex("#5b3a1e"), t.tier == "giant" ? Skin.Hex("#3a1610") : Skin.Hex("#3b2513"), tier.color, 2);
                    GUILayout.BeginVertical(new GUIStyle(plaque) { normal = { background = bg }, margin = new RectOffset(0, (int)S(6), (int)S(3), (int)S(3)) }, GUILayout.Width(cw));
                    var ir = GUILayoutUtility.GetRect(cw - S(24), S(56));
                    var icon = FishIcons.Get(t.species);
                    if (icon) GUI.DrawTexture(ir, icon, ScaleMode.ScaleToFit);
                    Icon(new Rect(ir.xMax - S(16), ir.y, S(18), S(18)), t.tier == "giant" ? "crown" : "trophy");
                    GUILayout.Label($"\"{t.name}\"", new GUIStyle(serif) { normal = { textColor = tier.color } });
                    GUILayout.Label(s?.name ?? t.species, new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, normal = { textColor = GameData.I.Rarity(s?.rarity ?? "common").Color } });
                    GUILayout.Label($"<b>{t.kg} kg</b> <color=#ffffffaa>· {tier.label}</color>", new GUIStyle(text) { alignment = TextAnchor.MiddleCenter });
                    GUILayout.Label($"{t.zone} · {Ago(t.at)}", new GUIStyle(smallDim) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(S(11)) });
                    GUILayout.EndVertical();
                }
                GUILayout.EndHorizontal();
            }
        }

        static Font serif;
        static Font SerifFont => serif ??= Font.CreateDynamicFontFromOSFont(new[] { "Georgia", "Times New Roman" }, 16);

        static string Ago(long ms)
        {
            long s = System.Math.Max(0, (Profile.Now - ms) / 1000);
            if (s < 60) return $"{s}s ago";
            if (s < 3600) return $"{s / 60}m ago";
            if (s < 86400) return $"{s / 3600}h ago";
            return $"{s / 86400}d ago";
        }

        // ---- history --------------------------------------------------------------------------------------------

        void DrawHistory()
        {
            if (P.history.Count == 0) { Note("No catches yet. Go cast a line!"); return; }
            Note($"Your last {P.history.Count} catches.");
            void Row(string a, string b, string c, string d, string e, GUIStyle st, Color? fish = null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(a, st, GUILayout.Width(S(80)));
                GUILayout.Label(b, fish.HasValue ? new GUIStyle(st) { normal = { textColor = fish.Value } } : st, GUILayout.Width(S(210)));
                GUILayout.Label(c, st, GUILayout.Width(S(80)));
                GUILayout.Label(d, st, GUILayout.Width(S(70)));
                GUILayout.Label(e, st);
                GUILayout.EndHorizontal();
            }
            Row("WHEN", "FISH", "WEIGHT", "POINTS", "WHERE", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
            foreach (var c in P.history)
            {
                var s = GameData.I.Fish(c.species);
                Row(Ago(c.at), s?.name ?? c.species, $"{c.kg} kg", $"+{c.points}", c.hotspot ? $"{c.zone} ★" : c.zone, small, GameData.I.Rarity(s?.rarity ?? "common").Color);
            }
        }

        // ---- options --------------------------------------------------------------------------------------------

        void DrawOptions()
        {
            DrawOptionPages();
            if (optionsPage == 1) { DrawGraphicsOptions(); return; }
            if (optionsPage == 2) { DrawControls(); return; }
            GUILayout.Label("SETTINGS", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
            GUILayout.BeginVertical(row);
            GUILayout.Label($"Mouse sensitivity: <b>{PlayerController.Sensitivity:0.00}</b>", text);
            PlayerController.Sensitivity = GUILayout.HorizontalSlider(PlayerController.Sensitivity, 0.03f, 0.4f);
            GUILayout.Space(S(8));
            GUILayout.Label($"Volume: <b>{Mathf.RoundToInt(Sfx.Volume * 100)}%</b>", text);
            Sfx.SetVolume(GUILayout.HorizontalSlider(Sfx.Volume, 0, 1));
            GUILayout.Space(S(4));
            GUILayout.EndVertical();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Resume (Esc)", buy, GUILayout.Height(S(34)))) Open = Menu.None;
            if (GUILayout.Button("Save and quit", button, GUILayout.Height(S(34))))
            {
                Game.I.SaveNow();
                Application.Quit();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(S(10));
            DrawUpdateOptions();
            GUILayout.Label("Your progress saves automatically.", smallDim);
        }

        void DrawControls()
        {
            GUILayout.Label("CONTROLS", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
            var lines = new[]
            {
                ("Move", "WASD or the arrow keys; hold Shift to sprint; Space to hop. You can walk slowly with a line out"),
                ("Look", "Mouse"),
                ("Cast", "Hold the left mouse button to charge, release to cast where you're looking. The ghost bobber shows where it will land"),
                ("Hook", "Click when the bobber dips"),
                ("Reel", "Hold the button; let go when the fish pulls, or the line snaps"),
                ("Reel in", "Right-click or R"),
                ("Shops", "E at a counter. The Bait Shop is on South Beach; three tackle shops are out in the wilds"),
                ("Menus", "G tackle · R armour · P pets · I fish index · T goals · K trophies · H history · V events · M map · O or Esc options"),
                ("Chum", "C: put a chum bucket down (buy them at the Bait Shop); C beside it picks it up again to move it"),
                ("Online", "N: host a game or join one with a code · Enter to chat"),
                ("Pets", "E at the Travelling Zoo wagon to adopt one; it moves every 15 minutes"),
                ("Travel", "M opens the Angler's Map (free at the Bait Shop): click a place or any dry land, then Travel"),
                ("Hotspots", "Golden rings on the water: faster bites and rarer fish. Share them: fishing beside others costs nothing"),
                ("Voyages", "Every 15 minutes a boat docks at South Beach: E to board. Three stops at sea, then a boss"),
                ("Boss", "Land fish to hurt it (double in the red weak spot), cast into the golden ring when it breaches, mash E on grabs, dodge the red"),
            };
            foreach (var (k, v) in lines)
            {
                GUILayout.BeginHorizontal(row);
                GUILayout.Label($"<b>{k}</b>", text, GUILayout.Width(S(100)));
                GUILayout.Label(v, text);
                GUILayout.EndHorizontal();
            }
        }

        // ---- the Angler's Map ----------------------------------------------------------------------------------

        void DrawMap()
        {
            var map = WorldMap.I;
            var data = GameData.I;
            mapTexture ??= BuildMapTexture();
            float W = Screen.width, H = Screen.height;
            GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(6 / 255f, 12 / 255f, 20 / 255f, 0.88f), 0, 0);
            float top = S(64), pad = S(24), bottom = S(84);
            float scale = Mathf.Min((W - pad * 2) / map.Width, (H - top - bottom) / map.Height);
            float mw = map.Width * scale, mh = map.Height * scale;
            float x0 = (W - mw) / 2, y0 = top + (H - top - bottom - mh) / 2;
            Shadowed(new Rect(0, S(12), W, S(30)), "THE ANGLER'S MAP", new GUIStyle(title) { alignment = TextAnchor.MiddleCenter }, Skin.Hex("#f4e1a0"));
            Label(new Rect(0, S(40), W, S(18)), "M or Esc to close", smallDim, null, TextAnchor.MiddleCenter);
            GUI.DrawTexture(new Rect(x0 - S(6), y0 - S(6), mw + S(12), mh + S(12)), Skin.Rounded(6, Skin.Hex("#c9b48a"), Skin.Hex("#b8a06c"), new Color(0, 0, 0, 0)));
            GUI.DrawTexture(new Rect(x0, y0, mw, mh), mapTexture);
            Vector2 M(float x, float y) => new Vector2(x0 + x * scale, y0 + y * scale);
            var tag = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
            foreach (var a in data.areas)
            {
                var at = a.label != null ? new Vector2(a.label.x, a.label.y) : new Vector2(a.rect.x + a.rect.w / 2, a.rect.y + a.rect.h / 2);
                var p = M(at.x, at.y);
                Shadowed(new Rect(p.x - S(100), p.y - S(10), S(200), S(20)), a.name.ToUpper(), tag, new Color(1, 0.95f, 0.84f));
            }
            foreach (var h in Game.I.Hotspots)
            {
                var p = M(h.x, h.y);
                GUI.DrawTexture(new Rect(p.x - S(3), p.y - S(3), S(6), S(6)), Skin.Rounded(3, Skin.Hex("#ffe066"), Skin.Hex("#ffe066"), new Color(0, 0, 0, 0)));
            }
            foreach (var s in data.shops)
            {
                var p = M(s.x, s.y);
                var c = Skin.Hex(s.minimap ?? "#ffd166");
                GUI.DrawTexture(new Rect(p.x - S(6), p.y - S(6), S(12), S(12)), Skin.Rounded(3, c, c, Color.black, 1.5f));
                Shadowed(new Rect(p.x - S(120), p.y + S(8), S(240), S(20)), s.IsTackle ? s.name : "Bait Shop", tag, c);
            }
            var zooNow = Zoo.Now.spot;
            var zm = M(zooNow.x, zooNow.y);
            GUI.DrawTexture(new Rect(zm.x - S(6), zm.y - S(6), S(12), S(12)), Skin.Rounded(6, Skin.Hex("#c77dff"), Skin.Hex("#c77dff"), Color.white, 1.5f));
            Shadowed(new Rect(zm.x - S(120), zm.y + S(8), S(240), S(20)), "Travelling Zoo", tag, Skin.Hex("#e0aaff"));
            var liveEv = Game.I.Events.Active;
            if (liveEv != null)
            {
                var ep = M(liveEv.place.x, liveEv.place.y);
                float ps = S(10 + Mathf.Sin(Time.time * 4) * 2);
                Icon(new Rect(ep.x - ps, ep.y - ps, ps * 2, ps * 2), "star", Skin.Hex(liveEv.T.color));
                Shadowed(new Rect(ep.x - S(120), ep.y - S(28), S(240), S(20)), liveEv.T.name, tag, Skin.Hex(liveEv.T.color));
            }
            // Other anglers: a dot in their colour with their name; those out at sea are listed under the map.
            foreach (var (st, at) in RemotesOnLake())
            {
                var rp = M(at.x, at.y);
                var c = RemoteColour(st.col);
                var rd = new Vector2(Mathf.Sin(st.yaw * Mathf.Deg2Rad), -Mathf.Cos(st.yaw * Mathf.Deg2Rad));
                for (int i = 1; i <= 3; i++)
                {
                    var q = rp + rd * S(6 + i * 4);
                    GUI.DrawTexture(new Rect(q.x - S(1.5f), q.y - S(1.5f), S(3), S(3)), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, c, 0, 0);
                }
                GUI.DrawTexture(new Rect(rp.x - S(5), rp.y - S(5), S(10), S(10)), Skin.Rounded(5, c, c, Color.white, 1.5f));
                Shadowed(new Rect(rp.x - S(80), rp.y - S(28), S(160), S(18)), st.n, tag, c);
            }
            var atSea = new List<string>();
            if (Multiplayer.I != null)
                foreach (var r in Multiplayer.I.Remotes.Values)
                    if (r && r.State != null && Place.IsSea(new Vector3(r.State.x, r.State.y, r.State.z))) atSea.Add(r.State.n);
            if (atSea.Count > 0)
                Label(new Rect(0, y0 + mh + S(12), W, S(18)), "Out at sea: " + string.Join(", ", atSea), smallDim, null, TextAnchor.MiddleCenter);
            var me = Game.I.Player.WorldPos;
            var mp = M(me.x, me.y);
            float pulse = S(9 + Mathf.Sin(Time.time * 4) * 3);
            GUI.DrawTexture(new Rect(mp.x - pulse, mp.y - pulse, pulse * 2, pulse * 2), Skin.Rounded(16, new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), Color.white, 2));
            GUI.DrawTexture(new Rect(mp.x - S(5), mp.y - S(5), S(10), S(10)), Skin.Rounded(5, Skin.Gold, Skin.Gold, Color.white, 1.5f));
            var dir = new Vector2(Mathf.Sin(Game.I.Player.Yaw * Mathf.Deg2Rad), -Mathf.Cos(Game.I.Player.Yaw * Mathf.Deg2Rad));
            for (int i = 1; i <= 4; i++)
            {
                var q = mp + dir * S(8 + i * 4);
                GUI.DrawTexture(new Rect(q.x - S(2), q.y - S(2), S(4), S(4)), Texture2D.whiteTexture);
            }
            Shadowed(new Rect(mp.x - S(40), mp.y - S(32), S(80), S(18)), "You", tag, Skin.Gold);
            DrawTravel(x0, y0, scale, mw, mh, tag);
        }

        /// <summary>The other anglers who are on the lake (not out at sea), with where they are in world units.</summary>
        static IEnumerable<(AnglerState st, Vector2 at)> RemotesOnLake()
        {
            var mp = Multiplayer.I;
            if (mp == null || !mp.Online) yield break;
            foreach (var r in mp.Remotes.Values)
            {
                if (!r || r.State == null) continue;
                var p = r.transform.position; // smoothed, so the dots glide rather than jump
                if (Place.IsSea(p)) continue;
                yield return (r.State, WorldMap.I.ToWorld(p));
            }
        }

        Texture2D BuildMapTexture()
        {
            var map = WorldMap.I;
            int tw = 800, th = 600;
            var tex = new Texture2D(tw, th, TextureFormat.RGB24, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[tw * th];
            Color32 grass = new Color32(79, 125, 58, 255), sand = new Color32(217, 196, 143, 255), rock = new Color32(124, 127, 134, 255),
                dirt = new Color32(139, 110, 74, 255), shallow = new Color32(72, 168, 176, 255), deep = new Color32(16, 54, 92, 255);
            for (int j = 0; j < th; j++)
                for (int i = 0; i < tw; i++)
                {
                    float x = (i + 0.5f) * map.Width / tw;
                    float y = (j + 0.5f) * map.Height / th;
                    float s = map.Sdf(x, y);
                    Color32 c;
                    if (s < 0) c = Color32.Lerp(shallow, deep, Noise.SmoothStep(0, 400, -s));
                    else
                    {
                        c = map.GroundAt(x, y) switch { Ground.Sand => sand, Ground.Rock => rock, Ground.Dirt => dirt, _ => grass };
                        c = MeshKit.Shade(c, 1 - map.ForestAt(x, y) * 0.25f);
                    }
                    if (map.OnStructure(x, y)) c = new Color32(138, 90, 51, 255);
                    px[(th - 1 - j) * tw + i] = c; // textures start at the bottom
                }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
