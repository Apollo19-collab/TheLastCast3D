// A shop stall: a wooden counter under a striped awning in the shop's colours,
// a sign, and its goods (bait buckets, or rods on a rack).

using UnityEngine;

namespace LastCast
{
    public static class ShopStall
    {
        static Font font;
        public static Font Font => font ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        static Material textMaterial;

        /// <summary>For text out in the world: hidden behind things and seen from the front only (Shaders/WorldText).</summary>
        public static Material TextMaterial
        {
            get
            {
                if (textMaterial) return textMaterial;
                textMaterial = new Material(Resources.Load<Shader>("Shaders/WorldText")) { mainTexture = Font.material.mainTexture };
                // The font's texture is rebuilt as it meets new letters.
                Font.textureRebuilt += f => { if (f == font && textMaterial) textMaterial.mainTexture = f.material.mainTexture; };
                return textMaterial;
            }
        }

        public static void Create(Transform parent, Shop shop)
        {
            var map = WorldMap.I;
            // The solid footprint in world.js is 6 x 4 m, a little north of the shop's point.
            var centre = map.ToUnity(shop.x, shop.y - 5);
            centre.y = map.GroundHeight(centre.x, centre.z);
            var root = new GameObject(shop.name);
            root.transform.SetParent(parent, false);
            root.transform.position = centre;

            var k = new MeshKit();
            var wood = MeshKit.Hex("#8a5a33");
            var dark = MeshKit.Hex("#5b3c22");
            Color32 a0 = MeshKit.Hex(shop.awning?[0] ?? "#e63946"), a1 = MeshKit.Hex(shop.awning?[1] ?? "#f1faee");
            // Floor, counter (facing south, -Z) and back wall.
            k.Box(new Vector3(0, 0.05f, 0), new Vector3(6, 0.2f, 4), MeshKit.Shade(wood, 0.8f));
            k.Box(new Vector3(0, 0.6f, -1.4f), new Vector3(5.6f, 1.1f, 0.9f), wood, MeshKit.Hex("#b5865a"));
            k.Box(new Vector3(0, 1.4f, 1.85f), new Vector3(5.8f, 2.7f, 0.2f), MeshKit.Shade(wood, 0.85f));
            // Posts.
            foreach (var x in new[] { -2.8f, 2.8f })
                foreach (var z in new[] { -1.85f, 1.85f })
                    k.Box(new Vector3(x, 1.5f, z), new Vector3(0.22f, 3f, 0.22f), dark);
            // Striped awning sloping down to the front.
            int stripes = 8;
            for (int i = 0; i < stripes; i++)
            {
                float x0 = -3.2f + i * 6.4f / stripes, x1 = x0 + 6.4f / stripes;
                var c = i % 2 == 0 ? a0 : a1;
                Vector3 fl = new Vector3(x0, 2.75f, -2.4f), fr = new Vector3(x1, 2.75f, -2.4f), bl = new Vector3(x0, 3.25f, 2.1f), br = new Vector3(x1, 3.25f, 2.1f);
                k.Quad(fl, bl, br, fr, c);
                k.Quad(fl, fr, br, bl, MeshKit.Shade(c, 0.7f)); // underside
                k.Quad(new Vector3(x0, 2.45f, -2.4f), fl, fr, new Vector3(x1, 2.45f, -2.4f), MeshKit.Shade(c, 0.9f)); // valance
            }
            if (shop.IsTackle)
            {
                // A rack of rods beside the counter, and reels on it.
                k.Box(new Vector3(3.6f, 0.9f, -0.6f), new Vector3(0.2f, 1.8f, 1.6f), dark);
                for (int r = 0; r < 4; r++)
                {
                    var col = r % 2 == 0 ? a0 : MeshKit.Hex("#2b2d42");
                    k.Cone(new Vector3(3.75f, 0, -1.2f + r * 0.4f), 0.04f, 3.2f, 4, col, 0.02f);
                }
                for (int r = 0; r < 3; r++) k.Box(new Vector3(-1.6f + r * 0.8f, 1.27f, -1.4f), new Vector3(0.35f, 0.25f, 0.35f), r == 1 ? a1 : a0);
            }
            else
            {
                Color32[] fills = { MeshKit.Hex("#d97c8a"), MeshKit.Hex("#ffd60a"), MeshKit.Hex("#7a2e3a") };
                for (int b = 0; b < 3; b++)
                {
                    k.Cone(new Vector3(-1.6f + b * 0.8f, 1.15f, -1.4f), 0.28f, 0.35f, 8, MeshKit.Hex("#6c757d"), 0.32f);
                    k.Cone(new Vector3(-1.6f + b * 0.8f, 1.48f, -1.4f), 0.3f, 0.02f, 8, fills[b], 0.3f);
                }
                k.Cone(new Vector3(3.6f, 0, -1f), 0.45f, 1.1f, 8, wood, 0.45f); // a barrel
            }
            // The Blender stall (bait buckets or a rod rack), with this shop's awning colours, if there is one.
            var model = Models.Get("stall", shop.IsTackle ? 1 : 0);
            var mesh = model ? Models.Recolour(model, a0, a1) : k.Build(shop.id);
            var body = new GameObject("Stall");
            body.transform.SetParent(root.transform, false);
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            body.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;

            // The sign over the awning.
            var sign = new GameObject("Sign");
            sign.transform.SetParent(root.transform, false);
            sign.transform.localPosition = new Vector3(0, 3.9f, -2.2f);
            var tm = sign.AddComponent<TextMesh>();
            tm.text = shop.IsTackle ? shop.name : "BAIT SHOP";
            tm.font = Font;
            tm.fontSize = 64;
            tm.characterSize = 0.05f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = new Color(1f, 0.95f, 0.84f);
            sign.GetComponent<MeshRenderer>().sharedMaterial = TextMaterial;
            // Text meshes read from the -Z side, which is the counter's (south) side.
            var board = new MeshKit();
            board.Box(Vector3.zero, new Vector3(Mathf.Max(2.4f, tm.text.Length * 0.2f), 0.7f, 0.08f), MeshKit.Hex("#3a2410"));
            var boardGo = new GameObject("Board");
            boardGo.transform.SetParent(root.transform, false);
            boardGo.transform.localPosition = new Vector3(0, 3.9f, -2.15f);
            boardGo.AddComponent<MeshFilter>().sharedMesh = board.Build("board");
            boardGo.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
        }
    }
}
