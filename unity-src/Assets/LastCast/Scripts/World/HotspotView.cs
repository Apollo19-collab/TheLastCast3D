// A hotspot on the water, as the 2D game draws it: a warm glow of churned-up
// water, a slowly turning dashed ring, a small school circling under the
// surface, and bubbles popping up.

using UnityEngine;

namespace LastCast
{
    public class HotspotView : MonoBehaviour
    {
        static Mesh glow, ring, fishShadow, bubble;
        float radius, seed;
        Transform ringT;
        readonly Transform[] fish = new Transform[5];
        readonly Transform[] bubbles = new Transform[7];
        readonly MeshRenderer[] bubbleR = new MeshRenderer[7];
        MaterialPropertyBlock mpb;

        public static GameObject Create(Vector3 centre, float radius)
        {
            var go = new GameObject("Hotspot");
            go.transform.position = new Vector3(centre.x, 0.08f, centre.z);
            var v = go.AddComponent<HotspotView>();
            v.radius = radius;
            v.seed = Random.value * 100;
            v.Build();
            return go;
        }

        static GameObject Part(Transform parent, string name, Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = RodView.Unlit;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        void Build()
        {
            glow ??= GlowMesh();
            ring ??= RingMesh();
            fishShadow ??= FishShadowMesh();
            bubble ??= BubbleMesh();
            var g = Part(transform, "Glow", glow);
            g.transform.localScale = new Vector3(radius, 1, radius);
            ringT = Part(transform, "Ring", ring).transform;
            ringT.localScale = new Vector3(radius * 0.92f, 1, radius * 0.92f);
            for (int i = 0; i < fish.Length; i++) fish[i] = Part(transform, "Fish", fishShadow).transform;
            for (int i = 0; i < bubbles.Length; i++)
            {
                var b = Part(transform, "Bubble", bubble);
                bubbles[i] = b.transform;
                bubbleR[i] = b.GetComponent<MeshRenderer>();
            }
            mpb = new MaterialPropertyBlock();
        }

        // Warm glow: gold in the middle, fading to nothing at the edge.
        static Mesh GlowMesh()
        {
            var m = new Mesh { name = "hotspot glow" };
            int n = 32;
            var v = new Vector3[n + 1];
            var c = new Color32[n + 1];
            var t = new int[n * 3];
            v[0] = Vector3.zero;
            c[0] = new Color32(255, 230, 140, 70);
            for (int i = 0; i < n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2;
                v[i + 1] = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                c[i + 1] = new Color32(255, 230, 140, 0);
                t[i * 3] = 0;
                t[i * 3 + 1] = 1 + (i + 1) % n;
                t[i * 3 + 2] = 1 + i;
            }
            m.vertices = v;
            m.colors32 = c;
            m.triangles = t;
            return m;
        }

        static Mesh RingMesh()
        {
            var k = new MeshKit();
            var col = new Color32(255, 240, 170, 160);
            int n = 26;
            for (int i = 0; i < n; i++)
            {
                float a0 = i / (float)n * Mathf.PI * 2, a1 = (i + 0.45f) / n * Mathf.PI * 2;
                var p0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                var p1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                k.Quad(p0 * 0.985f, p0 * 1.015f, p1 * 1.015f, p1 * 0.985f, col);
            }
            return k.Build("hotspot ring");
        }

        static Mesh FishShadowMesh()
        {
            var k = new MeshKit();
            var c = new Color32(10, 30, 40, 115);
            int n = 10;
            for (int i = 0; i < n; i++)
            {
                float a0 = i / (float)n * Mathf.PI * 2, a1 = (i + 1) / (float)n * Mathf.PI * 2;
                k.Tri(Vector3.zero, new Vector3(Mathf.Cos(a1) * 0.2f, 0, Mathf.Sin(a1) * 0.5f), new Vector3(Mathf.Cos(a0) * 0.2f, 0, Mathf.Sin(a0) * 0.5f), c);
            }
            k.Tri(new Vector3(0, 0, -0.4f), new Vector3(0.25f, 0, -0.8f), new Vector3(-0.25f, 0, -0.8f), c);
            return k.Build("fish shadow");
        }

        static Mesh BubbleMesh()
        {
            var k = new MeshKit();
            var c = new Color32(230, 250, 255, 205);
            int n = 10;
            for (int i = 0; i < n; i++)
            {
                float a0 = i / (float)n * Mathf.PI * 2, a1 = (i + 1) / (float)n * Mathf.PI * 2;
                var p0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                var p1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                k.Quad(p0 * 0.8f, p0, p1, p1 * 0.8f, c);
            }
            return k.Build("bubble");
        }

        void Update()
        {
            float t = Time.time;
            ringT.localRotation = Quaternion.Euler(0, t * 14 + seed, 0);
            for (int i = 0; i < fish.Length; i++)
            {
                float a = t / 1.4f + i / (float)fish.Length * Mathf.PI * 2 + seed;
                float rr = radius * (0.35f + 0.12f * Mathf.Sin(i * 2.1f + t / 0.9f));
                fish[i].localPosition = new Vector3(Mathf.Cos(a) * rr, -0.12f, Mathf.Sin(a) * rr);
                // Swim round the circle, nose first.
                fish[i].localRotation = Quaternion.LookRotation(new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a)));
            }
            for (int i = 0; i < bubbles.Length; i++)
            {
                float cycle = Mathf.Repeat(t / 1.1f + i * 0.37f + seed * 0.13f, 1);
                float a = i * 2.4f + seed;
                float d = radius * 0.6f * Mathf.Repeat(i * 0.37f, 1);
                bubbles[i].localPosition = new Vector3(Mathf.Cos(a) * d, 0.02f, Mathf.Sin(a) * d);
                bubbles[i].localScale = Vector3.one * (0.1f + cycle * 0.3f);
                mpb.SetColor("_Color", new Color(1, 1, 1, 1 - cycle));
                bubbleR[i].SetPropertyBlock(mpb);
            }
        }
    }
}
