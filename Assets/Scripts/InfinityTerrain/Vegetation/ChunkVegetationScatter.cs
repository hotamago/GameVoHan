using System;
using System.Collections.Generic;
using UnityEngine;

namespace InfinityTerrain.Vegetation
{
    /// <summary>
    /// Spawns vegetation prefabs as children of a chunk GameObject, deterministically based on chunk coords + seed.
    /// This is meant for InfinityTerrain mesh chunks (not Unity Terrain).
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkVegetationScatter : MonoBehaviour
    {
        [NonSerialized] public VegetationScatterSettings settings;
        [NonSerialized] public int globalSeed;
        [NonSerialized] public float heightMultiplier;
        [NonSerialized] public float waterSurfaceY;

        private long _lastChunkX;
        private long _lastChunkY;
        private int _lastLodResolution;
        private int _lastMeshInstanceId;
        private int _lastSeed;

        private Transform _container;

        public void Clear()
        {
            if (_container == null) _container = FindOrCreateContainer();
            for (int i = _container.childCount - 1; i >= 0; i--)
            {
                Destroy(_container.GetChild(i).gameObject);
            }

            _lastMeshInstanceId = 0;
            _lastSeed = 0;
        }

        public void EnsureGenerated(long chunkX, long chunkY, int lodResolution, float chunkSize)
        {
            if (settings == null)
            {
                Debug.LogWarning($"[ChunkVegetationScatter] Settings is null for chunk {chunkX},{chunkY}");
                return;
            }
            if (heightMultiplier <= 0.0001f)
            {
                Debug.LogWarning($"[ChunkVegetationScatter] Height multiplier too small ({heightMultiplier}) for chunk {chunkX},{chunkY}");
                return;
            }

            MeshCollider mc = GetComponent<MeshCollider>();
            MeshFilter mf = GetComponent<MeshFilter>();
            if (mc == null || mf == null || mf.sharedMesh == null)
            {
                Debug.LogWarning($"[ChunkVegetationScatter] Missing MeshCollider/MeshFilter/sharedMesh for chunk {chunkX},{chunkY}");
                return;
            }

            int meshId = mf.sharedMesh.GetInstanceID();
            int seed = CombineSeed(globalSeed, settings.seedOffset, chunkX, chunkY);

            bool same =
                _lastChunkX == chunkX &&
                _lastChunkY == chunkY &&
                _lastLodResolution == lodResolution &&
                _lastMeshInstanceId == meshId &&
                _lastSeed == seed;

            if (same) return;

            _lastChunkX = chunkX;
            _lastChunkY = chunkY;
            _lastLodResolution = lodResolution;
            _lastMeshInstanceId = meshId;
            _lastSeed = seed;

            Regenerate(mc, chunkX, chunkY, seed, chunkSize);
        }

        private void Regenerate(MeshCollider mc, long chunkX, long chunkY, int seed, float chunkSize)
        {
            if (_container == null) _container = FindOrCreateContainer();
            Clear();

            if (settings.prefabs == null || settings.prefabs.Count == 0)
            {
                Debug.LogWarning($"[ChunkVegetationScatter] No prefabs defined in settings for chunk {chunkX},{chunkY}.");
                return;
            }

            // Debug.Log($"[ChunkVegetationScatter] Starting vegetation generation for chunk {chunkX},{chunkY}. Prefabs: {settings.prefabs.Count}, Trees: {settings.treesPerChunk}, Rocks: {settings.rocksPerChunk}, Plants: {settings.plantsPerChunk}, Grass: {settings.grassPerChunk}");

            // Build category lists
            List<VegetationPrefabEntry> trees = new List<VegetationPrefabEntry>();
            List<VegetationPrefabEntry> rocks = new List<VegetationPrefabEntry>();
            List<VegetationPrefabEntry> plants = new List<VegetationPrefabEntry>();
            List<VegetationPrefabEntry> grass = new List<VegetationPrefabEntry>();

            for (int i = 0; i < settings.prefabs.Count; i++)
            {
                VegetationPrefabEntry e = settings.prefabs[i];
                if (e.prefab == null || e.weight <= 0f) continue;
                switch (e.category)
                {
                    case VegetationCategory.Tree: trees.Add(e); break;
                    case VegetationCategory.Rock: rocks.Add(e); break;
                    case VegetationCategory.Plant: plants.Add(e); break;
                    case VegetationCategory.Grass: grass.Add(e); break;
                    default: plants.Add(e); break;
                }
            }

            // Deterministic RNG streams per category (avoid cross-coupling)
            SpawnCategory(mc, seed ^ 0x1A2B3C4D, trees, settings.treesPerChunk, settings.treeMinSpacing, chunkSize);
            SpawnCategory(mc, seed ^ 0x22334455, rocks, settings.rocksPerChunk, settings.rockMinSpacing, chunkSize);
            SpawnCategory(mc, seed ^ 0x66778899, plants, settings.plantsPerChunk, settings.plantMinSpacing, chunkSize);
            SpawnCategory(mc, seed ^ 0x13579BDF, grass, settings.grassPerChunk, settings.grassMinSpacing, chunkSize);
        }

        private void SpawnCategory(
            MeshCollider mc,
            int seed,
            List<VegetationPrefabEntry> entries,
            int targetCount,
            float minSpacing,
            float chunkSize)
        {
            if (entries == null || entries.Count == 0)
            {
                Debug.Log($"[ChunkVegetationScatter] No entries for category, skipping");
                return;
            }
            if (targetCount <= 0)
            {
                Debug.Log($"[ChunkVegetationScatter] Target count <= 0 ({targetCount}), skipping");
                return;
            }

            minSpacing = Mathf.Max(0f, minSpacing);

            System.Random rng = new System.Random(seed);
            List<Vector3> placed = new List<Vector3>(targetCount);

            // Chunk bounds in world-space
            // We assume chunk meshes are generated in [0..chunkSize] on X/Z relative to chunk origin.
            
            if (chunkSize <= 0.001f) return;

            int attempts = Mathf.Max(targetCount * settings.attemptsPerSpawn, targetCount);

            float yTop = Mathf.Max(200f, heightMultiplier * 3f);
            float rayLen = yTop + 500f;
            
            // Debug.Log($"[ChunkVegetationScatter] Spawning {targetCount} items in chunk. Size={chunkSize}, yTop={yTop}");

            for (int a = 0; a < attempts && placed.Count < targetCount; a++)
            {
                float x = (float)rng.NextDouble() * chunkSize;
                float z = (float)rng.NextDouble() * chunkSize;

                Vector3 origin = transform.position + new Vector3(x, yTop, z);
                Ray ray = new Ray(origin, Vector3.down);

                if (!mc.Raycast(ray, out RaycastHit hit, rayLen))
                {
                    Debug.DrawRay(origin, Vector3.down * rayLen, Color.red, 10.0f);
                    // Debug.Log($"[ChunkVegetationScatter] Raycast missed at {origin}");
                    continue;
                }
                
                // Debug.DrawRay(origin, Vector3.down * hit.distance, Color.green, 10.0f);

                // Height filter (normalized)
                float h01 = Mathf.Clamp01(hit.point.y / Mathf.Max(0.0001f, heightMultiplier));
                if (h01 < settings.minHeight01 || h01 > settings.maxHeight01)
                {
                    // Debug.Log($"[ChunkVegetationScatter] Height filter failed: {h01} not in [{settings.minHeight01}, {settings.maxHeight01}]");
                    continue;
                }

                // Water exclusion
                if (hit.point.y < (waterSurfaceY + settings.waterExclusionYOffset))
                {
                    // Debug.Log($"[ChunkVegetationScatter] Water exclusion failed: {hit.point.y} < {waterSurfaceY + settings.waterExclusionYOffset}");
                    continue;
                }

                // Slope filter
                float slope = Vector3.Angle(hit.normal, Vector3.up);
                if (slope > settings.maxSlopeDeg)
                {
                    // Debug.Log($"[ChunkVegetationScatter] Slope filter failed: {slope} > {settings.maxSlopeDeg}");
                    continue;
                }

                Vector3 p = hit.point;
                p.y += 0.01f; // tiny lift to avoid z-fighting

                if (minSpacing > 0f)
                {
                    bool ok = true;
                    float minSq = minSpacing * minSpacing;
                    for (int i = 0; i < placed.Count; i++)
                    {
                        Vector3 d = placed[i] - p;
                        d.y = 0f;
                        if (d.sqrMagnitude < minSq)
                        {
                            ok = false;
                            // Debug.Log($"[ChunkVegetationScatter] Spacing too close to existing placement");
                            break;
                        }
                    }
                    if (!ok) continue;
                }

                VegetationPrefabEntry entry = PickWeighted(rng, entries);
                if (entry.prefab == null)
                {
                    // Debug.Log($"[ChunkVegetationScatter] Null prefab in entry");
                    continue;
                }

                Quaternion rot = ComputeRotation(rng, entry, hit.normal);
                float scale = RandomRange(rng, entry.minUniformScale, entry.maxUniformScale);
                if (scale <= 0f) scale = 1f;

                Vector3 pos = hit.point + (hit.normal * entry.yOffset);
                GameObject go = Instantiate(entry.prefab, pos, rot, _container);
                go.transform.localScale = Vector3.one * scale;

                placed.Add(pos);
            }

            if (placed.Count > 0)
                Debug.Log($"[ChunkVegetationScatter] Successfully spawned {placed.Count}/{targetCount} vegetation items in chunk");
        }

        private Transform FindOrCreateContainer()
        {
            Transform t = transform.Find("__Vegetation");
            if (t != null) return t;
            GameObject go = new GameObject("__Vegetation");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        private static int CombineSeed(int globalSeed, int seedOffset, long chunkX, long chunkY)
        {
            unchecked
            {
                // Simple 64-bit mix into 32-bit seed
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

        private static Quaternion ComputeRotation(System.Random rng, VegetationPrefabEntry entry, Vector3 normal)
        {
            float yaw = entry.randomYaw ? (float)(rng.NextDouble() * 360.0) : 0f;
            if (!entry.alignToNormal)
            {
                return Quaternion.Euler(0f, yaw, 0f);
            }

            Quaternion align = Quaternion.FromToRotation(Vector3.up, normal);
            return Quaternion.AngleAxis(yaw, normal) * align;
        }

        private static VegetationPrefabEntry PickWeighted(System.Random rng, List<VegetationPrefabEntry> entries)
        {
            float total = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                float w = Mathf.Max(0f, entries[i].weight);
                total += w;
            }
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


    }
}


