// The Options window's pages (General, Graphics, Controls) and the Graphics page itself: display, quality
// and camera settings (Core/GraphicsOptions.cs), each taking effect as soon as it's changed. Also the FPS
// counter in the corner when it's switched on.

using UnityEngine;

namespace LastCast
{
    public partial class Hud
    {
        static readonly string[] OptionPages = { "General", "Graphics", "Controls" };
        int optionsPage;
        float fpsShown, fpsTimer;
        int fpsFrames;

        /// <summary>The page buttons along the top of the Options window.</summary>
        void DrawOptionPages()
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < OptionPages.Length; i++)
                if (GUILayout.Button(OptionPages[i], optionsPage == i ? tabOn : tabOff, GUILayout.Height(S(30)), GUILayout.Width(S(120))))
                {
                    optionsPage = i;
                    scroll = Vector2.zero;
                }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            var line = GUILayoutUtility.GetLastRect();
            GUI.DrawTexture(new Rect(line.x, line.yMax + S(2), line.width, 1), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Skin.Border, 0, 0);
            GUILayout.Space(S(10));
        }

        // ---- the Graphics page ------------------------------------------------------------------------------

        void DrawGraphicsOptions()
        {
            bool changed = false, display = false;

            Heading("DISPLAY");
            GUILayout.BeginVertical(row);
            int mode = Choice("Display mode", GraphicsOptions.DisplayMode, GraphicsOptions.DisplayModes);
            if (mode != GraphicsOptions.DisplayMode) { GraphicsOptions.DisplayMode = mode; display = true; }
            var resolutions = GraphicsOptions.Resolutions();
            var names = new string[resolutions.Length + 1];
            names[0] = "Screen's own";
            int current = 0;
            for (int i = 0; i < resolutions.Length; i++)
            {
                names[i + 1] = $"{resolutions[i].x} × {resolutions[i].y}";
                if (resolutions[i].x == GraphicsOptions.ResW && resolutions[i].y == GraphicsOptions.ResH) current = i + 1;
            }
            int res = Choice("Resolution", current, names);
            if (res != current)
            {
                GraphicsOptions.ResW = res == 0 ? 0 : resolutions[res - 1].x;
                GraphicsOptions.ResH = res == 0 ? 0 : resolutions[res - 1].y;
                display = true;
            }
            changed |= Toggle("V-Sync (no tearing; frame rate follows your screen)", ref GraphicsOptions.VSync);
            if (!GraphicsOptions.VSync)
            {
                var caps = new string[GraphicsOptions.FrameCaps.Length];
                for (int i = 0; i < caps.Length; i++) caps[i] = GraphicsOptions.FrameCaps[i] == 0 ? "Unlimited" : $"{GraphicsOptions.FrameCaps[i]} fps";
                changed |= ChoiceRef("Frame-rate limit", ref GraphicsOptions.FrameCap, caps);
            }
            changed |= Toggle("Show frames per second", ref GraphicsOptions.ShowFps);
            GUILayout.EndVertical();

            Heading("QUALITY");
            GUILayout.BeginVertical(row);
            int preset = Choice("Preset", GraphicsOptions.Preset, GraphicsOptions.Presets);
            if (preset != GraphicsOptions.Preset)
            {
                if (preset == GraphicsOptions.CustomPreset) GraphicsOptions.Preset = preset;
                else GraphicsOptions.UsePreset(preset);
                changed = true;
            }
            bool tuned = false;
            tuned |= Slider("Render scale", ref GraphicsOptions.RenderScale, 0.5f, 2f, v => $"{Mathf.RoundToInt(v * 100)}%", 0.05f);
            tuned |= ChoiceRef("Anti-aliasing", ref GraphicsOptions.AntiAliasing, GraphicsOptions.AntiAliasingModes);
            tuned |= ChoiceRef("Shadows", ref GraphicsOptions.Shadows, GraphicsOptions.ShadowLevels);
            if (GraphicsOptions.Shadows > 0)
                tuned |= Slider("Shadow distance", ref GraphicsOptions.ShadowDistance, 30, 250, v => $"{Mathf.RoundToInt(v)} m", 5);
            tuned |= Slider("View distance", ref GraphicsOptions.ViewDistance, 250, 1200, v => $"{Mathf.RoundToInt(v)} m", 10);
            tuned |= Toggle("Bloom (a soft glow round bright things)", ref GraphicsOptions.Bloom);
            if (tuned) { GraphicsOptions.Preset = GraphicsOptions.CustomPreset; changed = true; }
            GUILayout.EndVertical();

            Heading("PICTURE AND CAMERA");
            GUILayout.BeginVertical(row);
            changed |= Slider("Brightness", ref GraphicsOptions.Brightness, -1f, 1f, v => v == 0 ? "Normal" : $"{(v > 0 ? "+" : "")}{v:0.0}", 0.1f);
            changed |= Slider("Field of view", ref GraphicsOptions.FieldOfView, 60, 100, v => $"{Mathf.RoundToInt(v)}°", 1);
            changed |= Toggle("Head bob while walking", ref GraphicsOptions.HeadBob);
            changed |= Toggle("Camera shake (a boss slamming the deck)", ref GraphicsOptions.CameraShake);
            GUILayout.EndVertical();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to defaults", button, GUILayout.Height(S(30))))
            {
                GraphicsOptions.UsePreset(2);
                GraphicsOptions.DisplayMode = 0;
                GraphicsOptions.ResW = GraphicsOptions.ResH = 0;
                GraphicsOptions.VSync = true;
                GraphicsOptions.FrameCap = 4;
                GraphicsOptions.FieldOfView = 70;
                GraphicsOptions.Brightness = 0;
                GraphicsOptions.HeadBob = GraphicsOptions.CameraShake = true;
                GraphicsOptions.ShowFps = false;
                changed = display = true;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label("Settings take effect straight away and are kept on this PC.", smallDim);

            if (display) GraphicsOptions.ApplyDisplay();
            if (changed || display)
            {
                GraphicsOptions.Apply();
                GraphicsOptions.Save();
            }
        }

        void Heading(string s)
        {
            GUILayout.Space(S(6));
            GUILayout.Label(s, new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
        }

        /// <summary>A setting with a few values: its name, then ◀ value ▶.</summary>
        int Choice(string label, int value, string[] options)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, text, GUILayout.Width(S(230)));
            if (GUILayout.Button("◀", button, GUILayout.Width(S(34)), GUILayout.Height(S(28)))) value = (value + options.Length - 1) % options.Length;
            GUILayout.Label($"<b>{options[Mathf.Clamp(value, 0, options.Length - 1)]}</b>", new GUIStyle(text) { alignment = TextAnchor.MiddleCenter }, GUILayout.Width(S(220)), GUILayout.Height(S(28)));
            if (GUILayout.Button("▶", button, GUILayout.Width(S(34)), GUILayout.Height(S(28)))) value = (value + 1) % options.Length;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(S(4));
            return value;
        }

        bool ChoiceRef(string label, ref int value, string[] options)
        {
            int v = Choice(label, value, options);
            if (v == value) return false;
            value = v;
            return true;
        }

        bool Slider(string label, ref float value, float min, float max, System.Func<float, string> show, float step)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, text, GUILayout.Width(S(230)));
            GUILayout.BeginVertical();
            GUILayout.Space(S(9));
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(S(290)));
            GUILayout.EndVertical();
            GUILayout.Space(S(10));
            v = Mathf.Round(v / step) * step;
            GUILayout.Label($"<b>{show(v)}</b>", text, GUILayout.Width(S(90)));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(S(4));
            if (Mathf.Approximately(v, value)) return false;
            value = v;
            return true;
        }

        /// <summary>A tick box and its label.</summary>
        bool Toggle(string label, ref bool value)
        {
            GUILayout.BeginHorizontal();
            bool click = GUILayout.Button(value ? "✔" : "", value ? buy : button, GUILayout.Width(S(30)), GUILayout.Height(S(26)));
            GUILayout.Space(S(8));
            click |= GUILayout.Button(label, new GUIStyle(text) { alignment = TextAnchor.MiddleLeft }, GUILayout.Height(S(26)));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(S(2));
            if (!click) return false;
            value = !value;
            return true;
        }

        // ---- the FPS counter ------------------------------------------------------------------------------

        void DrawFps()
        {
            if (Event.current.type == EventType.Repaint) // OnGUI runs more than once a frame
            {
                fpsFrames++;
                fpsTimer += Time.unscaledDeltaTime;
                if (fpsTimer >= 0.5f)
                {
                    fpsShown = fpsFrames / fpsTimer;
                    fpsFrames = 0;
                    fpsTimer = 0;
                }
            }
            var c = fpsShown >= 55 ? Skin.Green : fpsShown >= 30 ? Skin.Gold : Skin.Red;
            Shadowed(new Rect(Screen.width / 2f - S(240), S(2), S(80), S(18)), $"{Mathf.RoundToInt(fpsShown)} FPS", new GUIStyle(small) { fontStyle = FontStyle.Bold }, c, 1);
        }
    }
}
