// Another player, as you see them: a low-poly angler in their armour set's
// colours and hat (the looks from shared/armour.js), a name tag with their
// level, their rod, line and bobber, and their pet. Movement is smoothed
// between the ~12 updates a second.

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public class RemoteAngler : MonoBehaviour
    {
        static readonly string[] Colours = { "#e4572e", "#f3a712", "#a8c686", "#669bbc", "#c77dff", "#ff8fab", "#43aa8b", "#f9c74f", "#90be6d", "#577590" };

        public AnglerState State { get; private set; }
        public ulong Id { get; private set; }
        Vector3 target, vel;
        float yaw;
        string look = "?";
        MeshFilter body;
        Transform rodPivot, rodTip, pet;
        MeshFilter petMesh;
        string petShown;
        TextMesh tag;
        LineRenderer line;
        GameObject bobber;

        public static RemoteAngler Create(ulong id)
        {
            var go = new GameObject($"Angler {id}");
            var r = go.AddComponent<RemoteAngler>();
            r.Id = id;
            r.Build();
            return r;
        }

        void Build()
        {
            body = gameObject.AddComponent<MeshFilter>();
            gameObject.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;

            rodPivot = new GameObject("Rod").transform;
            rodPivot.SetParent(transform, false);
            rodPivot.localPosition = new Vector3(0.32f, 1.15f, 0.25f);
            var rodModel = Models.Get("rod");
            Mesh rodMesh;
            if (rodModel) rodMesh = Models.Recolour(rodModel, MeshKit.Hex("#8b5a2b"));
            else
            {
                var rk = new MeshKit();
                rk.Box(new Vector3(0, 0, 1.2f), new Vector3(0.04f, 0.04f, 2.4f), MeshKit.Hex("#8b5a2b"));
                rk.Box(new Vector3(0, -0.06f, 0.15f), new Vector3(0.1f, 0.1f, 0.1f), MeshKit.Hex("#c0c7cf"));
                rodMesh = rk.Build("remote rod");
            }
            rodPivot.gameObject.AddComponent<MeshFilter>().sharedMesh = rodMesh;
            rodPivot.gameObject.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            rodTip = new GameObject("Tip").transform;
            rodTip.SetParent(rodPivot, false);
            rodTip.localPosition = new Vector3(0, 0, rodModel ? 2.2f : 2.4f);

            var tagGo = new GameObject("Name");
            tagGo.transform.SetParent(transform, false);
            tagGo.transform.localPosition = new Vector3(0, 2.35f, 0);
            tag = tagGo.AddComponent<TextMesh>();
            tag.font = ShopStall.Font;
            tag.fontSize = 48;
            tag.characterSize = 0.035f;
            tag.anchor = TextAnchor.MiddleCenter;
            tag.alignment = TextAlignment.Center;
            tag.fontStyle = FontStyle.Bold;
            tagGo.GetComponent<MeshRenderer>().sharedMaterial = ShopStall.Font.material;

            line = new GameObject("Line").AddComponent<LineRenderer>();
            line.transform.SetParent(transform, false);
            line.useWorldSpace = true;
            line.sharedMaterial = RodView.Unlit;
            line.widthMultiplier = 0.02f;
            line.positionCount = 12;
            line.startColor = line.endColor = new Color(0.95f, 0.95f, 0.9f, 0.7f);
            line.enabled = false;

            bobber = new GameObject("Bobber");
            bobber.transform.SetParent(transform, false);
            var bk = new MeshKit();
            bk.Cone(Vector3.zero, 0.08f, 0.07f, 8, MeshKit.Hex("#f1faee"), 0.08f);
            bk.Cone(new Vector3(0, 0.07f, 0), 0.08f, 0.09f, 8, MeshKit.Hex("#e63946"));
            bobber.AddComponent<MeshFilter>().sharedMesh = bk.Build("remote bobber");
            bobber.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            bobber.SetActive(false);

            pet = new GameObject("Pet").transform;
            petMesh = pet.gameObject.AddComponent<MeshFilter>();
            pet.gameObject.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
        }

        public void Apply(AnglerState st)
        {
            bool first = State == null;
            State = st;
            target = new Vector3(st.x, st.y, st.z);
            if (first)
            {
                transform.position = target;
                pet.position = target;
            }
            string want = $"{st.hat}|{st.col}";
            if (want != look)
            {
                look = want;
                body.sharedMesh = AnglerMesh.Build(st.hat, Colours[st.col % Colours.Length]);
            }
            tag.text = $"{st.n}\n<size=32>Lv {st.lv}</size>";
            tag.richText = true;
        }

        void Update()
        {
            if (State == null) return;
            transform.position = Vector3.SmoothDamp(transform.position, target, ref vel, 0.1f);
            yaw = Mathf.LerpAngle(yaw, State.yaw, 1 - Mathf.Exp(-12 * Time.deltaTime));
            transform.rotation = Quaternion.Euler(0, yaw, 0);

            var ls = (LineState)State.ls;
            bool lineOut = ls != LineState.Idle;
            rodPivot.localRotation = Quaternion.Euler(lineOut ? -20 + State.tn * 25 : -55, 0, 0);
            bobber.SetActive(lineOut);
            line.enabled = lineOut;
            if (lineOut)
            {
                var b = new Vector3(State.bx, State.by, State.bz);
                bobber.transform.position = Vector3.Lerp(bobber.transform.position, b, 1 - Mathf.Exp(-15 * Time.deltaTime));
                var a = rodTip.position;
                float sag = ls == LineState.Reeling ? Mathf.Lerp(0.5f, 0, State.tn * 1.5f) : 1f;
                for (int i = 0; i < line.positionCount; i++)
                {
                    float t = i / (line.positionCount - 1f);
                    var p = Vector3.Lerp(a, bobber.transform.position + Vector3.up * 0.1f, t);
                    p.y -= Mathf.Sin(t * Mathf.PI) * sag * Mathf.Clamp01(Vector3.Distance(a, b) / 10f);
                    line.SetPosition(i, p);
                }
            }

            // Name tag faces the camera.
            var cam = Camera.main;
            if (cam) tag.transform.rotation = Quaternion.LookRotation(tag.transform.position - cam.transform.position);

            // Their pet.
            if (State.pet != petShown)
            {
                petShown = State.pet;
                petMesh.sharedMesh = State.pet != null ? PetMeshes.Build(State.pet) : null;
            }
            if (State.pet != null)
            {
                var look = PetMeshes.LookOf(State.pet);
                var fwd = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
                var right = Quaternion.Euler(0, yaw, 0) * Vector3.right;
                var goal = transform.position + right * 1.3f + fwd * 0.6f;
                float ground = Place.StandHeight(goal);
                goal.y = look.motion == PetMeshes.Motion.Fly ? ground + 2.4f : look.motion == PetMeshes.Motion.Float ? Mathf.Max(ground, 0) + 1.1f : ground;
                pet.position = Vector3.Lerp(pet.position, goal, 1 - Mathf.Exp(-5 * Time.deltaTime));
                pet.rotation = Quaternion.Euler(0, yaw, 0);
            }
        }

        void OnDestroy()
        {
            if (pet) Destroy(pet.gameObject);
        }
    }

    /// <summary>A low-poly angler: boots, waders, jacket, head and hat, in an armour set's look (or the player's colour).</summary>
    public static class AnglerMesh
    {
        static readonly Dictionary<string, Mesh> cache = new Dictionary<string, Mesh>();

        public static Mesh Build(string setId, string colour)
        {
            string key = $"{setId}|{colour}";
            if (cache.TryGetValue(key, out var m)) return m;
            var set = setId != null ? GameData.I.SetById(setId) : null;
            string Look(string k, string fallback) => set?.look != null && set.look.TryGetValue(k, out var v) ? v : fallback;
            Color32 jacket = MeshKit.Hex(Look("jacket", colour)), boots = MeshKit.Hex(Look("boots", "#3b2f2f")),
                waders = MeshKit.Shade(MeshKit.Hex(Look("boots", "#4a5a3a")), 1.25f), skin = MeshKit.Hex("#f1c27d"),
                hatColour = MeshKit.Hex(Look("hatColor", "#c2a878")), trim = MeshKit.Hex(Look("trim", Look("hatColor", "#c2a878")));
            string hat = Look("hat", "bucket");
            // The Blender angler and hat, in this set's colours.
            var bodyModel = Models.Get("angler");
            var hatModel = Models.Named("hat", "hat_" + hat) ?? Models.Named("hat", "hat_bucket");
            if (bodyModel && hatModel)
            {
                var combine = new[]
                {
                    new CombineInstance { mesh = Models.Recolour(bodyModel, jacket, boots, waders), transform = Matrix4x4.identity },
                    new CombineInstance { mesh = Models.Recolour(hatModel, default, default, default, hatColour, trim), transform = Matrix4x4.identity },
                };
                m = new Mesh { name = "angler " + key };
                m.CombineMeshes(combine, true, true);
                cache[key] = m;
                return m;
            }
            var k = new MeshKit();
            // Legs and boots.
            foreach (float x in new[] { -0.13f, 0.13f })
            {
                k.Box(new Vector3(x, 0.1f, 0.03f), new Vector3(0.2f, 0.2f, 0.32f), boots);
                k.Box(new Vector3(x, 0.55f, 0), new Vector3(0.2f, 0.75f, 0.22f), waders);
            }
            // Jacket, arms and head.
            k.Box(new Vector3(0, 1.2f, 0), new Vector3(0.55f, 0.65f, 0.32f), jacket);
            k.Box(new Vector3(0, 0.92f, 0), new Vector3(0.57f, 0.08f, 0.34f), MeshKit.Shade(jacket, 0.75f)); // belt
            k.Box(new Vector3(-0.35f, 1.2f, 0.05f), new Vector3(0.15f, 0.55f, 0.16f), jacket);
            k.Box(new Vector3(0.33f, 1.18f, 0.18f), new Vector3(0.15f, 0.16f, 0.45f), jacket); // the arm holding the rod
            k.Box(new Vector3(0, 1.66f, 0), new Vector3(0.32f, 0.32f, 0.3f), skin);
            k.Box(new Vector3(0.08f, 1.69f, 0.155f), new Vector3(0.05f, 0.05f, 0.01f), new Color32(30, 30, 30, 255));
            k.Box(new Vector3(-0.08f, 1.69f, 0.155f), new Vector3(0.05f, 0.05f, 0.01f), new Color32(30, 30, 30, 255));
            // The hat.
            var top = new Vector3(0, 1.82f, 0);
            switch (hat)
            {
                case "souwester":
                    k.Cone(top - Vector3.up * 0.04f, 0.32f, 0.08f, 10, hatColour, 0.22f);
                    k.Cone(top + Vector3.up * 0.04f, 0.2f, 0.14f, 10, hatColour, 0.14f);
                    break;
                case "straw":
                    k.Cone(top - Vector3.up * 0.03f, 0.38f, 0.04f, 12, hatColour, 0.36f);
                    k.Cone(top, 0.18f, 0.16f, 10, hatColour, 0.05f);
                    break;
                case "cap":
                case "captain":
                    k.Box(top + new Vector3(0, 0.04f, 0), new Vector3(0.34f, 0.1f, 0.32f), hatColour);
                    k.Box(top + new Vector3(0, -0.01f, 0.2f), new Vector3(0.28f, 0.03f, 0.16f), hat == "captain" ? new Color32(20, 20, 20, 255) : hatColour);
                    if (hat == "captain") k.Box(top + new Vector3(0, 0.05f, 0.165f), new Vector3(0.08f, 0.05f, 0.01f), MeshKit.Hex("#ffd166"));
                    break;
                case "beanie":
                    k.Blob(top + new Vector3(0, 0.02f, 0), new Vector3(0.18f, 0.12f, 0.17f), hatColour, new SeededRandom(5), 0.05f);
                    break;
                case "tricorn":
                    k.Cone(top - Vector3.up * 0.02f, 0.3f, 0.16f, 3, hatColour, 0.12f);
                    k.Box(top + new Vector3(0, 0.02f, 0.18f), new Vector3(0.05f, 0.05f, 0.02f), trim);
                    break;
                case "crown":
                    k.Cone(top, 0.18f, 0.12f, 8, hatColour, 0.2f);
                    for (int i = 0; i < 5; i++)
                    {
                        float a = i / 5f * Mathf.PI * 2;
                        k.Cone(top + new Vector3(Mathf.Cos(a) * 0.17f, 0.12f, Mathf.Sin(a) * 0.17f), 0.04f, 0.1f, 4, trim);
                    }
                    break;
                case "helm":
                    k.Blob(top - new Vector3(0, 0.1f, 0), new Vector3(0.24f, 0.24f, 0.24f), hatColour, new SeededRandom(9), 0.03f);
                    k.Box(top + new Vector3(0, -0.12f, 0.2f), new Vector3(0.16f, 0.14f, 0.03f), new Color32(120, 200, 220, 255));
                    break;
                case "halo":
                    for (int i = 0; i < 12; i++)
                    {
                        float a0 = i / 12f * Mathf.PI * 2;
                        k.Box(top + new Vector3(Mathf.Cos(a0) * 0.2f, 0.18f, Mathf.Sin(a0) * 0.2f), new Vector3(0.06f, 0.03f, 0.06f), hatColour);
                    }
                    break;
                default: // bucket hat
                    k.Cone(top - Vector3.up * 0.03f, 0.27f, 0.05f, 10, hatColour, 0.22f);
                    k.Cone(top, 0.18f, 0.12f, 10, hatColour, 0.16f);
                    break;
            }
            m = k.Build("angler " + key);
            cache[key] = m;
            return m;
        }
    }
}
