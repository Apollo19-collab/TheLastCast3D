// Out at sea: a trawler in the middle of open water (the web game's sea world,
// shared/voyage.js makeSeaWorld), far off to the west of the lake where it
// can't be seen from it. Walk the deck, cast over the rail. The water takes
// each location's colours, the sky follows the time of day at each stop, foam
// streams past while the boat sails, and in the boss fight the boss looms off
// the side: its weak spot glows red, it breaches inside a golden ring, its
// tentacles (or jaws, or coils) grab the rail and its slams light the deck up
// red first.
//
// Sea coordinates are the web game's sea world units (x east, y south, 10 to a
// metre; the trawler's centre at 850, 600).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LastCast
{
    public class SeaWorld : MonoBehaviour
    {
        public static SeaWorld I { get; private set; }
        /// <summary>Unity position of the trawler's centre, at the water line.</summary>
        public static readonly Vector3 Origin = new Vector3(-3000, 0, 480);
        public const float DeckY = 1.6f; // metres above the water

        static VoyageData V => GameData.I.voyage;

        // ---- coordinates and the deck ------------------------------------------------------------------

        public static Vector3 ToUnity(float x, float y, float height = 0) =>
            Origin + new Vector3((x - V.seaBoat.x) / WorldMap.UnitsPerMetre, height, -(y - V.seaBoat.y) / WorldMap.UnitsPerMetre);

        public static Vector2 ToSea(Vector3 p) =>
            new Vector2(V.seaBoat.x + (p.x - Origin.x) * WorldMap.UnitsPerMetre, V.seaBoat.y - (p.z - Origin.z) * WorldMap.UnitsPerMetre);

        // The fish crates stacked in the deck's back corners, by the cabin.
        static readonly RectU[] Crates = { new RectU { x = 680, y = 548, w = 16, h = 16 }, new RectU { x = 680, y = 636, w = 16, h = 16 } };

        public static bool OnDeck(float x, float y)
        {
            if (V.cabin.Contains(x, y)) return false;
            foreach (var c in Crates) if (c.Contains(x, y)) return false;
            foreach (var r in V.deck) if (r.Contains(x, y)) return true;
            return false;
        }

        public static bool IsWalkable(Vector3 p)
        {
            var s = ToSea(p);
            return OnDeck(s.x, s.y);
        }

        /// <summary>Over the trawler's hull (plus a margin), where a cast can't land.</summary>
        public static bool OnBoat(float x, float y, float margin = 0)
        {
            var b = V.seaBoat;
            return Mathf.Abs(x - (b.x + 15)) <= b.length / 2 + 15 + margin && Mathf.Abs(y - b.y) <= b.beam / 2 + 6 + margin;
        }

        public static bool IsWater(float x, float y) => x >= 0 && y >= 0 && x < V.width && y < V.height && !OnBoat(x, y);

        // ---- building ----------------------------------------------------------------------------------------

        GameObject root, boss, hump, slam, light_;
        Transform trawler;
        BossActs acts;
        readonly List<(float start, float roll, float pitch, float heave, float seconds, float freq)> rocks = new List<(float, float, float, float, float, float)>();
        readonly Dictionary<int, float> grabBorn = new Dictionary<int, float>();
        readonly List<(GameObject view, float since)> grabsGoing = new List<(GameObject, float)>();

        /// <summary>How the boat is tilted (rolled about its length, pitched about its beam) and lifted this frame, round
        /// its middle at the water line: the swell, and the bosses' blows. The deck, your view and the other anglers on
        /// it all move with it (Tilted).</summary>
        public static Quaternion Tilt { get; private set; } = Quaternion.identity;
        public static float Heave { get; private set; }
        public static Vector3 Tilted(Vector3 p) => Origin + Vector3.up * Heave + Tilt * (p - Origin);

        /// <summary>The Ghost Whale's mist (0 clear, 1 thick).</summary>
        public float Mist;
        public Vector3 BossPosition => boss ? boss.transform.position : Origin;
        LimbRig bossRig;
        Mesh ocean;
        Color32[] oceanColours;
        float[] oceanMix;
        Color deepNow, shallowNow, deepGoal, shallowGoal;
        Material seaMaterial, skybox, lakeSkybox, markMaterial;
        Sky skyNow, skyGoal, lakeSky;
        float shake;
        string bossShown;
        readonly List<Transform> foam = new List<Transform>();
        readonly Dictionary<SeaSpot, GameObject> spotViews = new Dictionary<SeaSpot, GameObject>();
        readonly Dictionary<int, GameObject> grabViews = new Dictionary<int, GameObject>();
        SpotRing weakRing, breachRing;
        MaterialPropertyBlock mpb;

        public static SeaWorld Create()
        {
            var go = new GameObject("Sea");
            I = go.AddComponent<SeaWorld>();
            I.Build();
            return I;
        }

        void Build()
        {
            mpb = new MaterialPropertyBlock();
            root = new GameObject("Sea world");
            root.transform.SetParent(transform, false);

            seaMaterial = new Material(WorldBuilder.WaterMaterial);
            seaMaterial.SetFloat("_WaveHeight", 0.22f);
            seaMaterial.SetFloat("_WaveScale", 0.16f);
            // The ocean is one big see-through mesh: draw it before the other see-through things
            // (hotspot rings, the boss's rings, foam, bobbers, lines), or it would cover them.
            seaMaterial.renderQueue = 2980;
            BuildOcean();
            acts = BossActs.Create(this, root.transform);

            var trawler = new GameObject("Trawler");
            trawler.transform.SetParent(root.transform, false);
            trawler.transform.position = Origin;
            trawler.transform.rotation = Quaternion.Euler(0, 90, 0); // built bow towards +Z; sails east (+X)
            trawler.AddComponent<MeshFilter>().sharedMesh = Models.Or("trawler", 0, TrawlerMesh);
            trawler.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            this.trawler = trawler.transform;

            // A deck light for the evening stops.
            light_ = new GameObject("Deck light");
            light_.transform.SetParent(root.transform, false);
            light_.transform.position = Origin + new Vector3(-14, DeckY + 6.5f, 0);
            var l = light_.AddComponent<Light>();
            l.type = LightType.Point;
            l.range = 32;
            l.intensity = 3;
            l.color = new Color(1f, 0.85f, 0.6f);
            l.shadows = LightShadows.None;

            // Foam streaming past while the boat is under way.
            var foamMesh = FlatQuad(new Color32(240, 250, 255, 110));
            markMaterial = new Material(RodView.Unlit);
            var rnd = new SeededRandom(77);
            for (int i = 0; i < 46; i++)
            {
                var f = new GameObject("Foam");
                f.transform.SetParent(root.transform, false);
                f.AddComponent<MeshFilter>().sharedMesh = foamMesh;
                var r = f.AddComponent<MeshRenderer>();
                r.sharedMaterial = markMaterial;
                r.shadowCastingMode = ShadowCastingMode.Off;
                float side = i % 2 == 0 ? 1 : -1;
                f.transform.position = Origin + new Vector3(rnd.Range(-60, 60), 0.3f, side * rnd.Range(6.5f, 30f));
                f.transform.localScale = new Vector3(rnd.Range(2f, 6f), 1, rnd.Range(0.15f, 0.4f));
                foam.Add(f.transform);
            }

            slam = new GameObject("Slam warning");
            slam.transform.SetParent(root.transform, false);
            slam.AddComponent<MeshFilter>().sharedMesh = FlatQuad(new Color32(255, 60, 60, 255), centred: true);
            var sr = slam.AddComponent<MeshRenderer>();
            sr.sharedMaterial = markMaterial;
            sr.shadowCastingMode = ShadowCastingMode.Off;
            slam.SetActive(false);

            weakRing = SpotRing.Create(root.transform, new Color(1f, 0.25f, 0.2f));
            breachRing = SpotRing.Create(root.transform, new Color(1f, 0.82f, 0.3f));
            root.SetActive(false);
        }

        void BuildOcean()
        {
            const float Size = 1000, Cell = 8;
            int n = Mathf.RoundToInt(Size / Cell);
            var verts = new Vector3[(n + 1) * (n + 1)];
            oceanColours = new Color32[verts.Length];
            oceanMix = new float[verts.Length];
            var uv = new Vector2[verts.Length];
            var tris = new int[n * n * 6];
            for (int j = 0, k = 0; j <= n; j++)
                for (int i = 0; i <= n; i++, k++)
                {
                    verts[k] = new Vector3(-Size / 2 + i * Cell, 0, -Size / 2 + j * Cell);
                    oceanMix[k] = Noise.Fbm(i * 0.11f, j * 0.11f, 3);
                    uv[k] = Vector2.zero; // no shore anywhere out here
                }
            for (int j = 0, t = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i;
                    tris[t++] = a; tris[t++] = a + n + 1; tris[t++] = a + n + 2;
                    tris[t++] = a; tris[t++] = a + n + 2; tris[t++] = a + 1;
                }
            ocean = new Mesh { name = "ocean", indexFormat = IndexFormat.UInt32 };
            ocean.vertices = verts;
            ocean.uv = uv;
            ocean.triangles = tris;
            ocean.RecalculateNormals();
            ocean.bounds = new Bounds(Vector3.zero, new Vector3(Size, 20, Size));
            var go = new GameObject("Ocean");
            go.transform.SetParent(root.transform, false);
            go.transform.position = Origin;
            go.AddComponent<MeshFilter>().sharedMesh = ocean;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = seaMaterial;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            deepNow = deepGoal = Skin.Hex("#123a66");
            shallowNow = shallowGoal = Skin.Hex("#25628f");
            PaintOcean();
        }

        void PaintOcean()
        {
            for (int k = 0; k < oceanColours.Length; k++)
            {
                Color c = Color.Lerp(deepNow, shallowNow, Mathf.Clamp01(oceanMix[k] * 1.6f - 0.3f));
                c.a = 0.94f;
                oceanColours[k] = c;
            }
            ocean.colors32 = oceanColours;
        }

        static Mesh FlatQuad(Color32 c, bool centred = false)
        {
            var k = new MeshKit();
            float x0 = centred ? -0.5f : 0, x1 = centred ? 0.5f : 1;
            k.Quad(new Vector3(x0, 0, -0.5f), new Vector3(x0, 0, 0.5f), new Vector3(x1, 0, 0.5f), new Vector3(x1, 0, -0.5f), c);
            return k.Build("flat quad");
        }

        // ---- the trawler ---------------------------------------------------------------------------------------

        /// <summary>A fishing trawler, bow towards +Z: navy hull, wooden deck, white rails, the cabin and
        /// wheelhouse at the stern, a mast, crates and life rings. It matches the sea world's deck and cabin.</summary>
        static Mesh TrawlerMesh()
        {
            Color32 navy = MeshKit.Hex("#1d3557"), red = MeshKit.Hex("#9d0208"), keel = MeshKit.Hex("#3a0a0a"), deck = MeshKit.Hex("#a07850"),
                rail = MeshKit.Hex("#f1faee"), cabin = MeshKit.Hex("#f1ece0"), roof = MeshKit.Hex("#e63946"), glass = MeshKit.Hex("#2b3a44"),
                wood = MeshKit.Hex("#5b3c22"), gold = MeshKit.Hex("#ffd166");
            var k = new MeshKit();
            float B = 5.6f, D = DeckY, K = -1.8f, stern = -28.2f;
            Vector3[] outline =
            {
                new Vector3(-B, D, stern), new Vector3(B, D, stern), new Vector3(B, D, 16), new Vector3(4.6f, D, 25.5f),
                new Vector3(2.2f, D, 29.2f), new Vector3(0, D + 0.3f, 30.8f), new Vector3(-2.2f, D, 29.2f), new Vector3(-4.6f, D, 25.5f), new Vector3(-B, D, 16),
            };
            var centre = new Vector3(0, D, 0);
            float stripe = (D - 0.2f) / (D - K);
            for (int i = 0; i < outline.Length; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Length];
                var ka = new Vector3(a.x * 0.5f, K, a.z * 0.93f);
                var kb = new Vector3(b.x * 0.5f, K, b.z * 0.93f);
                var ma = Vector3.Lerp(a, ka, stripe);
                var mb = Vector3.Lerp(b, kb, stripe);
                k.Quad(a, b, mb, ma, i % 2 == 0 ? navy : MeshKit.Shade(navy, 0.92f));
                k.Quad(ma, mb, kb, ka, red);
                k.Tri(new Vector3(0, K, 0), ka, kb, keel);
                k.Tri(centre, b, a, deck);
                // Bulwark rail with posts.
                var dir = (b - a).normalized;
                float len = Vector3.Distance(a, b);
                var rot = Quaternion.LookRotation(dir);
                RotatedBox(k, (a + b) / 2 + Vector3.up * 1.0f, new Vector3(0.14f, 0.12f, len), rot, rail);
                for (float t = 0; t < len; t += 1.8f) RotatedBox(k, a + dir * t + Vector3.up * 0.5f, new Vector3(0.1f, 1f, 0.1f), rot, rail);
            }
            // Planks across the deck.
            for (float z = -16.6f; z < 23; z += 1.4f) k.Box(new Vector3(0, D + 0.005f, z), new Vector3(B * 1.7f, 0.01f, 0.06f), MeshKit.Shade(deck, 0.8f));
            // Cabin at the stern (the sea world's solid block), wheelhouse on top.
            k.Box(new Vector3(0, D + 1.6f, -22.6f), new Vector3(11f, 3.2f, 11f), cabin);
            k.Box(new Vector3(0, D + 3.3f, -22.6f), new Vector3(11.6f, 0.25f, 11.6f), roof);
            k.Box(new Vector3(0, D + 4.3f, -24f), new Vector3(6f, 1.9f, 5.5f), cabin);
            k.Box(new Vector3(0, D + 5.35f, -24f), new Vector3(6.6f, 0.22f, 6.1f), roof);
            for (int w = -1; w <= 1; w++)
            {
                k.Box(new Vector3(w * 3.2f, D + 2f, -17.08f), new Vector3(2f, 0.9f, 0.05f), glass);
                k.Box(new Vector3(w * 1.9f, D + 4.5f, -21.23f), new Vector3(1.4f, 0.8f, 0.05f), glass);
            }
            k.Box(new Vector3(0, D + 1.05f, -17.06f), new Vector3(1.1f, 2.1f, 0.05f), wood); // door
            // Life rings on the cabin front.
            foreach (float x in new[] { -4.2f, 4.2f }) Ring(k, new Vector3(x, D + 1.8f, -17.04f), 0.45f, 0.12f, roof, rail);
            // Mast on the cabin roof (out of the way of the deck), with a crosstree, a lamp and a boom.
            k.Box(new Vector3(0, D + 7.3f, -19.4f), new Vector3(0.28f, 8f, 0.28f), wood);
            k.Box(new Vector3(0, D + 9.6f, -19.4f), new Vector3(4f, 0.18f, 0.18f), wood);
            k.Box(new Vector3(0, D + 11.4f, -19.4f), new Vector3(0.35f, 0.35f, 0.35f), gold);
            RotatedBox(k, new Vector3(0, D + 6.2f, -16.4f), new Vector3(0.18f, 0.18f, 6.5f), Quaternion.Euler(-25, 0, 0), wood);
            // Fish crates stacked by the cabin.
            Color32[] crates = { MeshKit.Hex("#2a9d8f"), MeshKit.Hex("#f4a261"), MeshKit.Hex("#457b9d") };
            int c = 0;
            foreach (float x in new[] { -4.4f, 4.4f })
                for (int h = 0; h < 2; h++)
                    k.Box(new Vector3(x, D + 0.3f + h * 0.6f, -16.2f), new Vector3(0.9f, 0.55f, 0.7f), crates[c++ % crates.Length]);
            // Name board on the bow.
            foreach (float s in new[] { -1f, 1f }) k.Box(new Vector3(s * 4.05f, D - 0.45f, 23f), new Vector3(0.05f, 0.4f, 2.8f), rail);
            return k.Build("trawler");
        }

        static void RotatedBox(MeshKit k, Vector3 centre, Vector3 size, Quaternion rot, Color32 c)
        {
            var h = size / 2;
            Vector3 P(float x, float y, float z) => centre + rot * new Vector3(x * h.x, y * h.y, z * h.z);
            k.Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), c);
            k.Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), MeshKit.Shade(c, 0.9f));
            k.Quad(P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1), MeshKit.Shade(c, 0.9f));
            k.Quad(P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1), MeshKit.Shade(c, 0.8f));
            k.Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), MeshKit.Shade(c, 0.8f));
            k.Quad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), MeshKit.Shade(c, 0.6f));
        }

        /// <summary>A ring standing upright, facing -Z (a life ring), in two alternating colours.</summary>
        static void Ring(MeshKit k, Vector3 c, float r, float w, Color32 a, Color32 b)
        {
            int n = 12;
            for (int i = 0; i < n; i++)
            {
                float a0 = i / (float)n * Mathf.PI * 2, a1 = (i + 1) / (float)n * Mathf.PI * 2;
                var d0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0);
                var d1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0);
                var col = (i / 3) % 2 == 0 ? a : b;
                k.Quad(c + d0 * (r + w), c + d1 * (r + w), c + d1 * (r - w), c + d0 * (r - w), col);
            }
        }

        // ---- going to sea and coming home -----------------------------------------------------------------

        struct Sky
        {
            public Vector3 sun;
            public Color sunColour, skyAmb, eqAmb, groundAmb, fog, water;
            public float intensity, fogStart, fogEnd, exposure;

            public static Sky Lerp(Sky a, Sky b, float t) => new Sky
            {
                sun = Vector3.Lerp(a.sun, b.sun, t), sunColour = Color.Lerp(a.sunColour, b.sunColour, t), intensity = Mathf.Lerp(a.intensity, b.intensity, t),
                skyAmb = Color.Lerp(a.skyAmb, b.skyAmb, t), eqAmb = Color.Lerp(a.eqAmb, b.eqAmb, t), groundAmb = Color.Lerp(a.groundAmb, b.groundAmb, t),
                fog = Color.Lerp(a.fog, b.fog, t), water = Color.Lerp(a.water, b.water, t), fogStart = Mathf.Lerp(a.fogStart, b.fogStart, t),
                fogEnd = Mathf.Lerp(a.fogEnd, b.fogEnd, t), exposure = Mathf.Lerp(a.exposure, b.exposure, t),
            };
        }

        static Sky TimeOfDay(string time) => time switch
        {
            "Morning" => new Sky
            {
                sun = new Vector3(20, 70, 0), sunColour = new Color(1f, 0.87f, 0.72f), intensity = 1.05f,
                skyAmb = new Color(0.62f, 0.68f, 0.8f), eqAmb = new Color(0.62f, 0.6f, 0.58f), groundAmb = new Color(0.3f, 0.28f, 0.25f),
                fog = new Color(0.78f, 0.82f, 0.86f), water = new Color(0.66f, 0.76f, 0.86f), fogStart = 90, fogEnd = 480, exposure = 1.1f,
            },
            "Sunset" => new Sky
            {
                sun = new Vector3(12, -100, 0), sunColour = new Color(1f, 0.72f, 0.5f), intensity = 1.05f,
                skyAmb = new Color(0.5f, 0.48f, 0.6f), eqAmb = new Color(0.6f, 0.5f, 0.5f), groundAmb = new Color(0.22f, 0.18f, 0.2f),
                fog = new Color(0.78f, 0.6f, 0.6f), water = new Color(0.58f, 0.52f, 0.64f), fogStart = 90, fogEnd = 460, exposure = 0.85f,
            },
            "Night" => new Sky
            {
                sun = new Vector3(38, 150, 0), sunColour = new Color(0.55f, 0.65f, 0.95f), intensity = 0.32f,
                skyAmb = new Color(0.13f, 0.17f, 0.3f), eqAmb = new Color(0.1f, 0.13f, 0.22f), groundAmb = new Color(0.04f, 0.05f, 0.08f),
                fog = new Color(0.05f, 0.08f, 0.15f), water = new Color(0.2f, 0.28f, 0.42f), fogStart = 30, fogEnd = 260, exposure = 0.12f,
            },
            _ => new Sky // Afternoon
            {
                sun = new Vector3(52, -35, 0), sunColour = new Color(1f, 0.96f, 0.88f), intensity = 1.25f,
                skyAmb = new Color(0.62f, 0.72f, 0.85f), eqAmb = new Color(0.55f, 0.6f, 0.6f), groundAmb = new Color(0.3f, 0.28f, 0.24f),
                fog = new Color(0.72f, 0.82f, 0.9f), water = new Color(0.62f, 0.8f, 0.92f), fogStart = 100, fogEnd = 500, exposure = 1.25f,
            },
        };

        static Sky Capture()
        {
            var sun = RenderSettings.sun;
            var sky = RenderSettings.skybox;
            return new Sky
            {
                sun = sun ? sun.transform.eulerAngles : new Vector3(48, -35, 0), sunColour = sun ? sun.color : Color.white, intensity = sun ? sun.intensity : 1,
                skyAmb = RenderSettings.ambientSkyColor, eqAmb = RenderSettings.ambientEquatorColor, groundAmb = RenderSettings.ambientGroundColor,
                fog = RenderSettings.fogColor, water = WorldBuilder.WaterMaterial.GetColor("_SkyColor"),
                fogStart = GraphicsOptions.FogStart, fogEnd = GraphicsOptions.FogEnd,
                exposure = sky && sky.HasProperty("_Exposure") ? sky.GetFloat("_Exposure") : 1,
            };
        }

        void Apply(Sky s)
        {
            var sun = RenderSettings.sun;
            if (sun)
            {
                sun.transform.rotation = Quaternion.Euler(s.sun);
                sun.color = s.sunColour;
                sun.intensity = s.intensity;
            }
            RenderSettings.ambientSkyColor = s.skyAmb;
            RenderSettings.ambientEquatorColor = s.eqAmb;
            RenderSettings.ambientGroundColor = s.groundAmb;
            // The Ghost Whale's mist closes the fog in and pales it.
            RenderSettings.fogColor = Color.Lerp(s.fog, new Color(0.82f, 0.88f, 0.92f), Mist * 0.85f);
            GraphicsOptions.SetFog(s.fogStart * Mathf.Lerp(1, 0.02f, Mist), s.fogEnd * Mathf.Lerp(1, 0.15f, Mist));
            seaMaterial.SetColor("_SkyColor", s.water);
            if (skybox && skybox.HasProperty("_Exposure")) skybox.SetFloat("_Exposure", s.exposure);
            var cam = Camera.main;
            if (cam) cam.backgroundColor = s.fog;
            if (light_) light_.GetComponent<Light>().intensity = Mathf.Clamp01(1.1f - s.intensity) * 4;
        }

        /// <summary>The boat has sailed out of sight of the lake: show the sea.</summary>
        public void Enter(string time)
        {
            lakeSky = Capture();
            lakeSkybox = RenderSettings.skybox;
            if (lakeSkybox && !skybox) skybox = new Material(lakeSkybox);
            if (skybox) RenderSettings.skybox = skybox;
            skyNow = skyGoal = TimeOfDay(time);
            Apply(skyNow);
            deepNow = deepGoal = Skin.Hex("#123a66");
            shallowNow = shallowGoal = Skin.Hex("#25628f");
            PaintOcean();
            root.SetActive(true);
        }

        /// <summary>Arriving at a stop: its water colours and time of day (eased in).</summary>
        public void SetStop(SeaLocation loc, string time)
        {
            deepGoal = Skin.Hex(loc.water[0]);
            shallowGoal = Skin.Hex(loc.water[1]);
            skyGoal = TimeOfDay(time);
        }

        /// <summary>Home again: the lake's light and sky back.</summary>
        public void Leave()
        {
            root.SetActive(false);
            Apply(lakeSky);
            RenderSettings.skybox = lakeSkybox;
            if (boss) Destroy(boss);
            if (hump) Destroy(hump);
            bossShown = null;
            foreach (var v in spotViews.Values) if (v) Destroy(v);
            spotViews.Clear();
            foreach (var v in grabViews.Values) if (v) Destroy(v);
            grabViews.Clear();
            shake = 0;
            acts.Clear();
            foreach (var (view, _) in grabsGoing) if (view) Destroy(view);
            grabsGoing.Clear();
            grabBorn.Clear();
            rocks.Clear();
            Mist = 0;
        }

        public void Shake(float amount) => shake = Mathf.Max(shake, amount);

        /// <summary>Rock the boat: roll and pitch (degrees) and a lift (metres) that swing at freq times a second and die away over seconds.</summary>
        public void Rock(float roll, float pitch, float heave, float seconds, float freq) => rocks.Add((Time.time, roll, pitch, heave, seconds, freq));

        void UpdateTilt()
        {
            float t = Time.time;
            // The swell, always; then whatever has hit the boat.
            float roll = Mathf.Sin(t * 0.55f) * 0.7f, pitch = Mathf.Sin(t * 0.37f + 1) * 0.4f, heave = Mathf.Sin(t * 0.7f) * 0.08f;
            for (int i = rocks.Count - 1; i >= 0; i--)
            {
                var r = rocks[i];
                float u = t - r.start;
                if (u > r.seconds) { rocks.RemoveAt(i); continue; }
                float env = Mathf.Exp(-3.2f * u / r.seconds) * Mathf.Clamp01(u * 6), w = u * r.freq * Mathf.PI * 2;
                roll += r.roll * env * Mathf.Sin(w + Mathf.PI / 2);
                pitch += r.pitch * env * Mathf.Sin(w + 1.9f);
                heave += r.heave * env * Mathf.Sin(w * 1.3f);
            }
            Tilt = Quaternion.AngleAxis(roll, Vector3.right) * Quaternion.AngleAxis(pitch, Vector3.forward);
            Heave = heave;
            if (trawler) trawler.SetPositionAndRotation(Origin + Vector3.up * heave, Tilt * Quaternion.Euler(0, 90, 0));
        }

        // ---- every frame -------------------------------------------------------------------------------------

        void Update()
        {
            var v = Voyage.I;
            if (v == null || !v.AtSea || !root.activeSelf)
            {
                Tilt = Quaternion.identity;
                Heave = 0;
                return;
            }
            float dt = Time.deltaTime;
            UpdateTilt();

            // Ease the water and the sky towards this stop's.
            float k = 1 - Mathf.Exp(-0.8f * dt);
            if (!Approximately(deepNow, deepGoal) || !Approximately(shallowNow, shallowGoal))
            {
                deepNow = Color.Lerp(deepNow, deepGoal, k);
                shallowNow = Color.Lerp(shallowNow, shallowGoal, k);
                PaintOcean();
            }
            skyNow = Sky.Lerp(skyNow, skyGoal, k);
            Apply(skyNow);
            // From the deck you mostly see the water at a low angle, where it reflects the sky: tint that with
            // the stop's water so each location keeps its colour (and a special event tints it a little more).
            var reflection = Color.Lerp(skyNow.water, Color.Lerp(shallowNow, deepNow, 0.3f), 0.55f);
            if (v.Phase == VoyagePhase.Fishing && v.Event != null)
                reflection = Color.Lerp(reflection, Skin.Hex(v.Event.color), 0.18f + 0.07f * Mathf.Sin(Time.time * 2));
            seaMaterial.SetColor("_SkyColor", reflection);

            // Under way: foam streams past.
            bool sailing = v.Phase == VoyagePhase.Outbound || v.Phase == VoyagePhase.Sailing || v.Phase == VoyagePhase.Results;
            foreach (var f in foam)
            {
                f.gameObject.SetActive(sailing);
                if (!sailing) continue;
                var p = f.position;
                p.x -= 9 * dt;
                if (p.x < Origin.x - 60) p.x += 120;
                f.position = p;
            }

            SyncSpots(v);
            SyncBoss(v, dt);

            if (shake > 0)
            {
                PlayerController.Shake = Mathf.Max(PlayerController.Shake, shake);
                shake = 0;
            }
        }

        static bool Approximately(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.004f;

        void SyncSpots(Voyage v)
        {
            // Ordinary hotspots: the same golden rings as on the lake.
            var live = new HashSet<SeaSpot>();
            foreach (var s in v.Hotspots)
            {
                if (s.weak) continue;
                live.Add(s);
                if (!spotViews.ContainsKey(s))
                {
                    var view = HotspotView.Create(ToUnity(s.x, s.y), s.r / WorldMap.UnitsPerMetre);
                    view.transform.SetParent(root.transform, true);
                    spotViews[s] = view;
                }
            }
            foreach (var s in new List<SeaSpot>(spotViews.Keys))
                if (!live.Contains(s))
                {
                    Destroy(spotViews[s]);
                    spotViews.Remove(s);
                }

            var b = v.Boss;
            bool fighting = v.Phase == VoyagePhase.Boss && b != null && b.result == null;
            weakRing.Show(fighting && b.weak != null, fighting && b.weak != null ? ToUnity(b.weak.x, b.weak.y, 0.1f) : Vector3.zero, fighting && b.weak != null ? b.weak.r / WorldMap.UnitsPerMetre : 1);
            breachRing.Show(fighting && b.breach != null, fighting && b.breach != null ? ToUnity(b.breach.x, b.breach.y, 0.12f) : Vector3.zero, fighting && b.breach != null ? b.breach.r / WorldMap.UnitsPerMetre : 1);

            // A slam about to land: the area of the deck glows red, faster as it comes.
            if (fighting && b.attackArea != null)
            {
                var area = V.DeckArea(b.attackArea);
                slam.SetActive(true);
                var c = ToUnity(area.x + area.w / 2, area.y + area.h / 2, DeckY + 0.04f);
                slam.transform.position = c;
                slam.transform.localScale = new Vector3(area.w / WorldMap.UnitsPerMetre, 1, area.h / WorldMap.UnitsPerMetre);
                float left = Mathf.Max(0.1f, b.attackAt - b.clock);
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * (6 + 14 / left));
                mpb.SetColor("_Color", new Color(1, 1, 1, 0.25f + 0.35f * pulse));
                slam.GetComponent<MeshRenderer>().SetPropertyBlock(mpb);
            }
            else slam.SetActive(false);

            // Things that have grabbed the rail.
            var liveGrabs = new HashSet<int>();
            if (fighting)
                foreach (var g in b.grabs)
                {
                    liveGrabs.Add(g.id);
                    if (!grabViews.TryGetValue(g.id, out var view))
                    {
                        view = GrabView(b.info, g);
                        grabViews[g.id] = view;
                        grabBorn[g.id] = Time.time;
                    }
                    // It rises out of the water beside the hull and hooks on, and moves with the deck.
                    var at = ToUnity(g.x, g.y);
                    view.transform.SetPositionAndRotation(Tilted(new Vector3(at.x, 0, at.z)), Tilt);
                    float risen = Mathf.SmoothStep(0, 1, Mathf.Clamp01((Time.time - (grabBorn.TryGetValue(g.id, out var born) ? born : 0)) / 0.9f));
                    view.transform.GetChild(0).localPosition = Vector3.down * (1 - risen) * 9;
                    var tm = view.GetComponentInChildren<TextMesh>();
                    tm.text = $"E  ×{g.hp}\n<size=36>{Mathf.CeilToInt(g.until - b.clock)} s</size>";
                    var cam = Camera.main;
                    if (cam) tm.transform.rotation = Quaternion.LookRotation(tm.transform.position - cam.transform.position);
                    // It squeezes and writhes, more as it's beaten.
                    float struggle = 1 - g.hp / (float)Mathf.Max(1, g.max);
                    var rig = view.GetComponentInChildren<LimbRig>();
                    if (rig) BossMotion.Grabber(rig, b.info.id, Time.time, struggle, g.id * 1.3f);
                    else
                    {
                        float w = Mathf.Sin(Time.time * (3 + 6 * struggle)) * 6;
                        view.transform.GetChild(0).localRotation = Quaternion.Euler(w, 0, w * 0.5f);
                    }
                }
            foreach (var id in new List<int>(grabViews.Keys))
                if (!liveGrabs.Contains(id))
                {
                    grabsGoing.Add((grabViews[id], Time.time)); // beaten off (or let go): it slides back into the sea
                    grabViews.Remove(id);
                    grabBorn.Remove(id);
                }
            for (int i = grabsGoing.Count - 1; i >= 0; i--)
            {
                var (view, since) = grabsGoing[i];
                float u = (Time.time - since) / 0.9f;
                if (!view || u >= 1)
                {
                    if (view) Destroy(view);
                    grabsGoing.RemoveAt(i);
                    continue;
                }
                var arm = view.transform.GetChild(0);
                arm.localPosition = Vector3.down * u * u * 10;
                arm.localRotation = Quaternion.Euler(Mathf.Sin(u * 20) * 6 * (1 - u), 0, 0);
                var tag = view.GetComponentInChildren<TextMesh>();
                if (tag) tag.gameObject.SetActive(false);
            }
        }

        GameObject GrabView(BossInfo info, BossGrab g)
        {
            var go = new GameObject($"Grab {g.id}");
            go.transform.SetParent(root.transform, false);
            var p = ToUnity(g.x, g.y);
            go.transform.position = new Vector3(p.x, 0, p.z);
            float side = Mathf.Sign(p.z - Origin.z); // which rail: out from the middle of the boat
            var arm = new GameObject("Arm");
            arm.transform.SetParent(go.transform, false);
            if (!LimbRig.Attach(arm, BossMeshes.Grabber(info, side), WorldBuilder.LitMaterial, 12))
            {
                arm.AddComponent<MeshFilter>().sharedMesh = BossMeshes.Grabber(info, side);
                arm.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            }
            var tag = new GameObject("Hits");
            tag.transform.SetParent(go.transform, false);
            tag.transform.localPosition = new Vector3(0, DeckY + 3.4f, side * -0.6f);
            var tm = tag.AddComponent<TextMesh>();
            tm.font = ShopStall.Font;
            tm.fontSize = 56;
            tm.characterSize = 0.05f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.richText = true;
            tm.color = new Color(1f, 0.85f, 0.4f);
            tag.GetComponent<MeshRenderer>().sharedMaterial = ShopStall.Font.material;
            return go;
        }

        void SyncBoss(Voyage v, float dt)
        {
            bool show = v.Phase == VoyagePhase.BossIntro || v.Phase == VoyagePhase.Boss || (v.Phase == VoyagePhase.Results && v.Boss != null);
            if (!show)
            {
                if (boss) Destroy(boss);
                if (hump) Destroy(hump);
                bossShown = null;
                return;
            }
            var info = v.BossInfo;
            if (bossShown != info.id || !boss)
            {
                if (boss) Destroy(boss);
                if (hump) Destroy(hump);
                bossShown = info.id;
                boss = new GameObject(info.name);
                boss.transform.SetParent(root.transform, false);
                // Rigged so its tentacles, tail, jaw, coils or flippers move (a static mesh if the model has no limbs).
                bossRig = LimbRig.Attach(boss, BossMeshes.Body(info), WorldBuilder.LitMaterial, info.id == "serpent" ? 14 : 10, BossMotion.Joints(info.id));
                if (bossRig == null)
                {
                    boss.AddComponent<MeshFilter>().sharedMesh = BossMeshes.Body(info);
                    boss.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
                }
                boss.transform.position = Origin + new Vector3(0, -16, -70);
                hump = new GameObject("Breach");
                hump.transform.SetParent(root.transform, false);
                hump.AddComponent<MeshFilter>().sharedMesh = BossMeshes.Hump(info);
                hump.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
                hump.SetActive(false);
            }

            // Where it lurks: out beyond its weak spot, facing the boat. It rises in the intro and
            // sinks away (or slinks off) at the end.
            var b = v.Boss;
            Vector3 lurk;
            float y;
            if (b?.weak != null)
            {
                var w = ToUnity(b.weak.x, b.weak.y);
                var dir = new Vector3(w.x - Origin.x, 0, w.z - Origin.z).normalized;
                lurk = Origin + dir * 62 + new Vector3(0, 0, 0);
            }
            else lurk = Origin + new Vector3(0, 0, -62);
            if (v.Phase == VoyagePhase.BossIntro) y = Mathf.Lerp(0, -16, Mathf.Clamp01(v.PhaseLeft / Mathf.Max(1, v.T.bossIntro)));
            else if (v.Phase == VoyagePhase.Results) y = boss.transform.position.y - dt * (b?.result == "won" ? 2.5f : 1.2f);
            else y = Mathf.Sin(Time.time * 0.6f) * 0.6f;
            // Its limbs: wilder as it's hurt, rearing up as a slam comes in.
            float agitation = b != null ? 1 - b.Fraction : 0;
            float rear = b != null && b.result == null && b.attackArea != null ? 1 - Mathf.Clamp01((b.attackAt - b.clock) / 3f) : 0;
            var pos = boss.transform.position;
            if (v.Phase == VoyagePhase.Boss && acts.Body(b, pos, out var actPos, out var actRot, ref agitation, ref rear))
            {
                // An attack is moving it (a charge, circling the boat, a leap): follow it closely, smoothed.
                float k = 1 - Mathf.Exp(-9 * dt);
                boss.transform.position = Vector3.Lerp(pos, actPos, k);
                boss.transform.rotation = Quaternion.Slerp(boss.transform.rotation, actRot, k);
            }
            else
            {
                // Back to its lurking spot, easing up or down to the surface rather than jumping; heading where it's
                // going while it's far off, then turning to face the boat.
                var flatNow = new Vector3(pos.x, 0, pos.z);
                var goal = new Vector3(lurk.x, 0, lurk.z);
                var flat = Vector3.Lerp(flatNow, goal, 1 - Mathf.Exp(-0.45f * dt));
                boss.transform.position = new Vector3(flat.x, Mathf.Lerp(pos.y, y, 1 - Mathf.Exp(-1.5f * dt)), flat.z);
                var face = Origin - boss.transform.position;
                face.y = 0;
                var travel = goal - flatNow;
                if (travel.magnitude > 18) face = travel;
                if (face.sqrMagnitude > 1)
                    boss.transform.rotation = Quaternion.Slerp(boss.transform.rotation, Quaternion.LookRotation(face.normalized) * Quaternion.Euler(0, 0, Mathf.Sin(Time.time * 0.8f) * 3), 1 - Mathf.Exp(-1.6f * dt));
            }
            BossMotion.Body(bossRig, info.id, Time.time, agitation, rear);

            // A breach: a great hump of it rears up inside the golden ring.
            var br = v.Phase == VoyagePhase.Boss ? b?.breach : null;
            hump.SetActive(br != null);
            if (br != null)
            {
                var c = ToUnity(br.x, br.y);
                float window = V.boss.breach.window, t = 1 - Mathf.Clamp01((br.until - b.clock) / window);
                float rise = Mathf.Sin(Mathf.Clamp01(t * 1.15f) * Mathf.PI); // up, then back down
                hump.transform.position = new Vector3(c.x, Mathf.Lerp(-6, 0.4f, rise), c.z);
                hump.transform.rotation = Quaternion.Euler(0, Time.time * 20, 0);
            }
        }

        /// <summary>A glowing ring on the water: the boss's weak spot (red) or a breach (gold).</summary>
        class SpotRing
        {
            GameObject gameObject;
            Transform transform, ring, glow;
            MeshRenderer ringR, glowR;
            Color colour;
            MaterialPropertyBlock block;

            public static SpotRing Create(Transform parent, Color colour)
            {
                var go = new GameObject("Spot ring");
                go.transform.SetParent(parent, false);
                var s = new SpotRing { gameObject = go, transform = go.transform };
                s.colour = colour;
                s.block = new MaterialPropertyBlock();
                var gk = new MeshKit();
                int n = 32;
                for (int i = 0; i < n; i++)
                {
                    float a0 = i / (float)n * Mathf.PI * 2, a1 = (i + 1) / (float)n * Mathf.PI * 2;
                    var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                    var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                    gk.Tri(Vector3.zero, d1, d0, new Color32(255, 255, 255, 90));
                }
                var rk = new MeshKit();
                for (int i = 0; i < n; i++)
                {
                    if (i % 4 == 3) continue; // dashed
                    float a0 = i / (float)n * Mathf.PI * 2, a1 = (i + 1) / (float)n * Mathf.PI * 2;
                    var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                    var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                    rk.Quad(d0 * 0.9f, d0 * 1.02f, d1 * 1.02f, d1 * 0.9f, new Color32(255, 255, 255, 230));
                }
                s.glow = Part(go.transform, gk.Build("spot glow"), out s.glowR);
                s.ring = Part(go.transform, rk.Build("spot ring"), out s.ringR);
                go.SetActive(false);
                return s;
            }

            static Transform Part(Transform parent, Mesh mesh, out MeshRenderer r)
            {
                var go = new GameObject("Part");
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = RodView.Unlit;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                return go.transform;
            }

            public void Show(bool on, Vector3 at, float radius)
            {
                if (gameObject.activeSelf != on) gameObject.SetActive(on);
                if (!on) return;
                transform.position = at + Vector3.up * Mathf.Sin(Time.time * 1.3f) * 0.05f;
                float pulse = 1 + Mathf.Sin(Time.time * 5) * 0.04f;
                glow.localScale = new Vector3(radius, 1, radius);
                ring.localScale = new Vector3(radius * pulse, 1, radius * pulse);
                ring.localRotation = Quaternion.Euler(0, Time.time * 25, 0);
                block.SetColor("_Color", colour);
                ringR.SetPropertyBlock(block);
                block.SetColor("_Color", new Color(colour.r, colour.g, colour.b, 0.55f + 0.25f * Mathf.Sin(Time.time * 4)));
                glowR.SetPropertyBlock(block);
            }
        }
    }

    /// <summary>The four bosses in low-poly, in their colours: the Kraken, Megalodon, the Sea Serpent and the Ghost Whale.</summary>
    public static class BossMeshes
    {
        static readonly Dictionary<string, Mesh> cache = new Dictionary<string, Mesh>();

        /// <summary>A tube along a path, tapering from r0 to r1, seen from both sides.</summary>
        public static void Tube(MeshKit k, IList<Vector3> pts, float r0, float r1, Color32 c, int sides = 7)
        {
            Vector3[] prev = null;
            for (int i = 0; i < pts.Count; i++)
            {
                var dir = (i < pts.Count - 1 ? pts[i + 1] - pts[i] : pts[i] - pts[i - 1]).normalized;
                var up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;
                var u = Vector3.Cross(dir, up).normalized;
                var v = Vector3.Cross(dir, u);
                float r = Mathf.Lerp(r0, r1, i / (pts.Count - 1f));
                var ring = new Vector3[sides];
                for (int s = 0; s < sides; s++)
                {
                    float a = s / (float)sides * Mathf.PI * 2;
                    ring[s] = pts[i] + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                }
                if (prev != null)
                    for (int s = 0; s < sides; s++)
                    {
                        int t = (s + 1) % sides;
                        var col = s % 2 == 0 ? c : MeshKit.Shade(c, 0.85f);
                        k.Quad(prev[s], prev[t], ring[t], ring[s], col);
                        k.Quad(prev[s], ring[s], ring[t], prev[t], col);
                    }
                prev = ring;
            }
        }

        static void TwoSidedTri(MeshKit k, Vector3 a, Vector3 b, Vector3 c, Color32 col)
        {
            k.Tri(a, b, c, col);
            k.Tri(a, c, b, MeshKit.Shade(col, 0.8f));
        }

        static List<Vector3> Curve(Vector3 a, Vector3 b, Vector3 c, int n = 10)
        {
            var list = new List<Vector3>();
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                list.Add((1 - t) * (1 - t) * a + 2 * (1 - t) * t * b + t * t * c);
            }
            return list;
        }

        /// <summary>The boss itself, facing +Z, its waterline at y = 0.</summary>
        public static Mesh Body(BossInfo info)
        {
            if (cache.TryGetValue("body " + info.id, out var m)) return m;
            var model = Models.Named("boss", "boss_" + info.id);
            if (model) return cache["body " + info.id] = model;
            var k = new MeshKit();
            var rnd = new SeededRandom(Noise.SeedFrom("boss", info.id));
            Color32 c = MeshKit.Hex(info.color), dark = MeshKit.Shade(c, 0.6f), light = MeshKit.Shade(c, 1.25f),
                eye = MeshKit.Hex("#ffd166"), pupil = MeshKit.Hex("#111111"), white = MeshKit.Hex("#f8f9fa");
            switch (info.id)
            {
                case "kraken":
                    // A great domed head, eyes like lanterns, tentacles rearing all round.
                    k.Blob(new Vector3(0, 7, -2), new Vector3(7, 10, 7), c, rnd, 0.12f);
                    k.Blob(new Vector3(0, 1.5f, 0), new Vector3(8, 4, 8), dark, rnd, 0.1f);
                    foreach (float x in new[] { -3f, 3f })
                    {
                        k.Blob(new Vector3(x, 4.5f, 5.2f), new Vector3(1.6f, 1.6f, 1), eye, rnd, 0.05f);
                        k.Box(new Vector3(x, 4.5f, 6.1f), new Vector3(0.5f, 1.8f, 0.2f), pupil);
                    }
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i / 8f * Mathf.PI * 2 + 0.2f;
                        var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                        float reach = rnd.Range(12, 18), height = rnd.Range(6, 14);
                        Tube(k, Curve(d * 5 + Vector3.up * 0.5f, d * reach * 0.6f + Vector3.up * height, d * reach + Vector3.up * rnd.Range(2, 7) + Vector3.Cross(d, Vector3.up) * rnd.Range(-3, 3)), 1.3f, 0.15f, i % 2 == 0 ? c : light);
                    }
                    break;
                case "megalodon":
                    // A huge shark rolling at the surface: back, dorsal fin, tail and open jaws.
                    k.Blob(new Vector3(0, 0.5f, 0), new Vector3(5, 4.2f, 15), c, rnd, 0.06f);
                    k.Blob(new Vector3(0, -0.8f, 2), new Vector3(4.2f, 2.6f, 12), MeshKit.Hex("#dee2e6"), rnd, 0.05f);
                    TwoSidedTri(k, new Vector3(0, 4, 2), new Vector3(0, 11, -4), new Vector3(0, 4, -6), dark);
                    TwoSidedTri(k, new Vector3(0, 1, -14), new Vector3(0, 9, -21), new Vector3(0, 1.5f, -17), dark);
                    TwoSidedTri(k, new Vector3(0, 0, -14), new Vector3(0, -6, -20), new Vector3(0, 0.5f, -17), dark);
                    foreach (float x in new[] { -1f, 1f })
                    {
                        TwoSidedTri(k, new Vector3(x * 4.5f, -0.5f, 3), new Vector3(x * 10, -2, -2), new Vector3(x * 4.5f, -0.8f, -1), dark);
                        k.Box(new Vector3(x * 3.4f, 2.4f, 9.5f), new Vector3(0.3f, 0.7f, 0.7f), pupil);
                    }
                    k.Box(new Vector3(0, 0.2f, 13.5f), new Vector3(5, 2.6f, 1.5f), MeshKit.Hex("#6a040f")); // the mouth
                    for (int i = 0; i < 9; i++)
                    {
                        float x = -2.2f + i * 0.55f;
                        k.Cone(new Vector3(x, -1, 14.3f), 0.25f, 1.2f, 4, white);
                        k.Cone(new Vector3(x + 0.25f, 1.5f, 14.3f), 0.25f, -1.2f, 4, white);
                    }
                    break;
                case "serpent":
                    // Coils of green scale arching out of the water, and the head reared up.
                    for (int i = 0; i < 4; i++)
                    {
                        float z = -24 + i * 9;
                        float h = 5 + i * 1.2f;
                        Tube(k, Curve(new Vector3(0, -2, z), new Vector3(0, h * 2, z + 4), new Vector3(0, -2, z + 8)), 2.1f - i * 0.15f, 2f - i * 0.15f, i % 2 == 0 ? c : light, 8);
                        for (int f = 0; f < 3; f++) TwoSidedTri(k, new Vector3(0, h * 0.9f + 1.5f, z + 2.5f + f * 1.3f), new Vector3(0, h * 0.9f + 3.5f, z + 3 + f * 1.3f), new Vector3(0, h * 0.9f + 1.5f, z + 3.6f + f * 1.3f), MeshKit.Hex("#e9c46a"));
                    }
                    Tube(k, Curve(new Vector3(0, -2, 12), new Vector3(0, 12, 12), new Vector3(0, 13, 16)), 2, 1.6f, c, 8);
                    k.Blob(new Vector3(0, 13.5f, 17.5f), new Vector3(2.2f, 1.8f, 3.4f), c, rnd, 0.08f);
                    foreach (float x in new[] { -1f, 1f })
                    {
                        k.Box(new Vector3(x * 1.6f, 14.4f, 19), new Vector3(0.3f, 0.5f, 0.6f), eye);
                        TwoSidedTri(k, new Vector3(x * 1.5f, 14.5f, 16), new Vector3(x * 4.5f, 17, 13), new Vector3(x * 1.4f, 13.5f, 15), MeshKit.Hex("#e9c46a"));
                    }
                    break;
                default: // the Ghost Whale: pale, glowing, rising beneath you
                {
                    var pale = MeshKit.Hex("#bde0fe");
                    k.Blob(new Vector3(0, 0, 0), new Vector3(7, 5, 20), pale, rnd, 0.06f);
                    k.Blob(new Vector3(0, -1.5f, 4), new Vector3(6, 3, 14), MeshKit.Hex("#e7f5ff"), rnd, 0.05f);
                    TwoSidedTri(k, new Vector3(0, 1, -19), new Vector3(9, 4, -27), new Vector3(0, 1.5f, -23), pale);
                    TwoSidedTri(k, new Vector3(0, 1, -19), new Vector3(-9, 4, -27), new Vector3(0, 1.5f, -23), pale);
                    foreach (float x in new[] { -1f, 1f })
                    {
                        TwoSidedTri(k, new Vector3(x * 6, -1, 6), new Vector3(x * 13, -3, 0), new Vector3(x * 6, -1.5f, 1), pale);
                        k.Blob(new Vector3(x * 5.2f, 1.8f, 13), new Vector3(0.6f, 0.6f, 0.6f), MeshKit.Hex("#90e0ef"), rnd, 0.05f);
                    }
                    for (int i = 0; i < 14; i++)
                        k.Blob(new Vector3(rnd.Range(-5, 5), rnd.Range(2.5f, 4.5f), rnd.Range(-14, 14)), Vector3.one * rnd.Range(0.3f, 0.7f), MeshKit.Hex("#a2d2ff"), rnd, 0.1f);
                    break;
                }
            }
            m = k.Build("boss " + info.id);
            cache["body " + info.id] = m;
            return m;
        }

        /// <summary>The part of it that breaches: a great scaly hump with spines.</summary>
        public static Mesh Hump(BossInfo info)
        {
            if (cache.TryGetValue("hump " + info.id, out var m)) return m;
            var model = Models.Named("boss", "hump_" + info.id);
            if (model) return cache["hump " + info.id] = model;
            var k = new MeshKit();
            var rnd = new SeededRandom(Noise.SeedFrom("hump", info.id));
            Color32 c = MeshKit.Hex(info.color);
            k.Blob(Vector3.zero, new Vector3(5, 4.5f, 7), c, rnd, 0.12f);
            for (int i = 0; i < 5; i++)
                k.Cone(new Vector3(0, 3.6f, -4 + i * 2), 0.6f, 2.2f - Mathf.Abs(i - 2) * 0.4f, 4, MeshKit.Shade(c, 0.6f));
            // Foam ring round it.
            for (int i = 0; i < 18; i++)
            {
                float a = i / 18f * Mathf.PI * 2;
                k.Blob(new Vector3(Mathf.Cos(a) * 6.5f, 0, Mathf.Sin(a) * 7.5f), new Vector3(1.4f, 0.5f, 1.4f), MeshKit.Hex("#f1faee"), rnd, 0.2f);
            }
            m = k.Build("hump " + info.id);
            cache["hump " + info.id] = m;
            return m;
        }

        /// <summary>What grabs the rail (tentacle, jaws, coil, barnacled fin): it rises out of the water beside the
        /// hull and hooks over the rail onto the deck. side: +1 if the rail is on the +Z side of the boat.</summary>
        public static Mesh Grabber(BossInfo info, float side)
        {
            string key = $"grab {info.id} {side}";
            if (cache.TryGetValue(key, out var m)) return m;
            var model = Models.Named("boss", $"grab_{info.id}_{(side > 0 ? "p" : "n")}");
            if (model) return cache[key] = model;
            var k = new MeshKit();
            var rnd = new SeededRandom(Noise.SeedFrom("grab", info.id));
            Color32 c = MeshKit.Hex(info.color), light = MeshKit.Shade(c, 1.25f);
            // From the water outside the hull, up over the rail and down onto the deck.
            var a = new Vector3(0, -1.5f, side * 4.5f);
            var b = new Vector3(0, SeaWorld.DeckY + 5.5f, side * 1.5f);
            var e = new Vector3(0, SeaWorld.DeckY + 0.3f, side * -2.2f);
            var path = Curve(a, b, e, 12);
            Tube(k, path, info.id == "serpent" ? 1.2f : 0.85f, info.id == "megalodon" ? 0.6f : 0.12f, c);
            if (info.id == "megalodon")
            {
                // Snapping jaws at the end, with teeth.
                k.Blob(e + Vector3.up * 0.4f, new Vector3(1.1f, 0.8f, 1.3f), c, rnd, 0.08f);
                for (int i = 0; i < 6; i++) k.Cone(e + new Vector3(-0.6f + i * 0.24f, -0.1f, side * -0.9f), 0.12f, 0.45f, 4, MeshKit.Hex("#f8f9fa"));
            }
            else
                // Suckers (or barnacles, or scales) along the underside.
                for (int i = 2; i < path.Count - 1; i += 2)
                    k.Blob(path[i] + Vector3.down * 0.5f, Vector3.one * 0.28f, light, rnd, 0.1f);
            m = k.Build(key);
            cache[key] = m;
            return m;
        }
    }
}
