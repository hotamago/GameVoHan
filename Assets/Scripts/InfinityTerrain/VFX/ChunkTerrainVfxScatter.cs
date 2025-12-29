using System;
using System.Collections.Generic;
using UnityEngine;

namespace InfinityTerrain.VFX
{
    /// <summary>
    /// Lightweight per-chunk VFX spawner for built-in Terrain streaming.
    /// Spawns a small number of ParticleSystem prefabs (e.g. Idyllic Fantasy Nature's Particles/GodRays)
    /// deterministically per chunk so results are stable while moving.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkTerrainVfxScatter : MonoBehaviour
    {
        [NonSerialized] public GameObject[] particlesPrefabs;
        [NonSerialized] public GameObject[] godRaysPrefabs;

        [NonSerialized] public int globalSeed;
        [NonSerialized] public float heightMultiplier = 30f;
        [NonSerialized] public float yOffset = 0f;

        [NonSerialized] public int particlesPerChunk = 1;
        [NonSerialized] public int godRaysPerChunk = 1;
        [NonSerialized] public float minHeight01 = 0f;   // 0..1
        [NonSerialized] public float maxHeight01 = 1f;   // 0..1

        [NonSerialized] public float randomYawDegrees = 360f;
        [NonSerialized] public Vector2 uniformScaleRange = new Vector2(1f, 1f);

        // GodRays rotation settings
        [NonSerialized] public bool godRaysUseAdvancedRotation = false;
        [NonSerialized] public Vector3[] godRaysFixedAngles; // Array of Euler angles (x=pitch, y=yaw, z=roll)
        [NonSerialized] public Vector2 godRaysRandomPitchRange = new Vector2(0f, 0f); // min/max pitch in degrees
        [NonSerialized] public Vector2 godRaysRandomRollRange = new Vector2(0f, 0f); // min/max roll in degrees
        [NonSerialized] public float godRaysRandomYawRange = 360f; // yaw range (0 = disabled, 360 = full rotation)

        private long _lastChunkX;
        private long _lastChunkY;
        private int _lastLodResolution;
        private int _lastSeed;
        private float _lastChunkSize;

        private readonly List<GameObject> _spawned = new List<GameObject>(8);
        private readonly Stack<GameObject> _pool = new Stack<GameObject>(8);

        public void Clear()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                GameObject go = _spawned[i];
                if (go == null) continue;
                go.SetActive(false);
                _pool.Push(go);
            }
            _spawned.Clear();
        }

        public bool EnsureGenerated(long chunkX, long chunkY, int lodResolution, float chunkSizeWorld, float[,] heights01, bool allowGeneration)
        {
            int seed = CombineSeed(globalSeed, chunkX, chunkY);

            bool same =
                _lastChunkX == chunkX &&
                _lastChunkY == chunkY &&
                _lastLodResolution == lodResolution &&
                Mathf.Abs(_lastChunkSize - chunkSizeWorld) < 0.0001f &&
                _lastSeed == seed;

            if (same) return false;

            if (!allowGeneration)
            {
                // If chunk identity changed, old VFX is invalid while we wait for budget.
                if (_lastChunkX != chunkX || _lastChunkY != chunkY)
                {
                    Clear();
                }
                return false;
            }

            _lastChunkX = chunkX;
            _lastChunkY = chunkY;
            _lastLodResolution = lodResolution;
            _lastChunkSize = chunkSizeWorld;
            _lastSeed = seed;

            Regenerate(chunkX, chunkY, lodResolution, chunkSizeWorld, heights01, seed);
            return true;
        }

        private void Regenerate(long chunkX, long chunkY, int lodResolution, float chunkSizeWorld, float[,] heights01, int seed)
        {
            Clear();

            if (chunkSizeWorld <= 0.001f) return;
            if (lodResolution <= 1) return;
            if (heights01 == null || heights01.GetLength(0) != lodResolution) return;

            // Apply density multipliers
            int pCount = Mathf.Max(0, Mathf.RoundToInt(particlesPerChunk));
            int gCount = Mathf.Max(0, Mathf.RoundToInt(godRaysPerChunk));

            // Filter out null prefabs from arrays
            int validParticlesCount = GetValidPrefabCount(particlesPrefabs);
            int validGodRaysCount = GetValidPrefabCount(godRaysPrefabs);

            if (validParticlesCount == 0) pCount = 0;
            if (validGodRaysCount == 0) gCount = 0;

            int total = pCount + gCount;
            if (total <= 0) return;

            // Deterministic PRNG
            uint state = (uint)seed;

            // Spawn particles first, then godrays (stable ordering)
            for (int i = 0; i < pCount; i++)
            {
                if (!TryPickPoint(heights01, lodResolution, ref state, out float u, out float v, out float h01)) continue;
                GameObject prefab = SelectRandomPrefab(particlesPrefabs, validParticlesCount, ref state);
                if (prefab != null)
                {
                    Spawn(prefab, ref state, chunkSizeWorld, u, v, h01);
                }
            }

            for (int i = 0; i < gCount; i++)
            {
                if (!TryPickPoint(heights01, lodResolution, ref state, out float u, out float v, out float h01)) continue;
                GameObject prefab = SelectRandomPrefab(godRaysPrefabs, validGodRaysCount, ref state);
                if (prefab != null)
                {
                    SpawnGodRay(prefab, ref state, chunkSizeWorld, u, v, h01);
                }
            }
        }

        private void Spawn(GameObject prefab, ref uint state, float chunkSizeWorld, float u, float v, float h01)
        {
            if (prefab == null) return;

            Vector3 localPos = new Vector3(u * chunkSizeWorld, (h01 * Mathf.Max(0.0001f, heightMultiplier)) + yOffset, v * chunkSizeWorld);
            float yaw = (Mathf.Abs(randomYawDegrees) < 0.001f) ? 0f : (Next01(ref state) * randomYawDegrees);
            Quaternion localRot = Quaternion.Euler(0f, yaw, 0f);

            float sMin = Mathf.Min(uniformScaleRange.x, uniformScaleRange.y);
            float sMax = Mathf.Max(uniformScaleRange.x, uniformScaleRange.y);
            float s = (Mathf.Abs(sMax - sMin) < 0.001f) ? sMin : Mathf.Lerp(sMin, sMax, Next01(ref state));

            GameObject inst = GetOrCreate(prefab);
            inst.transform.SetParent(transform, worldPositionStays: false);
            inst.transform.localPosition = localPos;
            inst.transform.localRotation = localRot;
            inst.transform.localScale = Vector3.one * Mathf.Max(0.0001f, s);
            inst.SetActive(true);

            _spawned.Add(inst);
        }

        private void SpawnGodRay(GameObject prefab, ref uint state, float chunkSizeWorld, float u, float v, float h01)
        {
            if (prefab == null) return;

            Vector3 localPos = new Vector3(u * chunkSizeWorld, (h01 * Mathf.Max(0.0001f, heightMultiplier)) + yOffset, v * chunkSizeWorld);
            
            Quaternion localRot;
            if (godRaysUseAdvancedRotation)
            {
                // Use advanced rotation mode
                if (godRaysFixedAngles != null && godRaysFixedAngles.Length > 0)
                {
                    // Select from fixed angles array
                    int index = Mathf.FloorToInt(Next01(ref state) * godRaysFixedAngles.Length);
                    Vector3 angles = godRaysFixedAngles[index];
                    localRot = Quaternion.Euler(angles);
                }
                else
                {
                    // Random rotation with pitch/roll/yaw ranges
                    float pitch = 0f;
                    float yaw = 0f;
                    float roll = 0f;

                    if (Mathf.Abs(godRaysRandomPitchRange.y - godRaysRandomPitchRange.x) > 0.001f)
                    {
                        pitch = Mathf.Lerp(godRaysRandomPitchRange.x, godRaysRandomPitchRange.y, Next01(ref state));
                    }

                    if (Mathf.Abs(godRaysRandomYawRange) > 0.001f)
                    {
                        yaw = Next01(ref state) * godRaysRandomYawRange;
                    }

                    if (Mathf.Abs(godRaysRandomRollRange.y - godRaysRandomRollRange.x) > 0.001f)
                    {
                        roll = Mathf.Lerp(godRaysRandomRollRange.x, godRaysRandomRollRange.y, Next01(ref state));
                    }

                    localRot = Quaternion.Euler(pitch, yaw, roll);
                }
            }
            else
            {
                // Default: simple yaw rotation (same as particles)
                float yaw = (Mathf.Abs(randomYawDegrees) < 0.001f) ? 0f : (Next01(ref state) * randomYawDegrees);
                localRot = Quaternion.Euler(0f, yaw, 0f);
            }

            float sMin = Mathf.Min(uniformScaleRange.x, uniformScaleRange.y);
            float sMax = Mathf.Max(uniformScaleRange.x, uniformScaleRange.y);
            float s = (Mathf.Abs(sMax - sMin) < 0.001f) ? sMin : Mathf.Lerp(sMin, sMax, Next01(ref state));

            GameObject inst = GetOrCreate(prefab);
            inst.transform.SetParent(transform, worldPositionStays: false);
            inst.transform.localPosition = localPos;
            inst.transform.localRotation = localRot;
            inst.transform.localScale = Vector3.one * Mathf.Max(0.0001f, s);
            inst.SetActive(true);

            _spawned.Add(inst);
        }

        private GameObject GetOrCreate(GameObject prefab)
        {
            // Reuse from pool when possible. If pooled instance is from a different prefab, we just destroy it.
            while (_pool.Count > 0)
            {
                GameObject go = _pool.Pop();
                if (go == null) continue;

                // Unity doesn't expose "source prefab" at runtime, so we use name prefix to detect mismatch.
                // If mismatch, destroy to avoid odd visuals.
                if (!go.name.StartsWith(prefab.name, StringComparison.Ordinal))
                {
                    Destroy(go);
                    continue;
                }
                return go;
            }

            GameObject inst = Instantiate(prefab);
            inst.name = prefab.name; // stable name for pooling heuristic
            return inst;
        }

        private bool TryPickPoint(float[,] heights01, int lodResolution, ref uint state, out float u, out float v, out float h01)
        {
            // Try a few times to satisfy height band; avoids spawning in deep water if desired.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                u = Next01(ref state);
                v = Next01(ref state);
                h01 = SampleHeight01Bilinear(heights01, lodResolution, u, v);

                if (h01 >= Mathf.Clamp01(minHeight01) && h01 <= Mathf.Clamp01(maxHeight01))
                {
                    return true;
                }
            }

            u = 0f;
            v = 0f;
            h01 = 0f;
            return false;
        }

        private static float SampleHeight01Bilinear(float[,] h, int res, float u, float v)
        {
            if (h == null || res <= 1) return 0f;

            float fx = Mathf.Clamp(u * (res - 1), 0f, res - 1.001f);
            float fz = Mathf.Clamp(v * (res - 1), 0f, res - 1.001f);

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

        private static int CombineSeed(int globalSeed, long chunkX, long chunkY)
        {
            unchecked
            {
                // Mix long coords into a 32-bit seed deterministically.
                int hx = (int)(chunkX ^ (chunkX >> 32));
                int hy = (int)(chunkY ^ (chunkY >> 32));
                int h = globalSeed;
                h = (h * 73856093) ^ hx;
                h = (h * 19349663) ^ hy;
                h ^= 0x6C8E9CF5;
                return h;
            }
        }

        private static float Next01(ref uint state)
        {
            // Xorshift32
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            // Convert to [0,1)
            return (state & 0x00FFFFFF) / 16777216f;
        }

        private static int GetValidPrefabCount(GameObject[] prefabs)
        {
            if (prefabs == null || prefabs.Length == 0) return 0;
            int count = 0;
            for (int i = 0; i < prefabs.Length; i++)
            {
                if (prefabs[i] != null) count++;
            }
            return count;
        }

        private static GameObject SelectRandomPrefab(GameObject[] prefabs, int validCount, ref uint state)
        {
            if (prefabs == null || validCount == 0) return null;
            if (validCount == 1)
            {
                // Fast path: find first non-null
                for (int i = 0; i < prefabs.Length; i++)
                {
                    if (prefabs[i] != null) return prefabs[i];
                }
                return null;
            }

            // Random index from valid prefabs only
            int index = Mathf.FloorToInt(Next01(ref state) * validCount);
            int found = 0;
            for (int i = 0; i < prefabs.Length; i++)
            {
                if (prefabs[i] != null)
                {
                    if (found == index) return prefabs[i];
                    found++;
                }
            }
            return null;
        }
    }
}


