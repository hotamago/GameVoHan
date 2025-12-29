using System;
using UnityEngine;

namespace InfinityTerrain.Vegetation
{
    /// <summary>
    /// Deterministic, stable-at-infinity 2D value-noise helpers for vegetation clustering.
    /// Uses double/long math to avoid float precision loss at extreme world coordinates.
    /// </summary>
    public static class VegetationNoise
    {
        public static float PatchFactor(double wx, double wz, GameObject prefab, int globalSeed, VegetationScatterSettings settings)
        {
            if (prefab == null || settings == null) return 1f;
            if (!settings.enableTreeClustering) return 1f;

            float cell = Mathf.Max(1f, settings.treeClusterCellSize);

            int salt = prefab.GetInstanceID();
            int seed = unchecked(globalSeed * 73856093) ^ unchecked(salt * 19349663) ^ 0x5bd1e995;

            float n = ValueNoise2D(wx, wz, cell, seed);
            float t = Mathf.InverseLerp(settings.treeClusterThreshold, 1f, n);
            t = Mathf.Clamp01(t);
            t = Mathf.Pow(t, Mathf.Max(0.01f, settings.treeClusterSharpness));

            // Keep a small base chance so species can still appear outside the best patch.
            return 0.15f + 0.85f * t;
        }

        public static float ValueNoise2D(double wx, double wz, float cellSize, int seed)
        {
            if (cellSize <= 0.0001f) return 0.5f;

            double gx = wx / cellSize;
            double gz = wz / cellSize;
            long x0 = (long)Math.Floor(gx);
            long z0 = (long)Math.Floor(gz);
            long x1 = x0 + 1;
            long z1 = z0 + 1;

            float fx = (float)(gx - x0);
            float fz = (float)(gz - z0);
            fx = Mathf.Clamp01(fx);
            fz = Mathf.Clamp01(fz);

            float u = fx * fx * (3f - 2f * fx);
            float v = fz * fz * (3f - 2f * fz);

            float a = HashTo01(Hash2D(x0, z0, seed));
            float b = HashTo01(Hash2D(x1, z0, seed));
            float c = HashTo01(Hash2D(x0, z1, seed));
            float d = HashTo01(Hash2D(x1, z1, seed));

            float ab = Mathf.Lerp(a, b, u);
            float cd = Mathf.Lerp(c, d, u);
            return Mathf.Lerp(ab, cd, v);
        }

        private static uint Hash32(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }

        private static uint Hash2D(long x, long z, int seed)
        {
            unchecked
            {
                uint h = Hash32((uint)seed ^ 0xA341316Cu);
                uint xLo = (uint)x;
                uint xHi = (uint)(x >> 32);
                uint zLo = (uint)z;
                uint zHi = (uint)(z >> 32);

                h = Hash32(h ^ xLo);
                h = Hash32(h ^ xHi);
                h = Hash32(h ^ zLo);
                h = Hash32(h ^ zHi);
                return h;
            }
        }

        private static float HashTo01(uint h)
        {
            return (h & 0x00FFFFFFu) / 16777216.0f;
        }
    }
}


