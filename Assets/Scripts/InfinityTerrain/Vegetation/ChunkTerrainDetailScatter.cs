using System;
using System.Collections.Generic;
using UnityEngine;

namespace InfinityTerrain.Vegetation
{
    /// <summary>
    /// Spawns vegetation into Unity Terrain Detail Layers (fast grass-like rendering).
    /// Intended for Built-in Terrain chunks (TerrainData.detailPrototypes + SetDetailLayer).
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkTerrainDetailScatter : MonoBehaviour
    {
        [NonSerialized] public VegetationScatterSettings settings;
        [NonSerialized] public int globalSeed;
        [NonSerialized] public float heightMultiplier;
        [NonSerialized] public float waterSurfaceY;

        public bool includePlants = true;
        public bool includeGrass = true;

        private long _lastChunkX;
        private long _lastChunkY;
        private int _lastLodResolution;
        private int _lastSeed;
        private int _lastSettingsHash;

        private struct EntryRef
        {
            public VegetationPrefabEntry entry;
            public int prototypeIndex;
        }

        public void Clear()
        {
            Terrain t = GetComponent<Terrain>();
            if (t == null || t.terrainData == null) return;
            TerrainData td = t.terrainData;

            try
            {
                int res = td.detailResolution;
                int layers = td.detailPrototypes != null ? td.detailPrototypes.Length : 0;
                if (res > 0 && layers > 0)
                {
                    int[,] zero = new int[res, res];
                    for (int l = 0; l < layers; l++)
                    {
                        td.SetDetailLayer(0, 0, l, zero);
                    }
                }
            }
            catch
            {
                // Terrain detail APIs can throw if resolution/prototypes are not initialized; ignore.
            }

            _lastSeed = 0;
        }

        public bool EnsureGenerated(long chunkX, long chunkY, int lodResolution, float chunkSizeWorld, float[,] heights01, bool allowGeneration)
        {
            if (settings == null) return false;
            if (heights01 == null) return false;
            if (heightMultiplier <= 0.0001f) return false;

            Terrain terrain = GetComponent<Terrain>();
            if (terrain == null || terrain.terrainData == null) return false;

            int seed = CombineSeed(globalSeed, settings.seedOffset, chunkX, chunkY);
            int settingsHash = ComputeSettingsHash(settings, includeGrass, includePlants);

            bool same =
                _lastChunkX == chunkX &&
                _lastChunkY == chunkY &&
                _lastLodResolution == lodResolution &&
                _lastSeed == seed &&
                _lastSettingsHash == settingsHash;

            if (same) return false;
            if (!allowGeneration)
            {
                if (_lastChunkX != chunkX || _lastChunkY != chunkY) Clear();
                return false;
            }

            _lastChunkX = chunkX;
            _lastChunkY = chunkY;
            _lastLodResolution = lodResolution;
            _lastSeed = seed;
            _lastSettingsHash = settingsHash;

            Regenerate(terrain, seed, chunkX, chunkY, chunkSizeWorld, heights01);
            return true;
        }

        private void Regenerate(Terrain terrain, int seed, long chunkX, long chunkY, float chunkSizeWorld, float[,] heights01)
        {
            TerrainData td = terrain.terrainData;
            if (td == null) return;

            CollectEntries(settings, includeGrass, includePlants, out List<VegetationPrefabEntry> grass, out List<VegetationPrefabEntry> plants);
            if (grass.Count == 0 && plants.Count == 0)
            {
                Clear();
                return;
            }

            // Build unique detail prototypes (order: grass then plants)
            List<EntryRef> grassRefs = new List<EntryRef>();
            List<EntryRef> plantRefs = new List<EntryRef>();

            DetailPrototype[] protos = BuildDetailPrototypes(grass, plants, grassRefs, plantRefs);
            if (protos == null || protos.Length == 0)
            {
                Clear();
                return;
            }

            td.detailPrototypes = protos;

            int detailRes = Mathf.Clamp(settings.terrainDetailResolution, 32, 1024);
            int perPatch = Mathf.Clamp(settings.terrainDetailResolutionPerPatch, 4, 64);
            td.SetDetailResolution(detailRes, perPatch);

            // Generate layers (intensity per cell)
            float area = chunkSizeWorld * chunkSizeWorld;
            float areaScale = area / (100f * 100f);
            if (areaScale < 0.01f) areaScale = 0.01f;

            float grassDensity = settings.grassDensityPerM2 > 0f ? settings.grassDensityPerM2 : (Mathf.Max(0, settings.grassPerChunk) * areaScale) / Mathf.Max(0.0001f, area);
            float plantDensity = settings.plantsDensityPerM2 > 0f ? settings.plantsDensityPerM2 : (Mathf.Max(0, settings.plantsPerChunk) * areaScale) / Mathf.Max(0.0001f, area);

            int grassCapTotal = Mathf.RoundToInt(settings.maxGrassPerChunk * areaScale);
            int plantsCapTotal = Mathf.RoundToInt(settings.maxPlantsPerChunk * areaScale);

            // Pre-allocate all layers so we can SetDetailLayer once per layer.
            int layers = protos.Length;
            int[][,] maps = new int[layers][,];
            for (int i = 0; i < layers; i++) maps[i] = new int[detailRes, detailRes];

            float cellSize = chunkSizeWorld / Mathf.Max(1, detailRes);
            float cellArea = cellSize * cellSize;

            int grassSpawned = 0;
            int plantsSpawned = 0;

            int baseSeed = seed;

            for (int y = 0; y < detailRes; y++)
            {
                float z = (y + 0.5f) * cellSize;
                for (int x = 0; x < detailRes; x++)
                {
                    float xx = (x + 0.5f) * cellSize;

                    // Sample height/slope from heightmap
                    float h01 = SampleHeight01Bilinear(heights01, xx, z, chunkSizeWorld);
                    float hWorld = h01 * heightMultiplier;
                    if (h01 < settings.minHeight01 || h01 > settings.maxHeight01) continue;
                    if (hWorld < (waterSurfaceY + settings.waterExclusionYOffset)) continue;
                    float slopeDeg = SampleSlopeDeg(heights01, xx, z, chunkSizeWorld, heightMultiplier);
                    if (slopeDeg > settings.maxSlopeDeg) continue;

                    uint cellHash = Hash2D(chunkX, chunkY, x, y, baseSeed);

                    // Grass
                    if (grassRefs.Count > 0 && grassDensity > 0f && grassSpawned < grassCapTotal)
                    {
                        int maxPerCell = Mathf.Clamp(settings.grassMaxPerCell, 0, 16);
                        int c = SampleCount(grassDensity, cellArea, maxPerCell, ref cellHash);
                        for (int i = 0; i < c && grassSpawned < grassCapTotal; i++)
                        {
                            int idx = PickWeightedIndex(grass, ref cellHash);
                            int layerIndex = grassRefs[idx].prototypeIndex;
                            maps[layerIndex][y, x] += 1;
                            grassSpawned++;
                        }
                    }

                    // Plants
                    if (plantRefs.Count > 0 && plantDensity > 0f && plantsSpawned < plantsCapTotal)
                    {
                        int maxPerCell = Mathf.Clamp(settings.plantsMaxPerCell, 0, 16);
                        int c = SampleCount(plantDensity, cellArea, maxPerCell, ref cellHash);
                        for (int i = 0; i < c && plantsSpawned < plantsCapTotal; i++)
                        {
                            int idx = PickWeightedIndex(plants, ref cellHash);
                            int layerIndex = plantRefs[idx].prototypeIndex;
                            maps[layerIndex][y, x] += 1;
                            plantsSpawned++;
                        }
                    }
                }
            }

            // Apply maps
            for (int l = 0; l < layers; l++)
            {
                td.SetDetailLayer(0, 0, l, maps[l]);
            }

            terrain.Flush();
        }

        private static int SampleCount(float densityPerM2, float cellArea, int maxPerCell, ref uint state)
        {
            if (maxPerCell <= 0) return 0;
            float expected = Mathf.Max(0f, densityPerM2) * Mathf.Max(0f, cellArea);
            // Clamp expected to keep generation stable
            expected = Mathf.Min(expected, maxPerCell);
            int baseCount = Mathf.FloorToInt(expected);
            float frac = expected - baseCount;
            int c = baseCount;
            if (c < maxPerCell && Next01(ref state) < frac) c++;
            return Mathf.Clamp(c, 0, maxPerCell);
        }

        private static void CollectEntries(
            VegetationScatterSettings settings,
            bool includeGrass,
            bool includePlants,
            out List<VegetationPrefabEntry> grass,
            out List<VegetationPrefabEntry> plants)
        {
            grass = new List<VegetationPrefabEntry>();
            plants = new List<VegetationPrefabEntry>();
            if (settings == null || settings.prefabs == null) return;

            for (int i = 0; i < settings.prefabs.Count; i++)
            {
                var e = settings.prefabs[i];
                if (e.prefab == null || e.weight <= 0f) continue;
                if (includeGrass && e.category == VegetationCategory.Grass) grass.Add(e);
                if (includePlants && e.category == VegetationCategory.Plant) plants.Add(e);
            }
        }

        private static DetailPrototype[] BuildDetailPrototypes(
            List<VegetationPrefabEntry> grassEntries,
            List<VegetationPrefabEntry> plantEntries,
            List<EntryRef> grassRefs,
            List<EntryRef> plantRefs)
        {
            // Unique by prefab instance id
            Dictionary<int, int> prefabToProto = new Dictionary<int, int>();
            List<DetailPrototype> protos = new List<DetailPrototype>();

            void add(List<VegetationPrefabEntry> list, List<EntryRef> refs)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    GameObject p = list[i].prefab;
                    int id = p.GetInstanceID();
                    if (!prefabToProto.TryGetValue(id, out int protoIndex))
                    {
                        protoIndex = protos.Count;
                        prefabToProto[id] = protoIndex;

                        DetailPrototype dp = new DetailPrototype
                        {
                            prototype = p,
                            renderMode = DetailRenderMode.VertexLit,
                            usePrototypeMesh = true,
                            healthyColor = new Color(0.85f, 0.95f, 0.85f),
                            dryColor = new Color(0.75f, 0.75f, 0.65f),
                            noiseSpread = 0.15f,
                            minWidth = 0.8f,
                            maxWidth = 1.4f,
                            minHeight = 0.8f,
                            maxHeight = 1.6f
                        };

                        protos.Add(dp);
                    }

                    refs.Add(new EntryRef { entry = list[i], prototypeIndex = protoIndex });
                }
            }

            add(grassEntries, grassRefs);
            add(plantEntries, plantRefs);

            return protos.ToArray();
        }

        private static int PickWeightedIndex(List<VegetationPrefabEntry> entries, ref uint state)
        {
            if (entries == null || entries.Count == 0) return 0;
            float total = 0f;
            for (int i = 0; i < entries.Count; i++) total += Mathf.Max(0f, entries[i].weight);
            if (total <= 0f) return 0;

            float r = Next01(ref state) * total;
            float acc = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                acc += Mathf.Max(0f, entries[i].weight);
                if (r <= acc) return i;
            }
            return entries.Count - 1;
        }

        private static float SampleHeight01Bilinear(float[,] h, float x, float z, float chunkSizeWorld)
        {
            int res = h.GetLength(0);
            if (res <= 1) return Mathf.Clamp01(h[0, 0]);

            float step = chunkSizeWorld / Mathf.Max(1, res - 1);
            float fx = Mathf.Clamp(x / Mathf.Max(0.0001f, step), 0f, res - 1.001f);
            float fz = Mathf.Clamp(z / Mathf.Max(0.0001f, step), 0f, res - 1.001f);

            int x0 = Mathf.FloorToInt(fx);
            int z0 = Mathf.FloorToInt(fz);
            int x1 = Mathf.Min(res - 1, x0 + 1);
            int z1 = Mathf.Min(res - 1, z0 + 1);

            float tx = fx - x0;
            float tz = fz - z0;

            float a = h[z0, x0];
            float b = h[z0, x1];
            float c = h[z1, x0];
            float d = h[z1, x1];

            return Mathf.Clamp01(Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz));
        }

        private static float SampleSlopeDeg(float[,] h, float x, float z, float chunkSizeWorld, float heightMultiplier)
        {
            int res = h.GetLength(0);
            if (res <= 2) return 0f;

            float step = chunkSizeWorld / Mathf.Max(1, res - 1);
            int ix = Mathf.Clamp(Mathf.RoundToInt(x / Mathf.Max(0.0001f, step)), 1, res - 2);
            int iz = Mathf.Clamp(Mathf.RoundToInt(z / Mathf.Max(0.0001f, step)), 1, res - 2);

            float hL = h[iz, ix - 1] * heightMultiplier;
            float hR = h[iz, ix + 1] * heightMultiplier;
            float hD = h[iz - 1, ix] * heightMultiplier;
            float hU = h[iz + 1, ix] * heightMultiplier;

            float dx = (hR - hL) / (2f * Mathf.Max(0.0001f, step));
            float dz = (hU - hD) / (2f * Mathf.Max(0.0001f, step));

            Vector3 n = Vector3.Normalize(new Vector3(-dx, 1f, -dz));
            return Vector3.Angle(n, Vector3.up);
        }

        private static int CombineSeed(int globalSeed, int seedOffset, long chunkX, long chunkY)
        {
            unchecked
            {
                ulong x = (ulong)chunkX;
                ulong y = (ulong)chunkY;
                ulong h = 1469598103934665603UL;
                h ^= x; h *= 1099511628211UL;
                h ^= (x >> 32); h *= 1099511628211UL;
                h ^= y; h *= 1099511628211UL;
                h ^= (y >> 32); h *= 1099511628211UL;
                uint s = (uint)(h ^ (h >> 32));
                return (int)(s ^ (uint)globalSeed ^ (uint)seedOffset);
            }
        }

        private static int ComputeSettingsHash(VegetationScatterSettings s, bool includeGrass, bool includePlants)
        {
            unchecked
            {
                int h = 17;
                h = (h * 31) ^ (s != null ? s.GetInstanceID() : 0);
                h = (h * 31) ^ includeGrass.GetHashCode();
                h = (h * 31) ^ includePlants.GetHashCode();
                return h;
            }
        }

        private static uint Hash2D(long chunkX, long chunkY, int x, int y, int seed)
        {
            unchecked
            {
                uint h = Hash32((uint)seed ^ 0x9e3779b9u);
                h = Hash32(h ^ (uint)chunkX);
                h = Hash32(h ^ (uint)(chunkX >> 32));
                h = Hash32(h ^ (uint)chunkY);
                h = Hash32(h ^ (uint)(chunkY >> 32));
                h = Hash32(h ^ (uint)x);
                h = Hash32(h ^ (uint)y);
                return h;
            }
        }

        private static uint Hash32(uint v)
        {
            v ^= v >> 16;
            v *= 0x7feb352d;
            v ^= v >> 15;
            v *= 0x846ca68b;
            v ^= v >> 16;
            return v;
        }

        private static float Next01(ref uint state)
        {
            state = Hash32(state + 0x9e3779b9u);
            return (state & 0x00FFFFFFu) / 16777216.0f;
        }
    }
}


