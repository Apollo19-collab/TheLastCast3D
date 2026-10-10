// The river bridge the voyage boat sails under: its middle is a drawbridge whose two leaves lift to let
// the boat through and come down again behind it. It follows the boat's schedule (the wall clock), so
// it's up at the same moments for everyone. While it's up, the gap can't be walked on, and anyone standing
// on a leaf as it starts to lift is moved off to the nearer end.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public class Drawbridge : MonoBehaviour
    {
        const float OpenWithin = 45; // metres from the boat to the bridge
        const float LookAhead = 4; // seconds: it's up before the boat gets there
        const float Speed = 0.35f; // of the way up, per second
        const float MaxAngle = 78;

        static readonly List<Drawbridge> all = new List<Drawbridge>();
        Transform[] leaves;
        Quaternion[] rest;
        Vector2 crossing; // world units: where the boat passes under
        Rect span; // world units: the lifting part
        Vector2[] ends; // world units: where you're put if you're on it as it lifts
        float open;

        /// <summary>Is this point on a drawbridge that's up (so not on the bridge at all)?</summary>
        public static bool Blocks(float x, float y)
        {
            foreach (var d in all)
                if (d.open > 0.02f && d.span.Contains(new Vector2(x, y))) return true;
            return false;
        }

        /// <summary>Where along a bridge (metres) the voyage boat's route passes under it, or null if it doesn't.</summary>
        public static float? Crossing(RectU r, Func<float, float, float> alongOf)
        {
            var path = GameData.I.boat?.path;
            if (path == null) return null;
            float sum = 0;
            int n = 0;
            for (int i = 0; i < path.Length - 1; i++)
                for (int k = 0; k <= 40; k++)
                {
                    float t = k / 40f;
                    float x = Mathf.Lerp(path[i][0], path[i + 1][0], t), y = Mathf.Lerp(path[i][1], path[i + 1][1], t);
                    if (x < r.x || x > r.x + r.w || y < r.y || y > r.y + r.h) continue;
                    sum += alongOf(x, y);
                    n++;
                }
            return n > 0 ? sum / n : (float?)null;
        }

        /// <summary>The two leaves of the gap [middle - half, middle + half] along a bridge, hinged at its edges.</summary>
        public static void Create(Transform parent, RectU r, Func<float, float, float, Vector3> at, bool alongX, float middle, float half, float across, float top)
        {
            var go = new GameObject("Drawbridge");
            go.transform.SetParent(parent, false);
            var db = go.AddComponent<Drawbridge>();
            var mesh = LeafMesh(half - 0.03f, across);
            db.leaves = new Transform[2];
            db.rest = new Quaternion[2];
            for (int i = 0; i < 2; i++)
            {
                float hinge = i == 0 ? middle - half : middle + half;
                float towardGap = i == 0 ? 1 : -1;
                var leaf = new GameObject($"Leaf {i}").transform;
                leaf.SetParent(go.transform, false);
                leaf.position = at(hinge, across / 2, top);
                var dir = alongX ? new Vector3(towardGap, 0, 0) : new Vector3(0, 0, towardGap);
                leaf.rotation = Quaternion.LookRotation(dir);
                leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
                db.leaves[i] = leaf;
                db.rest[i] = leaf.rotation;
            }
            // The same places in world units.
            var map = WorldMap.I;
            Vector2 W(Vector3 p) => map.ToWorld(p);
            var c0 = W(at(middle - half, 0, 0));
            var c1 = W(at(middle + half, across, 0));
            db.span = Rect.MinMaxRect(Mathf.Min(c0.x, c1.x), Mathf.Min(c0.y, c1.y), Mathf.Max(c0.x, c1.x), Mathf.Max(c0.y, c1.y));
            db.crossing = W(at(middle, across / 2, 0));
            db.ends = new[] { W(at(middle - half - 1.5f, across / 2, 0)), W(at(middle + half + 1.5f, across / 2, 0)) };
            all.Add(db);
        }

        /// <summary>One leaf, from its hinge (local origin, at deck height) along +Z: planks, edge boards, beams underneath and a rail each side.</summary>
        static Mesh LeafMesh(float length, float across)
        {
            var k = new MeshKit();
            Color32 plank = new Color32(158, 112, 70, 255), dark = MeshKit.Hex("#5b3c22"), iron = MeshKit.Hex("#3d3d3d");
            var rnd = new SeededRandom(17);
            for (float t = 0; t < length; t += 0.7f)
            {
                float pl = Mathf.Min(0.64f, length - t);
                k.Box(new Vector3(0, -0.06f, t + pl / 2), new Vector3(across - 0.1f, 0.12f, pl), MeshKit.Shade(plank, 1 + (rnd.Next() - 0.5f) * 0.32f));
            }
            foreach (float s in new[] { -1f, 1f })
            {
                k.Box(new Vector3(s * (across / 2 - 0.04f), -0.08f, length / 2), new Vector3(0.1f, 0.2f, length), dark);
                k.Box(new Vector3(s * (across / 4), -0.3f, length / 2), new Vector3(0.25f, 0.3f, length), dark);
                k.Box(new Vector3(s * (across / 2 + 0.2f), 0.95f, length / 2), new Vector3(0.14f, 0.14f, length), dark);
                for (float t = 0.3f; t < length; t += 2f)
                    k.Box(new Vector3(s * (across / 2 + 0.2f), 0.45f, t), new Vector3(0.14f, 1f, 0.14f), dark);
                // The hinge.
                k.Box(new Vector3(s * (across / 2 - 0.3f), -0.2f, 0.1f), new Vector3(0.5f, 0.4f, 0.4f), iron);
            }
            return k.Build("drawbridge leaf");
        }

        void Update()
        {
            // Up if the boat is near the bridge now or will be in the next few seconds.
            double now = BoatSchedule.Now;
            float target = 0;
            for (int s = 0; s <= LookAhead; s++)
            {
                var pose = BoatSchedule.At(now + s);
                if (pose.visible && Vector2.Distance(new Vector2(pose.x, pose.y), crossing) / WorldMap.UnitsPerMetre < OpenWithin) { target = 1; break; }
            }
            if (target > 0 && open <= 0.02f) MoveOffIfOn();
            open = Mathf.MoveTowards(open, target, Time.deltaTime * Speed);
            float angle = Mathf.SmoothStep(0, 1, open) * MaxAngle;
            for (int i = 0; i < leaves.Length; i++) leaves[i].rotation = rest[i] * Quaternion.Euler(-angle, 0, 0);
        }

        /// <summary>Anyone on the lifting part goes to the nearer end.</summary>
        void MoveOffIfOn()
        {
            var g = Game.I;
            if (g == null || g.Player == null || g.Player.Anchor != null || Place.AtSea) return;
            var me = g.Player.WorldPos;
            if (!span.Contains(me)) return;
            var to = Vector2.Distance(me, ends[0]) < Vector2.Distance(me, ends[1]) ? ends[0] : ends[1];
            g.Player.Teleport(WorldMap.I.ToUnity(to.x, to.y), g.Player.Yaw);
            g.Flash("The bridge is lifting to let the boat through!", 3);
        }

        void OnDestroy() => all.Remove(this);
    }
}
