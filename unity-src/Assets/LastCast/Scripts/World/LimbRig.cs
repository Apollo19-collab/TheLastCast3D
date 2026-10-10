// Bones for the Blender models' moving parts (blender/lastcast_models.py, Kit.limb). In those models the
// UV channel carries animation data: u is the limb a vertex belongs to (0 = the rigid body, then 1, 2...)
// and v how far along that limb it is (0 at its root, 1 at its tip). Each limb becomes a chain of bones down
// its middle (or at given joints: hips and knees, shoulders and elbows) and the mesh is skinned to them, so
// turning the bones curls a tentacle, swings a leg or wags a tail. The code that moves them is in
// BossMotion (the bosses), RemoteAngler (other players) and RodView (a fish you've just landed).

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LastCast
{
    public class LimbRig : MonoBehaviour
    {
        public class Limb
        {
            public Transform[] Bones;
            public Vector3[] Joints; // where each bone pivots, in the model's space
            /// <summary>From the limb's root towards its tip, and a level axis across it (turning about it lifts or lowers the limb).</summary>
            public Vector3 Dir, Across;
            public int Count => Bones.Length;
        }

        public Transform Root { get; private set; }
        public SkinnedMeshRenderer Renderer { get; private set; }
        /// <summary>The limbs by number (Limbs[0], the rigid body, is the root bone alone).</summary>
        public Limb[] Limbs { get; private set; }

        static readonly Dictionary<(Mesh, string), (Mesh mesh, Vector3[][] joints)> cache = new Dictionary<(Mesh, string), (Mesh, Vector3[][])>();

        /// <summary>Whether a model has limbs to animate.</summary>
        public static bool HasLimbs(Mesh m)
        {
            if (!m || !m.isReadable) return false;
            var uv = new List<Vector2>();
            m.GetUVs(0, uv);
            return uv.Any(u => u.x > 0.5f);
        }

        /// <summary>
        /// Skin a model onto go with a bone chain per limb: `bones` bones down each limb's middle, or at the
        /// joints given for a limb. hard: each vertex follows one bone (a knee or an elbow bends sharply) rather
        /// than blending between neighbours (a tentacle bends smoothly). Returns null if the model has no limbs.
        /// </summary>
        public static LimbRig Attach(GameObject go, Mesh src, Material material, int bones = 8, Dictionary<int, Vector3[]> joints = null, bool hard = false)
        {
            if (!HasLimbs(src)) return null;
            var key = (src, $"{bones}|{hard}|" + (joints == null ? "" : string.Join(";", joints.OrderBy(j => j.Key).Select(j => j.Key + ":" + string.Join(",", j.Value)))));
            if (!cache.TryGetValue(key, out var built)) cache[key] = built = Build(src, bones, joints, hard);

            var rig = go.AddComponent<LimbRig>();
            rig.Root = new GameObject("Bones").transform;
            rig.Root.SetParent(go.transform, false);
            var all = new List<Transform> { rig.Root };
            rig.Limbs = new Limb[built.joints.Length];
            rig.Limbs[0] = new Limb { Bones = new[] { rig.Root }, Joints = new[] { Vector3.zero }, Dir = Vector3.forward, Across = Vector3.right };
            for (int l = 1; l < built.joints.Length; l++)
            {
                var js = built.joints[l];
                if (js == null) continue;
                var limb = new Limb { Bones = new Transform[js.Length], Joints = js };
                Transform parent = rig.Root;
                var prev = Vector3.zero;
                for (int i = 0; i < js.Length; i++)
                {
                    var b = new GameObject($"Limb {l}.{i}").transform;
                    b.SetParent(parent, false);
                    b.localPosition = js[i] - prev;
                    limb.Bones[i] = b;
                    all.Add(b);
                    parent = b;
                    prev = js[i];
                }
                var tip = js.Length > 1 ? js[js.Length - 1] : LimbTip(src, l, js[0]);
                limb.Dir = (tip - js[0]).sqrMagnitude > 1e-6f ? (tip - js[0]).normalized : Vector3.forward;
                var across = Vector3.Cross(Vector3.up, limb.Dir);
                limb.Across = across.sqrMagnitude > 1e-4f ? across.normalized : Vector3.right;
                rig.Limbs[l] = limb;
            }

            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = built.mesh;
            smr.bones = all.ToArray();
            smr.rootBone = rig.Root;
            smr.sharedMaterial = material;
            var bounds = built.mesh.bounds;
            bounds.Expand(bounds.size.magnitude * 0.5f);
            smr.localBounds = bounds;
            rig.Renderer = smr;
            return rig;
        }

        /// <summary>A limb's farthest point from its root (for a single-bone limb's direction).</summary>
        static Vector3 LimbTip(Mesh src, int limb, Vector3 root)
        {
            var uv = new List<Vector2>();
            src.GetUVs(0, uv);
            var v = src.vertices;
            var best = root;
            float far = 0;
            for (int i = 0; i < v.Length; i++)
                if (Mathf.RoundToInt(uv[i].x) == limb && (v[i] - root).sqrMagnitude > far)
                {
                    far = (v[i] - root).sqrMagnitude;
                    best = v[i];
                }
            return best;
        }

        static (Mesh, Vector3[][]) Build(Mesh src, int bones, Dictionary<int, Vector3[]> given, bool hard)
        {
            var uv = new List<Vector2>();
            src.GetUVs(0, uv);
            var verts = src.vertices;
            int limbs = 0;
            foreach (var u in uv) limbs = Mathf.Max(limbs, Mathf.RoundToInt(u.x));
            var joints = new Vector3[limbs + 1][];
            for (int l = 1; l <= limbs; l++)
            {
                if (given != null && given.TryGetValue(l, out var j)) { joints[l] = j; continue; }
                // Down the limb's middle: the centre of each stretch of it.
                var sum = new Vector3[bones];
                var count = new int[bones];
                for (int i = 0; i < verts.Length; i++)
                {
                    if (Mathf.RoundToInt(uv[i].x) != l) continue;
                    int b = Mathf.Clamp((int)(uv[i].y * bones), 0, bones - 1);
                    sum[b] += verts[i];
                    count[b]++;
                }
                if (count.All(c => c == 0)) continue;
                var js = new Vector3[bones];
                for (int b = 0; b < bones; b++)
                {
                    int near = b;
                    for (int d = 1; count[near] == 0 && d < bones; d++)
                        near = b - d >= 0 && count[b - d] > 0 ? b - d : b + d < bones && count[b + d] > 0 ? b + d : near;
                    js[b] = sum[near] / Mathf.Max(1, count[near]);
                }
                joints[l] = js;
            }

            // Bone 0 is the root; then each limb's bones in order.
            var first = new int[limbs + 1];
            int next = 1;
            for (int l = 1; l <= limbs; l++)
            {
                first[l] = next;
                if (joints[l] != null) next += joints[l].Length;
            }
            var weights = new BoneWeight[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                int l = Mathf.RoundToInt(uv[i].x);
                if (l <= 0 || l > limbs || joints[l] == null) { weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 }; continue; }
                int n = joints[l].Length;
                float t = Mathf.Clamp01(uv[i].y);
                if (hard || n == 1)
                {
                    weights[i] = new BoneWeight { boneIndex0 = first[l] + Mathf.Clamp((int)(t * n), 0, n - 1), weight0 = 1 };
                    continue;
                }
                float f = t * n - 0.5f;
                int s = Mathf.FloorToInt(f);
                float w = f - s;
                if (s < 0) weights[i] = new BoneWeight { boneIndex0 = first[l], weight0 = 1 };
                else if (s >= n - 1) weights[i] = new BoneWeight { boneIndex0 = first[l] + n - 1, weight0 = 1 };
                else weights[i] = new BoneWeight { boneIndex0 = first[l] + s, weight0 = 1 - w, boneIndex1 = first[l] + s + 1, weight1 = w };
            }
            var bind = new List<Matrix4x4> { Matrix4x4.identity };
            for (int l = 1; l <= limbs; l++)
                if (joints[l] != null)
                    foreach (var j in joints[l]) bind.Add(Matrix4x4.Translate(-j));

            var m = Instantiate(src);
            m.name = src.name + " (rigged)";
            m.boneWeights = weights;
            m.bindposes = bind.ToArray();
            return (m, joints);
        }

        /// <summary>Put every bone back in its rest pose.</summary>
        public void Rest()
        {
            if (Limbs == null) return;
            foreach (var limb in Limbs)
                if (limb != null)
                    foreach (var b in limb.Bones) b.localRotation = Quaternion.identity;
        }

        public Limb this[int limb] => Limbs != null && limb < Limbs.Length ? Limbs[limb] : null;
    }
}
