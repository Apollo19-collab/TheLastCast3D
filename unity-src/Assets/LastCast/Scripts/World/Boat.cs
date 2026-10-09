// The boat that visits the lake every 15 minutes (shared/voyage.js boatState):
// it sails in from up the East River, docks beside the South Beach dock for two
// minutes while anglers board (E), then sails out with its passengers and the
// voyage begins. The schedule follows the wall clock, so it's the same for
// everyone (and the same as the web game).

using System.Collections.Generic;
using UnityEngine;

namespace LastCast
{
    public enum BoatPhase { Arriving, Docked, Departing, Away }

    public struct BoatPose
    {
        public BoatPhase phase;
        public double left, nextDock; // seconds left in this phase; until it next docks
        public bool visible;
        public float x, y, h; // world units; heading (canvas angle: 0 east, pi/2 south)
    }

    public static class BoatSchedule
    {
        static List<Vector2> pts;
        static List<float> dist;
        static BoatMooring B => GameData.I.boat;

        /// <summary>For testing: seconds added to the clock.</summary>
        public static double Offset;
        public static double Now => MapEvents.Now + Offset;

        static void Build()
        {
            // A smooth curve through the route (Catmull-Rom), sampled by distance along it.
            pts = new List<Vector2>();
            var points = B.path;
            Vector2 P(int i) { var p = points[Mathf.Clamp(i, 0, points.Length - 1)]; return new Vector2(p[0], p[1]); }
            int n = Mathf.CeilToInt(240f / (points.Length - 1));
            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector2 p0 = P(i - 1), p1 = P(i), p2 = P(i + 1), p3 = P(i + 2);
                for (int k = 0; k < n; k++)
                {
                    float t = k / (float)n, t2 = t * t, t3 = t2 * t;
                    pts.Add(0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3));
                }
            }
            pts.Add(P(points.Length - 1));
            dist = new List<float> { 0 };
            for (int i = 1; i < pts.Count; i++) dist.Add(dist[i - 1] + Vector2.Distance(pts[i], pts[i - 1]));
        }

        /// <summary>Position and heading at fraction s (0..1) of the route into the dock.</summary>
        static (float x, float y, float h) RouteAt(float s)
        {
            if (pts == null) Build();
            float target = Mathf.Clamp01(s) * dist[dist.Count - 1];
            int i = 1;
            while (i < dist.Count - 1 && dist[i] < target) i++;
            Vector2 a = pts[i - 1], b = pts[i];
            float seg = dist[i] - dist[i - 1];
            float k = seg > 0 ? (target - dist[i - 1]) / seg : 0;
            var p = Vector2.Lerp(a, b, k);
            return (p.x, p.y, Mathf.Atan2(b.y - a.y, b.x - a.x));
        }

        public static BoatPose At(double now)
        {
            var b = B;
            double interval = b.interval;
            double c = ((now % interval) + interval) % interval;
            BoatPhase phase;
            double t, len;
            if (c < b.board) (phase, t, len) = (BoatPhase.Docked, c, b.board);
            else if (c < b.board + b.depart) (phase, t, len) = (BoatPhase.Departing, c - b.board, b.depart);
            else if (c >= interval - b.arrive) (phase, t, len) = (BoatPhase.Arriving, c - (interval - b.arrive), b.arrive);
            else (phase, t, len) = (BoatPhase.Away, c - b.board - b.depart, interval - b.arrive - b.board - b.depart);
            float u = (float)(t / len);
            var pose = new BoatPose { phase = phase, left = len - t, nextDock = phase == BoatPhase.Docked ? 0 : interval - c, visible = phase != BoatPhase.Away };
            if (phase == BoatPhase.Docked) (pose.x, pose.y, pose.h) = (b.x, b.y, b.heading);
            else if (phase == BoatPhase.Arriving) (pose.x, pose.y, pose.h) = RouteAt(1 - (1 - u) * (1 - u)); // slows as it docks
            else if (phase == BoatPhase.Departing)
            {
                var p = RouteAt(1 - u * u); // the same route backwards, speeding up
                // It swings round from its docked heading before heading off.
                pose.x = p.x;
                pose.y = p.y;
                pose.h = Mathf.LerpAngle(b.heading * Mathf.Rad2Deg, (p.h + Mathf.PI) * Mathf.Rad2Deg, Mathf.Min(1, u * 3)) * Mathf.Deg2Rad;
            }
            return pose;
        }

        /// <summary>When the voyage from this (or the next) visit sets off: the moment the boat is out of sight.
        /// Every passenger's game uses it as the voyage's id and seed.</summary>
        public static long LaunchTime(double now)
        {
            var b = B;
            double c = ((now % b.interval) + b.interval) % b.interval;
            return (long)System.Math.Round(now - c + b.board + b.depart);
        }

        /// <summary>Unity yaw for a heading (canvas angle).</summary>
        public static float Yaw(float h) => 90 + h * Mathf.Rad2Deg;

        /// <summary>World position of passenger slot i on the boat (shared/voyage.js boatSlot).</summary>
        public static Vector2 Slot(BoatPose pose, int i)
        {
            const int perSide = 8;
            float length = B.length, beam = B.beam;
            int side = i % 2 == 1 ? 1 : -1;
            int row = (i / 2) % perSide;
            float along = length * 0.28f - row * (length * 0.56f / (perSide - 1)) + (i / (perSide * 2)) * 7;
            float across = side * beam * 0.24f;
            float cos = Mathf.Cos(pose.h), sin = Mathf.Sin(pose.h);
            return new Vector2(pose.x + along * cos - across * sin, pose.y + along * sin + across * cos);
        }
    }

    /// <summary>The boat on the lake, following the schedule.</summary>
    public class LakeBoat : MonoBehaviour
    {
        public const float DeckY = 1.3f; // metres above the water (BoatMesh)
        public static LakeBoat I { get; private set; }
        public BoatPose Pose { get; private set; }
        GameObject hull;
        float seed;

        public static LakeBoat Create(Transform parent)
        {
            var b = GameData.I.boat;
            var go = new GameObject("Boat");
            go.transform.SetParent(parent, false);
            var lb = go.AddComponent<LakeBoat>();
            lb.hull = new GameObject("Hull");
            lb.hull.transform.SetParent(go.transform, false);
            lb.hull.AddComponent<MeshFilter>().sharedMesh = Models.Or("boat", 0, () => BoatMesh.Build(b.length / WorldMap.UnitsPerMetre, b.beam / WorldMap.UnitsPerMetre));
            lb.hull.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            I = lb;
            lb.Update();
            return lb;
        }

        /// <summary>How far the hull rises and rolls on the swell just now.</summary>
        float Heave => Mathf.Sin((Time.time + seed) * 0.9f) * 0.08f;

        void Update()
        {
            Pose = BoatSchedule.At(BoatSchedule.Now);
            hull.SetActive(Pose.visible);
            if (!Pose.visible) return;
            var map = WorldMap.I;
            float t = Time.time + seed;
            transform.position = map.ToUnity(Pose.x, Pose.y, Heave);
            transform.rotation = Quaternion.Euler(0, BoatSchedule.Yaw(Pose.h), 0) * Quaternion.Euler(Mathf.Sin(t * 0.7f) * 1.2f, 0, Mathf.Sin(t * 0.55f) * 1.8f);
        }

        /// <summary>Where passenger slot i stands (Unity space, on the deck).</summary>
        public Vector3 SlotPosition(int i)
        {
            var s = BoatSchedule.Slot(Pose, i);
            return WorldMap.I.ToUnity(s.x, s.y, DeckY + Heave);
        }

        /// <summary>Close enough to the docked boat to step aboard?</summary>
        public static bool InReach(Vector2 worldPos)
        {
            var b = GameData.I.boat;
            return Vector2.Distance(worldPos, new Vector2(b.x, b.y)) <= b.boardRange;
        }
    }
}
