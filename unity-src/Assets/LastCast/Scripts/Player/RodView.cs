// What you see of your fishing in first person: the rod in your hands (it
// bends under tension), the line, the bobber on the water, and the fish you
// just landed, held up for a moment.

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public class RodView : MonoBehaviour
    {
        Transform rodPivot, tip;
        GameObject bobber, heldFish;
        LineRenderer line;
        float holdTimer, castKick;
        string rodId;
        MeshRenderer rodRenderer;
        static Material unlit;
        static readonly Dictionary<string, Mesh> fishMeshes = new Dictionary<string, Mesh>();

        public Vector3 TipPosition => tip.position;

        public static Material Unlit => unlit ??= new Material(Resources.Load<Shader>("Shaders/Unlit"));

        public static RodView Create(Transform cam)
        {
            var go = new GameObject("Rod view");
            go.transform.SetParent(cam, false);
            var v = go.AddComponent<RodView>();
            v.Build(cam);
            return v;
        }

        void Build(Transform cam)
        {
            rodPivot = new GameObject("Rod").transform;
            rodPivot.SetParent(transform, false);
            rodPivot.localPosition = new Vector3(0.28f, -0.32f, 0.45f);
            rodPivot.localRotation = Quaternion.Euler(-35, -8, 0);
            var mf = rodPivot.gameObject.AddComponent<MeshFilter>();
            rodRenderer = rodPivot.gameObject.AddComponent<MeshRenderer>();
            rodRenderer.sharedMaterial = WorldBuilder.LitMaterial;
            rodRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mf.sharedMesh = RodMesh("#8b5a2b");
            tip = new GameObject("Tip").transform;
            tip.SetParent(rodPivot, false);
            tip.localPosition = new Vector3(0, 0, 2.2f);

            bobber = new GameObject("Bobber");
            var k = new MeshKit();
            k.Cone(Vector3.zero, 0.07f, 0.06f, 8, MeshKit.Hex("#f1faee"), 0.07f);
            k.Cone(new Vector3(0, 0.06f, 0), 0.07f, 0.08f, 8, MeshKit.Hex("#e63946"));
            k.Cone(new Vector3(0, -0.08f, 0), 0.02f, 0.08f, 4, MeshKit.Hex("#e63946"), 0.02f);
            bobber.AddComponent<MeshFilter>().sharedMesh = k.Build("bobber");
            bobber.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            bobber.SetActive(false);

            line = new GameObject("Line").AddComponent<LineRenderer>();
            line.sharedMaterial = Unlit;
            line.startColor = line.endColor = new Color(0.95f, 0.95f, 0.9f, 0.8f);
            line.widthMultiplier = 0.012f;
            line.positionCount = 16;
            line.numCapVertices = 0;
            line.enabled = false;
        }

        /// <summary>A simple tapered rod: grip, reel seat and a thin blank, in the rod's colour.</summary>
        static Mesh RodMesh(string hex)
        {
            var model = Models.Get("rod");
            if (model) return Models.Recolour(model, MeshKit.Hex(hex)); // the Blender rod, in this rod's colour
            var k = new MeshKit();
            var cork = MeshKit.Hex("#c49a6c");
            k.Box(new Vector3(0, 0, -0.15f), new Vector3(0.05f, 0.05f, 0.4f), cork);
            k.Box(new Vector3(0, -0.06f, 0.08f), new Vector3(0.11f, 0.11f, 0.08f), MeshKit.Hex("#c0c7cf")); // the reel
            var blank = MeshKit.Hex(hex);
            int segments = 6;
            for (int s = 0; s < segments; s++)
            {
                float z0 = 0.05f + s * 2.15f / segments, z1 = z0 + 2.15f / segments;
                float r0 = Mathf.Lerp(0.022f, 0.006f, s / (float)segments), r1 = Mathf.Lerp(0.022f, 0.006f, (s + 1f) / segments);
                k.Box(new Vector3(0, 0, (z0 + z1) / 2), new Vector3(r0 + r1, r0 + r1, z1 - z0), s % 2 == 0 ? blank : MeshKit.Shade(blank, 0.85f));
            }
            return k.Build("rod");
        }

        public void UpdateView(Fishing f, PlayerController player, float dt)
        {
            // Rod colour follows your equipped rod.
            var eq = Game.I.Profile.equipped["rod"];
            if (eq != rodId)
            {
                rodId = eq;
                float hue = (Noise.SeedFrom(eq) % 1000) / 1000f;
                rodPivot.GetComponent<MeshFilter>().sharedMesh = RodMesh("#" + ColorUtility.ToHtmlStringRGB(Color.HSVToRGB(hue, 0.45f, 0.55f)));
            }

            // Bend and raise the rod: up while charging, flicked forward on a cast, pulled down by tension.
            float charge = Mathf.Max(0, f.Charge);
            if (f.State == LineState.Casting) castKick = Mathf.MoveTowards(castKick, 1, dt * 6);
            else castKick = Mathf.MoveTowards(castKick, 0, dt * 3);
            float pitch = -35 - charge * 40 + castKick * 30 + f.Tension * 25 + (f.Pulling ? Mathf.Sin(Time.time * 25) * 2 : 0);
            float yaw = -8 + (f.Pulling ? Mathf.Sin(Time.time * 7) * 4 : 0);
            rodPivot.localRotation = Quaternion.Slerp(rodPivot.localRotation, Quaternion.Euler(pitch, yaw, 0), 1 - Mathf.Exp(-12 * dt));

            bool out_ = f.State != LineState.Idle;
            bobber.SetActive(out_);
            line.enabled = out_;
            if (out_)
            {
                bobber.transform.position = f.BobberPos;
                var a = tip.position;
                var b = f.BobberPos + Vector3.up * 0.1f;
                float sag = f.State == LineState.Reeling ? Mathf.Lerp(0.6f, 0, f.Tension * 1.5f) : f.State == LineState.Casting ? 0.2f : 1.2f;
                for (int i = 0; i < line.positionCount; i++)
                {
                    float t = i / (line.positionCount - 1f);
                    var p = Vector3.Lerp(a, b, t);
                    p.y -= Mathf.Sin(t * Mathf.PI) * sag * Mathf.Clamp01(Vector3.Distance(a, b) / 10f);
                    line.SetPosition(i, p);
                }
                line.startColor = line.endColor = Color.Lerp(new Color(0.95f, 0.95f, 0.9f, 0.8f), new Color(1, 0.3f, 0.25f, 0.95f), f.State == LineState.Reeling ? f.Tension : 0);
            }

            // A fish you just landed, held up in front of you.
            if (heldFish)
            {
                holdTimer -= dt;
                heldFish.transform.localRotation = Quaternion.Euler(0, 90 + Mathf.Sin(Time.time * 9) * 12 * Mathf.Clamp01(holdTimer), Mathf.Sin(Time.time * 5) * 8);
                if (holdTimer <= 0) Destroy(heldFish);
            }
        }

        public void ShowFish(Species s, float kg, string tier)
        {
            if (heldFish) Destroy(heldFish);
            heldFish = new GameObject("Held fish");
            heldFish.transform.SetParent(transform, false);
            float length = Mathf.Clamp(0.18f + 0.11f * Mathf.Pow(kg, 1 / 3f), 0.2f, 1.1f);
            heldFish.transform.localPosition = new Vector3(0, -0.12f, 0.55f + length * 0.4f);
            heldFish.transform.localScale = Vector3.one * length;
            heldFish.AddComponent<MeshFilter>().sharedMesh = FishMesh(s);
            var r = heldFish.AddComponent<MeshRenderer>();
            r.sharedMaterial = WorldBuilder.LitMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            holdTimer = tier != null ? 4f : 2.6f;
        }

        /// <summary>A low-poly fish, coloured by species (and edged in its rarity's colour).</summary>
        public static Mesh FishMesh(Species s)
        {
            if (fishMeshes.TryGetValue(s.id, out var m)) return m;
            var rnd = new SeededRandom(Noise.SeedFrom("fish", s.id));
            var body = Color.HSVToRGB(rnd.Next(), 0.25f + rnd.Next() * 0.45f, 0.45f + rnd.Next() * 0.45f);
            Color32 bodyC = body, belly = Color.Lerp(body, Color.white, 0.55f);
            Color32 fin = GameData.I.Rarity(s.rarity).Color;
            // The Blender body for its family (junk has its own: the boot, the chest), in this species' colours.
            var model = Models.Named("fish", s.family == "junk" ? "fish_" + s.id : "fish_" + s.family);
            if (model)
            {
                m = Models.Recolour(model, bodyC, belly, fin);
                fishMeshes[s.id] = m;
                return m;
            }
            float slim = s.family == "pike" || s.family == "ancient" ? 0.55f : s.family == "panfish" ? 1.2f : 0.85f;
            var k = new MeshKit();
            k.Blob(new Vector3(0, 0, 0), new Vector3(0.12f, 0.17f * slim, 0.45f), bodyC, rnd, 0.06f);
            k.Blob(new Vector3(0, -0.05f * slim, 0.02f), new Vector3(0.1f, 0.1f * slim, 0.36f), belly, rnd, 0.05f);
            // Tail and fins.
            k.Tri(new Vector3(0, 0, -0.4f), new Vector3(0, 0.16f, -0.62f), new Vector3(0, -0.16f, -0.62f), fin);
            k.Tri(new Vector3(0, 0, -0.4f), new Vector3(0, -0.16f, -0.62f), new Vector3(0, 0.16f, -0.62f), fin);
            k.Tri(new Vector3(0, 0.15f * slim, 0.1f), new Vector3(0, 0.26f * slim, -0.12f), new Vector3(0, 0.14f * slim, -0.22f), fin);
            k.Tri(new Vector3(0, 0.15f * slim, 0.1f), new Vector3(0, 0.14f * slim, -0.22f), new Vector3(0, 0.26f * slim, -0.12f), fin);
            // Eyes.
            k.Box(new Vector3(0.1f, 0.04f, 0.3f), new Vector3(0.03f, 0.04f, 0.04f), new Color32(20, 20, 20, 255));
            k.Box(new Vector3(-0.1f, 0.04f, 0.3f), new Vector3(0.03f, 0.04f, 0.04f), new Color32(20, 20, 20, 255));
            m = k.Build(s.id);
            fishMeshes[s.id] = m;
            return m;
        }
    }
}
