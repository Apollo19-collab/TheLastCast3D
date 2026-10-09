// Builds the 3D world from the map: low-poly terrain and water in chunks,
// docks and bridges, the shop stalls, and the scenery (Scenery.cs). Colours
// follow the 2D game's theme (client/js/theme.js).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LastCast
{
    public class WorldBuilder : MonoBehaviour
    {
        const int ChunkCells = 64; // terrain cells per chunk side (128 m)
        const float WaterCell = 4f; // metres

        public static Material LitMaterial, WaterMaterial, TreeMaterial;

        static readonly Color32 GrassC = MeshKit.Hex("#4f7d3a"), SandC = MeshKit.Hex("#d9c48f"), RockC = MeshKit.Hex("#7c7f86"),
            DirtC = MeshKit.Hex("#8b6e4a"), BedC = MeshKit.Hex("#6f6a52");

        WorldMap map;
        GameData data;

        public static void LoadMaterials()
        {
            if (LitMaterial) return;
            LitMaterial = new Material(Resources.Load<Shader>("Shaders/LowPolyLit")) { enableInstancing = true };
            TreeMaterial = new Material(LitMaterial) { enableInstancing = true };
            TreeMaterial.SetFloat("_Wind", 1);
            WaterMaterial = new Material(Resources.Load<Shader>("Shaders/LowPolyWater"));
        }

        public void Build()
        {
            map = WorldMap.I;
            data = GameData.I;
            LoadMaterials();
            BuildTerrain();
            BuildWater();
            BuildStructures();
            foreach (var s in data.shops) ShopStall.Create(transform, s);
            var scenery = new GameObject("Scenery").AddComponent<Scenery>();
            scenery.transform.SetParent(transform, false);
            scenery.Build();
        }

        GameObject MeshObject(string name, Mesh mesh, Material mat, bool shadows = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            go.isStatic = true;
            return go;
        }

        // ---- terrain ---------------------------------------------------------------------------

        Color32 GroundColour(float x, float y, float h)
        {
            var g = map.GroundAt(x, y);
            Color32 c = g switch
            {
                Ground.Sand => SandC,
                Ground.Rock => RockC,
                Ground.Dirt => DirtC,
                Ground.Water => h < -0.2f ? BedC : SandC,
                _ => GrassC,
            };
            float shade = 1 - map.ForestAt(x, y) * 0.22f;
            if (g == Ground.Grass) shade *= 0.92f + Mathf.Clamp01(h / 12f) * 0.15f; // hilltops a touch lighter
            shade *= 0.95f + Noise.Value(x / 9f, y / 9f) * 0.1f; // a little variety between facets
            return MeshKit.Shade(c, shade);
        }

        void BuildTerrain()
        {
            int cellsX = map.VW - 1, cellsZ = map.VH - 1;
            float sp = WorldMap.VertexSpacing;
            for (int cz = 0; cz * ChunkCells < cellsZ; cz++)
                for (int cx = 0; cx * ChunkCells < cellsX; cx++)
                {
                    var kit = new MeshKit();
                    for (int j = cz * ChunkCells; j < Mathf.Min(cellsZ, (cz + 1) * ChunkCells); j++)
                        for (int i = cx * ChunkCells; i < Mathf.Min(cellsX, (cx + 1) * ChunkCells); i++)
                        {
                            Vector3 V(int a, int b) => new Vector3(a * sp, map.Heights[b * map.VW + a], b * sp);
                            Vector3 p00 = V(i, j), p10 = V(i + 1, j), p01 = V(i, j + 1), p11 = V(i + 1, j + 1);
                            // Alternate the diagonal for a less regular look.
                            bool flip = ((i + j) & 1) == 0;
                            if (flip)
                            {
                                AddGroundTri(kit, p00, p01, p11);
                                AddGroundTri(kit, p00, p11, p10);
                            }
                            else
                            {
                                AddGroundTri(kit, p00, p01, p10);
                                AddGroundTri(kit, p10, p01, p11);
                            }
                        }
                    var go = MeshObject($"Terrain {cx},{cz}", kit.Build($"terrain {cx},{cz}"), LitMaterial);
                    go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off; // hills are gentle; saves a lot
                }
        }

        void AddGroundTri(MeshKit kit, Vector3 a, Vector3 b, Vector3 c)
        {
            var mid = (a + b + c) / 3;
            var w = map.ToWorld(mid);
            kit.Tri(a, b, c, GroundColour(w.x, w.y, mid.y));
        }

        // ---- water -----------------------------------------------------------------------------

        void BuildWater()
        {
            Color32 shallow = new Color32(72, 168, 176, 140), deep = new Color32(16, 54, 92, 220);
            int nx = Mathf.CeilToInt(map.WidthMetres / WaterCell), nz = Mathf.CeilToInt(map.HeightMetres / WaterCell);
            int chunk = 48;
            for (int cz = 0; cz * chunk < nz; cz++)
                for (int cx = 0; cx * chunk < nx; cx++)
                {
                    var verts = new List<Vector3>();
                    var cols = new List<Color32>();
                    var shore = new List<Vector2>(); // x: how close to the shore, y: surf phase
                    var tris = new List<int>();
                    for (int j = cz * chunk; j < Mathf.Min(nz, (cz + 1) * chunk); j++)
                        for (int i = cx * chunk; i < Mathf.Min(nx, (cx + 1) * chunk); i++)
                        {
                            // Only cells at or near water.
                            bool wet = false;
                            for (int k = 0; k < 4 && !wet; k++)
                            {
                                var w = map.ToWorld(new Vector3((i + (k & 1)) * WaterCell, 0, (j + (k >> 1)) * WaterCell));
                                if (map.Sdf(w.x, w.y) < 25) wet = true;
                            }
                            if (!wet) continue;
                            int b = verts.Count;
                            for (int k = 0; k < 4; k++)
                            {
                                var p = new Vector3((i + (k & 1)) * WaterCell, 0, (j + (k >> 1)) * WaterCell);
                                var w = map.ToWorld(p);
                                float sdf = map.Sdf(w.x, w.y);
                                float depth = Noise.SmoothStep(0, 350, -sdf);
                                verts.Add(p);
                                cols.Add(Color32.Lerp(shallow, deep, depth));
                                shore.Add(new Vector2(Noise.SmoothStep(-70, 0, sdf), Noise.Value(w.x * 0.05f, w.y * 0.05f) * 12));
                            }
                            tris.AddRange(new[] { b, b + 2, b + 3, b, b + 3, b + 1 });
                        }
                    if (tris.Count == 0) continue;
                    var m = new Mesh { name = $"water {cx},{cz}" };
                    if (verts.Count > 65000) m.indexFormat = IndexFormat.UInt32;
                    m.SetVertices(verts);
                    m.SetColors(cols);
                    m.SetUVs(0, shore);
                    m.SetTriangles(tris, 0);
                    m.RecalculateNormals();
                    m.RecalculateBounds();
                    var go = MeshObject($"Water {cx},{cz}", m, WaterMaterial, false);
                    go.GetComponent<MeshRenderer>().receiveShadows = false;
                }
        }

        // ---- docks and bridges -------------------------------------------------------------------

        void BuildStructures()
        {
            // As the 2D game draws them: posts along both long edges, planks running across in
            // slightly different shades, a darker edge, and rails on the bridges.
            var kit = new MeshKit();
            Color32 plank = new Color32(158, 112, 70, 255), edge = MeshKit.Hex("#5b3c22"), post = MeshKit.Hex("#4a3020"), rail = MeshKit.Hex("#5b3c22");
            float top = WorldMap.DockHeight;
            foreach (var r in data.structures)
            {
                var rnd = new SeededRandom(Noise.SeedFrom("structure", r.x, r.y));
                var a = map.ToUnity(r.x, r.y + r.h);
                float w = r.w / WorldMap.UnitsPerMetre, d = r.h / WorldMap.UnitsPerMetre;
                bool alongX = w > d;
                float len = alongX ? w : d, across = alongX ? d : w;
                Vector3 At(float along, float side, float y) => alongX ? new Vector3(a.x + along, y, a.z + side) : new Vector3(a.x + side, y, a.z + along);
                Vector3 Size(float along, float side, float h) => alongX ? new Vector3(along, h, side) : new Vector3(side, h, along);

                // Planks, 0.7 m wide, each a slightly different shade.
                for (float t = 0; t < len; t += 0.7f)
                {
                    float shade = 1 + (rnd.Next() - 0.5f) * 0.32f;
                    float pl = Mathf.Min(0.64f, len - t);
                    kit.Box(At(t + pl / 2, across / 2, top - 0.06f), Size(pl, across - 0.1f, 0.12f), MeshKit.Shade(plank, shade));
                }
                // Edge boards.
                foreach (float side in new[] { 0.04f, across - 0.04f })
                    kit.Box(At(len / 2, side, top - 0.1f), Size(len, 0.1f, 0.2f), edge);
                // Posts every 4.4 m along both edges, down to the bed.
                for (float t = 0.6f; t < len; t += 4.4f)
                    foreach (float side in new[] { -0.2f, across + 0.2f })
                    {
                        var pos = At(t, side, 0);
                        float bottom = Mathf.Min(map.GroundHeight(pos.x, pos.z), -0.5f);
                        float postTop = top + (r.type == "bridge" ? 1.0f : 0.2f);
                        kit.Box(new Vector3(pos.x, (postTop + bottom) / 2, pos.z), new Vector3(0.3f, postTop - bottom, 0.3f), post);
                    }
                if (r.type == "bridge")
                    foreach (float side in new[] { -0.2f, across + 0.2f })
                        kit.Box(At(len / 2, side, top + 0.95f), Size(len, 0.14f, 0.14f), rail);
            }
            MeshObject("Docks", kit.Build("docks"), LitMaterial);
        }
    }
}
