using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace InfinityTerrain.Vegetation
{
    /// <summary>
    /// Burst + Jobs implementation for generating Unity Terrain detail layers from a heightmap.
    /// Designed to be called from main thread, then applied to TerrainData on main thread.
    /// </summary>
    public static class TerrainDetailScatterBurstJobs
    {
        [BurstCompile(FloatPrecision.Standard, FloatMode.Fast)]
        public struct DetailScatterJob : IJobParallelFor
        {
            // Heightmap (0..1), flattened [z*heightRes + x] == heights01[z,x]
            [ReadOnly] public NativeArray<float> heights01Flat;
            [ReadOnly] public int heightRes;
            [ReadOnly] public float heightStep; // world units per heightmap cell
            [ReadOnly] public float heightMultiplier;
            [ReadOnly] public float waterSurfaceY;

            [ReadOnly] public float minHeight01;
            [ReadOnly] public float maxHeight01;
            [ReadOnly] public float maxSlopeDeg;
            [ReadOnly] public float waterExclusionYOffset;

            // Detail map resolution (cells)
            [ReadOnly] public int detailRes;
            [ReadOnly] public float chunkSizeWorld;

            // Deterministic per cell
            [ReadOnly] public long chunkX;
            [ReadOnly] public long chunkY;
            [ReadOnly] public int seed;

            [ReadOnly] public bool useCoverage;

            // Grass
            [ReadOnly] public bool includeGrass;
            [ReadOnly] public float grassDensity01;
            [ReadOnly] public int grassMaxPerCell;
            [ReadOnly] public float grassPerChunk;
            [ReadOnly] public float areaScale;
            [ReadOnly] public float chunkArea;
            [ReadOnly] public float cellArea;
            [ReadOnly] public NativeArray<int> grassProtoIdx;
            [ReadOnly] public NativeArray<float> grassCumWeights;

            // Plants
            [ReadOnly] public bool includePlants;
            [ReadOnly] public float plantsDensity01;
            [ReadOnly] public int plantsMaxPerCell;
            [ReadOnly] public float plantsPerChunk;
            [ReadOnly] public NativeArray<int> plantProtoIdx;
            [ReadOnly] public NativeArray<float> plantCumWeights;

            // Outputs: per-cell chosen layer index + value
            public NativeArray<short> outGrassLayer;
            public NativeArray<byte> outGrassValue;
            public NativeArray<short> outPlantLayer;
            public NativeArray<byte> outPlantValue;

            public void Execute(int index)
            {
                int y = index / detailRes;
                int x = index - (y * detailRes);

                float cellSize = chunkSizeWorld / math.max(1, detailRes);
                float localX = (x + 0.5f) * cellSize;
                float localZ = (y + 0.5f) * cellSize;

                // Filters
                float h01 = SampleHeight01Bilinear(localX, localZ);
                float hWorld = h01 * heightMultiplier;
                if (h01 < minHeight01 || h01 > maxHeight01)
                    return;
                if (hWorld < (waterSurfaceY + waterExclusionYOffset))
                    return;

                float slope = SampleSlopeDeg(localX, localZ);
                if (slope > maxSlopeDeg)
                    return;

                // Init outputs (default is 0); only write when placing.
                if (includeGrass && grassProtoIdx.Length > 0)
                {
                    if (useCoverage)
                    {
                        if (grassDensity01 > 0f)
                        {
                            float rCov = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x13579BDF));
                            int cov = SampleValue01ToInt(grassDensity01, 255, rCov);
                            if (cov > 0)
                            {
                                float rPick = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x2468ACE0));
                                int layer = PickWeightedIndex(rPick, grassProtoIdx, grassCumWeights);
                                if (layer >= 0)
                                {
                                    outGrassLayer[index] = (short)layer;
                                    outGrassValue[index] = (byte)cov;
                                }
                            }
                        }
                    }
                    else
                    {
                        float rCount = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x13579BDF));
                        int count;
                        if (grassDensity01 > 0f)
                        {
                            count = SampleValue01ToInt(grassDensity01, 16, rCount);
                        }
                        else
                        {
                            if (grassMaxPerCell <= 0) count = 0;
                            else
                            {
                                float lambda = ComputeLambdaPerCell(0f, grassPerChunk * areaScale, chunkArea, cellArea);
                                count = SampleCount(lambda, grassMaxPerCell, rCount);
                            }
                        }

                        if (count > 0)
                        {
                            float rPick = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x2468ACE0));
                            int layer = PickWeightedIndex(rPick, grassProtoIdx, grassCumWeights);
                            if (layer >= 0)
                            {
                                outGrassLayer[index] = (short)layer;
                                outGrassValue[index] = (byte)count;
                            }
                        }
                    }
                }

                if (includePlants && plantProtoIdx.Length > 0)
                {
                    if (useCoverage)
                    {
                        if (plantsDensity01 > 0f)
                        {
                            float rCov = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x66778899));
                            int cov = SampleValue01ToInt(plantsDensity01, 255, rCov);
                            if (cov > 0)
                            {
                                float rPick = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ unchecked((int)0x99AABBCC)));
                                int layer = PickWeightedIndex(rPick, plantProtoIdx, plantCumWeights);
                                if (layer >= 0)
                                {
                                    outPlantLayer[index] = (short)layer;
                                    outPlantValue[index] = (byte)cov;
                                }
                            }
                        }
                    }
                    else
                    {
                        float rCount = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x66778899));
                        int count;
                        if (plantsDensity01 > 0f)
                        {
                            count = SampleValue01ToInt(plantsDensity01, 16, rCount);
                        }
                        else
                        {
                            if (plantsMaxPerCell <= 0) count = 0;
                            else
                            {
                                float lambda = ComputeLambdaPerCell(0f, plantsPerChunk * areaScale, chunkArea, cellArea);
                                count = SampleCount(lambda, plantsMaxPerCell, rCount);
                            }
                        }

                        if (count > 0)
                        {
                            float rPick = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ unchecked((int)0x99AABBCC)));
                            int layer = PickWeightedIndex(rPick, plantProtoIdx, plantCumWeights);
                            if (layer >= 0)
                            {
                                outPlantLayer[index] = (short)layer;
                                outPlantValue[index] = (byte)count;
                            }
                        }
                    }
                }
            }

            private float SampleHeight01Bilinear(float localX, float localZ)
            {
                if (heightRes <= 1) return math.saturate(heights01Flat[0]);

                float step = math.max(0.0001f, heightStep);
                float fx = math.clamp(localX / step, 0f, heightRes - 1.001f);
                float fz = math.clamp(localZ / step, 0f, heightRes - 1.001f);

                int x0 = (int)math.floor(fx);
                int z0 = (int)math.floor(fz);
                int x1 = math.min(heightRes - 1, x0 + 1);
                int z1 = math.min(heightRes - 1, z0 + 1);

                float tx = fx - x0;
                float tz = fz - z0;

                float a = heights01Flat[z0 * heightRes + x0];
                float b = heights01Flat[z0 * heightRes + x1];
                float c = heights01Flat[z1 * heightRes + x0];
                float d = heights01Flat[z1 * heightRes + x1];

                return math.saturate(math.lerp(math.lerp(a, b, tx), math.lerp(c, d, tx), tz));
            }

            private float SampleSlopeDeg(float localX, float localZ)
            {
                if (heightRes <= 2) return 0f;
                float step = math.max(0.0001f, heightStep);

                int ix = math.clamp((int)math.round(localX / step), 1, heightRes - 2);
                int iz = math.clamp((int)math.round(localZ / step), 1, heightRes - 2);

                float hL = heights01Flat[iz * heightRes + (ix - 1)] * heightMultiplier;
                float hR = heights01Flat[iz * heightRes + (ix + 1)] * heightMultiplier;
                float hD = heights01Flat[(iz - 1) * heightRes + ix] * heightMultiplier;
                float hU = heights01Flat[(iz + 1) * heightRes + ix] * heightMultiplier;

                float dx = (hR - hL) / (2f * step);
                float dz = (hU - hD) / (2f * step);

                float3 n = math.normalize(new float3(-dx, 1f, -dz));
                // Angle between n and up
                float dot = math.clamp(n.y, -1f, 1f);
                return math.degrees(math.acos(dot));
            }

            private static float ComputeLambdaPerCell(float densityPerM2, float perChunkTarget, float chunkArea, float cellArea)
            {
                if (chunkArea <= 0.0001f || cellArea <= 0.0001f) return 0f;
                float total = densityPerM2 > 0f ? densityPerM2 * chunkArea : math.max(0f, perChunkTarget);
                float effectiveDensity = total / chunkArea;
                return effectiveDensity * cellArea;
            }

            private static int SampleValue01ToInt(float value01, int maxInclusive, float r01)
            {
                if (maxInclusive <= 0) return 0;
                float v01 = math.clamp(value01, 0f, 1f);
                float scaled = v01 * maxInclusive;
                int baseV = (int)math.floor(scaled);
                float frac = scaled - baseV;
                int v = baseV + ((r01 < frac) ? 1 : 0);
                if (v < 0) return 0;
                if (v > maxInclusive) return maxInclusive;
                return v;
            }

            private static int SampleCount(float lambda, int maxPerCell, float r01)
            {
                if (maxPerCell <= 0) return 0;
                if (lambda <= 0f) return 0;
                if (lambda >= maxPerCell) return maxPerCell;
                int baseCount = (int)math.floor(lambda);
                float frac = lambda - baseCount;
                int c = baseCount + ((r01 < frac) ? 1 : 0);
                if (c > maxPerCell) c = maxPerCell;
                if (c < 0) c = 0;
                return c;
            }

            private static int PickWeightedIndex(float r01, NativeArray<int> indices, NativeArray<float> cumWeights)
            {
                if (indices.Length == 0 || cumWeights.Length != indices.Length) return -1;
                float total = cumWeights[cumWeights.Length - 1];
                if (total <= 0f) return indices[0];
                float r = math.clamp(r01, 0f, 1f) * total;

                // Binary search first cum >= r
                int lo = 0;
                int hi = cumWeights.Length - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (r <= cumWeights[mid]) hi = mid;
                    else lo = mid + 1;
                }
                return indices[lo];
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

            private static uint HashCell(long chunkX, long chunkY, int x, int y, int salt)
            {
                unchecked
                {
                    uint h = 0x811C9DC5u;
                    h = Hash32(h ^ (uint)chunkX);
                    h = Hash32(h ^ (uint)(chunkX >> 32));
                    h = Hash32(h ^ (uint)chunkY);
                    h = Hash32(h ^ (uint)(chunkY >> 32));
                    h = Hash32(h ^ (uint)x);
                    h = Hash32(h ^ (uint)y);
                    h = Hash32(h ^ (uint)salt);
                    return h;
                }
            }

            private static float Hash01(uint h)
            {
                return (h & 0x00FFFFFFu) / 16777216.0f;
            }
        }
    }
}


