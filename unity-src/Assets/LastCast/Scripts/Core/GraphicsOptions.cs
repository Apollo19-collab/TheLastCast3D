// The graphics settings (Options → Graphics): display mode and resolution, V-Sync and a frame-rate limit,
// a quality preset, render scale, anti-aliasing, shadows, view distance, field of view, bloom, brightness,
// head bob, camera shake and an FPS counter. They're saved in PlayerPrefs (not your profile, so each PC
// keeps its own) and applied when the game starts and whenever one changes.

using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LastCast
{
    public static class GraphicsOptions
    {
        public static readonly string[] Presets = { "Low", "Medium", "High", "Ultra", "Custom" };
        public static readonly string[] DisplayModes = { "Borderless fullscreen", "Exclusive fullscreen", "Windowed", "Maximised window" };
        public static readonly int[] FrameCaps = { 30, 60, 90, 120, 144, 165, 240, 0 };
        public static readonly string[] AntiAliasingModes = { "Off", "FXAA (fast)", "SMAA", "MSAA 2x", "MSAA 4x", "MSAA 8x" };
        public static readonly string[] ShadowLevels = { "Off", "Low", "Medium", "High", "Ultra" };
        public const int CustomPreset = 4;

        public static int Preset = 2;
        public static int DisplayMode;
        public static int ResW, ResH; // 0: the screen's own
        public static bool VSync = true;
        public static int FrameCap = 4; // index into FrameCaps (144)
        public static float RenderScale = 1;
        public static int AntiAliasing = 1;
        public static int Shadows = 3;
        public static float ShadowDistance = 90;
        public static float ViewDistance = 560; // metres: where the fog closes in
        public static float FieldOfView = 70;
        public static bool Bloom;
        public static float Brightness; // exposure, -1 .. +1
        public static bool HeadBob = true, CameraShake = true, ShowFps;

        // Fog, as the world sets it (time of day, the sea): the view distance scales it.
        static float fogStart = 160, fogEnd = 560;
        public static float FogStart => fogStart;
        public static float FogEnd => fogEnd;
        public static float ViewScale => ViewDistance / 560f;

        static Volume volume;
        static Bloom bloom;
        static ColorAdjustments colour;

        /// <summary>Set the fog distances (before the view distance is applied).</summary>
        public static void SetFog(float start, float end)
        {
            fogStart = start;
            fogEnd = end;
            RenderSettings.fogStartDistance = start * ViewScale;
            RenderSettings.fogEndDistance = end * ViewScale;
        }

        /// <summary>A preset's settings (Low, Medium, High, Ultra).</summary>
        public static void UsePreset(int p)
        {
            Preset = p;
            switch (p)
            {
                case 0: RenderScale = 0.75f; AntiAliasing = 0; Shadows = 0; ShadowDistance = 40; ViewDistance = 320; Bloom = false; break;
                case 1: RenderScale = 1; AntiAliasing = 1; Shadows = 1; ShadowDistance = 60; ViewDistance = 440; Bloom = false; break;
                case 2: RenderScale = 1; AntiAliasing = 1; Shadows = 3; ShadowDistance = 90; ViewDistance = 560; Bloom = false; break;
                case 3: RenderScale = 1; AntiAliasing = 4; Shadows = 4; ShadowDistance = 150; ViewDistance = 820; Bloom = true; break;
            }
        }

        public static void Load()
        {
            Preset = PlayerPrefs.GetInt("gfx.preset", 2);
            UsePreset(Mathf.Min(Preset, 3));
            Preset = PlayerPrefs.GetInt("gfx.preset", 2);
            DisplayMode = PlayerPrefs.GetInt("gfx.display", DisplayMode);
            ResW = PlayerPrefs.GetInt("gfx.resw", 0);
            ResH = PlayerPrefs.GetInt("gfx.resh", 0);
            VSync = PlayerPrefs.GetInt("gfx.vsync", 1) == 1;
            FrameCap = Mathf.Clamp(PlayerPrefs.GetInt("gfx.cap", FrameCap), 0, FrameCaps.Length - 1);
            RenderScale = PlayerPrefs.GetFloat("gfx.scale", RenderScale);
            AntiAliasing = Mathf.Clamp(PlayerPrefs.GetInt("gfx.aa", AntiAliasing), 0, AntiAliasingModes.Length - 1);
            Shadows = Mathf.Clamp(PlayerPrefs.GetInt("gfx.shadows", Shadows), 0, ShadowLevels.Length - 1);
            ShadowDistance = PlayerPrefs.GetFloat("gfx.shadowdist", ShadowDistance);
            ViewDistance = PlayerPrefs.GetFloat("gfx.view", ViewDistance);
            FieldOfView = PlayerPrefs.GetFloat("gfx.fov", FieldOfView);
            Bloom = PlayerPrefs.GetInt("gfx.bloom", Bloom ? 1 : 0) == 1;
            Brightness = PlayerPrefs.GetFloat("gfx.bright", 0);
            HeadBob = PlayerPrefs.GetInt("gfx.bob", 1) == 1;
            CameraShake = PlayerPrefs.GetInt("gfx.shake", 1) == 1;
            ShowFps = PlayerPrefs.GetInt("gfx.fps", 0) == 1;
        }

        public static void Save()
        {
            PlayerPrefs.SetInt("gfx.preset", Preset);
            PlayerPrefs.SetInt("gfx.display", DisplayMode);
            PlayerPrefs.SetInt("gfx.resw", ResW);
            PlayerPrefs.SetInt("gfx.resh", ResH);
            PlayerPrefs.SetInt("gfx.vsync", VSync ? 1 : 0);
            PlayerPrefs.SetInt("gfx.cap", FrameCap);
            PlayerPrefs.SetFloat("gfx.scale", RenderScale);
            PlayerPrefs.SetInt("gfx.aa", AntiAliasing);
            PlayerPrefs.SetInt("gfx.shadows", Shadows);
            PlayerPrefs.SetFloat("gfx.shadowdist", ShadowDistance);
            PlayerPrefs.SetFloat("gfx.view", ViewDistance);
            PlayerPrefs.SetFloat("gfx.fov", FieldOfView);
            PlayerPrefs.SetInt("gfx.bloom", Bloom ? 1 : 0);
            PlayerPrefs.SetFloat("gfx.bright", Brightness);
            PlayerPrefs.SetInt("gfx.bob", HeadBob ? 1 : 0);
            PlayerPrefs.SetInt("gfx.shake", CameraShake ? 1 : 0);
            PlayerPrefs.SetInt("gfx.fps", ShowFps ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>The resolutions this screen offers (widest first), without repeats for refresh rates.</summary>
        public static Vector2Int[] Resolutions()
        {
            var list = Screen.resolutions.Select(r => new Vector2Int(r.width, r.height)).Distinct().OrderByDescending(r => r.x * 10000 + r.y).ToList();
            var native = new Vector2Int(Display.main.systemWidth, Display.main.systemHeight);
            if (!list.Contains(native)) list.Insert(0, native);
            return list.ToArray();
        }

        static FullScreenMode Mode => DisplayMode switch
        {
            1 => FullScreenMode.ExclusiveFullScreen,
            2 => FullScreenMode.Windowed,
            3 => FullScreenMode.MaximizedWindow,
            _ => FullScreenMode.FullScreenWindow,
        };

        /// <summary>Put the display mode and resolution into effect.</summary>
        public static void ApplyDisplay()
        {
            if (Application.isEditor) return;
            int w = ResW > 0 ? ResW : Display.main.systemWidth, h = ResH > 0 ? ResH : Display.main.systemHeight;
            if (Mode == FullScreenMode.Windowed && ResW == 0) { w = Mathf.RoundToInt(w * 0.8f); h = Mathf.RoundToInt(h * 0.8f); }
            if (Screen.width != w || Screen.height != h || Screen.fullScreenMode != Mode) Screen.SetResolution(w, h, Mode);
        }

        /// <summary>Put everything but the display mode into effect (cheap: called whenever a setting changes).</summary>
        public static void Apply()
        {
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = VSync || FrameCaps[FrameCap] == 0 ? -1 : FrameCaps[FrameCap];

            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                urp.renderScale = Mathf.Clamp(RenderScale, 0.5f, 2f);
                urp.msaaSampleCount = AntiAliasing switch { 3 => 2, 4 => 4, 5 => 8, _ => 1 };
                urp.shadowDistance = ShadowDistance;
                urp.shadowCascadeCount = Shadows switch { 1 => 1, 2 => 2, _ => 4 };
                urp.mainLightShadowmapResolution = Shadows switch { 1 => 1024, 2 => 2048, 3 => 2048, _ => 4096 };
            }
            var sun = RenderSettings.sun;
            if (sun) sun.shadows = Shadows == 0 ? LightShadows.None : Shadows == 1 ? LightShadows.Hard : LightShadows.Soft;
            RenderSettings.fogStartDistance = fogStart * ViewScale;
            RenderSettings.fogEndDistance = fogEnd * ViewScale;

            var cam = Game.I != null && Game.I.Player != null ? Game.I.Player.Cam : Camera.main;
            if (cam)
            {
                cam.fieldOfView = FieldOfView;
                cam.farClipPlane = Mathf.Max(600, ViewDistance * 1.1f);
                var data = cam.GetUniversalAdditionalCameraData();
                data.antialiasing = AntiAliasing == 1 ? AntialiasingMode.FastApproximateAntialiasing : AntiAliasing == 2 ? AntialiasingMode.SubpixelMorphologicalAntiAliasing : AntialiasingMode.None;
                data.antialiasingQuality = AntialiasingQuality.High;
                data.renderPostProcessing = AntiAliasing is 1 or 2 || Bloom || Mathf.Abs(Brightness) > 0.01f;
            }

            // Bloom and brightness, in a volume of their own over the default one.
            if (!volume)
            {
                var go = new GameObject("Graphics options volume");
                Object.DontDestroyOnLoad(go);
                volume = go.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 100;
                volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();
                bloom = volume.profile.Add<Bloom>(true);
                colour = volume.profile.Add<ColorAdjustments>(true);
            }
            bloom.active = Bloom;
            bloom.intensity.Override(Bloom ? 0.6f : 0);
            bloom.threshold.Override(0.95f);
            bloom.scatter.Override(0.6f);
            colour.postExposure.Override(Brightness);
        }
    }
}
