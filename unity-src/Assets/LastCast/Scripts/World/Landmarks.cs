// Landmarks from the 2D game, in 3D: wooden signposts with each area's name
// (the 2D map paints them on the ground), the voyage boat moored at the South
// Beach dock (red hull, wooden deck, cream rails), and the Travelling Zoo's
// striped wagon (purple and cream, with a flag on top).

using UnityEngine;

namespace LastCast
{
    public static class Signpost
    {
        public static void Create(Transform parent, Vector3 at, string name)
        {
            var map = WorldMap.I;
            at.y = map.GroundHeight(at.x, at.z);
            var root = new GameObject($"Sign: {name}");
            root.transform.SetParent(parent, false);
            root.transform.position = at;
            // Face the spawn, so the names read as you walk out into the world.
            var spawn = map.ToUnity(GameData.I.spawn.x, GameData.I.spawn.y);
            var to = spawn - at;
            to.y = 0;
            if (to.sqrMagnitude > 1) root.transform.rotation = Quaternion.LookRotation(-to.normalized);

            var k = new MeshKit();
            var wood = MeshKit.Hex("#8a5a33");
            var dark = MeshKit.Hex("#5b3c22");
            float width = Mathf.Max(2.2f, name.Length * 0.2f + 0.6f);
            k.Box(new Vector3(0, 1.4f, 0), new Vector3(0.18f, 2.8f, 0.18f), dark);
            k.Box(new Vector3(0, 2.35f, 0), new Vector3(width, 0.6f, 0.1f), wood, MeshKit.Hex("#b5865a"));
            k.Box(new Vector3(0, 2.35f, 0), new Vector3(width + 0.12f, 0.08f, 0.12f), dark);
            k.Box(new Vector3(0, 2.0f, 0), new Vector3(width * 0.5f, 0.06f, 0.11f), dark);
            var post = new GameObject("Post");
            post.transform.SetParent(root.transform, false);
            var postModel = Models.Get("signpost", 0);
            var boardModel = Models.Get("signpost", 1);
            post.AddComponent<MeshFilter>().sharedMesh = postModel && boardModel ? postModel : k.Build("signpost");
            post.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            if (postModel && boardModel)
            {
                // The Blender board is 1 m wide: stretch it to fit the name.
                var board = new GameObject("Board");
                board.transform.SetParent(root.transform, false);
                board.transform.localPosition = new Vector3(0, 2.35f, 0);
                board.transform.localScale = new Vector3(width, 1, 1);
                board.AddComponent<MeshFilter>().sharedMesh = boardModel;
                board.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            }
            foreach (int side in new[] { -1, 1 })
            {
                var text = new GameObject("Name");
                text.transform.SetParent(root.transform, false);
                text.transform.localPosition = new Vector3(0, 2.35f, 0.06f * side);
                text.transform.localRotation = Quaternion.Euler(0, side > 0 ? 180 : 0, 0);
                var tm = text.AddComponent<TextMesh>();
                tm.text = name.ToUpper();
                tm.font = ShopStall.Font;
                tm.fontSize = 64;
                tm.fontStyle = FontStyle.Bold;
                tm.characterSize = 0.045f;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.color = new Color(1f, 0.95f, 0.84f);
                text.GetComponent<MeshRenderer>().sharedMaterial = ShopStall.Font.material;
            }
        }
    }

    public static class BoatMesh
    {
        /// <summary>A fishing boat, bow towards +Z: hull, deck, rails and a wheelhouse.</summary>
        public static Mesh Build(float length, float beam)
        {
            Color32 hull = MeshKit.Hex("#7a2e2e"), hullDark = MeshKit.Hex("#4a1a1a"), deck = MeshKit.Hex("#b5865a"),
                rail = MeshKit.Hex("#efe0bd"), cabin = MeshKit.Hex("#f1ece0"), glass = MeshKit.Hex("#2b3a44");
            var k = new MeshKit();
            float L = length / 2, B = beam / 2, deckY = 1.3f, keelY = -1f;
            // Deck outline, going round anticlockwise seen from above.
            Vector3[] outline =
            {
                new Vector3(-B, deckY, -L), new Vector3(B, deckY, -L), new Vector3(B, deckY, L * 0.25f),
                new Vector3(B * 0.55f, deckY + 0.3f, L * 0.75f), new Vector3(0, deckY + 0.6f, L),
                new Vector3(-B * 0.55f, deckY + 0.3f, L * 0.75f), new Vector3(-B, deckY, L * 0.25f),
            };
            var centre = new Vector3(0, deckY, 0);
            for (int i = 0; i < outline.Length; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Length];
                // Hull side down to the keel, narrower at the bottom.
                var ka = new Vector3(a.x * 0.55f, keelY, a.z * 0.92f);
                var kb = new Vector3(b.x * 0.55f, keelY, b.z * 0.92f);
                k.Quad(a, b, kb, ka, i % 2 == 0 ? hull : MeshKit.Shade(hull, 0.9f));
                k.Tri(new Vector3(0, keelY, 0), ka, kb, hullDark);
                k.Tri(centre, b, a, deck);
                // Rail along the edge.
                var mid = (a + b) / 2 + Vector3.up * 0.45f;
                var len = Vector3.Distance(a, b);
                var dir = (b - a).normalized;
                var rot = Quaternion.LookRotation(dir);
                AddRotatedBox(k, mid, new Vector3(0.12f, 0.12f, len), rot, rail);
                for (float t = 0; t < len; t += 1.6f) AddRotatedBox(k, a + dir * t + Vector3.up * 0.22f, new Vector3(0.08f, 0.45f, 0.08f), rot, rail);
            }
            // Wheelhouse at the stern, with windows and a red roof.
            k.Box(new Vector3(0, deckY + 1.2f, -L * 0.45f), new Vector3(B * 1.2f, 2.4f, L * 0.4f), cabin);
            k.Box(new Vector3(0, deckY + 2.5f, -L * 0.45f), new Vector3(B * 1.35f, 0.2f, L * 0.46f), hull);
            for (int w = -1; w <= 1; w++)
                k.Box(new Vector3(w * B * 0.38f, deckY + 1.6f, -L * 0.45f + L * 0.2f + 0.01f), new Vector3(B * 0.28f, 0.7f, 0.05f), glass);
            // A mast with a little flag.
            k.Box(new Vector3(0, deckY + 3.5f, -L * 0.3f), new Vector3(0.15f, 4.5f, 0.15f), MeshKit.Hex("#5b3c22"));
            k.Box(new Vector3(0.45f, deckY + 5.3f, -L * 0.3f), new Vector3(0.8f, 0.45f, 0.03f), MeshKit.Hex("#e63946"));
            return k.Build("boat");
        }

        static void AddRotatedBox(MeshKit k, Vector3 centre, Vector3 size, Quaternion rot, Color32 c)
        {
            var h = size / 2;
            Vector3 P(float x, float y, float z) => centre + rot * new Vector3(x * h.x, y * h.y, z * h.z);
            k.Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), c);
            k.Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), MeshKit.Shade(c, 0.9f));
            k.Quad(P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1), MeshKit.Shade(c, 0.9f));
            k.Quad(P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1), MeshKit.Shade(c, 0.8f));
            k.Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), MeshKit.Shade(c, 0.8f));
        }
    }

    public class ZooWagon : MonoBehaviour
    {
        Transform flag;

        public static GameObject Create(Transform parent, Vector3 at)
        {
            var root = new GameObject("Travelling Zoo");
            root.transform.SetParent(parent, false);
            root.transform.position = at;
            var spawn = WorldMap.I.ToUnity(GameData.I.spawn.x, GameData.I.spawn.y);
            var to = spawn - at;
            to.y = 0;
            if (to.sqrMagnitude > 1) root.transform.rotation = Quaternion.LookRotation(-to.normalized); // the sign side faces the spawn

            var k = new MeshKit();
            Color32 bed = MeshKit.Hex("#7a3e1d"), gold = MeshKit.Hex("#ffd166"), wheel = MeshKit.Hex("#3a2410"), hub = MeshKit.Hex("#b5865a"),
                purple = MeshKit.Hex("#9d4edd"), cream = MeshKit.Hex("#fdfcdc"), pole = MeshKit.Hex("#5a189a");
            // Wagon bed with a gold stripe, on four wheels.
            k.Box(new Vector3(0, 1.25f, 0), new Vector3(6.4f, 0.9f, 3f), bed);
            k.Box(new Vector3(0, 1.3f, 0), new Vector3(6.45f, 0.18f, 3.05f), gold);
            foreach (float x in new[] { -2.2f, 2.2f })
                foreach (float z in new[] { -1.6f, 1.6f })
                {
                    Wheel(k, new Vector3(x, 0.8f, z), 0.8f, 0.24f, wheel);
                    k.Box(new Vector3(x, 0.8f, z + 0.15f * Mathf.Sign(z)), new Vector3(0.35f, 0.35f, 0.1f), hub);
                }
            // Striped tent: eight panels up to a point.
            var apex = new Vector3(0, 5.6f, 0);
            Vector3[] corners = { new Vector3(-3.2f, 1.7f, -1.5f), new Vector3(3.2f, 1.7f, -1.5f), new Vector3(3.2f, 1.7f, 1.5f), new Vector3(-3.2f, 1.7f, 1.5f) };
            int stripe = 0;
            for (int side = 0; side < 4; side++)
            {
                var a = corners[side];
                var b = corners[(side + 1) % 4];
                for (int s = 0; s < 2; s++)
                {
                    var p0 = Vector3.Lerp(a, b, s / 2f);
                    var p1 = Vector3.Lerp(a, b, (s + 1) / 2f);
                    var c = stripe++ % 2 == 0 ? purple : cream;
                    k.Tri(p1, p0, apex, c);
                    k.Tri(p0, p1, apex, MeshKit.Shade(c, 0.7f)); // inside
                }
            }
            k.Box(new Vector3(0, 6.1f, 0), new Vector3(0.12f, 1.1f, 0.12f), pole);
            root.AddComponent<MeshFilter>().sharedMesh = Models.Or("zoo", 0, () => k.Build("zoo"));
            root.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;

            // The flag (it flaps) and the sign.
            var wagon = root.AddComponent<ZooWagon>();
            var fk = new MeshKit();
            fk.Tri(Vector3.zero, new Vector3(1.4f, -0.3f, 0), new Vector3(0, -0.8f, 0), gold);
            fk.Tri(Vector3.zero, new Vector3(0, -0.8f, 0), new Vector3(1.4f, -0.3f, 0), gold);
            var flagGo = new GameObject("Flag");
            flagGo.transform.SetParent(root.transform, false);
            flagGo.transform.localPosition = new Vector3(0.06f, 6.6f, 0);
            flagGo.AddComponent<MeshFilter>().sharedMesh = fk.Build("flag");
            flagGo.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            wagon.flag = flagGo.transform;

            var sign = new GameObject("Sign");
            sign.transform.SetParent(root.transform, false);
            sign.transform.localPosition = new Vector3(0, 1.25f, -1.56f);
            var tm = sign.AddComponent<TextMesh>();
            tm.text = "TRAVELLING ZOO";
            tm.font = ShopStall.Font;
            tm.fontSize = 64;
            tm.fontStyle = FontStyle.Bold;
            tm.characterSize = 0.04f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.color = new Color(1f, 0.82f, 0.4f);
            sign.GetComponent<MeshRenderer>().sharedMaterial = ShopStall.Font.material;
            return root;
        }

        /// <summary>A wheel turning about the Z axis.</summary>
        static void Wheel(MeshKit k, Vector3 c, float r, float thickness, Color32 col)
        {
            int n = 12;
            var front = Vector3.forward * thickness / 2;
            for (int i = 0; i < n; i++)
            {
                float a0 = i / (float)n * Mathf.PI * 2, a1 = (i + 1) / (float)n * Mathf.PI * 2;
                var p0 = c + new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0) * r;
                var p1 = c + new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0) * r;
                k.Tri(c + front, p0 + front, p1 + front, col);
                k.Tri(c - front, p1 - front, p0 - front, col);
                k.Quad(p0 - front, p1 - front, p1 + front, p0 + front, MeshKit.Shade(col, 0.8f));
            }
        }

        void Update()
        {
            if (flag) flag.localRotation = Quaternion.Euler(0, Mathf.Sin(Time.time * 5) * 18, 0);
        }
    }
}
