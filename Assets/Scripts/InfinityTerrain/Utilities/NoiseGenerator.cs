using UnityEngine;

namespace InfinityTerrain.Utilities
{
    /// <summary>
    /// CPU-based noise generation for terrain height approximation (used for safety checks).
    /// Stable at infinity using 64-bit integer coordinates.
    /// </summary>
    public static class NoiseGenerator
    {
        private static uint Hash32(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }

        private static uint HashCombine(uint h, uint v)
        {
            unchecked
            {
                return Hash32(h ^ (v + 0x9e3779b9u + (h << 6) + (h >> 2)));
            }
        }

        private static float HashTo01(uint h) => (h & 0x00FFFFFFu) / 16777216.0f;

        private static float Hash2D64(ulong x, ulong y, uint salt)
        {
            uint h = Hash32(salt);
            h = HashCombine(h, (uint)x);
            h = HashCombine(h, (uint)(x >> 32));
            h = HashCombine(h, (uint)y);
            h = HashCombine(h, (uint)(y >> 32));
            return HashTo01(h);
        }

        private static float ValueNoise64(ulong gx, ulong gz, int shift, uint salt)
        {
            shift = Mathf.Clamp(shift, 0, 30);
            if (shift == 0) return Hash2D64(gx, gz, salt);

            ulong mask = (1ul << shift) - 1ul;
            float fx = (gx & mask) / (float)(1ul << shift);
            float fz = (gz & mask) / (float)(1ul << shift);
            float ux = fx * fx * (3f - 2f * fx);
            float uz = fz * fz * (3f - 2f * fz);

            ulong cellX = gx >> shift;
            ulong cellZ = gz >> shift;

            float a = Hash2D64(cellX, cellZ, salt);
            float b = Hash2D64(cellX + 1ul, cellZ, salt);
            float c = Hash2D64(cellX, cellZ + 1ul, salt);
            float d = Hash2D64(cellX + 1ul, cellZ + 1ul, salt);

            float ab = Mathf.Lerp(a, b, ux);
            float cd = Mathf.Lerp(c, d, ux);
            return Mathf.Lerp(ab, cd, uz);
        }

        private static float Fbm64(ulong gx, ulong gz, int baseShift, int oct, float pers, uint saltBase)
        {
            float value = 0f;
            float amplitude = 0.5f;
            float maxValue = 0f;

            for (int i = 0; i < oct; i++)
            {
                int s = Mathf.Max(0, baseShift - i);
                value += amplitude * ValueNoise64(gx, gz, s, saltBase + (uint)(i * 1013));
                maxValue += amplitude;
                amplitude *= pers;
            }

            return value / Mathf.Max(maxValue, 1e-6f);
        }

        /// <summary>
        /// Get terrain height at a specific chunk coordinate and in-chunk position.
        /// Uses CPU noise approximation matching the GPU shader.
        /// </summary>
        public static float GetTerrainHeightCPU(
            long chunkX, 
            long chunkY, 
            float inChunkX, 
            float inChunkZ,
            int resolution,
            int chunkSize,
            float heightMultiplier,
            float noiseScale,
            float mountainStrength,
            int seed)
        {
            // Match the shader's integer base vertex-grid space (approximation).
            int vertsPerChunk = Mathf.Max(1, resolution - 1);
            float stepWorld = chunkSize / (float)vertsPerChunk;

            int vx = Mathf.Clamp(Mathf.RoundToInt(inChunkX / stepWorld), 0, vertsPerChunk);
            int vz = Mathf.Clamp(Mathf.RoundToInt(inChunkZ / stepWorld), 0, vertsPerChunk);

            ulong gx = unchecked((ulong)chunkX) * (ulong)vertsPerChunk + (ulong)vx;
            ulong gz = unchecked((ulong)chunkY) * (ulong)vertsPerChunk + (ulong)vz;

            uint s0 = Hash32((uint)seed ^ 0xA341316Cu);
            int noiseShift = ComputeNoiseShift(noiseScale, resolution, chunkSize);
            float continents = Fbm64(gx, gz, noiseShift + 4, 3, 0.5f, s0);

            float height01 = (continents < 0.30f) ? (continents * 0.80f) : continents;
            if (continents > 0.6f) height01 += 0.2f * mountainStrength;

            return Mathf.Clamp01(height01) * heightMultiplier;
        }

        /// <summary>
        /// Convert noise scale to a power-of-two shift value for stable noise generation.
        /// </summary>
        public static int ComputeNoiseShift(float scale, int resolution, int chunkSize)
        {
            if (scale <= 0f) return 8;

            float stepWorld = chunkSize / (float)(resolution - 1);
            float cellSizeVerts = (1f / scale) / stepWorld;
            if (cellSizeVerts <= 1f) return 0;

            int shift = Mathf.RoundToInt(Mathf.Log(cellSizeVerts, 2f));
            return Mathf.Clamp(shift, 0, 30);
        }
    }
}

