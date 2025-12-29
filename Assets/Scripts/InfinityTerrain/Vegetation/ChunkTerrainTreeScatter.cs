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

        [Tooltip("Skip Plant category (usually better as detail layers).")]
        public bool excludePlants = false;

        private long _lastChunkX;
        private long _lastChunkY;
        private int _lastLodResolution;
        private int _lastSeed;
        private int _lastSettingsHash;
        private Transform _colliderContainer;

        private struct CachedBounds
        {
            public bool valid;
            public Bounds boundsLocal; // In prefab root local space
            public bool capsulePreferred;
        }

        private static readonly Dictionary<GameObject, CachedBounds> _boundsCache = new Dictionary<GameObject, CachedBounds>();

        public void Clear()
        {
            Terrain t = GetComponent<Terrain>();
            if (t == null || t.terrainData == null) return;
            t.terrainData.treeInstances = Array.Empty<TreeInstance>();
            // Keep prototypes (optional); but clearing instances is enough.
            t.Flush();
            ClearColliderProxies();
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
            int settingsHash = ComputeSettingsHash(settings, includeNonTreesAsTrees, excludeGrass, excludePlants);

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

            // Split by category for correct budgets + better control.
            CollectEntriesByCategory(
                settings,
                includeNonTreesAsTrees,
                excludeGrass,
                excludePlants,
                out List<VegetationPrefabEntry> treeEntries,
                out List<VegetationPrefabEntry> rockEntries,
                out List<VegetationPrefabEntry> plantEntries,
                out List<VegetationPrefabEntry> grassEntries);

            List<VegetationPrefabEntry> allForPrototypes = new List<VegetationPrefabEntry>(treeEntries.Count + rockEntries.Count + plantEntries.Count + grassEntries.Count);
            allForPrototypes.AddRange(treeEntries);
            allForPrototypes.AddRange(rockEntries);
            allForPrototypes.AddRange(plantEntries);
            allForPrototypes.AddRange(grassEntries);

            if (allForPrototypes.Count == 0)
            {
                Clear();
                return;
            }

            TreePrototype[] prototypes = BuildTreePrototypes(allForPrototypes);
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

            System.Random rng = new System.Random(seed);
            List<Vector2> placedXZ = new List<Vector2>(Mathf.Max(64, totalTarget));
            List<TreeInstance> instances = new List<TreeInstance>(totalTarget);

            int res = heights01.GetLength(0);
            float step = (res > 1) ? (chunkSizeWorld / (res - 1)) : chunkSizeWorld;
            float invChunk = 1.0f / Mathf.Max(0.0001f, chunkSizeWorld);
            EnsureColliderContainer();
            ClearColliderProxies();

            // Spawn per category (fixes "plants/grass not showing" caused by mixed weighted picking).
            SpawnFixedCountCategory(
                terrain, td, rng, chunkSizeWorld, heights01, step, invChunk,
                treeEntries, targetTrees, settings.treeMinSpacing, placedXZ, instances, enableClustering: true);

            SpawnFixedCountCategory(
                terrain, td, rng, chunkSizeWorld, heights01, step, invChunk,
                rockEntries, targetRocks, settings.rockMinSpacing, placedXZ, instances, enableClustering: false);

            // Plants: prefer density/probability if configured
            if (includeNonTreesAsTrees && plantEntries.Count > 0)
            {
                if (settings.plantsDensityPerM2 > 0f)
                {
                    int cap = Mathf.RoundToInt(settings.maxPlantsPerChunk * areaScale);
                    SpawnDensityCategory(
                        terrain, td, rng, chunkSizeWorld, heights01, step, invChunk,
                        plantEntries, settings.plantsDensityPerM2, cap, settings.plantMinSpacing, instances, enableClustering: false);
                }
                else
                {
                    SpawnFixedCountCategory(
                        terrain, td, rng, chunkSizeWorld, heights01, step, invChunk,
                        plantEntries, targetPlants, settings.plantMinSpacing, placedXZ, instances, enableClustering: false);
                }
            }

            // Grass: prefer density/probability if configured
            if (!excludeGrass && includeNonTreesAsTrees && grassEntries.Count > 0)
            {
                if (settings.grassDensityPerM2 > 0f)
                {
                    int cap = Mathf.RoundToInt(settings.maxGrassPerChunk * areaScale);
                    SpawnDensityCategory(
                        terrain, td, rng, chunkSizeWorld, heights01, step, invChunk,
                        grassEntries, settings.grassDensityPerM2, cap, settings.grassMinSpacing, instances, enableClustering: false);
                }
                else
                {
                    SpawnFixedCountCategory(
                        terrain, td, rng, chunkSizeWorld, heights01, step, invChunk,
                        grassEntries, targetGrassAsTrees, settings.grassMinSpacing, placedXZ, instances, enableClustering: false);
                }
            }

            td.treeInstances = instances.ToArray();
            terrain.Flush();
        }

        private void SpawnFixedCountCategory(
            Terrain terrain,
            TerrainData td,
            System.Random rng,
            float chunkSizeWorld,
            float[,] heights01,
            float step,
            float invChunk,
            List<VegetationPrefabEntry> entries,
            int targetCount,
            float minSpacing,
            List<Vector2> placedXZ,
            List<TreeInstance> instances,
            bool enableClustering)
        {
            if (settings == null) return;
            if (entries == null || entries.Count == 0) return;
            if (targetCount <= 0) return;

            int attempts = Mathf.Max(targetCount * settings.attemptsPerSpawn, targetCount);
            float minSq = Mathf.Max(0f, minSpacing) * Mathf.Max(0f, minSpacing);
            int spawned = 0;

            for (int a = 0; a < attempts && (instances.Count < int.MaxValue); a++)
            {
                if (spawned >= targetCount) break;

                float x = (float)rng.NextDouble() * chunkSizeWorld;
                float z = (float)rng.NextDouble() * chunkSizeWorld;

                if (!PassPlacementFilters(heights01, x, z, step, out float h01)) continue;

                if (minSq > 0f && !PassSpacing(placedXZ, x, z, minSq)) continue;

                VegetationPrefabEntry entry = enableClustering
                    ? PickWeightedClustered(rng, entries, terrain, x, z)
                    : PickWeighted(rng, entries);
                if (entry.prefab == null) continue;

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
                spawned++;

                MaybeCreateColliderProxy(entry, terrain.transform.position + new Vector3(x, h01 * heightMultiplier, z), yawDeg, scale);
            }
        }

        private void SpawnDensityCategory(
            Terrain terrain,
            TerrainData td,
            System.Random rng,
            float chunkSizeWorld,
            float[,] heights01,
            float step,
            float invChunk,
            List<VegetationPrefabEntry> entries,
            float densityPerM2,
            int maxPerChunk,
            float minSpacing,
            List<TreeInstance> instances,
            bool enableClustering)
        {
            if (settings == null) return;
            if (entries == null || entries.Count == 0) return;
            if (densityPerM2 <= 0f) return;

            float area = chunkSizeWorld * chunkSizeWorld;
            int target = Mathf.RoundToInt(densityPerM2 * area);
            if (maxPerChunk > 0) target = Mathf.Min(target, maxPerChunk);
            if (target <= 0) return;

            int attempts = Mathf.Max(target * settings.attemptsPerSpawn, target);
            int spawned = 0;

            for (int a = 0; a < attempts && spawned < target; a++)
            {
                float x = (float)rng.NextDouble() * chunkSizeWorld;
                float z = (float)rng.NextDouble() * chunkSizeWorld;

                if (!PassPlacementFilters(heights01, x, z, step, out float h01)) continue;

                VegetationPrefabEntry entry = enableClustering
                    ? PickWeightedClustered(rng, entries, terrain, x, z)
                    : PickWeighted(rng, entries);
                if (entry.prefab == null) continue;

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
                spawned++;
            }
        }

        private bool PassPlacementFilters(float[,] heights01, float x, float z, float step, out float h01)
        {
            h01 = SampleHeight01Bilinear(heights01, x, z, step);
            float hWorld = h01 * heightMultiplier;

            // Height filter
            if (h01 < settings.minHeight01 || h01 > settings.maxHeight01) return false;

            // Water exclusion
            if (hWorld < (waterSurfaceY + settings.waterExclusionYOffset)) return false;

            // Slope filter (approx from heightmap)
            float slopeDeg = SampleSlopeDeg(heights01, x, z, step, heightMultiplier);
            if (slopeDeg > settings.maxSlopeDeg) return false;

            return true;
        }

        private static bool PassSpacing(List<Vector2> placedXZ, float x, float z, float minSq)
        {
            if (placedXZ == null) return true;
            Vector2 p = new Vector2(x, z);
            for (int i = 0; i < placedXZ.Count; i++)
            {
                Vector2 d = placedXZ[i] - p;
                if (d.sqrMagnitude < minSq) return false;
            }
            return true;
        }

        private static void CollectEntriesByCategory(
            VegetationScatterSettings settings,
            bool includeNonTreesAsTrees,
            bool excludeGrass,
            bool excludePlants,
            out List<VegetationPrefabEntry> trees,
            out List<VegetationPrefabEntry> rocks,
            out List<VegetationPrefabEntry> plants,
            out List<VegetationPrefabEntry> grass)
        {
            trees = new List<VegetationPrefabEntry>();
            rocks = new List<VegetationPrefabEntry>();
            plants = new List<VegetationPrefabEntry>();
            grass = new List<VegetationPrefabEntry>();

            if (settings == null || settings.prefabs == null) return;

            for (int i = 0; i < settings.prefabs.Count; i++)
            {
                var e = settings.prefabs[i];
                if (e.prefab == null || e.weight <= 0f) continue;

                if (!includeNonTreesAsTrees && e.category != VegetationCategory.Tree) continue;
                if (excludeGrass && e.category == VegetationCategory.Grass) continue;
                if (excludePlants && e.category == VegetationCategory.Plant) continue;

                switch (e.category)
                {
                    case VegetationCategory.Tree: trees.Add(e); break;
                    case VegetationCategory.Rock: rocks.Add(e); break;
                    case VegetationCategory.Plant: plants.Add(e); break;
                    case VegetationCategory.Grass: grass.Add(e); break;
                    default: plants.Add(e); break;
                }
            }
        }

        private VegetationPrefabEntry PickWeightedClustered(System.Random rng, List<VegetationPrefabEntry> entries, Terrain terrain, float localX, float localZ)
        {
            if (settings == null || !settings.enableTreeClustering || settings.treeClusterCellSize <= 0.01f)
            {
                return PickWeighted(rng, entries);
            }

            // Compute stable "patch noise" per entry at this world position.
            Vector3 chunkOrigin = (terrain != null) ? terrain.transform.position : Vector3.zero;
            double wx = chunkOrigin.x + localX;
            double wz = chunkOrigin.z + localZ;

            float total = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                float w = Mathf.Max(0f, entries[i].weight);
                if (w <= 0f) continue;
                float patch = VegetationNoise.PatchFactor(wx, wz, entries[i].prefab, globalSeed, settings);
                total += w * patch;
            }
            if (total <= 0f) return PickWeighted(rng, entries);

            float r = (float)rng.NextDouble() * total;
            float acc = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                float w = Mathf.Max(0f, entries[i].weight);
                if (w <= 0f) continue;
                float patch = VegetationNoise.PatchFactor(wx, wz, entries[i].prefab, globalSeed, settings);
                acc += w * patch;
                if (r <= acc) return entries[i];
            }
            return entries[entries.Count - 1];
        }

        private void EnsureColliderContainer()
        {
            if (_colliderContainer != null) return;
            Transform t = transform.Find("__VegetationColliders");
            if (t != null) { _colliderContainer = t; return; }
            GameObject go = new GameObject("__VegetationColliders");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            _colliderContainer = go.transform;
        }

        private void ClearColliderProxies()
        {
            if (_colliderContainer == null) return;
            for (int i = _colliderContainer.childCount - 1; i >= 0; i--)
            {
                Destroy(_colliderContainer.GetChild(i).gameObject);
            }
        }

        private void MaybeCreateColliderProxy(VegetationPrefabEntry entry, Vector3 worldPos, float yawDeg, float scale)
        {
            if (settings == null) return;
            if (!settings.createColliderProxies) return;
            if (entry.prefab == null) return;
            if (entry.category != VegetationCategory.Tree && entry.category != VegetationCategory.Rock) return;

            EnsureColliderContainer();

            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            Vector3 pos = worldPos + new Vector3(0f, entry.yOffset, 0f);

            // If prefab already has colliders, clone them (best accuracy).
            Collider[] cols = entry.prefab.GetComponentsInChildren<Collider>(true);
            if (cols != null && cols.Length > 0)
            {
                for (int i = 0; i < cols.Length; i++)
                {
                    // Only support common colliders for proxy
                    Collider c = cols[i];
                    if (c == null) continue;
                    GameObject go = new GameObject("Col");
                    go.layer = entry.prefab.layer;
                    go.transform.SetParent(_colliderContainer, worldPositionStays: true);
                    go.transform.position = pos;
                    go.transform.rotation = rot;
                    go.transform.localScale = Vector3.one * Mathf.Max(0.001f, scale);

                    // Copy local offset by parenting a child under instance root
                    go.transform.position = pos;

                    if (c is BoxCollider bc)
                    {
                        var n = go.AddComponent<BoxCollider>();
                        n.center = bc.center;
                        n.size = bc.size;
                    }
                    else if (c is SphereCollider sc)
                    {
                        var n = go.AddComponent<SphereCollider>();
                        n.center = sc.center;
                        n.radius = sc.radius;
                    }
                    else if (c is CapsuleCollider cc)
                    {
                        var n = go.AddComponent<CapsuleCollider>();
                        n.center = cc.center;
                        n.radius = cc.radius;
                        n.height = cc.height;
                        n.direction = cc.direction;
                    }
                    else if (c is MeshCollider mc)
                    {
                        var n = go.AddComponent<MeshCollider>();
                        n.sharedMesh = mc.sharedMesh;
                        n.convex = mc.convex;
                    }
                    else
                    {
                        Destroy(go);
                    }
                }
                return;
            }

            if (!settings.autoGenerateColliderWhenMissing) return;

            CachedBounds cb = GetOrComputeBounds(entry.prefab);
            if (!cb.valid) return;

            GameObject auto = new GameObject("ColAuto");
            auto.layer = entry.prefab.layer;
            auto.transform.SetParent(_colliderContainer, worldPositionStays: true);
            auto.transform.position = pos;
            auto.transform.rotation = rot;
            auto.transform.localScale = Vector3.one * Mathf.Max(0.001f, scale);

            // Prefer capsule for tall assets, else box.
            if (cb.capsulePreferred)
            {
                var cap = auto.AddComponent<CapsuleCollider>();
                cap.direction = 1;
                cap.center = cb.boundsLocal.center;
                float r = Mathf.Max(0.05f, Mathf.Min(cb.boundsLocal.extents.x, cb.boundsLocal.extents.z));
                cap.radius = r;
                cap.height = Mathf.Max(r * 2f, cb.boundsLocal.size.y);
            }
            else
            {
                var box = auto.AddComponent<BoxCollider>();
                box.center = cb.boundsLocal.center;
                box.size = cb.boundsLocal.size;
            }
        }

        private static CachedBounds GetOrComputeBounds(GameObject prefab)
        {
            if (prefab == null) return default;
            if (_boundsCache.TryGetValue(prefab, out CachedBounds cached)) return cached;

            CachedBounds cb = new CachedBounds { valid = false };
            Renderer[] rs = prefab.GetComponentsInChildren<Renderer>(true);
            if (rs == null || rs.Length == 0)
            {
                _boundsCache[prefab] = cb;
                return cb;
            }

            // Approximate bounds in prefab root local space by using Transform.InverseTransformPoint on renderer world bounds.
            // In edit-time this may be imperfect for nested scaling, but is good enough for gameplay collision.
            bool has = false;
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);
            Transform root = prefab.transform;
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                Bounds wb = rs[i].bounds;
                Vector3 c = root.InverseTransformPoint(wb.center);
                // Convert size approximately by sampling extents along axes (safe-ish for mostly uniform scaling)
                Vector3 e = wb.extents;
                Vector3 eLocal = new Vector3(
                    Mathf.Abs(root.InverseTransformVector(new Vector3(e.x, 0, 0)).x),
                    Mathf.Abs(root.InverseTransformVector(new Vector3(0, e.y, 0)).y),
                    Mathf.Abs(root.InverseTransformVector(new Vector3(0, 0, e.z)).z));

                Bounds lb = new Bounds(c, eLocal * 2f);
                if (!has) { b = lb; has = true; }
                else { b.Encapsulate(lb.min); b.Encapsulate(lb.max); }
            }

            if (!has)
            {
                _boundsCache[prefab] = cb;
                return cb;
            }

            cb.valid = true;
            cb.boundsLocal = b;
            cb.capsulePreferred = (b.size.y > Mathf.Max(b.size.x, b.size.z) * 1.2f);
            _boundsCache[prefab] = cb;
            return cb;
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

        private static int ComputeSettingsHash(VegetationScatterSettings s, bool includeNonTrees, bool excludeGrass, bool excludePlants)
        {
            unchecked
            {
                int h = ComputeSettingsHash(s, includeNonTrees, excludeGrass);
                h = (h * 31) ^ excludePlants.GetHashCode();
                return h;
            }
        }
    }
}


