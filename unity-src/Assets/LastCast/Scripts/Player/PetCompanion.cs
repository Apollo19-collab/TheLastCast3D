// Your pet, following you around: walkers hop along beside you, birds flutter
// over your shoulder and spirit pets float. It jumps for joy when you land a fish.

using UnityEngine;

namespace LastCast
{
    public class PetCompanion : MonoBehaviour
    {
        string shown;
        MeshFilter filter;
        MeshRenderer render;
        Vector3 vel;
        float cheer, hop, seed;

        public static PetCompanion Create()
        {
            var go = new GameObject("Pet");
            var c = go.AddComponent<PetCompanion>();
            c.filter = go.AddComponent<MeshFilter>();
            c.render = go.AddComponent<MeshRenderer>();
            c.render.sharedMaterial = WorldBuilder.LitMaterial;
            c.seed = Random.value * 10;
            return c;
        }

        public void Cheer() => cheer = 1;

        void Update()
        {
            var g = Game.I;
            if (g == null || g.Player == null) return;
            var id = g.Profile.pet;
            if (id != shown)
            {
                shown = id;
                filter.sharedMesh = id != null ? PetMeshes.Build(id) : null;
                render.enabled = id != null;
                if (id != null) transform.position = g.Player.transform.position - g.Player.Forward * 1.5f;
            }
            if (id == null) return;

            var map = WorldMap.I;
            var look = PetMeshes.LookOf(id);
            var pl = g.Player.transform.position;
            var fwd = g.Player.Forward;
            var right = Vector3.Cross(Vector3.up, fwd);
            // Beside you and a step ahead, where you can see it; walkers stay on land.
            var target = look.motion switch
            {
                PetMeshes.Motion.Fly => pl + right * 2.6f + fwd * 4f, // out in front, above eye level
                PetMeshes.Motion.Float => pl + right * 2.2f + fwd * 3f,
                _ => pl + right * 1.4f + fwd * 1.6f,
            };
            if (look.motion == PetMeshes.Motion.Walk && !Place.IsWalkable(target)) target = pl - fwd * 1.2f;
            var flat = new Vector3(transform.position.x, 0, transform.position.z);
            var goal = new Vector3(target.x, 0, target.z);
            if ((goal - flat).sqrMagnitude > 40 * 40) flat = goal; // left far behind: catch up
            var next = Vector3.SmoothDamp(flat, goal, ref vel, 0.35f, 12f);
            float speed = vel.magnitude;

            // Aboard the boat at the dock, your pet rides on deck with you.
            float ground = g.Player.Anchor != null ? g.Player.transform.position.y : Place.StandHeight(next);
            float t = Time.time + seed;
            cheer = Mathf.MoveTowards(cheer, 0, Time.deltaTime * 1.2f);
            float y = look.motion switch
            {
                PetMeshes.Motion.Fly => ground + 2.7f + Mathf.Sin(t * 2.3f) * 0.15f,
                PetMeshes.Motion.Float => Mathf.Max(ground, 0) + 1.1f + Mathf.Sin(t * 1.4f) * 0.12f,
                _ => ground,
            };
            if (look.motion == PetMeshes.Motion.Walk && speed > 0.3f)
            {
                hop += Time.deltaTime * 11;
                y += Mathf.Abs(Mathf.Sin(hop)) * 0.12f;
            }
            y += Mathf.Sin(cheer * Mathf.PI) * 0.6f; // a happy jump
            transform.position = new Vector3(next.x, y, next.z);

            // Face where it's going, or the way you're looking when it stops.
            var dir = speed > 0.3f ? vel : fwd;
            dir.y = 0;
            if (dir.sqrMagnitude > 0.001f)
            {
                var want = Quaternion.LookRotation(dir.normalized);
                if (look.motion == PetMeshes.Motion.Fly) want *= Quaternion.Euler(0, 0, Mathf.Sin(t * 9) * 8);
                transform.rotation = Quaternion.Slerp(transform.rotation, want, 1 - Mathf.Exp(-8 * Time.deltaTime));
            }
            if (cheer > 0) transform.rotation *= Quaternion.Euler(0, cheer * 360 * Time.deltaTime * 3, 0);
        }
    }
}
