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
        float holdTimer, whip = 9;
        LineState lastState;
        Transform hand;
        string handLook = "?";
        LimbRig fishRig;
        static readonly Vector3 RestPos = new Vector3(0.28f, -0.32f, 0.45f), WoundPos = new Vector3(0.34f, -0.1f, 0.22f);
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
            hand = new GameObject("Hand").transform;
            hand.SetParent(rodPivot, false);
            hand.gameObject.AddComponent<MeshFilter>();
            var hr = hand.gameObject.AddComponent<MeshRenderer>();
            hr.sharedMaterial = WorldBuilder.LitMaterial;
            hr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

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

        /// <summary>Your right hand round the grip (just behind the reel, thumb on top), and the sleeve running back out of sight.</summary>
        static Mesh HandMesh(Color32 jacket)
        {
            var k = new MeshKit();
            var skin = MeshKit.Hex("#f1c27d");
            var rnd = new SeededRandom(3);
            k.Blob(new Vector3(0, -0.006f, -0.03f), new Vector3(0.045f, 0.05f, 0.06f), skin, rnd, 0.05f);
            k.Blob(new Vector3(0, 0.036f, -0.008f), new Vector3(0.016f, 0.016f, 0.032f), MeshKit.Shade(skin, 0.95f), rnd, 0.05f);
            k.Box(new Vector3(0.008f, -0.036f, -0.11f), new Vector3(0.085f, 0.085f, 0.06f), MeshKit.Shade(jacket, 0.82f));
            for (int i = 0; i < 3; i++)
                k.Box(new Vector3(0.02f + i * 0.015f, -0.06f - i * 0.05f, -0.2f - i * 0.1f), new Vector3(0.1f + i * 0.01f, 0.1f + i * 0.01f, 0.13f), jacket);
            return k.Build("hand");
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

            // Your hand on the grip, in your jacket's colour.
            var prof = Game.I.Profile;
            prof.armour.TryGetValue("body", out var bodyPiece);
            string look = bodyPiece != null ? GameData.I.Piece(bodyPiece).set?.id : null;
            if (look != handLook || !hand.GetComponent<MeshFilter>().sharedMesh)
            {
                handLook = look;
                hand.GetComponent<MeshFilter>().sharedMesh = HandMesh(AnglerMesh.Jacket(look, "#e4572e"));
            }

            // The cast: drawn back over your shoulder as you charge, whipped forward when you let go, then held
            // out (pulled down by the line's tension, shaking as a fish pulls).
            if (f.State == LineState.Casting && lastState != LineState.Casting) whip = 0;
            lastState = f.State;
            whip += dt;
            float c = Mathf.SmoothStep(0, 1, Mathf.Max(0, f.Charge));
            var pos = Vector3.Lerp(RestPos, WoundPos, c);
            float pitch = Mathf.Lerp(-35, -118, c), yaw = Mathf.Lerp(-8, -14, c), roll = Mathf.Lerp(0, -12, c);
            if (f.State != LineState.Idle) pitch = -28 + f.Tension * 25 + (f.Pulling ? Mathf.Sin(Time.time * 25) * 2 : 0);
            if (f.Pulling) yaw += Mathf.Sin(Time.time * 7) * 4;
            float rate = 1 - Mathf.Exp(-12 * dt);
            if (whip < 0.7f && f.Charge < 0)
            {
                float a = Mathf.Clamp01(whip / 0.16f), b = Mathf.SmoothStep(0, 1, Mathf.Clamp01((whip - 0.16f) / 0.5f));
                pitch = Mathf.Lerp(Mathf.Lerp(-118, 8, a), pitch, b);
                yaw = Mathf.Lerp(Mathf.Lerp(-14, -6, a), yaw, b);
                roll = Mathf.Lerp(-12, 0, a);
                pos = Vector3.Lerp(Vector3.Lerp(WoundPos, RestPos + new Vector3(0, -0.04f, 0.14f), a), RestPos, b);
                rate = 1 - Mathf.Exp(-45 * dt);
            }
            // Walking sways it with your steps.
            float stride = player ? player.Striding : 0, step = player ? player.Bob : 0;
            pos += new Vector3(Mathf.Sin(step) * 0.014f, -Mathf.Abs(Mathf.Cos(step)) * 0.018f, 0) * stride;
            roll += Mathf.Sin(step) * 2.5f * stride;
            pitch += Mathf.Cos(step * 2) * 1.2f * stride;
            rodPivot.localPosition = Vector3.Lerp(rodPivot.localPosition, pos, rate);
            rodPivot.localRotation = Quaternion.Slerp(rodPivot.localRotation, Quaternion.Euler(pitch, yaw, roll), rate);

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

            // A fish you just landed, held up in front of you, flapping its tail (less and less as it tires).
            if (heldFish)
            {
                holdTimer -= dt;
                float life = Mathf.Clamp01(holdTimer);
                heldFish.transform.localRotation = Quaternion.Euler(0, 90 + Mathf.Sin(Time.time * 9) * 6 * life, Mathf.Sin(Time.time * 5) * 8);
                var body = fishRig ? fishRig[1] : null;
                if (body != null)
                    for (int i = 0; i < body.Count; i++)
                    {
                        float s = i / Mathf.Max(1f, body.Count - 1);
                        body.Bones[i].localRotation = Quaternion.AngleAxis(Mathf.Sin(Time.time * 13 - i * 0.9f) * (2 + 16 * s * s) * (0.25f + 0.75f * life), Vector3.up);
                    }
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
            var mesh = FishMesh(s);
            fishRig = LimbRig.Attach(heldFish, mesh, WorldBuilder.LitMaterial, 6);
            Renderer r = fishRig ? fishRig.Renderer : null;
            if (!r)
            {
                heldFish.AddComponent<MeshFilter>().sharedMesh = mesh;
                r = heldFish.AddComponent<MeshRenderer>();
                r.sharedMaterial = WorldBuilder.LitMaterial;
            }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            holdTimer = tier != null ? 4f : 2.6f;
        }

        /// <summary>A species' low-poly model (Blender, in its own colours), or a simple fish coloured by species and edged in its rarity's colour.</summary>
        public static Mesh FishMesh(Species s)
        {
            if (fishMeshes.TryGetValue(s.id, out var m)) return m;
            var model = Models.Named("fish", "fish_" + s.id);
            if (model) return fishMeshes[s.id] = model;
            var rnd = new SeededRandom(Noise.SeedFrom("fish", s.id));
            var body = Color.HSVToRGB(rnd.Next(), 0.25f + rnd.Next() * 0.45f, 0.45f + rnd.Next() * 0.45f);
            Color32 bodyC = body, belly = Color.Lerp(body, Color.white, 0.55f);
            Color32 fin = GameData.I.Rarity(s.rarity).Color;
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
