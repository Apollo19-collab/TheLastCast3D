// The look of the 2D game's UI (client/css/style.css), rebuilt for Unity's
// immediate-mode GUI: dark navy gradient panels with rounded corners and a
// faint border, pills for the status line and prompts, gold for levels and
// coins, cyan accents (#4cc9f0) for tabs and buy buttons, and small icons
// drawn in code.
//
// Every texture is generated once (anti-aliased signed-distance shapes) and
// nine-sliced, so panels stretch to any size without blurring their corners.

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public static class Skin
    {
        // ---- the 2D game's palette ----------------------------------------------------------------
        public static readonly Color Text = Hex("#f1f1f1");
        public static readonly Color Dim = new Color(241 / 255f, 241 / 255f, 241 / 255f, 0.68f);
        public static readonly Color Accent = Hex("#4cc9f0");
        public static readonly Color Accent2 = Hex("#3a86a8");
        public static readonly Color Gold = Hex("#ffd166");
        public static readonly Color Green = Hex("#7bd389");
        public static readonly Color Red = Hex("#ff6b6b");
        public static readonly Color Location = Hex("#ffe9a8");
        public static readonly Color PanelTop = new Color(20 / 255f, 38 / 255f, 50 / 255f, 0.92f);
        public static readonly Color PanelBottom = new Color(11 / 255f, 23 / 255f, 31 / 255f, 0.92f);
        public static readonly Color Border = new Color(1, 1, 1, 0.1f);

        public static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.white;
        public static string Tag(Color c) => "#" + ColorUtility.ToHtmlStringRGBA(c);

        // ---- fonts ---------------------------------------------------------------------------------
        static Font regular, heavy;
        public static Font Regular => regular ??= OsFont("Segoe UI", "Arial");
        public static Font Heavy => heavy ??= Regular; // drawn bold: Segoe UI Black doesn't render as a dynamic font

        static Font OsFont(params string[] names)
        {
            try { return Font.CreateDynamicFontFromOSFont(names, 16); }
            catch { return ShopStall.Font; }
        }

        // ---- shapes ----------------------------------------------------------------------------------
        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

        /// <summary>A rounded rectangle with a vertical gradient and an optional border, for nine-slicing.</summary>
        public static Texture2D Rounded(int radius, Color top, Color bottom, Color border, float borderWidth = 1, int height = 0, Color? leftStripe = null)
        {
            string key = $"r{radius}{top}{bottom}{border}{borderWidth}{height}{leftStripe}";
            if (cache.TryGetValue(key, out var t)) return t;
            int w = radius * 2 + 8, h = height > 0 ? height : radius * 2 + 8;
            t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = RoundedRectDistance(x + 0.5f, y + 0.5f, w, h, radius);
                    float inside = Mathf.Clamp01(0.5f - d);
                    float edge = Mathf.Clamp01(0.5f - Mathf.Abs(d + borderWidth / 2) + borderWidth / 2);
                    var fill = Color.Lerp(bottom, top, y / (h - 1f)); // textures start at the bottom
                    if (leftStripe.HasValue && x < 4) fill = leftStripe.Value;
                    var c = Color.Lerp(fill, new Color(border.r, border.g, border.b, 1), edge * border.a);
                    c.a = Mathf.Max(fill.a, edge * border.a) * inside;
                    px[y * w + x] = c;
                }
            t.SetPixels(px);
            t.Apply();
            cache[key] = t;
            return t;
        }

        static float RoundedRectDistance(float x, float y, float w, float h, float r)
        {
            float qx = Mathf.Abs(x - w / 2) - (w / 2 - r);
            float qy = Mathf.Abs(y - h / 2) - (h / 2 - r);
            return Mathf.Sqrt(Mathf.Max(qx, 0) * Mathf.Max(qx, 0) + Mathf.Max(qy, 0) * Mathf.Max(qy, 0)) + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
        }

        /// <summary>A horizontal gradient bar fill with rounded ends.</summary>
        public static Texture2D BarFill(Color left, Color right)
        {
            string key = $"bar{left}{right}";
            if (cache.TryGetValue(key, out var t)) return t;
            int w = 64, h = 8;
            t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = RoundedRectDistance(x + 0.5f, y + 0.5f, w, h, 4);
                    var c = Color.Lerp(left, right, x / (w - 1f));
                    c.a *= Mathf.Clamp01(0.5f - d);
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            cache[key] = t;
            return t;
        }

        /// <summary>A circle with a radial gradient (the level badge).</summary>
        public static Texture2D Badge()
        {
            if (cache.TryGetValue("badge", out var t)) return t;
            int s = 96;
            t = new Texture2D(s, s, TextureFormat.RGBA32, false);
            Color c0 = Hex("#fff3b0"), c1 = Gold, c2 = Hex("#e09f3e");
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = x + 0.5f - s / 2f, dy = y + 0.5f - s / 2f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float hd = Vector2.Distance(new Vector2(x, y), new Vector2(s * 0.35f, s * 0.7f)) / s;
                    var c = hd < 0.3f ? Color.Lerp(c0, c1, hd / 0.3f) : Color.Lerp(c1, c2, (hd - 0.3f) / 0.6f);
                    if (d > s / 2f - 4) c = Color.Lerp(c, new Color(0, 0, 0, 1), 0.45f); // dark ring
                    c.a = Mathf.Clamp01(s / 2f - d);
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            cache["badge"] = t;
            return t;
        }

        /// <summary>The catch card's background: lighter in the middle, with a border in the rarity's colour.</summary>
        public static Texture2D CatchCard(Color rarity, float borderWidth)
        {
            string key = $"card{rarity}{borderWidth}";
            if (cache.TryGetValue(key, out var t)) return t;
            int w = 340, h = 230, r = 16;
            t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            Color inner = new Color(40 / 255f, 70 / 255f, 85 / 255f, 0.97f), outer = new Color(12 / 255f, 24 / 255f, 30 / 255f, 0.97f);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float d = RoundedRectDistance(x + 0.5f, y + 0.5f, w, h, r);
                    float ex = (x - w / 2f) / (w / 2f), ey = (y - h * 0.65f) / (h * 0.6f);
                    var c = Color.Lerp(inner, outer, Mathf.Clamp01(Mathf.Sqrt(ex * ex + ey * ey)));
                    float edge = Mathf.Clamp01(0.5f - Mathf.Abs(d + borderWidth / 2) + borderWidth / 2);
                    c = Color.Lerp(c, rarity, edge);
                    c.a = Mathf.Clamp01(0.5f - d) * Mathf.Max(c.a, edge);
                    px[y * w + x] = c;
                }
            t.SetPixels(px);
            t.Apply();
            cache[key] = t;
            return t;
        }

        /// <summary>Light rays (the catch card behind rare fish): alternating wedges, faded to the edge.</summary>
        public static Texture2D Rays(Color c)
        {
            string key = $"rays{c}";
            if (cache.TryGetValue(key, out var t)) return t;
            int s = 256;
            t = new Texture2D(s, s, TextureFormat.RGBA32, false);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = x - s / 2f, dy = y - s / 2f;
                    float a = (Mathf.Atan2(dy, dx) * Mathf.Rad2Deg + 360) % 24;
                    float on = Mathf.Clamp01(Mathf.Min(a, 10 - a) * 0.6f + 0.5f) * (a < 10 ? 1 : 0);
                    float fade = 1 - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / (s / 2f));
                    t.SetPixel(x, y, new Color(c.r, c.g, c.b, 0.22f * on * fade));
                }
            t.Apply();
            cache[key] = t;
            return t;
        }

        // ---- icons (64 x 64, drawn with signed distances) ----------------------------------------------

        public static Texture2D Icon(string name)
        {
            string key = "icon " + name;
            if (cache.TryGetValue(key, out var t)) return t;
            var p = new Painter(64);
            switch (name)
            {
                case "coin":
                    p.Circle(32, 32, 26, Hex("#e09f3e"));
                    p.Circle(32, 32, 22, Gold);
                    p.Ring(32, 32, 15, 3, Hex("#e09f3e"));
                    p.Circle(26, 40, 5, new Color(1, 1, 1, 0.5f));
                    break;
                case "star":
                    p.Star(32, 33, 28, 12, 5, Gold);
                    break;
                case "fish":
                    p.Ellipse(28, 32, 20, 11, Hex("#9ad1ff"));
                    p.Triangle(44, 32, 60, 20, 60, 44, Hex("#9ad1ff"));
                    p.Circle(18, 35, 3, Hex("#1b2b36"));
                    break;
                case "pin":
                    p.Circle(32, 38, 16, Hex("#e63946"));
                    p.Triangle(18, 32, 46, 32, 32, 6, Hex("#e63946"));
                    p.Circle(32, 38, 6, Color.white);
                    break;
                case "worm":
                    p.Line(12, 30, 26, 40, 6, Hex("#d97c8a"));
                    p.Line(26, 40, 40, 26, 6, Hex("#d97c8a"));
                    p.Line(40, 26, 54, 36, 6, Hex("#d97c8a"));
                    break;
                case "rod":
                    p.Line(10, 6, 54, 56, 4, Hex("#c49a6c"));
                    p.Line(10, 6, 24, 22, 6, Hex("#5b3c22"));
                    p.Circle(20, 22, 7, Hex("#c0c7cf"));
                    p.Line(54, 56, 56, 20, 1.5f, new Color(1, 1, 1, 0.8f));
                    p.Circle(56, 18, 4, Hex("#e63946"));
                    break;
                case "book":
                    p.Rect(10, 12, 44, 42, Hex("#3a86a8"));
                    p.Rect(14, 16, 18, 34, Hex("#f1ece0"));
                    p.Rect(34, 16, 16, 34, Hex("#f1ece0"));
                    p.Line(31, 14, 31, 52, 2, Hex("#2b3a44"));
                    break;
                case "trophy":
                    p.Ellipse(32, 24, 16, 14, Gold);
                    p.Rect(16, 10, 32, 14, Gold);
                    p.Ring(14, 24, 7, 3, Gold);
                    p.Ring(50, 24, 7, 3, Gold);
                    p.Rect(28, 36, 8, 10, Hex("#e09f3e"));
                    p.Rect(20, 46, 24, 8, Hex("#e09f3e"));
                    break;
                case "scroll":
                    p.Rect(14, 10, 36, 44, Hex("#f4e1a0"));
                    p.Circle(14, 14, 5, Hex("#d9c48f"));
                    p.Circle(50, 50, 5, Hex("#d9c48f"));
                    for (int i = 0; i < 4; i++) p.Line(20, 20 + i * 8, 44, 20 + i * 8, 1.5f, Hex("#8b6e4a"));
                    break;
                case "map":
                    p.Triangle(8, 14, 24, 8, 24, 52, Hex("#d9c48f"));
                    p.Triangle(8, 14, 24, 52, 8, 58, Hex("#d9c48f"));
                    p.Rect(24, 8, 16, 44, Hex("#b8a06c"));
                    p.Triangle(40, 8, 56, 14, 56, 58, Hex("#d9c48f"));
                    p.Triangle(40, 8, 56, 58, 40, 52, Hex("#d9c48f"));
                    p.Circle(46, 30, 4, Hex("#e63946"));
                    p.Line(14, 40, 30, 26, 2, Hex("#3a86a8"));
                    break;
                case "gear":
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI / 4;
                        p.Line(32, 32, 32 + Mathf.Cos(a) * 26, 32 + Mathf.Sin(a) * 26, 7, Hex("#c0c7cf"));
                    }
                    p.Circle(32, 32, 19, Hex("#c0c7cf"));
                    p.Circle(32, 32, 8, Hex("#1b2b36"));
                    break;
                case "help":
                    p.Circle(32, 32, 26, Accent2);
                    p.Ring(32, 26, 9, 5, Color.white, upperOnly: true);
                    p.Line(41, 26, 32, 36, 5, Color.white);
                    p.Line(32, 36, 32, 40, 5, Color.white);
                    p.Circle(32, 49, 3.5f, Color.white);
                    break;
                case "crown":
                    p.Triangle(10, 44, 16, 14, 26, 36, Hex("#ff7b54"));
                    p.Triangle(22, 40, 32, 10, 42, 40, Hex("#ff7b54"));
                    p.Triangle(38, 36, 48, 14, 54, 44, Hex("#ff7b54"));
                    p.Rect(10, 40, 44, 12, Hex("#ff7b54"));
                    break;
                case "shield":
                    p.Triangle(10, 12, 54, 12, 32, 58, Hex("#3a86a8"));
                    p.Rect(10, 8, 44, 18, Hex("#3a86a8"));
                    p.Triangle(16, 16, 48, 16, 32, 50, Hex("#90e0ef"));
                    p.Rect(16, 12, 32, 10, Hex("#90e0ef"));
                    p.Rect(29, 14, 6, 34, Hex("#3a86a8"));
                    break;
                case "paw":
                    p.Ellipse(32, 42, 14, 11, Hex("#e0aaff"));
                    p.Circle(16, 26, 6, Hex("#e0aaff"));
                    p.Circle(26, 16, 6, Hex("#e0aaff"));
                    p.Circle(38, 16, 6, Hex("#e0aaff"));
                    p.Circle(48, 26, 6, Hex("#e0aaff"));
                    break;
                case "medal":
                    p.Triangle(18, 4, 30, 4, 34, 30, Hex("#e63946"));
                    p.Triangle(46, 4, 34, 4, 30, 30, Hex("#3a86a8"));
                    p.Circle(32, 40, 17, Hex("#e09f3e"));
                    p.Circle(32, 40, 13, Gold);
                    p.Star(32, 41, 9, 4, 5, Hex("#e09f3e"));
                    break;
                case "calendar":
                    p.Rect(8, 12, 48, 44, Hex("#f1ece0"));
                    p.Rect(8, 12, 48, 12, Hex("#e63946"));
                    p.Rect(16, 6, 5, 12, Hex("#495057"));
                    p.Rect(43, 6, 5, 12, Hex("#495057"));
                    for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) p.Rect(13 + c * 10, 28 + r * 9, 7, 6, Hex("#adb5bd"));
                    break;
                case "bucket":
                    p.Triangle(12, 20, 52, 20, 46, 56, Hex("#6c757d"));
                    p.Triangle(12, 20, 46, 56, 18, 56, Hex("#6c757d"));
                    p.Ellipse(32, 20, 20, 5, Hex("#9d0208"));
                    p.Ring(32, 20, 22, 3, Hex("#343a40"), upperOnly: true);
                    break;
                case "globe":
                    p.Circle(32, 32, 26, Hex("#3a86a8"));
                    p.Ellipse(24, 24, 10, 8, Hex("#7bd389"));
                    p.Ellipse(42, 40, 9, 11, Hex("#7bd389"));
                    p.Ring(32, 32, 26, 3, Hex("#90e0ef"));
                    break;
                case "shop":
                    p.Rect(10, 30, 44, 24, Hex("#8a5a33"));
                    for (int i = 0; i < 4; i++) p.Rect(8 + i * 12, 14, 12, 14, i % 2 == 0 ? Hex("#e63946") : Hex("#f1faee"));
                    break;
                case "boat":
                    p.Rect(4, 51, 56, 5, Hex("#4cc9f0"));
                    p.Triangle(6, 40, 58, 40, 50, 52, Hex("#9d2b2b"));
                    p.Triangle(6, 40, 50, 52, 14, 52, Hex("#9d2b2b"));
                    p.Line(30, 8, 30, 40, 3, Hex("#5b3c22"));
                    p.Triangle(33, 11, 33, 37, 52, 35, Hex("#f1faee"));
                    p.Triangle(30, 8, 30, 15, 20, 11, Hex("#e63946"));
                    break;
                case "boss":
                    p.Circle(32, 28, 20, Hex("#9d4edd"));
                    for (int i = 0; i < 4; i++) p.Line(14 + i * 12, 40, 10 + i * 13, 58, 5, Hex("#7b2cbf"));
                    p.Circle(24, 26, 5, Hex("#ffd166"));
                    p.Circle(40, 26, 5, Hex("#ffd166"));
                    p.Rect(23, 22, 2, 8, Hex("#111111"));
                    p.Rect(39, 22, 2, 8, Hex("#111111"));
                    break;
            }
            t = p.ToTexture();
            cache[key] = t;
            return t;
        }

        /// <summary>A tiny anti-aliased painter: shapes are evaluated per pixel by signed distance. (0, 0) is the top-left.</summary>
        class Painter
        {
            readonly int s;
            readonly Color[] px;
            public Painter(int size) { s = size; px = new Color[size * size]; }

            void Fill(System.Func<float, float, float> distance, Color c)
            {
                for (int y = 0; y < s; y++)
                    for (int x = 0; x < s; x++)
                    {
                        float a = Mathf.Clamp01(0.5f - distance(x + 0.5f, y + 0.5f)) * c.a;
                        if (a <= 0) continue;
                        int i = (s - 1 - y) * s + x; // flip: textures start at the bottom
                        var d = px[i];
                        float outA = a + d.a * (1 - a);
                        px[i] = outA <= 0 ? d : new Color((c.r * a + d.r * d.a * (1 - a)) / outA, (c.g * a + d.g * d.a * (1 - a)) / outA, (c.b * a + d.b * d.a * (1 - a)) / outA, outA);
                    }
            }

            public void Circle(float cx, float cy, float r, Color c) => Fill((x, y) => Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy)) - r, c);
            public void Ring(float cx, float cy, float r, float w, Color c, bool upperOnly = false) =>
                Fill((x, y) => upperOnly && y > cy ? 99 : Mathf.Abs(Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy)) - r) - w / 2, c);
            public void Ellipse(float cx, float cy, float rx, float ry, Color c) =>
                Fill((x, y) => (Mathf.Sqrt((x - cx) * (x - cx) / (rx * rx) + (y - cy) * (y - cy) / (ry * ry)) - 1) * Mathf.Min(rx, ry), c);
            public void Rect(float x0, float y0, float w, float h, Color c) =>
                Fill((x, y) => Mathf.Max(Mathf.Max(x0 - x, x - (x0 + w)), Mathf.Max(y0 - y, y - (y0 + h))), c);

            public void Line(float ax, float ay, float bx, float by, float width, Color c) => Fill((x, y) =>
            {
                var p = new Vector2(x - ax, y - ay);
                var ba = new Vector2(bx - ax, by - ay);
                float h = Mathf.Clamp01(Vector2.Dot(p, ba) / Vector2.Dot(ba, ba));
                return (p - ba * h).magnitude - width / 2;
            }, c);

            public void Triangle(float ax, float ay, float bx, float by, float cx, float cy, Color c) => Fill((x, y) =>
            {
                float E(float x0, float y0, float x1, float y1)
                {
                    float ex = x1 - x0, ey = y1 - y0, len = Mathf.Sqrt(ex * ex + ey * ey);
                    return ((x - x0) * ey - (y - y0) * ex) / len;
                }
                float sign = Mathf.Sign((bx - ax) * (cy - ay) - (by - ay) * (cx - ax));
                return -Mathf.Min(Mathf.Min(-sign * E(ax, ay, bx, by), -sign * E(bx, by, cx, cy)), -sign * E(cx, cy, ax, ay));
            }, c);

            public void Star(float cx, float cy, float r, float inner, int points, Color c)
            {
                for (int i = 0; i < points; i++)
                {
                    float a0 = -Mathf.PI / 2 + i * Mathf.PI * 2 / points;
                    float a1 = a0 + Mathf.PI / points, am = a0 - Mathf.PI / points;
                    Triangle(cx, cy, cx + Mathf.Cos(a0) * r, cy + Mathf.Sin(a0) * r, cx + Mathf.Cos(a1) * inner, cy + Mathf.Sin(a1) * inner, c);
                    Triangle(cx, cy, cx + Mathf.Cos(am) * inner, cy + Mathf.Sin(am) * inner, cx + Mathf.Cos(a0) * r, cy + Mathf.Sin(a0) * r, c);
                }
            }

            public Texture2D ToTexture()
            {
                var t = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                t.SetPixels(px);
                t.Apply();
                return t;
            }
        }
    }
}
