// Another player, as you see them: a low-poly angler in their armour set's
// colours and hat (the looks from shared/armour.js), a name tag with their
// level, their rod, line and bobber, and their pet. Movement is smoothed
// between the ~12 updates a second.

using System.Collections.Generic;
using System.Linq;
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
        Transform body; // the rigged angler (Body), rebuilt when their look changes
        LimbRig rig;
        // The walk and cast animation.
        float phase, walk, run, castT = 9;
        LineState lastLine;
        /// <summary>AutoShot's poses: animate as if moving at this velocity.</summary>
        public Vector3? TestVelocity;
        public void RestartCast() => castT = 0;
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
            string want = $"{st.ar}|{st.col}";
            if (want != look)
            {
                look = want;
                RebuildBody(AnglerMesh.Build(st.ar, Colours[st.col % Colours.Length]));
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
            if (body)
            {
                // On the trawler's deck they ride its roll and pitch too.
                if (Place.IsSea(transform.position)) body.SetPositionAndRotation(SeaWorld.Tilted(transform.position), SeaWorld.Tilt * transform.rotation);
                else body.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            }

            var ls = (LineState)State.ls;
            bool lineOut = ls != LineState.Idle;
            Animate(ls, Time.deltaTime);
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

        /// <summary>A new body for a new look: the angler model skinned to its hips, knees, shoulders, elbows and neck, with the rod in its right hand.</summary>
        void RebuildBody(Mesh mesh)
        {
            rodPivot.SetParent(transform, false);
            if (body) Destroy(body.gameObject);
            body = new GameObject("Body").transform;
            body.SetParent(transform, false);
            rig = LimbRig.Attach(body.gameObject, mesh, WorldBuilder.LitMaterial, joints: AnglerMesh.Joints, hard: true);
            var arm = rig ? rig[4] : null;
            if (arm != null && arm.Count > 1)
            {
                rodPivot.SetParent(arm.Bones[1], false);
                rodPivot.localPosition = AnglerMesh.Hand - arm.Joints[1];
            }
            else
            {
                if (!rig)
                {
                    body.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    body.gameObject.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
                }
                rodPivot.localPosition = AnglerMesh.Hand;
            }
        }

        /// <summary>
        /// Walking and running (legs and arms swinging, a bob, a lean when sprinting), a hop, and fishing: the rod
        /// drawn back over the shoulder while they charge a cast, whipped forward when they let go, held out with
        /// both hands while the line is out, and the reel cranked as they reel in.
        /// </summary>
        void Animate(LineState ls, float dt)
        {
            var st = State;
            if (ls == LineState.Casting && lastLine != LineState.Casting) castT = 0;
            lastLine = ls;
            castT += dt;
            float tension = st.tn;
            if (!rig)
            {
                rodPivot.localRotation = Quaternion.Euler(ls != LineState.Idle ? -20 + tension * 25 : -55, 0, 0);
                return;
            }

            var v = TestVelocity ?? vel;
            float speed = new Vector3(v.x, 0, v.z).magnitude;
            bool airborne = transform.position.y - Place.StandHeight(transform.position) > 0.15f;
            walk = Mathf.MoveTowards(walk, Mathf.Clamp01(speed / 1.5f), dt * 4);
            run = Mathf.MoveTowards(run, st.sp && speed > Tuning.WalkSpeed * 1.1f ? 1 : 0, dt * 3);
            phase = (phase + dt * speed / Mathf.Lerp(2.2f, 3.0f, run) * Mathf.PI * 2) % (Mathf.PI * 200);
            float swing = Mathf.Lerp(24, 42, run) * walk;
            float k = 1 - Mathf.Exp(-18 * dt);

            void Turn(int limb, int bone, Quaternion q, float rate)
            {
                var l = rig[limb];
                if (l != null && bone < l.Count) l.Bones[bone].localRotation = Quaternion.Slerp(l.Bones[bone].localRotation, q, rate);
            }

            // Legs: thighs swing (negative: forward), knees bend on the forward swing; tucked up in a hop.
            for (int side = 0; side < 2; side++)
            {
                float p = phase + side * Mathf.PI;
                float thigh = -swing * Mathf.Sin(p), knee = swing * 1.3f * Mathf.Max(0, Mathf.Cos(p)) + 4 * walk;
                if (airborne) { thigh = -28; knee = 48; }
                Turn(1 + side, 0, Quaternion.Euler(thigh, 0, 0), k);
                Turn(1 + side, 1, Quaternion.Euler(knee, 0, 0), k);
            }
            // The whole body bobs with each step and leans into a sprint.
            rig.Root.localPosition = new Vector3(0, (Mathf.Abs(Mathf.Cos(phase)) - 0.6f) * 0.045f * walk, 0);
            rig.Root.localRotation = Quaternion.Slerp(rig.Root.localRotation, Quaternion.Euler(7 * run * walk, 0, 0), k);
            // Looking up and down.
            Turn(5, 0, Quaternion.Euler(Mathf.Clamp(st.pitch, -40, 40) * 0.6f, 0, 0), k);

            // Arms. The left swings opposite the left leg (or holds the rod and cranks the reel); the right holds the rod.
            float shL = swing * 0.7f * Mathf.Sin(phase), shLz = 0, elL = -12 - 50 * run * walk;
            float shR = swing * 0.25f * Mathf.Sin(phase + Mathf.PI), shRz = 0, elR = -10, rod = -55;
            if (ls != LineState.Idle)
            {
                rod = -20 + tension * 25;
                shL = -30; shLz = 22; elL = -45;
            }
            if (ls == LineState.Reeling)
            {
                shL = -40 + Mathf.Sin(Time.time * 14) * 7;
                elL = -55 + Mathf.Cos(Time.time * 14) * 10;
                rod += Mathf.Sin(Time.time * 25) * 2 * tension;
            }
            float rate = k;
            if (st.ch >= 0)
            {
                // Drawing the rod back over the shoulder.
                float c = Mathf.SmoothStep(0, 1, st.ch);
                shR = -105 * c; shRz = -10 * c; elR = -10 - 25 * c; rod = Mathf.Lerp(-55, -28, c);
            }
            else if (castT < 0.8f)
            {
                // The cast: a fast whip forward, then the rod settles.
                float a = Mathf.Clamp01(castT / 0.18f), b = Mathf.SmoothStep(0, 1, Mathf.Clamp01((castT - 0.18f) / 0.6f));
                shR = Mathf.Lerp(Mathf.Lerp(-105, -30, a), 0, b);
                elR = Mathf.Lerp(Mathf.Lerp(-35, -5, a), -10, b);
                rod = Mathf.Lerp(Mathf.Lerp(-28, 15, a), -20 + tension * 25, b);
                rate = 1 - Mathf.Exp(-40 * dt);
            }
            Turn(3, 0, Quaternion.Euler(shL, 0, shLz), k);
            Turn(3, 1, Quaternion.Euler(elL, 0, 0), k);
            Turn(4, 0, Quaternion.Euler(shR, 0, shRz), rate);
            Turn(4, 1, Quaternion.Euler(elR, 0, 0), rate);
            rodPivot.localRotation = Quaternion.Slerp(rodPivot.localRotation, Quaternion.Euler(rod, 0, 0), rate);
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

        /// <summary>The Blender angler's joints (its limbs: 1 left leg, 2 right leg, 3 left arm, 4 right arm, 5 head): hip and knee, shoulder and elbow, neck.</summary>
        public static readonly Dictionary<int, Vector3[]> Joints = new Dictionary<int, Vector3[]>
        {
            [1] = new[] { new Vector3(-0.12f, 0.9f, 0), new Vector3(-0.122f, 0.5f, 0.01f) },
            [2] = new[] { new Vector3(0.12f, 0.9f, 0), new Vector3(0.122f, 0.5f, 0.01f) },
            [3] = new[] { new Vector3(-0.3f, 1.42f, 0), new Vector3(-0.36f, 1.17f, 0.03f) },
            [4] = new[] { new Vector3(0.3f, 1.42f, 0), new Vector3(0.38f, 1.22f, 0.12f) },
            [5] = new[] { new Vector3(0, 1.52f, 0) },
        };

        /// <summary>Where the right hand grips the rod.</summary>
        public static readonly Vector3 Hand = new Vector3(0.32f, 1.15f, 0.25f);

        /// <summary>An armour set's jacket colour (or the fallback).</summary>
        public static Color32 Jacket(string setId, string fallback)
        {
            var set = setId != null ? GameData.I.SetById(setId) : null;
            return MeshKit.Hex(set?.look != null && set.look.TryGetValue("jacket", out var v) ? v : fallback);
        }

        public static readonly string[] Slots = { "head", "body", "legs", "feet" };

        /// <summary>What a profile is wearing, as AnglerState.ar: each slot's armour set (head|body|legs|feet), empty for none.</summary>
        public static string GearOf(Profile p) => string.Join("|", Slots.Select(sl =>
            p.armour.TryGetValue(sl, out var id) && id != null ? GameData.I.Piece(id).set?.id ?? "" : ""));

        /// <summary>The same with one piece swapped in (trying it on in the Armour tab).</summary>
        public static string With(string gear, string slot, string setId)
        {
            var parts = (gear ?? "|||").Split('|');
            if (parts.Length < 4) parts = new[] { "", "", "", "" };
            parts[System.Array.IndexOf(Slots, slot)] = setId ?? "";
            return string.Join("|", parts);
        }

        /// <summary>
        /// An angler in the armour they're wearing (gear: each slot's set, head|body|legs|feet): the Blender angler
        /// (head, face, hands) with each slot's piece from gear.fbx, its set's own model (an oilskin coat, a pea
        /// coat, a diving helmet...), or the plain default (a shirt in the player's colour, jeans, trainers; no hat).
        /// </summary>
        public static Mesh Build(string gear, string colour)
        {
            string key = $"{gear}|{colour}";
            if (cache.TryGetValue(key, out var m)) return m;
            var sets = (gear ?? "").Split('|');
            string SetIn(int slot) => slot < sets.Length && sets[slot].Length > 0 ? sets[slot] : null;
            var baseModel = Models.Get("angler");
            if (baseModel && Models.Named("gear", "gear_body_default"))
            {
                var parts = new List<CombineInstance> { new CombineInstance { mesh = baseModel, transform = Matrix4x4.identity } };
                for (int i = 0; i < Slots.Length; i++)
                {
                    var set = SetIn(i);
                    var piece = (set != null ? Models.Named("gear", $"gear_{Slots[i]}_{set}") : null) ?? Models.Named("gear", $"gear_{Slots[i]}_default");
                    if (!piece) continue; // no hat
                    if (set == null) piece = Models.Recolour(piece, MeshKit.Hex(colour)); // the default shirt, in the player's colour
                    parts.Add(new CombineInstance { mesh = piece, transform = Matrix4x4.identity });
                }
                m = new Mesh { name = "angler " + key, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                m.CombineMeshes(parts.ToArray(), true, true);
                cache[key] = m;
                return m;
            }
            return Simple(SetIn(1), colour, key);
        }

        /// <summary>Boxes, if the models are missing.</summary>
        static Mesh Simple(string setId, string colour, string key)
        {
            Mesh m;
            var set = setId != null ? GameData.I.SetById(setId) : null;
            string Look(string k, string fallback) => set?.look != null && set.look.TryGetValue(k, out var v) ? v : fallback;
            Color32 jacket = MeshKit.Hex(Look("jacket", colour)), boots = MeshKit.Hex(Look("boots", "#3b2f2f")),
                waders = MeshKit.Shade(MeshKit.Hex(Look("boots", "#4a5a3a")), 1.25f), skin = MeshKit.Hex("#f1c27d"),
                hatColour = MeshKit.Hex(Look("hatColor", "#c2a878")), trim = MeshKit.Hex(Look("trim", Look("hatColor", "#c2a878")));
            string hat = Look("hat", "bucket");
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
