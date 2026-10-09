// Builds flat-shaded, vertex-coloured meshes: every triangle gets its own
// vertices and normal, which gives the faceted low-poly look.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LastCast
{
    public class MeshKit
    {
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector3> normals = new List<Vector3>();
        readonly List<Color32> colors = new List<Color32>();
        readonly List<int> tris = new List<int>();

        public int VertexCount => verts.Count;

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color32 color)
        {
            var n = Vector3.Cross(b - a, c - a).normalized;
            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            normals.Add(n); normals.Add(n); normals.Add(n);
            colors.Add(color); colors.Add(color); colors.Add(color);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 color)
        {
            Tri(a, b, c, color);
            Tri(a, c, d, color);
        }

        /// <summary>An axis-aligned box from its centre and size.</summary>
        public void Box(Vector3 centre, Vector3 size, Color32 color, Color32? top = null)
        {
            var h = size / 2;
            Vector3 P(float x, float y, float z) => centre + new Vector3(x * h.x, y * h.y, z * h.z);
            Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), top ?? color); // top
            Quad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), Shade(color, 0.6f)); // bottom
            Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), Shade(color, 0.9f)); // south
            Quad(P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1), Shade(color, 0.9f)); // north
            Quad(P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1), Shade(color, 0.8f)); // west
            Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), Shade(color, 0.8f)); // east
        }

        /// <summary>A cone (or a cylinder when topRadius > 0) standing on `base`.</summary>
        public void Cone(Vector3 bottom, float radius, float height, int sides, Color32 color, float topRadius = 0, float twist = 0)
        {
            var top = bottom + Vector3.up * height;
            for (int s = 0; s < sides; s++)
            {
                float a0 = (s / (float)sides) * Mathf.PI * 2 + twist, a1 = ((s + 1) / (float)sides) * Mathf.PI * 2 + twist;
                var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                var c = s % 2 == 0 ? color : Shade(color, 0.88f);
                if (topRadius <= 0) Tri(bottom + d1 * radius, bottom + d0 * radius, top, c);
                else Quad(bottom + d1 * radius, bottom + d0 * radius, top + d0 * topRadius, top + d1 * topRadius, c);
            }
        }

        /// <summary>A lumpy low-poly ball (an octahedron subdivided once, with its points jittered).</summary>
        public void Blob(Vector3 centre, Vector3 radius, Color32 color, SeededRandom rnd, float lumpiness = 0.18f)
        {
            Vector3[] baseV = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            int[,] faces = { { 0, 4, 3 }, { 0, 3, 5 }, { 0, 5, 2 }, { 0, 2, 4 }, { 1, 3, 4 }, { 1, 5, 3 }, { 1, 2, 5 }, { 1, 4, 2 } };
            var cache = new Dictionary<Vector3, Vector3>();
            Vector3 J(Vector3 v)
            {
                v = v.normalized;
                if (!cache.TryGetValue(v, out var j)) cache[v] = j = v * (1 + (rnd.Next() - 0.5f) * 2 * lumpiness);
                return centre + Vector3.Scale(j, radius);
            }
            for (int f = 0; f < 8; f++)
            {
                Vector3 a = baseV[faces[f, 0]], b = baseV[faces[f, 1]], c = baseV[faces[f, 2]];
                Vector3 ab = (a + b) / 2, bc = (b + c) / 2, ca = (c + a) / 2;
                var col = Shade(color, 0.9f + rnd.Next() * 0.2f);
                Tri(J(a), J(ab), J(ca), col);
                Tri(J(ab), J(b), J(bc), col);
                Tri(J(ca), J(bc), J(c), col);
                Tri(J(ab), J(bc), J(ca), Shade(col, 0.95f));
            }
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            if (verts.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.SetColors(colors);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        public static Color32 Shade(Color32 c, float k) =>
            new Color32((byte)Mathf.Clamp(c.r * k, 0, 255), (byte)Mathf.Clamp(c.g * k, 0, 255), (byte)Mathf.Clamp(c.b * k, 0, 255), c.a);

        public static Color32 Hex(string hex, byte alpha = 255)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            Color32 c32 = c;
            c32.a = alpha;
            return c32;
        }
    }
}
