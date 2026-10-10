// Your angler in the Armour tab: the same rigged model other players see (RemoteAngler), wearing your armour
// (or a piece you're trying on), turning slowly on a stand far below the world, drawn by a small camera of
// its own into a texture. It only renders while the tab asks for it.

using UnityEngine;

namespace LastCast
{
    public class AnglerPreview : MonoBehaviour
    {
        const int W = 480, H = 640;
        static AnglerPreview instance;
        static readonly Vector3 Origin = new Vector3(40, -800, 0);

        Camera cam;
        RenderTexture rt;
        RemoteAngler model;
        float lastAsked = -9, yaw = 20, lastDrag = -9;

        /// <summary>The picture of an angler in this armour (head|body|legs|feet sets), turned by dragDegrees.</summary>
        public static Texture Get(string gear, int colour, float dragDegrees)
        {
            if (!instance) instance = new GameObject("Angler preview").AddComponent<AnglerPreview>();
            return instance.Show(gear, colour, dragDegrees);
        }

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam = new GameObject("Preview camera").AddComponent<Camera>();
            cam.transform.SetParent(transform, false);
            cam.transform.position = Origin + new Vector3(0, 1.15f, 4.2f);
            cam.transform.LookAt(Origin + new Vector3(0, 0.98f, 0));
            cam.fieldOfView = 30;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.cullingMask = 1 << FishIcons.Layer;
            cam.targetTexture = rt;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 20;
            cam.enabled = false;
            model = RemoteAngler.Create(999_999);
            model.transform.SetParent(transform, true);
        }

        Texture Show(string gear, int colour, float drag)
        {
            lastAsked = Time.unscaledTime;
            if (Mathf.Abs(drag) > 0.01f) lastDrag = Time.unscaledTime;
            yaw += drag;
            model.Apply(new AnglerState { id = 999_999, n = "", ar = gear, col = colour, x = Origin.x, y = Origin.y, z = Origin.z, yaw = yaw, pitch = -6 });
            return rt;
        }

        void Update()
        {
            bool on = Time.unscaledTime - lastAsked < 0.25f;
            cam.enabled = on;
            model.gameObject.SetActive(on);
            if (!on) return;
            if (Time.unscaledTime - lastDrag > 2) yaw += Time.unscaledDeltaTime * 18; // turns by itself unless you've just dragged it
        }

        void LateUpdate()
        {
            // Everything it's made of (rebuilt when the armour changes) on the preview's layer, and no name tag.
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = FishIcons.Layer;
                if (t.name == "Name" || t.name == "Line" || t.name == "Bobber") t.gameObject.SetActive(false);
            }
        }
    }
}
