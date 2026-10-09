// Pictures of each fish for the catch card and the Fish Index (the 2D game
// shows its fish art there): each species' low-poly model is rendered side-on
// by a small hidden camera, one fish a frame, and kept as a texture.

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public class FishIcons : MonoBehaviour
    {
        const int Layer = 31; // only the icon camera sees this layer
        const int W = 256, H = 128;

        static FishIcons instance;
        readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
        readonly List<string> queue = new List<string>();
        Camera cam;
        RenderTexture rt;
        MeshFilter model;
        string rendering;

        public static void Init(Camera main)
        {
            if (instance) return;
            main.cullingMask &= ~(1 << Layer);
            var go = new GameObject("Fish icons");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<FishIcons>();
            instance.Setup();
        }

        /// <summary>The picture of a species, or null while it's still being drawn (it's queued).</summary>
        public static Texture2D Get(string speciesId, bool urgent = false) => Request(speciesId, urgent);

        /// <summary>The picture of a pet (three-quarter view).</summary>
        public static Texture2D GetPet(string petId, bool urgent = false) => petId == null ? null : Request("pet:" + petId, urgent);

        static Texture2D Request(string key, bool urgent)
        {
            if (!instance || key == null) return null;
            if (instance.icons.TryGetValue(key, out var t)) return t;
            if (!instance.queue.Contains(key))
            {
                if (urgent) instance.queue.Insert(0, key);
                else instance.queue.Add(key);
            }
            return null;
        }

        Vector3 origin;

        void Setup()
        {
            origin = new Vector3(0, -800, 0); // far below the world
            rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var camGo = new GameObject("Icon camera");
            camGo.transform.SetParent(transform, false);
            camGo.transform.position = origin + new Vector3(0, 0.05f, -3);
            cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 0.32f;
            cam.aspect = W / (float)H;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.cullingMask = 1 << Layer;
            cam.targetTexture = rt;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 10;
            cam.enabled = false;

            var fish = new GameObject("Icon fish") { layer = Layer };
            fish.transform.SetParent(transform, false);
            fish.transform.position = origin;
            // Side-on, nose to the left, tilted a touch towards the camera.
            fish.transform.rotation = Quaternion.Euler(0, -90, 0) * Quaternion.Euler(8, 0, 0);
            model = fish.AddComponent<MeshFilter>();
            var r = fish.AddComponent<MeshRenderer>();
            r.sharedMaterial = WorldBuilder.LitMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        void Update()
        {
            // The fish set up last frame has been drawn: keep the picture.
            if (rendering != null)
            {
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                icons[rendering] = tex;
                rendering = null;
            }
            // Set up the next one.
            while (queue.Count > 0 && rendering == null)
            {
                var id = queue[0];
                queue.RemoveAt(0);
                if (icons.ContainsKey(id)) continue;
                if (id.StartsWith("pet:"))
                {
                    // Pets: standing, turned three-quarters towards the camera.
                    model.sharedMesh = PetMeshes.Build(id.Substring(4));
                    var b = model.sharedMesh.bounds;
                    float size = Mathf.Max(b.size.y, b.size.x * 0.5f, b.size.z * 0.5f);
                    model.transform.rotation = Quaternion.Euler(0, 210, 0);
                    model.transform.localScale = Vector3.one * (0.55f / Mathf.Max(0.1f, size));
                    model.transform.position = origin - Vector3.up * b.center.y * model.transform.localScale.y;
                }
                else
                {
                    var s = GameData.I.Fish(id);
                    if (s == null) continue;
                    model.sharedMesh = RodView.FishMesh(s);
                    model.transform.rotation = Quaternion.Euler(0, -90, 0) * Quaternion.Euler(8, 0, 0);
                    model.transform.localScale = Vector3.one;
                    model.transform.position = origin;
                }
                rendering = id;
            }
            cam.enabled = rendering != null;
        }
    }
}
