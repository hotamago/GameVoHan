using System;
using System.Collections.Generic;
using InfinityTerrain.Settings;
using UnityEngine;

namespace InfinityTerrain.Vegetation
{
    /// <summary>
    /// Spawns vegetation as Unity Terrain TreeInstances on a Terrain chunk.
    /// This enables built-in Terrain tree rendering (billboards, batching, etc).
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkTerrainTreeScatter : MonoBehaviour
    {
        [NonSerialized] public VegetationScatterSettings settings;
        [NonSerialized] public int globalSeed;
        [NonSerialized] public float heightMultiplier;
        [NonSerialized] public float waterSurfaceY;

        [Tooltip("If true, uses Rocks/Plants as Terrain trees too (TreePrototype).")]
        public bool includeNonTreesAsTrees = true;

        [Tooltip("Skip Grass category (usually better as detail layers).")]
        public bool excludeGrass = true;

        private long _lastChunkX;
        private long _lastChunkY;
        private int _lastLodResolution;
        private int _lastSeed;
        private int _lastSettingsHash;

        public void Clear()
        {
            Terrain t = GetComponent<Terrain>();
            if (t == null || t.terrainData == null) return;
            t.terrainData.treeInstances = Array.Empty<TreeInstance>();
            // Keep prototypes (optional); but clearing instances is enough.
            t.Flush();
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
            int settingsHash = ComputeSettingsHash(settings, includeNonTreesAsTrees, excludeGrass);

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

            Regenerate(terrain, seed, chunkSizeWorld, heights01);
            return true;
        }

        private void Regenerate(Terrain terrain, int seed, float chunkSizeWorld, float[,] heights01)
        {
            TerrainData td = terrain.terrainData;
            if (td == null) return;

            // Build prototypes list
            List<VegetationPrefabEntry> entries = CollectEntries(settings, includeNonTreesAsTrees, excludeGrass);
            if (entries.Count == 0)
            {
                Clear();
                return;
            }

            TreePrototype[] prototypes = BuildTreePrototypes(entries);
            if (prototypes == null || prototypes.Length == 0)
            {
                Clear();
                return;
            }
            td.treePrototypes = prototypes;

            // Budgets (scaled by area, like existing instanced scatter)
            float areaScale = (chunkSizeWorld * chunkSizeWorld) / (100f * 100f);
            if (areaScale < 0.01f) areaScale = 0.01f;

            int targetTrees = Mathf.RoundToInt(settings.treesPerChunk * areaScale);
            int targetRocks = includeNonTreesAsTrees ? Mathf.RoundToInt(settings.rocksPerChunk * areaScale) : 0;
            int targetPlants = includeNonTreesAsTrees ? Mathf.RoundToInt(settings.plantsPerChunk * areaScale) : 0;
            int targetGrassAsTrees = (!excludeGrass && includeNonTreesAsTrees) ? Mathf.RoundToInt(settings.grassPerChunk * areaScale) : 0;

            int totalTarget = Mathf.Max(0, targetTrees + targetRocks + targetPlants + targetGrassAsTrees);
            if (totalTarget == 0)
            {
                Clear();
                return;
            }

            // Use category-aware min spacing
            // NOTE: Terrain trees only support Y-rotation (no align-to-normal).
            System.Random rng = new System.Random(seed);
            List<Vector2> placedXZ = new List<Vector2>(totalTarget);
            List<TreeInstance> instances = new List<TreeInstance>(totalTarget);

            int attempts = Mathf.Max(totalTarget * settings.attemptsPerSpawn, totalTarget);
            int res = heights01.GetLength(0);
            float step = (res > 1) ? (chunkSizeWorld / (res - 1)) : chunkSizeWorld;
            float invChunk = 1.0f / Mathf.Max(0.0001f, chunkSizeWorld);
            float invHm = 1.0f / Mathf.Max(0.0001f, heightMultiplier);

            for (int a = 0; a < attempts && instances.Count < totalTarget; a++)
            {
                float x = (float)rng.NextDouble() * chunkSizeWorld;
                float z = (float)rng.NextDouble() * chunkSizeWorld;

                float h01 = SampleHeight01Bilinear(heights01, x, z, step);
                float hWorld = h01 * heightMultiplier;

                // Height filter
                if (h01 < settings.minHeight01 || h01 > settings.maxHeight01) continue;

                // Water exclusion
                if (hWorld < (waterSurfaceY + settings.waterExclusionYOffset)) continue;

                // Slope filter (approx from heightmap)
                float slopeDeg = SampleSlopeDeg(heights01, x, z, step, heightMultiplier);
                if (slopeDeg > settings.maxSlopeDeg) continue;

                // Pick prefab weighted (category-aware)
                VegetationPrefabEntry entry = PickWeighted(rng, entries);
                if (entry.prefab == null) continue;

                float minSpacing = GetMinSpacingForCategory(entry.category);
                if (minSpacing > 0f)
                {
                    float minSq = minSpacing * minSpacing;
                    bool ok = true;
                    Vector2 p = new Vector2(x, z);
                    for (int i = 0; i < placedXZ.Count; i++)
                    {
                        Vector2 d = placedXZ[i] - p;
                        if (d.sqrMagnitude < minSq) { ok = false; break; }
                    }
                    if (!ok) continue;
                }

                int protoIndex = FindPrototypeIndex(td.treePrototypes, entry.prefab);
                if (protoIndex < 0) continue;

                float scale = RandomRange(rng, entry.minUniformScale, entry.maxUniformScale);
                if (scale <= 0f) scale = 1f;

                float yawDeg = entry.randomYaw ? (float)(rng.NextDouble() * 360.0) : 0f;
                float yawRad = yawDeg * Mathf.Deg2Rad;

                TreeInstance ti = new TreeInstance
                {
                    prototypeIndex = protoIndex,
                    position = new Vector3(x * invChunk, h01, z * invChunk),
                    widthScale = scale,
                    heightScale = scale,
                    rotation = yawRad,
                    color = Color.white,
                    lightmapColor = Color.white
                };

                instances.Add(ti);
                placedXZ.Add(new Vector2(x, z));
            }

            td.treeInstances = instances.ToArray();
            terrain.Flush();
        }

        private float GetMinSpacingForCategory(VegetationCategory cat)
        {
            if (settings == null) return 0f;
            switch (cat)
            {
                case VegetationCategory.Tree: return settings.treeMinSpacing;
                case VegetationCategory.Rock: return settings.rockMinSpacing;
                case VegetationCategory.Plant: return settings.plantMinSpacing;
                case VegetationCategory.Grass: return settings.grassMinSpacing;
                default: return settings.plantMinSpacing;
            }
        }

        private static List<VegetationPrefabEntry> CollectEntries(VegetationScatterSettings settings, bool includeNonTreesAsTrees, bool excludeGrass)
        {
            var list = new List<VegetationPrefabEntry>();
            if (settings == null || settings.prefabs == null) return list;

            for (int i = 0; i < settings.prefabs.Count; i++)
            {
                var e = settings.prefabs[i];
                if (e.prefab == null || e.weight <= 0f) continue;

                if (excludeGrass && e.category == VegetationCategory.Grass) continue;
                if (!includeNonTreesAsTrees && e.category != VegetationCategory.Tree) continue;

                list.Add(e);
            }
            return list;
        }

        private static TreePrototype[] BuildTreePrototypes(List<VegetationPrefabEntry> entries)
        {
            // Unique by prefab instance id
            Dictionary<int, GameObject> unique = new Dictionary<int, GameObject>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].prefab == null) continue;
                int id = entries[i].prefab.GetInstanceID();
                if (!unique.ContainsKey(id)) unique[id] = entries[i].prefab;
            }

            TreePrototype[] protos = new TreePrototype[unique.Count];
            int k = 0;
            foreach (var kvp in unique)
            {
                protos[k++] = new TreePrototype
                {
                    prefab = kvp.Value,
                    bendFactor = 0.5f
                };
            }
            return protos;
        }

        private static int FindPrototypeIndex(TreePrototype[] prototypes, GameObject prefab)
        {
            if (prototypes == null || prefab == null) return -1;
            for (int i = 0; i < prototypes.Length; i++)
            {
                if (prototypes[i].prefab == prefab) return i;
            }
            return -1;
        }

        private static float SampleHeight01Bilinear(float[,] h, float x, float z, float step)
        {
            int res = h.GetLength(0);
            if (res <= 1) return Mathf.Clamp01(h[0, 0]);

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

        private static float SampleSlopeDeg(float[,] h, float x, float z, float step, float heightMultiplier)
        {
            int res = h.GetLength(0);
            if (res <= 2) return 0f;

            // Sample using nearest grid point for slope estimation
            int ix = Mathf.Clamp(Mathf.RoundToInt(x / Mathf.Max(0.0001f, step)), 1, res - 2);
            int iz = Mathf.Clamp(Mathf.RoundToInt(z / Mathf.Max(0.0001f, step)), 1, res - 2);

            float hL = h[iz, ix - 1] * heightMultiplier;
            float hR = h[iz, ix + 1] * heightMultiplier;
            float hD = h[iz - 1, ix] * heightMultiplier;
            float hU = h[iz + 1, ix] * heightMultiplier;

            float dx = (hR - hL) / (2f * Mathf.Max(0.0001f, step));
            float dz = (hU - hD) / (2f * Mathf.Max(0.0001f, step));

            Vector3 n = Vector3.Normalize(new Vector3(-dx, 1f, -dz));
            float slope = Vector3.Angle(n, Vector3.up);
            return slope;
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

        private static float RandomRange(System.Random rng, float a, float b)
        {
            if (a > b) { float t = a; a = b; b = t; }
            if (Mathf.Abs(a - b) < 0.0001f) return a;
            return a + ((float)rng.NextDouble() * (b - a));
        }

        private static VegetationPrefabEntry PickWeighted(System.Random rng, List<VegetationPrefabEntry> entries)
        {
            float total = 0f;
            for (int i = 0; i < entries.Count; i++) total += Mathf.Max(0f, entries[i].weight);
            if (total <= 0f) return entries[0];

            float r = (float)rng.NextDouble() * total;
            float acc = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                acc += Mathf.Max(0f, entries[i].weight);
                if (r <= acc) return entries[i];
            }
            return entries[entries.Count - 1];
        }

        private static int ComputeSettingsHash(VegetationScatterSettings s, bool includeNonTrees, bool excludeGrass)
        {
            unchecked
            {
                int h = 17;
                h = (h * 31) ^ (s != null ? s.GetInstanceID() : 0);
                h = (h * 31) ^ includeNonTrees.GetHashCode();
                h = (h * 31) ^ excludeGrass.GetHashCode();
                return h;
            }
        }
    }
}


