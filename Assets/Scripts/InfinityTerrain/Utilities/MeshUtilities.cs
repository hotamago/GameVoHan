using UnityEngine;
using UnityEngine.Rendering;
using System;

namespace InfinityTerrain.Utilities
{
    /// <summary>
    /// Utility functions for mesh generation and manipulation.
    /// </summary>
    public static class MeshUtilities
    {
        /// <summary>
        /// Add a downward skirt to mesh borders to hide tiny cracks from LOD T-junctions.
        /// </summary>
        public static void AddSkirt(ref Mesh mesh, int res, float depth)
        {
            if (mesh == null || res < 3 || depth <= 0.001f) return;

            Vector3[] v = mesh.vertices;
            Vector2[] uv = mesh.uv;
            Vector3[] n = mesh.normals;
            int[] t = mesh.triangles;

            int borderCount = 4 * (res - 1);
            int baseVertCount = v.Length;
            Vector3[] v2 = new Vector3[baseVertCount + borderCount];
            Vector2[] uv2 = new Vector2[uv.Length + borderCount];
            Vector3[] n2 = new Vector3[n.Length + borderCount];
            Array.Copy(v, v2, baseVertCount);
            Array.Copy(uv, uv2, uv.Length);
            Array.Copy(n, n2, n.Length);

            // Build border index list in clockwise order.
            int[] border = new int[borderCount];
            int k = 0;
            // bottom row (z=0): x 0..res-2
            for (int x = 0; x < res - 1; x++) border[k++] = x;
            // right col (x=res-1): z 0..res-2
            for (int z = 0; z < res - 1; z++) border[k++] = (res - 1) + z * res;
            // top row (z=res-1): x res-1..1
            for (int x = res - 1; x > 0; x--) border[k++] = x + (res - 1) * res;
            // left col (x=0): z res-1..1
            for (int z = res - 1; z > 0; z--) border[k++] = z * res;

            // Add skirt verts
            for (int i = 0; i < borderCount; i++)
            {
                int src = border[i];
                int dst = baseVertCount + i;
                Vector3 p = v[src];
                p.y -= Mathf.Abs(depth);
                v2[dst] = p;
                uv2[dst] = uv[src];
                n2[dst] = n[src];
            }

            // Add triangles: for each border edge, connect top border vertex to its skirt copy.
            int extraTris = borderCount * 6;
            int[] t2 = new int[t.Length + extraTris];
            Array.Copy(t, t2, t.Length);
            int ti = t.Length;
            for (int i = 0; i < borderCount; i++)
            {
                int next = (i + 1) % borderCount;
                int a = border[i];
                int b = border[next];
                int a2i = baseVertCount + i;
                int b2i = baseVertCount + next;
                // Two triangles (a, b, b2) and (a, b2, a2)
                t2[ti++] = a;
                t2[ti++] = b;
                t2[ti++] = b2i;
                t2[ti++] = a;
                t2[ti++] = b2i;
                t2[ti++] = a2i;
            }

            mesh.vertices = v2;
            mesh.uv = uv2;
            mesh.normals = n2;
            mesh.triangles = t2;
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// Create a flat grid mesh for water tiles.
        /// </summary>
        public static Mesh CreateWaterGridMesh(int vertsPerSide, int sizeWorld)
        {
            vertsPerSide = Mathf.Max(2, vertsPerSide);
            if ((vertsPerSide & 1) == 0) vertsPerSide += 1; // Keep odd for symmetry
            sizeWorld = Mathf.Max(1, sizeWorld);

            int vps = vertsPerSide;
            int vertCount = vps * vps;
            int quadCount = (vps - 1) * (vps - 1);
            int triCount = quadCount * 2;
            int indexCount = triCount * 3;

            float half = sizeWorld * 0.5f;
            float step = sizeWorld / (float)(vps - 1);

            Vector3[] vertices = new Vector3[vertCount];
            Vector3[] normals = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];
            int[] tris = new int[indexCount];

            int vi = 0;
            for (int z = 0; z < vps; z++)
            {
                float wz = -half + (z * step);
                float vz = (wz + half) / sizeWorld;
                for (int x = 0; x < vps; x++)
                {
                    float wx = -half + (x * step);
                    float vx = (wx + half) / sizeWorld;

                    vertices[vi] = new Vector3(wx, 0f, wz);
                    normals[vi] = Vector3.up;
                    uvs[vi] = new Vector2(vx, vz);
                    vi++;
                }
            }

            int ti = 0;
            for (int z = 0; z < vps - 1; z++)
            {
                for (int x = 0; x < vps - 1; x++)
                {
                    int i0 = (z * vps) + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + vps;
                    int i3 = i2 + 1;

                    // Two triangles (clockwise when viewed from above)
                    tris[ti++] = i0;
                    tris[ti++] = i2;
                    tris[ti++] = i1;

                    tris[ti++] = i1;
                    tris[ti++] = i2;
                    tris[ti++] = i3;
                }
            }

            Mesh mesh = new Mesh();
            if (vertCount > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.name = $"WaterGrid_{sizeWorld}m_{vertsPerSide}";
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}

