// A pet's body, rigged and moving (the Blender pets' limbs, blender/lastcast_models.py): legs walking in step
// (faster as it runs), wings flapping as it flies, a tail wagging (or a fish's tail sweeping it along), tentacles
// rippling, a crab's claws clacking and its legs scuttling, and its head looking about. It works out its own
// speed from how it moves, so whatever carries it (your pet, another angler's) just moves the parent.
// Limbs: 1-4 legs (front left, front right, back left, back right; a bird's are 3 and 4), 5-6 wings, flippers,
// fins or claws, 7 the tail (a serpent's whole body), 8 the head, 9-16 tentacles or a crab's legs.

using UnityEngine;

namespace LastCast
{
    public class PetAnimator : MonoBehaviour
    {
        LimbRig rig;
        string id;
        PetMeshes.Motion motion;
        Vector3 last;
        float speed, phase, seed;
        /// <summary>A jump for joy (1, dying away), set by whatever owns the pet.</summary>
        public float Cheer;

        static int BonesFor(int limb) => limb switch { 7 => 4, 8 => 1, >= 9 => 4, _ => 2 };

        /// <summary>The pet's body under parent: rigged if its model has limbs, else a plain mesh.</summary>
        public static PetAnimator Create(Transform parent, string id)
        {
            var go = new GameObject("Pet body");
            go.transform.SetParent(parent, false);
            var a = go.AddComponent<PetAnimator>();
            a.id = id;
            a.motion = PetMeshes.LookOf(id).motion;
            a.seed = Random.value * 10;
            var mesh = PetMeshes.Build(id);
            a.rig = LimbRig.Attach(go, mesh, WorldBuilder.LitMaterial, 2, bonesFor: BonesFor, rooted: true, tag: "pet");
            if (!a.rig)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = WorldBuilder.LitMaterial;
            }
            a.last = go.transform.position;
            return a;
        }

        void Set(int limb, int bone, Quaternion q)
        {
            var l = rig[limb];
            if (l != null && bone < l.Count) l.Bones[bone].localRotation = q;
        }

        void LateUpdate()
        {
            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            var p = transform.position;
            var moved = p - last;
            last = p;
            moved.y = 0;
            speed = Mathf.Lerp(speed, Mathf.Min(12, moved.magnitude / dt), 1 - Mathf.Exp(-8 * dt));
            if (!rig) return;
            float t = Time.time + seed;
            float walk = Mathf.Clamp01(speed / 2.5f);
            phase += dt * (2.5f + speed * 2.2f);
            Cheer = Mathf.MoveTowards(Cheer, 0, dt);

            // Legs: diagonal pairs swing together; the knee bends on the swing forward.
            float swing = 30 * walk;
            for (int leg = 1; leg <= 4; leg++)
            {
                float ph = phase + (leg == 1 || leg == 4 ? 0 : Mathf.PI);
                float hip = -swing * Mathf.Sin(ph), knee = swing * 0.9f * Mathf.Max(0, Mathf.Cos(ph));
                if (motion == PetMeshes.Motion.Fly && speed > 0.5f) { hip = 25; knee = 35; } // tucked up in flight
                if (id == "lilyfrog" && leg >= 3) { float jump = Mathf.Max(0, Mathf.Sin(phase * 0.5f)) * walk; hip = -40 * jump; knee = -30 * jump; }
                Set(leg, 0, Quaternion.Euler(hip, 0, 0));
                Set(leg, 1, Quaternion.Euler(knee, 0, 0));
            }

            // Wings, flippers, fins and claws.
            for (int limb = 5; limb <= 6; limb++)
            {
                float side = limb == 5 ? -1 : 1;
                float a;
                if (id == "sandcrab" || id == "hermit")
                {
                    float clack = Mathf.Max(0, Mathf.Sin(t * 3.1f + side)) * 18;
                    Set(limb, 0, Quaternion.Euler(-clack * 0.4f, side * Mathf.Sin(t * 1.3f) * 10, 0));
                    Set(limb, 1, Quaternion.Euler(-clack, 0, 0));
                    continue;
                }
                if (motion == PetMeshes.Motion.Fly)
                    a = (id == "moonmoth" ? Mathf.Sin(t * 5) * 40 : Mathf.Sin(t * (speed > 0.5f ? 14 : 4)) * (speed > 0.5f ? 45 : 10)) + 5;
                else if (motion == PetMeshes.Motion.Float)
                    a = Mathf.Sin(t * 3 + side) * 18; // fins paddling
                else
                    a = id == "penguin" ? Mathf.Sin(phase) * 25 * walk + 10 : Mathf.Sin(t * 2) * 4 + 20 * Cheer;
                Set(limb, 0, Quaternion.AngleAxis(side * a, Vector3.forward));
                Set(limb, 1, Quaternion.AngleAxis(side * a * 0.6f, Vector3.forward));
            }

            // The tail: a wag (faster when it's happy), or a swimmer's sweep.
            var tail = rig[7];
            if (tail != null)
            {
                bool swimmer = id is "spiritkoi" or "starwhale" or "wyrmling";
                for (int i = 0; i < tail.Count; i++)
                {
                    float s = (i + 1f) / tail.Count;
                    Quaternion q;
                    if (id == "starwhale") q = Quaternion.AngleAxis(Mathf.Sin(t * 2 - i * 0.8f) * 12 * s, tail.Across); // flukes beat up and down
                    else if (swimmer) q = Quaternion.AngleAxis(Mathf.Sin(t * (id == "wyrmling" ? 2.5f : 4) - i * 0.9f) * (id == "wyrmling" ? 14 : 18) * s, Vector3.up);
                    else q = Quaternion.AngleAxis(Mathf.Sin(t * (6 + 8 * Cheer) - i * 0.7f) * (12 + 20 * Cheer) * s, Vector3.up)
                             * Quaternion.AngleAxis(Mathf.Sin(t * 1.3f - i * 0.5f) * 5 * s, tail.Across);
                    tail.Bones[i].localRotation = q;
                }
            }

            // The head looks about, nods with its steps, and tips up when it cheers.
            float look = Mathf.Sin(t * 0.45f) * 18 + Mathf.Sin(t * 1.1f) * 6;
            float nod = Mathf.Sin(phase * 2) * 5 * walk - 15 * Cheer;
            Set(8, 0, Quaternion.Euler(nod, look, Mathf.Sin(t * 0.7f) * 4));

            // Tentacles ripple; a crab's legs scuttle.
            for (int limb = 9; limb <= 16; limb++)
            {
                var l = rig[limb];
                if (l == null) continue;
                bool crab = id is "sandcrab" or "hermit";
                for (int i = 0; i < l.Count; i++)
                {
                    float s = (i + 1f) / l.Count;
                    if (crab)
                    {
                        float ph = phase * 1.6f + limb * 1.9f;
                        l.Bones[i].localRotation = Quaternion.Euler(i == 0 ? -Mathf.Max(0, Mathf.Sin(ph)) * 25 * (0.3f + walk) : 0, i == 0 ? Mathf.Sin(ph) * 15 * (0.3f + walk) : 0, 0);
                    }
                    else
                        l.Bones[i].localRotation = Quaternion.AngleAxis(Mathf.Sin(t * 2.2f - i * 0.9f + limb) * 14 * s, l.Across)
                                                   * Quaternion.AngleAxis(Mathf.Sin(t * 1.6f - i * 0.7f + limb * 1.3f) * 10 * s, Vector3.up);
                }
            }
        }
    }
}
