// The Last Cast, first person. This sets everything up when any scene loads:
// the world, the angler, light and fog, hotspots, the HUD and menus, and the
// save file. No scene setup is needed beyond the template's default scene.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace LastCast
{
    public class Hotspot
    {
        public float x, y, r, life; // world units, seconds
        public GameObject view;
    }

    public class Game : MonoBehaviour
    {
        public static Game I { get; private set; }

        public Profile Profile { get; private set; }
        public PlayerController Player { get; private set; }
        public Fishing Fishing { get; private set; }
        public Hud Hud { get; private set; }
        public Chum Chum { get; private set; }
        public MapEvents Events { get; private set; }
        public PetCompanion Pet { get; private set; }
        public readonly List<Hotspot> Hotspots = new List<Hotspot>();
        public bool MenuOpen => Hud != null && Hud.MenuOpen;

        float saveTimer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (I) return;
            new GameObject("The Last Cast").AddComponent<Game>();
        }

        void Awake()
        {
            I = this;
            DontDestroyOnLoad(gameObject);
            Application.targetFrameRate = 144;
            Profile = Profile.LoadOrCreate();

            SetUpScene();
            var world = new GameObject("World").AddComponent<WorldBuilder>();
            world.Build();

            var map = WorldMap.I;
            var data = GameData.I;
            var playerGo = new GameObject("Player");
            var camGo = new GameObject("Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(playerGo.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 600;
            cam.fieldOfView = 70;
            camGo.AddComponent<AudioListener>();
            Player = playerGo.AddComponent<PlayerController>();
            Vector3 start = Profile.position is { Length: 3 } saved && map.IsWalkable(map.ToWorld(new Vector3(saved[0], 0, saved[2])).x, map.ToWorld(new Vector3(saved[0], 0, saved[2])).y)
                ? new Vector3(saved[0], 0, saved[2])
                : map.ToUnity(data.spawn.x, data.spawn.y);
            Player.Init(start, 0); // facing north, towards the lake
            Fishing = playerGo.AddComponent<Fishing>();
            Fishing.Init(Player);
            Sfx.Init(camGo);
            FishIcons.Init(cam);

            Multiplayer.Create();
            Multiplayer.I.Received += OnNetMessage;
            SeaWorld.Create();
            Voyage.Create(gameObject);
            Chum = gameObject.AddComponent<Chum>();
            Events = gameObject.AddComponent<MapEvents>();
            Pet = PetCompanion.Create();
            Hud = gameObject.AddComponent<Hud>();
            FillHotspots();
            AutoShot.StartIfAsked();
            NetTest.StartIfAsked();
            Updater.Create(gameObject); // after the test modes, which skip it
            // Goals you'd already met (e.g. from before achievements were added).
            Achievements.Check(Profile);
            Profile.Save();
            Hud.Feed("Welcome to The Last Cast! Look at the water, hold the left mouse button to charge a cast and let go to throw.", Skin.Accent);
            Hud.Feed("Better bait gets more bites and rarer fish: the Bait Shop is right here on South Beach (press E at the counter).", Skin.Gold);
        }

        /// <summary>Light, sky, fog, and the template's own camera and light out of the way.</summary>
        void SetUpScene()
        {
            foreach (var c in FindObjectsByType<Camera>()) c.gameObject.SetActive(false);
            Light sun = null;
            foreach (var l in FindObjectsByType<Light>())
                if (l.type == LightType.Directional) sun = l;
            if (!sun) sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(48, -35, 0);
            sun.color = new Color(1f, 0.95f, 0.85f);
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.72f, 0.85f);
            RenderSettings.ambientEquatorColor = new Color(0.55f, 0.6f, 0.55f);
            RenderSettings.ambientGroundColor = new Color(0.3f, 0.28f, 0.22f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 160;
            RenderSettings.fogEndDistance = 560;
            RenderSettings.fogColor = new Color(0.72f, 0.82f, 0.9f);
            QualitySettings.shadowDistance = 90;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            UpdateHotspots(dt);
            saveTimer += dt;
            if (saveTimer > 30) { saveTimer = 0; SaveNow(); }
            var kb = Keyboard.current;
            if (kb != null && !MenuOpen && !AutoShot.Active && Application.isFocused && Fishing.State == LineState.Idle)
            {
                var voyage = Voyage.I;
                if (kb.eKey.wasPressedThisFrame)
                {
                    if (voyage.AtSea) voyage.TryStrike(); // beat off whatever has grabbed the rail
                    else if (voyage.Boarded != 0 || voyage.CanBoard) voyage.ToggleBoard();
                    else
                    {
                        var shop = NearShop();
                        if (shop != null) Hud.OpenShop(shop);
                        else if (Zoo.AtWagon(Player.WorldPos)) Hud.OpenZoo();
                    }
                }
                if (kb.cKey.wasPressedThisFrame)
                {
                    if (voyage.AtSea || voyage.Boarded != 0) Flash("Save your chum for the lake.");
                    else Chum.Place();
                }
            }
        }

        void OnApplicationQuit() => SaveNow();

        void OnApplicationPause(bool paused)
        {
            if (paused) SaveNow();
        }

        public void SaveNow()
        {
            if (AutoShot.Active || NetTest.Active) return; // test runs leave the save alone
            if (Player)
            {
                // Quit at sea (or aboard the boat at the dock) and you're back on the dock next time.
                var at = Player.transform.position;
                if (Voyage.I != null && (Voyage.I.AtSea || Voyage.I.Boarded != 0))
                    at = WorldMap.I.ToUnity(GameData.I.boat.landing.x, GameData.I.boat.landing.y);
                Profile.position = new[] { at.x, 0f, at.z };
            }
            Profile.Save();
        }

        // ---- shops ----------------------------------------------------------------------------

        public Shop NearShop()
        {
            var w = Player.WorldPos;
            foreach (var s in GameData.I.shops)
                if (Vector2.Distance(w, new Vector2(s.x, s.y)) <= s.range) return s;
            return null;
        }

        // ---- hotspots (as in the web game: 30 roaming spots where rare fish bite) ---------------

        void UpdateHotspots(float dt)
        {
            for (int i = Hotspots.Count - 1; i >= 0; i--)
            {
                var h = Hotspots[i];
                h.life -= dt;
                if (h.life <= 0)
                {
                    Destroy(h.view);
                    Hotspots.RemoveAt(i);
                }
            }
            FillHotspots();
        }

        void FillHotspots()
        {
            if (Multiplayer.I != null && Multiplayer.I.IsClient) return; // the host decides where the hotspots are
            int before = Hotspots.Count;
            var cfg = GameData.I.hotspots;
            var map = WorldMap.I;
            int guard = 0;
            while (Hotspots.Count < cfg.count && guard++ < 200)
            {
                float x = Random.value * map.Width, y = Random.value * map.Height, m = cfg.radius * 0.6f;
                if (!(map.IsWater(x, y) && map.IsWater(x + m, y) && map.IsWater(x - m, y) && map.IsWater(x, y + m) && map.IsWater(x, y - m))) continue;
                var h = new Hotspot { x = x, y = y, r = cfg.radius, life = Random.Range(cfg.minLife, cfg.maxLife) * 2 };
                h.view = HotspotView.Create(map.ToUnity(x, y), cfg.radius / WorldMap.UnitsPerMetre);
                Hotspots.Add(h);
            }
            if (Hotspots.Count != before && Multiplayer.I != null && Multiplayer.I.IsHost) SendHotspots();
        }

        // ---- multiplayer ---------------------------------------------------------------------------------

        void SendHotspots(ulong? to = null)
        {
            var list = new JArray(Hotspots.Select(h => new JObject { ["x"] = h.x, ["y"] = h.y, ["r"] = h.r, ["life"] = h.life }));
            Multiplayer.I.SendReliable("hotspots", new JObject { ["list"] = list }, to);
        }

        void ClearHotspots()
        {
            foreach (var h in Hotspots) Destroy(h.view);
            Hotspots.Clear();
        }

        public void OnBecameHost() => SendHotspots();

        public void OnJoinedGame()
        {
            ClearHotspots(); // the host's hotspots (and event) arrive in a moment
            Events.OnLeftGame();
        }

        public void OnLeftGame()
        {
            Events.OnLeftGame();
            Chum.ClearRemote();
            FillHotspots();
        }

        /// <summary>The host welcomes a new player: the hotspots, and a hello in the feed.</summary>
        public void OnPlayerJoined(ulong id)
        {
            SendHotspots(id);
            Events.SendState(id);
        }

        void OnNetMessage(string type, JObject m, ulong from)
        {
            string Name() => Multiplayer.I.Remotes.TryGetValue(from, out var r) && r && r.State != null ? r.State.n : (string)m["name"] ?? "An angler";
            switch (type)
            {
                case "chat":
                    Hud.Feed($"{Name()}: {(string)m["text"]}", Color.white);
                    Sfx.Play("bite", 0.4f);
                    break;
                case "catch":
                {
                    var s = GameData.I.Fish((string)m["species"]);
                    if (s == null) break;
                    string tier = (string)m["tier"];
                    float kg = (float)m["kg"];
                    if (tier != null)
                    {
                        var t = Trophies.Tiers[tier];
                        Hud.Feed($"{(tier == "giant" ? "♛" : "★")} {Name()} landed a {t.label.ToUpper()}: \"{(string)m["trophyName"]}\", a {kg} kg {s.name}!", t.color);
                        if (tier == "giant") Banner("A GIANT IS LANDED!", $"{Name()} landed \"{(string)m["trophyName"]}\", a {kg} kg {s.name}!", t.color);
                    }
                    else Hud.Feed($"{Name()} caught a {kg} kg {s.name} (+{(int)m["points"]}) in the {(string)m["zone"]}", GameData.I.Rarity(s.rarity).Color);
                    if (s.rarity == "mythic") Banner("MYTHIC CATCH!", $"{Name()} landed {s.name}!", GameData.I.Rarity("mythic").Color);
                    Sfx.Play("catch", 0.35f);
                    break;
                }
                case "levelup":
                    Hud.Feed($"{Name()} reached level {(int)m["level"]}!", Skin.Green);
                    break;
                case "achievement":
                    Hud.Feed($"{Name()} earned the \"{(string)m["name"]}\" achievement!", Skin.Gold);
                    break;
                case "pet":
                    Hud.Feed($"{Name()} adopted a {(string)m["name"]}!", Skin.Hex("#e0aaff"));
                    break;
                case "hotspots":
                    if (!Multiplayer.I.IsClient) break;
                    ClearHotspots();
                    foreach (var h in m["list"])
                    {
                        var hs = new Hotspot { x = (float)h["x"], y = (float)h["y"], r = (float)h["r"], life = (float)h["life"] };
                        hs.view = HotspotView.Create(WorldMap.I.ToUnity(hs.x, hs.y), hs.r / WorldMap.UnitsPerMetre);
                        Hotspots.Add(hs);
                    }
                    break;
                case "chum":
                    Chum.AddRemote(from, (float)m["x"], (float)m["y"], (float)m["life"]);
                    break;
                case "chumgone":
                    Chum.RemoveRemote(from);
                    break;
                case "left":
                {
                    ulong who = m["who"].Value<ulong>();
                    if (Multiplayer.I.Remotes.TryGetValue(who, out var r))
                    {
                        Hud.Feed($"{r.State?.n ?? "An angler"} packed up and left.", Skin.Dim);
                        if (r) Destroy(r.gameObject);
                        Multiplayer.I.Remotes.Remove(who);
                    }
                    Chum.RemoveRemote(who);
                    break;
                }
                case "evcatch":
                    if (Multiplayer.I.IsHost) Events.OnRemoteCatch(from, (float)m["x"], (float)m["y"], (int)m["points"]);
                    break;
                case "event":
                    if (Multiplayer.I.IsClient) Events.ApplyState(m);
                    break;
                case "evreward":
                    if (Multiplayer.I.IsClient) Events.ApplyReward(m);
                    break;
                case "vcatch":
                case "vdmg":
                case "vstrike":
                    Voyage.I.OnNet(type, m, from);
                    break;
            }
        }

        public Hotspot HotspotAt(float x, float y)
        {
            foreach (var h in Hotspots) if (Vector2.Distance(new Vector2(h.x, h.y), new Vector2(x, y)) <= h.r) return h;
            return null;
        }

        // ---- messages (shown by the HUD) ----------------------------------------------------------

        public void Flash(string text, float seconds = 2.5f) => Hud?.Flash(text, seconds);
        public void Banner(string title, string sub, Color color) => Hud?.Banner(title, sub, color);

        public void OnCatch(CatchInfo c)
        {
            Hud.ShowCatch(c);
            Pet.Cheer();
            Voyage.I.OnCatch(c);
            var mp = Multiplayer.I;
            if (mp.Online)
            {
                mp.SessionCatches++;
                mp.SessionBest = Mathf.Max(mp.SessionBest, c.kg);
                mp.SendReliable("catch", new JObject
                {
                    ["species"] = c.species.id, ["kg"] = c.kg, ["points"] = c.points, ["zone"] = c.zone,
                    ["tier"] = c.tier, ["trophyName"] = c.trophyName,
                });
                if (c.levelUp > 0) mp.SendReliable("levelup", new JObject { ["level"] = c.levelUp });
            }
            if (c.levelUp > 0)
            {
                var sets = GameData.I.sets.FindAll(s => s.level == c.levelUp).ConvertAll(s => s.name);
                Banner($"LEVEL {c.levelUp}!", $"+{Levels.LevelUpCoins(c.levelUp)} coins" + (sets.Count > 0 ? $". New armour: {string.Join(", ", sets)}" : ". New tackle may be in the shops."), new Color(0.48f, 0.83f, 0.54f));
                Sfx.Play("level");
            }
            else if (c.tier == "giant") Banner("A GIANT IS LANDED!", $"\"{c.trophyName}\", a {c.kg} kg {c.species.name}", Trophies.Tiers["giant"].color);
            else if (c.tier == "trophy") Banner("TROPHY!", $"\"{c.trophyName}\" · {c.kg} kg {c.species.name}", Trophies.Tiers["trophy"].color);
            else if (c.species.rarity == "mythic") Banner("MYTHIC CATCH!", c.species.name, GameData.I.Rarity("mythic").Color);
        }
    }
}
