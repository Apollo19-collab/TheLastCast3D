// How the bosses move (their bones come from LimbRig): the Kraken's tentacles writhe in waves that run out
// to their tips and rear up before it slams; Megalodon swims in place, its body and tail sweeping side to
// side, its jaw gaping and snapping; the Serpent's coils roll through the water one after another while
// its neck sways and its head strikes; the Ghost Whale's body and flukes rise and fall and its long
// flippers flap. Whatever has grabbed the rail squeezes and writhes. All of it grows wilder as the boss
// is hurt (agitation) and as a slam comes in (rear: 0 → 1 just before it lands).

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public static class BossMotion
    {
        /// <summary>Megalodon's lower jaw pivots at the back of its mouth (in the model's space).</summary>
        public static readonly Dictionary<int, Vector3[]> MegalodonJoints = new Dictionary<int, Vector3[]>
        {
            [2] = new[] { new Vector3(0, -1.2f, 10.5f) },
        };

        public static Dictionary<int, Vector3[]> Joints(string boss) => boss == "megalodon" ? MegalodonJoints : null;

        static void Set(LimbRig.Limb limb, int i, Quaternion q)
        {
            if (limb != null && i < limb.Count) limb.Bones[i].localRotation = q;
        }

        public static void Body(LimbRig rig, string boss, float t, float agitation, float rear)
        {
            if (rig == null) return;
            float wild = 1 + agitation * 0.8f;
            switch (boss)
            {
                case "kraken":
                    for (int l = 1; l <= 8; l++)
                    {
                        var limb = rig[l];
                        if (limb == null) continue;
                        float phase = l * 1.7f, speed = 1.3f + agitation * 1.4f;
                        int n = limb.Count;
                        for (int i = 0; i < n; i++)
                        {
                            float s = i / (n - 1f);
                            // A wave running out along the tentacle, a slow curl, and (before a slam) rearing up from the root.
                            float lift = (Mathf.Sin(t * speed - i * 0.9f + phase) * 7f + Mathf.Sin(t * 0.37f + phase) * 5f) * (0.35f + s) * wild
                                         - rear * (i < 3 ? 12f : -4f);
                            float sway = Mathf.Sin(t * speed * 0.8f - i * 0.7f + phase * 1.3f) * 6f * (0.35f + s) * wild;
                            Set(limb, i, Quaternion.AngleAxis(lift, limb.Across) * Quaternion.AngleAxis(sway, Vector3.up));
                        }
                    }
                    break;
                case "megalodon":
                {
                    var body = rig[1];
                    if (body != null)
                    {
                        int n = body.Count;
                        float speed = 2.0f + agitation * 1.6f;
                        for (int i = 0; i < n; i++)
                        {
                            float s = i / (n - 1f);
                            // Nothing at the snout, sweeping more and more towards the tail.
                            float sway = (0.5f + 7f * s * s) * Mathf.Sin(t * speed - i * 0.55f) * wild;
                            float roll = Mathf.Sin(t * 0.6f - i * 0.3f) * 1.5f * s;
                            Set(body, i, Quaternion.AngleAxis(sway, Vector3.up) * Quaternion.AngleAxis(roll, body.Across));
                        }
                    }
                    var jaw = rig[2];
                    if (jaw != null)
                    {
                        float gape = Mathf.Pow(0.5f + 0.5f * Mathf.Sin(t * 0.9f), 3) * 14f + 3f;
                        float snap = agitation > 0.3f || rear > 0 ? Mathf.Max(0, Mathf.Sin(t * (6 + rear * 6))) * (6f + rear * 10f) : 0;
                        Set(jaw, 0, Quaternion.AngleAxis(gape + snap, Vector3.right));
                    }
                    break;
                }
                case "serpent":
                    for (int l = 1; l <= 4; l++)
                    {
                        var coil = rig[l];
                        if (coil == null) continue;
                        // One long body: the coils roll up and down one after another and slide along as it swims,
                        // and a ripple runs over each arch from end to end.
                        float ph = t * 1.25f - l * 1.15f;
                        coil.Bones[0].localPosition = coil.Joints[0] + Vector3.up * Mathf.Sin(ph) * 1.4f * wild
                                                      + Vector3.forward * Mathf.Sin(ph + 1.3f) * 1.2f + Vector3.right * Mathf.Sin(t * 0.6f + l) * 0.7f;
                        int n = coil.Count;
                        for (int i = 0; i < n; i++)
                        {
                            float bend = Mathf.Sin(t * 2.1f - i * 0.75f + l) * 4f * wild;
                            float sway = Mathf.Sin(t * 1.05f - i * 0.45f + l * 1.7f) * 3.5f;
                            float twist = Mathf.Sin(t * 1.4f - i * 0.6f + l) * 3f;
                            Set(coil, i, Quaternion.AngleAxis(bend, coil.Across) * Quaternion.AngleAxis(sway, Vector3.up) * Quaternion.AngleAxis(twist, coil.Dir));
                        }
                    }
                    var neck = rig[5];
                    if (neck != null)
                    {
                        // The neck weaves in a slow S like a cobra's, more towards the head; drawing back as a slam comes.
                        int n = neck.Count;
                        for (int i = 0; i < n; i++)
                        {
                            float s = i / (n - 1f);
                            float sway = Mathf.Sin(t * 0.85f - i * 0.42f) * 6.5f * wild * (0.4f + s);
                            float lift = Mathf.Sin(t * 0.7f - i * 0.38f) * 4f * wild * (0.4f + s) + (rear > 0 ? -rear * 6f * (1 - s) + rear * 9f * s : 0);
                            float roll = Mathf.Sin(t * 0.6f - i * 0.3f) * 4f * s;
                            Set(neck, i, Quaternion.AngleAxis(lift, neck.Across) * Quaternion.AngleAxis(sway, Vector3.up) * Quaternion.AngleAxis(roll, neck.Dir));
                        }
                    }
                    break;
                default: // the Ghost Whale
                {
                    var body = rig[1];
                    if (body != null)
                    {
                        int n = body.Count;
                        for (int i = 0; i < n; i++)
                        {
                            float s = i / (n - 1f);
                            float rise = (0.6f + 5.5f * s * s) * Mathf.Sin(t * (1.0f + agitation) - i * 0.5f) * wild;
                            Set(body, i, Quaternion.AngleAxis(rise, body.Across));
                        }
                    }
                    for (int l = 2; l <= 3; l++)
                    {
                        var flipper = rig[l];
                        if (flipper == null) continue;
                        float side = l == 2 ? -1 : 1;
                        for (int i = 0; i < flipper.Count; i++)
                        {
                            float beat = Mathf.Sin(t * 0.9f - i * 0.6f + l) * (i == 0 ? 14f : 6f) * wild;
                            Set(flipper, i, Quaternion.AngleAxis(beat * side, Vector3.forward));
                        }
                    }
                    break;
                }
            }
        }

        /// <summary>Something that has grabbed the rail: it squeezes and writhes, harder as it's beaten off (struggle 0 → 1).</summary>
        public static void Grabber(LimbRig rig, string boss, float t, float struggle, float seed)
        {
            var limb = rig?[1];
            if (limb == null) return;
            int n = limb.Count;
            float speed = 2.2f + struggle * 5f, amp = 1.6f + struggle * 3.5f;
            for (int i = 0; i < n; i++)
            {
                float s = i / (n - 1f);
                Quaternion q;
                if (boss == "megalodon")
                    // The body heaves; the jaws on the end thrash and snap.
                    q = Quaternion.AngleAxis(Mathf.Sin(t * 1.6f - i * 0.5f + seed) * 1.5f + (s > 0.75f ? Mathf.Sin(t * (9 + struggle * 8) + seed) * (3 + struggle * 6) : 0), limb.Across)
                        * Quaternion.AngleAxis(Mathf.Sin(t * 2.4f + seed) * (s > 0.75f ? 5f : 1f) * (1 + struggle), Vector3.up);
                else
                    q = Quaternion.AngleAxis(Mathf.Sin(t * speed - i * 0.8f + seed) * amp * (0.4f + s), limb.Across)
                        * Quaternion.AngleAxis(Mathf.Sin(t * speed * 0.7f - i * 0.6f + seed * 2) * amp * 0.8f * (0.4f + s), limb.Dir);
                limb.Bones[i].localRotation = q;
            }
        }
    }
}
