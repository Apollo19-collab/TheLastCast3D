// The bosses' attacks, acted out. Each boss attacks the way that animal would attack a boat:
//   the Kraken    Tentacle Slam (a tentacle rears up beside the boat and crashes across the deck),
//                 Tentacle Thrash (tentacles burst up all round and whip the water), Ink Cloud
//   Megalodon     Ramming Charge (it charges in, fin cutting the water, and rams the hull), Death Roll
//                 (it bites the stern and rolls), Circling Frenzy (it circles the boat, churning the water)
//   the Serpent   Coil Crush (coils swing up out of the sea over the deck and squeeze), Maelstrom (it swims
//                 circles round the boat), Silt Storm
//   the Ghost Whale  Tail Slam (its flukes rise and come down on the deck), Breach Wave (it leaps clear of the
//                 water nearby and the wave rocks the boat), Ghost Mist, Whale Song
// The fight itself (what each attack does to you, and when) is in Core/Voyage.cs; everything here follows its
// clock: the warning before an attack lands (b.attack, b.attackAt), the attack that just landed (b.lastAttack,
// b.landedAt) and a lingering effect (b.effect). So it plays out the same on every angler's screen.

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    /// <summary>What each boss calls its attacks, and what it says they do.</summary>
    public static class BossMoves
    {
        static readonly Dictionary<string, (string name, string desc)> moves = new Dictionary<string, (string, string)>
        {
            ["kraken.thrash"] = ("Tentacle Thrash", "Tentacles burst up all round and whip the water: the boat rocks and lines jerk. Ease off!"),
            ["kraken.ink"] = ("Ink Cloud", "It squirts ink all round the boat: bites are half as fast for 10 seconds."),
            ["kraken.slam"] = ("Tentacle Slam", "A tentacle rears up over {0}: get away from it! Anyone caught there is dazed and loses their fish."),
            ["megalodon.thrash"] = ("Death Roll", "It's going to bite the rail by the stern and roll: the boat will shake and lines jerk. Ease off!"),
            ["megalodon.whirlpool"] = ("Circling Frenzy", "It circles the boat, churning the water: fish fight 40% harder for 10 seconds."),
            ["megalodon.slam"] = ("Ramming Charge", "It's charging at {0}! Get away from that side before it rams the boat!"),
            ["serpent.ink"] = ("Silt Storm", "It thrashes along the seabed: the water clouds and bites are half as fast for 10 seconds."),
            ["serpent.whirlpool"] = ("Maelstrom", "It swims circles round the boat, dragging the water round: fish fight 40% harder for 10 seconds."),
            ["serpent.slam"] = ("Coil Crush", "Coils are rising over {0}: get clear before they crush it!"),
            ["ghostwhale.thrash"] = ("Breach Wave", "It's going to leap clear of the water nearby: the wave will rock the boat and jerk lines. Ease off!"),
            ["ghostwhale.ink"] = ("Ghost Mist", "A cold mist rolls in round it: bites are half as fast for 10 seconds."),
            ["ghostwhale.whirlpool"] = ("Whale Song", "Its song rings through the water and unsettles the fish: they fight 40% harder for 10 seconds."),
            ["ghostwhale.slam"] = ("Tail Slam", "Its tail is rising over {0}: get away before it comes down!"),
        };

        /// <summary>An attack's name and warning for this boss (area: the deck area a slam is aimed at).</summary>
        public static (string name, string desc) Describe(BossInfo boss, BossAttack a, string area)
        {
            if (moves.TryGetValue($"{boss.id}.{a.id}", out var m)) return (m.name, string.Format(m.desc, area));
            return a.area ? (boss.slam, $"Get away from {area}! Anyone caught there is dazed and loses their fish.") : (a.name, a.desc);
        }
    }

    public class BossActs : MonoBehaviour
    {
        public static BossActs I { get; private set; }

        SeaWorld sea;
        string bossId;
        readonly List<GameObject> props = new List<GameObject>();
        readonly List<LimbRig> propRigs = new List<LimbRig>();
        string actKey; // the act the props belong to
        bool landed; // its moment of impact has happened (rocks and splashes fired)
        Vector3 bodyFrom; // where the boss was when it began a move of its own
        GameObject disc; // ink, silt, or a swirl on the water
        float discShown;
        float nextRing;
        Material discMat;

        public static BossActs Create(SeaWorld sea, Transform parent)
        {
            var go = new GameObject("Boss attacks");
            go.transform.SetParent(parent, false);
            I = go.AddComponent<BossActs>();
            I.sea = sea;
            return I;
        }

        public void Clear()
        {
            foreach (var p in props) if (p) Destroy(p);
            props.Clear();
            propRigs.Clear();
            actKey = null;
            if (disc) Destroy(disc);
            sea.Mist = 0;
        }

        // ---- which act, and how far into it ----------------------------------------------------------------------

        /// <summary>The attack being acted out: its id, deck area, seed, and the time from its landing (negative while it's coming).</summary>
        static bool Current(BossFight b, out string id, out string area, out int seed, out float t)
        {
            if (b.attack != null) { id = b.attack; area = b.attackArea; seed = b.attackSeed; t = b.clock - b.attackAt; return true; }
            if (b.lastAttack != null && b.clock - b.landedAt < 12) { id = b.lastAttack; area = b.lastArea; seed = b.lastSeed; t = b.clock - b.landedAt; return true; }
            id = area = null;
            seed = 0;
            t = 0;
            return false;
        }

        /// <summary>Where a deck area is hit from: a point on the hull and the way out from it (port, starboard or, for the
        /// bow and stern, a side picked by the seed; Megalodon rams the bow head-on and bites the stern from behind).</summary>
        Vector3 HitPoint(string area, int seed, out Vector3 outward, string boss)
        {
            var a = GameData.I.voyage.DeckArea(area);
            var c = SeaWorld.ToUnity(a.x + a.w / 2, a.y + a.h / 2);
            float side = area == "port" ? 1 : area == "starboard" ? -1 : seed % 2 == 0 ? 1 : -1;
            if (boss == "megalodon" && area == "bow") { outward = Vector3.right; return new Vector3(SeaWorld.Origin.x + 30, 0, SeaWorld.Origin.z); }
            outward = new Vector3(0, 0, side);
            return new Vector3(c.x, 0, SeaWorld.Origin.z + side * 6);
        }

        // ---- the boss's own moves (ramming, circling, breaching) ------------------------------------------------

        /// <summary>
        /// While an attack moves the boss itself, where it should be (and facing) this frame; false to leave it lurking.
        /// agitation and rear feed its limbs (BossMotion).
        /// </summary>
        public bool Body(BossFight b, Vector3 current, out Vector3 pos, out Quaternion rot, ref float agitation, ref float rear)
        {
            pos = current;
            rot = Quaternion.identity;
            if (b == null || b.result != null || !Current(b, out var id, out var area, out int seed, out float t)) return false;
            var o = SeaWorld.Origin;
            string boss = b.info.id;
            string key = $"{id}.{seed}";
            if (key != bodyKey) { bodyKey = key; bodyFrom = current; }

            if (boss == "megalodon" && (id == "slam" || id == "thrash"))
            {
                // Ramming Charge: around under water to a run-up point, then a charge at the hull, fin out; a bounce
                // off. Death Roll: the same at the stern, then it hangs on and rolls.
                Vector3 hit, n;
                if (id == "slam") hit = HitPoint(area, seed, out n, boss);
                else { n = new Vector3(0, 0, seed % 2 == 0 ? 1 : -1); hit = new Vector3(o.x - 13, 0, o.z + n.z * 6); } // the Death Roll: a bite on the rail by the stern
                var face = Quaternion.LookRotation(-n);
                var runUp = hit + n * 60 + Vector3.down * 7;
                var strike = hit + n * 17.2f;
                if (t < -1.7f)
                {
                    float u = Mathf.Clamp01((t + 3) / 1.3f);
                    pos = Vector3.Lerp(bodyFrom, runUp, Mathf.SmoothStep(0, 1, u));
                    var heading = runUp - bodyFrom;
                    rot = heading.sqrMagnitude > 1 ? Quaternion.Slerp(Quaternion.LookRotation(new Vector3(heading.x, 0, heading.z)), face, u) : face;
                }
                else if (t < 0)
                {
                    float u = (t + 1.7f) / 1.7f;
                    pos = Vector3.Lerp(runUp, strike + Vector3.down * 1.4f, u * u);
                    pos.y = Mathf.Lerp(-7, -1.4f, Mathf.SmoothStep(0, 1, u));
                    rot = face;
                    agitation = 1;
                    rear = u;
                }
                else if (id == "thrash" && t < 3.2f)
                {
                    // Hanging on to the stern and rolling.
                    pos = strike + Vector3.down * (1.2f - Mathf.Sin(t * 7) * 0.4f);
                    rot = face * Quaternion.Euler(Mathf.Sin(t * 5) * 6, Mathf.Sin(t * 9) * 10, Mathf.Sin(t * 6) * 28);
                    agitation = 1;
                    rear = 1;
                }
                else if (t < 0.6f)
                {
                    pos = strike + n * (t / 0.6f * 6) + Vector3.down * 1.4f; // knocked back off the hull
                    rot = face;
                    agitation = 1;
                }
                else return false; // then it swims back out to lurk
                return true;
            }
            if ((boss == "megalodon" || boss == "serpent") && id == "whirlpool")
            {
                // Circling the boat, close in.
                float radius = boss == "serpent" ? 46 : 36, speed = boss == "serpent" ? 0.24f : 0.42f;
                float a0 = Mathf.Atan2(bodyFrom.z - o.z, bodyFrom.x - o.x);
                float duration = (GameData.I.voyage.Attack(id).seconds ?? 10) + 0.5f;
                if (t > duration) return false;
                float ang = a0 + speed * Mathf.Max(0, t);
                var on = o + new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * radius;
                if (t < 0) on = Vector3.Lerp(bodyFrom, on, Mathf.SmoothStep(0, 1, (t + 3) / 3));
                pos = on + Vector3.down * (boss == "serpent" ? 0.5f : 1.8f);
                var tangent = new Vector3(-Mathf.Sin(ang), 0, Mathf.Cos(ang));
                rot = Quaternion.LookRotation(tangent) * Quaternion.Euler(0, 0, boss == "megalodon" ? -12 : 0);
                agitation = 0.8f;
                return true;
            }
            if (boss == "ghostwhale" && id == "thrash")
            {
                // Breach Wave: it dives, then bursts out of the water nearby, arcs over and crashes back down.
                var toBoat = new Vector3(o.x - bodyFrom.x, 0, o.z - bodyFrom.z).normalized;
                var leap = o - toBoat * 42;
                var side = Quaternion.LookRotation(Vector3.Cross(Vector3.up, toBoat));
                if (t < -0.7f)
                {
                    float u = Mathf.Clamp01((t + 3) / 2.3f);
                    pos = Vector3.Lerp(bodyFrom, leap + Vector3.down * 26, Mathf.SmoothStep(0, 1, u));
                    rot = Quaternion.Slerp(Quaternion.LookRotation(toBoat), side * Quaternion.Euler(-60, 0, 0), u);
                }
                else if (t < 2.0f)
                {
                    float u = (t + 0.7f) / 2.7f; // out of the water, over, and back in
                    pos = leap + Vector3.up * (-26 + 34 * Mathf.Sin(u * Mathf.PI)) + side * Vector3.forward * (u - 0.5f) * 16;
                    rot = side * Quaternion.Euler(Mathf.Lerp(-60, 70, u), 0, Mathf.Lerp(0, 35, u));
                    agitation = 1;
                    if (u > 0.82f && !splashedBreach) { splashedBreach = true; Splash.Create(leap + side * Vector3.forward * 6, 9); Sfx.Play("splash"); }
                }
                else if (t < 4.5f)
                {
                    pos = leap + Vector3.down * Mathf.Lerp(14, 4, (t - 2) / 2.5f);
                    rot = side * Quaternion.Euler(Mathf.Lerp(70, 0, (t - 2) / 2.5f), 0, Mathf.Lerp(35, 0, (t - 2) / 2.5f));
                }
                else return false;
                if (t < 0) splashedBreach = false;
                return true;
            }
            return false;
        }

        string bodyKey;
        bool splashedBreach;

        // ---- props, effects on the water, and the boat being hit ------------------------------------------------

        void Update()
        {
            var v = Voyage.I;
            var b = v?.Boss;
            bool fighting = v != null && v.AtSea && v.Phase == VoyagePhase.Boss && b != null && b.result == null;
            if (!fighting)
            {
                if (props.Count > 0 || disc) Clear();
                return;
            }
            bossId = b.info.id;
            float mist = 0;
            UpdateEffect(b, ref mist);
            sea.Mist = Mathf.MoveTowards(sea.Mist, mist, Time.deltaTime * 0.6f);

            if (!Current(b, out var id, out var area, out int seed, out float t))
            {
                ClearProps();
                return;
            }
            string key = $"{id}.{seed}";
            if (key != actKey)
            {
                ClearProps();
                actKey = key;
                landed = false;
                BuildProps(id, area, seed);
            }
            if (t >= 0 && !landed)
            {
                landed = true;
                Impact(id, area, seed);
            }
            AnimateProps(id, area, seed, t);
        }

        void ClearProps()
        {
            foreach (var p in props) if (p) Destroy(p);
            props.Clear();
            propRigs.Clear();
            actKey = null;
        }

        GameObject Prop(Mesh mesh, int bones)
        {
            var go = new GameObject("Attack");
            go.transform.SetParent(transform, false);
            var rig = LimbRig.Attach(go, mesh, WorldBuilder.LitMaterial, bones);
            if (!rig)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            }
            props.Add(go);
            propRigs.Add(rig);
            go.SetActive(false);
            return go;
        }

        void BuildProps(string id, string area, int seed)
        {
            var mesh = Models.Named("boss", "slam_" + bossId);
            if (!mesh) return;
            if (id == "slam" && (bossId == "kraken" || bossId == "ghostwhale")) Prop(mesh, 14);
            else if (id == "slam" && bossId == "serpent") for (int i = 0; i < 3; i++) Prop(mesh, 14);
            else if (id == "thrash" && bossId == "kraken")
                for (int i = 0; i < 3; i++) Prop(mesh, 14).transform.localScale = Vector3.one * 0.7f;
        }

        void AnimateProps(string id, string area, int seed, float t)
        {
            if (props.Count == 0) return;
            var o = SeaWorld.Origin;
            if (id == "slam" && (bossId == "kraken" || bossId == "ghostwhale"))
            {
                // Up out of the water beside the deck area, rearing back, then down across it, and away again.
                var hit = HitPoint(area, seed, out var n, bossId);
                float lean, y;
                if (t < 0)
                {
                    float u = (t + 3) / 3;
                    y = Mathf.Lerp(-20, bossId == "ghostwhale" ? -4 : -2, Mathf.SmoothStep(0, 1, u / 0.4f));
                    lean = u < 0.4f ? -12 : u < 0.82f ? Mathf.Lerp(-12, -32, (u - 0.4f) / 0.42f) : Mathf.Lerp(-32, 78, Mathf.Pow((u - 0.82f) / 0.18f, 2));
                }
                else if (t < 0.9f) { y = bossId == "ghostwhale" ? -4 : -2; lean = 78 - Mathf.Sin(t / 0.9f * Mathf.PI) * 6; }
                else { float u = Mathf.Clamp01((t - 0.9f) / 1.8f); y = Mathf.Lerp(-2, -22, u * u); lean = Mathf.Lerp(78, 20, u); }
                Place(0, hit + n * (bossId == "ghostwhale" ? 7 : 3.5f) + Vector3.up * y, Quaternion.LookRotation(-n) * Quaternion.Euler(lean, 0, 0), t, 1.2f);
            }
            else if (id == "slam" && bossId == "serpent")
            {
                // Three coils swing up out of the sea on one side and over the deck area, squeeze, then slide back.
                var hit = HitPoint(area, seed, out var n, bossId);
                var a = GameData.I.voyage.DeckArea(area);
                float span = a.w / WorldMap.UnitsPerMetre;
                for (int i = 0; i < 3; i++)
                {
                    var at = new Vector3(hit.x + (i - 1) * span * 0.32f, -3, o.z + n.z * 8.5f);
                    float angle, squash;
                    if (t < 0) { float u = Mathf.Clamp01((t + 3 - i * 0.15f) / 2.8f); angle = Mathf.Lerp(-115, 0, Mathf.SmoothStep(0, 1, u)); squash = 0.62f; }
                    else if (t < 1.8f) { angle = 0; squash = Mathf.Lerp(0.62f, 0.3f, Mathf.Clamp01(t / 0.35f)) + Mathf.Sin(t * 9) * 0.02f; }
                    else { float u = Mathf.Clamp01((t - 1.8f) / 1.6f); angle = Mathf.Lerp(0, -115, u); squash = Mathf.Lerp(0.3f, 0.62f, u); }
                    var p = props[i];
                    p.SetActive(t < 3.5f);
                    p.transform.position = at;
                    p.transform.rotation = Quaternion.LookRotation(-new Vector3(0, 0, n.z)) * Quaternion.Euler(angle, 0, 0);
                    p.transform.localScale = new Vector3(1, squash, 0.95f);
                    BossMotion.Grabber(propRigs[i], "serpent", Time.time + i, t >= 0 && t < 1.8f ? 1 : 0.4f, i);
                }
            }
            else if (id == "thrash" && bossId == "kraken")
            {
                // Tentacles burst up round the hull and whip back and forth.
                for (int i = 0; i < props.Count; i++)
                {
                    float side = (seed + i) % 2 == 0 ? 1 : -1;
                    var at = new Vector3(o.x + (i - 1) * 16 + (seed % 3) * 3, 0, o.z + side * 10);
                    float y = t < 0 ? Mathf.Lerp(-16, -1.5f, Mathf.SmoothStep(0, 1, (t + 3) / 2.2f)) : t < 2.6f ? -1.5f : Mathf.Lerp(-1.5f, -16, (t - 2.6f) / 1.5f);
                    float whip = t < 0 ? -10 : 18 + Mathf.Sin(t * 6 + i * 1.7f) * 30;
                    Place(i, at + Vector3.up * y, Quaternion.LookRotation(new Vector3(0, 0, -side)) * Quaternion.Euler(whip, Mathf.Sin(t * 3 + i) * 15, 0), t, 4.2f);
                }
            }
        }

        void Place(int i, Vector3 pos, Quaternion rot, float t, float until)
        {
            var p = props[i];
            p.SetActive(t < until);
            p.transform.SetPositionAndRotation(pos, rot);
            BossMotion.Grabber(propRigs[i], bossId, Time.time * 1.3f + i, t > -0.5f && t < 1.5f ? 1 : 0.35f, i * 2.1f);
        }

        /// <summary>The moment an attack lands: the boat rocks, the water flies.</summary>
        void Impact(string id, string area, int seed)
        {
            var o = SeaWorld.Origin;
            if (id == "slam")
            {
                var hit = HitPoint(area, seed, out var n, bossId);
                float side = n.z != 0 ? n.z : 0;
                if (bossId == "megalodon")
                {
                    // Rammed: the boat lurches away from the blow.
                    if (n.x != 0) sea.Rock(0, -7 * n.x, 0.3f, 3.5f, 0.55f);
                    else sea.Rock(-8 * side, 2, 0.35f, 4, 0.5f);
                    Splash.Create(hit + n * 1.5f, 6);
                }
                else
                {
                    var a = GameData.I.voyage.DeckArea(area);
                    sea.Rock(-5 * side, (a.x + a.w / 2 - GameData.I.voyage.seaBoat.x) / 100f, 0.45f, 3, 0.6f);
                    Splash.Create(SeaWorld.ToUnity(a.x + a.w / 2, a.y + a.h / 2, SeaWorld.DeckY), bossId == "serpent" ? 4 : 5);
                    Splash.Create(hit + n * 4, 5);
                }
                sea.Shake(0.8f);
                Sfx.Play("splash");
            }
            else if (id == "thrash")
            {
                if (bossId == "megalodon") { float side = seed % 2 == 0 ? 1 : -1; sea.Rock(-6 * side, -3, 0.3f, 3.5f, 1.4f); Splash.Create(new Vector3(o.x - 13, 0, o.z + side * 7), 5); }
                else if (bossId == "kraken") sea.Rock(5, 1.5f, 0.3f, 3.5f, 0.8f);
                else sea.Rock(7, 2, 0.5f, 6, 0.4f); // the whale's wave (it arrives a moment after the leap)
                sea.Shake(0.5f);
            }
            else if (id == "whirlpool") sea.Rock(2.5f, 1, 0.15f, 10, 0.3f);
            else sea.Shake(0.2f);
        }

        /// <summary>Ink or silt clouding the water, a swirl round the boat, the whale's song, the mist.</summary>
        void UpdateEffect(BossFight b, ref float mist)
        {
            string e = b.effect;
            float left = b.effectUntil - b.clock, age = b.clock - b.landedAt;
            bool cloud = e == "ink" && bossId != "ghostwhale", swirl = e == "whirlpool" && bossId != "ghostwhale";
            if (e == "ink" && bossId == "ghostwhale") mist = 1;
            if (cloud || swirl)
            {
                if (!disc)
                {
                    disc = new GameObject("Effect on the water");
                    disc.transform.SetParent(transform, false);
                    disc.AddComponent<MeshFilter>().sharedMesh = swirl ? SwirlMesh() : CloudMesh();
                    discMat ??= new Material(RodView.Unlit) { renderQueue = 2990 };
                    disc.AddComponent<MeshRenderer>().sharedMaterial = discMat;
                    discShown = 0;
                }
                discShown = Mathf.MoveTowards(discShown, left > 1.5f ? 1 : 0, Time.deltaTime * 0.7f);
                float grow = Mathf.SmoothStep(0, 1, Mathf.Clamp01(age / 2));
                var col = swirl ? new Color(0.92f, 0.97f, 1f, 0.55f) : bossId == "serpent" ? new Color(0.36f, 0.28f, 0.16f, 0.6f) : new Color(0.02f, 0.02f, 0.06f, 0.65f);
                col.a *= discShown;
                discMat.SetColor("_Color", col);
                disc.transform.position = SeaWorld.Origin + Vector3.up * 0.15f;
                disc.transform.localScale = Vector3.one * (swirl ? 1 : Mathf.Lerp(6, 62, grow));
                if (swirl) disc.transform.rotation = Quaternion.Euler(0, -Time.time * 40, 0);
            }
            else if (disc) { Destroy(disc); disc = null; }

            // Whale Song: rings of light spreading over the water from the whale.
            if (e == "whirlpool" && bossId == "ghostwhale" && Time.time >= nextRing && left > 1)
            {
                nextRing = Time.time + 1.1f;
                var whale = sea.BossPosition;
                Ring.Create(transform, new Vector3(whale.x, 0.2f, whale.z), new Color(0.56f, 0.88f, 0.94f, 0.8f));
                if (Random.value < 0.4f) Sfx.Play("song");
            }
        }

        static Mesh cloudMesh, swirlMesh;

        /// <summary>A soft-edged blot (radius 1): solid in the middle, fading out in lumps.</summary>
        static Mesh CloudMesh()
        {
            if (cloudMesh) return cloudMesh;
            var m = new Mesh();
            int n = 48;
            var v = new List<Vector3> { Vector3.zero };
            var c = new List<Color> { Color.white };
            var tris = new List<int>();
            for (int i = 0; i < n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2, r = 0.75f + 0.25f * Mathf.PerlinNoise(i * 0.35f, 3.1f);
                v.Add(new Vector3(Mathf.Cos(a) * r * 0.7f, 0, Mathf.Sin(a) * r * 0.7f));
                c.Add(Color.white);
                v.Add(new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r));
                c.Add(new Color(1, 1, 1, 0));
            }
            for (int i = 0; i < n; i++)
            {
                int a0 = 1 + i * 2, a1 = 1 + ((i + 1) % n) * 2;
                tris.AddRange(new[] { 0, a1, a0, a0, a1, a1 + 1, a0, a1 + 1, a0 + 1 });
            }
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(tris, 0);
            return cloudMesh = m;
        }

        /// <summary>Three spiral arms of foam round the boat (it turns).</summary>
        static Mesh SwirlMesh()
        {
            if (swirlMesh) return swirlMesh;
            var m = new Mesh();
            var v = new List<Vector3>();
            var c = new List<Color>();
            var tris = new List<int>();
            for (int arm = 0; arm < 3; arm++)
            {
                int start = v.Count, steps = 40;
                for (int s = 0; s <= steps; s++)
                {
                    float u = s / (float)steps;
                    float a = arm * Mathf.PI * 2 / 3 + u * 4.2f, r = 52 - u * 26, w = 1.2f + 2.2f * Mathf.Sin(u * Mathf.PI);
                    var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    v.Add(dir * (r - w));
                    v.Add(dir * (r + w));
                    var col = new Color(1, 1, 1, Mathf.Sin(u * Mathf.PI));
                    c.Add(col);
                    c.Add(col);
                    if (s > 0)
                    {
                        int i0 = start + (s - 1) * 2;
                        tris.AddRange(new[] { i0, i0 + 1, i0 + 3, i0, i0 + 3, i0 + 2 });
                    }
                }
            }
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return swirlMesh = m;
        }
    }

    /// <summary>A burst of spray: white chunks thrown up that fall back and shrink away.</summary>
    public class Splash : MonoBehaviour
    {
        static Mesh chunk;
        static Material spray;
        readonly List<(Transform t, Vector3 v, float s)> bits = new List<(Transform, Vector3, float)>();
        float age;

        public static void Create(Vector3 at, float size)
        {
            if (!chunk)
            {
                var k = new MeshKit();
                k.Blob(Vector3.zero, new Vector3(0.5f, 0.42f, 0.5f), new Color32(255, 255, 255, 255), new SeededRandom(4), 0.2f);
                chunk = k.Build("spray");
                spray = new Material(RodView.Unlit) { renderQueue = 2995 };
                spray.SetColor("_Color", new Color(0.93f, 0.97f, 1f, 0.92f));
            }
            var go = new GameObject("Splash");
            go.transform.position = at;
            var s = go.AddComponent<Splash>();
            int n = Mathf.RoundToInt(16 + size * 6);
            for (int i = 0; i < n; i++)
            {
                var b = new GameObject("Spray").transform;
                b.SetParent(go.transform, false);
                b.localPosition = Random.insideUnitSphere * size * 0.3f;
                b.rotation = Random.rotation;
                b.gameObject.AddComponent<MeshFilter>().sharedMesh = chunk;
                var r = b.gameObject.AddComponent<MeshRenderer>();
                r.sharedMaterial = spray; // bright white water, whatever the light
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var dir = Random.insideUnitSphere;
                dir.y = Mathf.Abs(dir.y) + 0.8f;
                s.bits.Add((b, dir * size * Random.Range(1.2f, 2.2f), Random.Range(0.15f, 0.45f) * Mathf.Sqrt(size)));
            }
        }

        void Update()
        {
            age += Time.deltaTime;
            for (int i = 0; i < bits.Count; i++)
            {
                var (t, v, s) = bits[i];
                v += Vector3.down * 16 * Time.deltaTime;
                t.localPosition += v * Time.deltaTime;
                t.localScale = Vector3.one * s * Mathf.Clamp01(1.6f - age);
                bits[i] = (t, v, s);
            }
            if (age > 1.6f) Destroy(gameObject);
        }
    }

    /// <summary>A ring of light spreading out over the water and fading (the Ghost Whale's song).</summary>
    public class Ring : MonoBehaviour
    {
        static Mesh mesh;
        static Material mat;
        MaterialPropertyBlock block;
        Color colour;
        float age;

        public static void Create(Transform parent, Vector3 at, Color colour)
        {
            if (!mesh)
            {
                mesh = new Mesh();
                var v = new List<Vector3>();
                var tris = new List<int>();
                int n = 64;
                for (int i = 0; i <= n; i++)
                {
                    float a = i / (float)n * Mathf.PI * 2;
                    var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    v.Add(d * 0.94f);
                    v.Add(d);
                    if (i > 0) tris.AddRange(new[] { i * 2 - 2, i * 2 + 1, i * 2 - 1, i * 2 - 2, i * 2, i * 2 + 1 });
                }
                mesh.SetVertices(v);
                mesh.SetColors(new List<Color>(new Color[v.Count]).ConvertAll(_ => Color.white));
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();
            }
            mat ??= new Material(RodView.Unlit) { renderQueue = 2991 };
            var go = new GameObject("Song ring");
            go.transform.SetParent(parent, false);
            go.transform.position = at;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            var r = go.AddComponent<Ring>();
            r.colour = colour;
            r.block = new MaterialPropertyBlock();
        }

        void Update()
        {
            age += Time.deltaTime;
            float u = age / 3.5f;
            transform.localScale = Vector3.one * Mathf.Lerp(4, 70, u);
            var c = colour;
            c.a *= 1 - u;
            block.SetColor("_Color", c);
            GetComponent<MeshRenderer>().SetPropertyBlock(block);
            if (u >= 1) Destroy(gameObject);
        }
    }
}
