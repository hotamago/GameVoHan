using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace InfinityTerrain.Vegetation
{
    /// <summary>
    /// Spawns vegetation using GPU Instancing (Graphics.DrawMeshInstanced) for maximum performance.
    /// Replaces the heavy GameObject-based approach.
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

        // --- Instancing Data ---
        private class InstanceBatch
        {
            public Mesh mesh;
            public Material material;
            public int submeshIndex;
            public int layer;
            public List<Matrix4x4> matrices = new List<Matrix4x4>();
            public ShadowCastingMode castShadows = ShadowCastingMode.On;
            public bool receiveShadows = true;
        }

        // Key: Hash of (MeshID ^ MaterialID ^ SubmeshIndex)
        private Dictionary<int, InstanceBatch> _batches = new Dictionary<int, InstanceBatch>();

        // Shared buffer for drawing (max 1023 instances per draw call)
        private static readonly List<Matrix4x4> _drawBuffer = new List<Matrix4x4>(1023);

        // Prefab structure cache to avoid parsing Transform hierarchies every spawn
        private struct PrefabPart
        {
            public Mesh mesh;
            public Material material;
            public int submeshIndex;
            public Matrix4x4 localMatrix; // Relative to prefab root
            public int layer;
            public ShadowCastingMode castShadows;
            public bool receiveShadows;
        }
        private static readonly Dictionary<GameObject, List<PrefabPart>> _prefabCache = new Dictionary<GameObject, List<PrefabPart>>();

        private Transform _container; // Kept only to clean up old objects if any exist

        public void Clear()
        {
            // Clear instancing data
            foreach (var batch in _batches.Values)
            {
                batch.matrices.Clear();
            }

            // Cleanup legacy GameObjects if they exist
            if (_container != null)
            {
                for (int i = _container.childCount - 1; i >= 0; i--)
                {
                    Destroy(_container.GetChild(i).gameObject);
                }
            }
            
            _lastMeshInstanceId = 0;
            _lastSeed = 0;
        }

        public bool EnsureGenerated(long chunkX, long chunkY, int lodResolution, float chunkSize, bool allowGeneration)
        {
            // Update every frame for DrawMeshInstanced
            // Note: Update() is separate, but we check if we need to regenerate here.
            
            if (settings == null) return false;
            if (heightMultiplier <= 0.0001f) return false;

            MeshCollider mc = GetComponent<MeshCollider>();
            MeshFilter mf = GetComponent<MeshFilter>();
            if (mc == null || mf == null || mf.sharedMesh == null) return false;

            int meshId = mf.sharedMesh.GetInstanceID();
            int seed = CombineSeed(globalSeed, settings.seedOffset, chunkX, chunkY);

            bool same =
                _lastChunkX == chunkX &&
                _lastChunkY == chunkY &&
                _lastLodResolution == lodResolution &&
                _lastMeshInstanceId == meshId &&
                _lastSeed == seed;

            if (same) return false; // Already up to date

            // Needs update. Check allowance.
            if (!allowGeneration)
            {
                // We need to generate but are not allowed this frame.
                // Should we Clear() old data? Only if the chunk coords changed completely.
                // If we moved to a new chunk coord, the old vegetation is invalid.
                if (_lastChunkX != chunkX || _lastChunkY != chunkY)
                {
                    Clear(); // Clear incorrect vegetation while waiting
                }
                return false; 
            }

            _lastChunkX = chunkX;
            _lastChunkY = chunkY;
            _lastLodResolution = lodResolution;
            _lastMeshInstanceId = meshId;
            _lastSeed = seed;

            Regenerate(mc, chunkX, chunkY, seed, chunkSize);
            return true;
        }

        // Called every frame to verify drawing
        public void Update()
        {
            DrawBatches();
        }

        private void DrawBatches()
        {
            if (_batches == null || _batches.Count == 0) return;

            foreach (var batch in _batches.Values)
            {
                int total = batch.matrices.Count;
                if (total == 0) continue;

                for (int i = 0; i < total; i += 1023)
                {
                    int count = Mathf.Min(1023, total - i);
                    
                    _drawBuffer.Clear();
                    // Avoid LINQ or GetRange allocs
                    for (int j = 0; j < count; j++)
                    {
                        _drawBuffer.Add(batch.matrices[i + j]);
                    }

                    Graphics.DrawMeshInstanced(
                        batch.mesh,
                        batch.submeshIndex,
                        batch.material,
                        _drawBuffer,
                        null, // MaterialPropertyBlock
                        batch.castShadows,
                        batch.receiveShadows,
                        batch.layer
                    );
                }
            }
        }

        // --- Collider Proxy Cache ---
        private struct ColliderInfo
        {
            public Type type;
            public Vector3 center;
            public Vector3 size; // For Box
            public float radius; // For Sphere/Capsule
            public float height; // For Capsule
            public int direction; // For Capsule
            public Mesh mesh; // For MeshCollider
            public bool convex; // For MeshCollider
            public Matrix4x4 localMatrix;
        }
        private static readonly Dictionary<GameObject, List<ColliderInfo>> _colliderCache = new Dictionary<GameObject, List<ColliderInfo>>();
        private static readonly Dictionary<GameObject, Bounds> _boundsCache = new Dictionary<GameObject, Bounds>();

        private void Regenerate(MeshCollider mc, long chunkX, long chunkY, int seed, float chunkSize)
        {
            Clear();

            if (settings.prefabs == null || settings.prefabs.Count == 0) return;

            // Ensure container exists for colliders
            if (_container == null) _container = FindOrCreateContainer();

            // Prepare categories
            var trees = new List<VegetationPrefabEntry>();
            var rocks = new List<VegetationPrefabEntry>();
            var plants = new List<VegetationPrefabEntry>();
            var grass = new List<VegetationPrefabEntry>();

            for (int i = 0; i < settings.prefabs.Count; i++)
            {
                var e = settings.prefabs[i];
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

            // Calculate density-based scale factor
            // Base settings are assumed to be for a 100x100 chunk
            float areaScale = (chunkSize * chunkSize) / (100f * 100f);
            if (areaScale < 0.01f) areaScale = 0.01f;

            // Spawn with collision for Trees and Rocks
            int treeCount = Mathf.RoundToInt(settings.treesPerChunk * areaScale);
            int rockCount = Mathf.RoundToInt(settings.rocksPerChunk * areaScale);
            int plantCount = Mathf.RoundToInt(settings.plantsPerChunk * areaScale);
            int grassCount = Mathf.RoundToInt(settings.grassPerChunk * areaScale);

            SpawnCategoryInstanced(mc, seed ^ 0x1A2B3C4D, trees, treeCount, settings.treeMinSpacing, chunkSize, true, enableClustering: true);
            SpawnCategoryInstanced(mc, seed ^ 0x22334455, rocks, rockCount, settings.rockMinSpacing, chunkSize, true);
            
            // Visual only for Plants and Grass
            if (settings.plantsDensityPerM2 > 0f)
            {
                SpawnCategoryDensityInstanced(mc, seed ^ 0x66778899, plants, settings.plantsDensityPerM2, settings.plantMinSpacing, chunkSize, false);
            }
            else
            {
                SpawnCategoryInstanced(mc, seed ^ 0x66778899, plants, plantCount, settings.plantMinSpacing, chunkSize, false);
            }

            if (settings.grassDensityPerM2 > 0f)
            {
                SpawnCategoryDensityInstanced(mc, seed ^ 0x13579BDF, grass, settings.grassDensityPerM2, settings.grassMinSpacing, chunkSize, false);
            }
            else
            {
                SpawnCategoryInstanced(mc, seed ^ 0x13579BDF, grass, grassCount, settings.grassMinSpacing, chunkSize, false);
            }
        }

        private void SpawnCategoryInstanced(
            MeshCollider mc,
            int seed,
            List<VegetationPrefabEntry> entries,
            int targetCount,
            float minSpacing,
            float chunkSize,
            bool createColliders,
            bool enableClustering = false)
        {
            if (entries == null || entries.Count == 0 || targetCount <= 0) return;

            minSpacing = Mathf.Max(0f, minSpacing);
            System.Random rng = new System.Random(seed);
            List<Vector3> placed = new List<Vector3>(targetCount);

            if (chunkSize <= 0.001f) return;

            int attempts = Mathf.Max(targetCount * settings.attemptsPerSpawn, targetCount);
            float yTop = Mathf.Max(200f, heightMultiplier * 3f);
            float rayLen = yTop + 500f;

            for (int a = 0; a < attempts && placed.Count < targetCount; a++)
            {
                float x = (float)rng.NextDouble() * chunkSize;
                float z = (float)rng.NextDouble() * chunkSize;

                Vector3 origin = transform.position + new Vector3(x, yTop, z);
                Ray ray = new Ray(origin, Vector3.down);

                if (!mc.Raycast(ray, out RaycastHit hit, rayLen)) continue;

                // Height filter
                float h01 = Mathf.Clamp01(hit.point.y / Mathf.Max(0.0001f, heightMultiplier));
                if (h01 < settings.minHeight01 || h01 > settings.maxHeight01) continue;

                // Water exclusion
                if (hit.point.y < (waterSurfaceY + settings.waterExclusionYOffset)) continue;

                // Slope filter
                float slope = Vector3.Angle(hit.normal, Vector3.up);
                if (slope > settings.maxSlopeDeg) continue;

                Vector3 p = hit.point; 

                // Spacing check
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
                            break;
                        }
                    }
                    if (!ok) continue;
                }

                // Pick Prefab
                VegetationPrefabEntry entry = enableClustering ? PickWeightedClustered(rng, entries, hit.point) : PickWeighted(rng, entries);
                if (entry.prefab == null) continue;

                // Transform
                Quaternion rot = ComputeRotation(rng, entry, hit.normal);
                float scale = RandomRange(rng, entry.minUniformScale, entry.maxUniformScale);
                if (scale <= 0f) scale = 1f;

                // Register Instance
                RegisterInstance(entry.prefab, p + (hit.normal * entry.yOffset), rot, scale);

                // Create Collider Proxy if needed
                if (createColliders)
                {
                    CreateColliderProxies(entry.prefab, p + (hit.normal * entry.yOffset), rot, scale);
                }

                placed.Add(p);
            }
        }

        private void SpawnCategoryDensityInstanced(
            MeshCollider mc,
            int seed,
            List<VegetationPrefabEntry> entries,
            float densityPerM2,
            float minSpacing,
            float chunkSize,
            bool createColliders,
            bool enableClustering = false)
        {
            if (entries == null || entries.Count == 0) return;
            if (densityPerM2 <= 0f) return;
            if (chunkSize <= 0.001f) return;

            float cell = Mathf.Max(0.25f, Mathf.Max(0f, minSpacing));
            float cellArea = cell * cell;
            float pSpawn = Mathf.Clamp01(densityPerM2 * cellArea);
            if (pSpawn <= 0f) return;

            int cellsX = Mathf.Max(1, Mathf.CeilToInt(chunkSize / cell));
            int cellsZ = Mathf.Max(1, Mathf.CeilToInt(chunkSize / cell));

            System.Random rng = new System.Random(seed);
            List<Vector3> placed = new List<Vector3>(Mathf.RoundToInt(chunkSize * chunkSize * densityPerM2));

            float minSq = Mathf.Max(0f, minSpacing) * Mathf.Max(0f, minSpacing);
            float yTop = Mathf.Max(200f, heightMultiplier * 3f);
            float rayLen = yTop + 500f;

            for (int iz = 0; iz < cellsZ; iz++)
            {
                for (int ix = 0; ix < cellsX; ix++)
                {
                    if (rng.NextDouble() > pSpawn) continue;

                    float x = (ix + (float)rng.NextDouble()) * cell;
                    float z = (iz + (float)rng.NextDouble()) * cell;
                    if (x < 0f || z < 0f || x > chunkSize || z > chunkSize) continue;

                    Vector3 origin = transform.position + new Vector3(x, yTop, z);
                    Ray ray = new Ray(origin, Vector3.down);
                    if (!mc.Raycast(ray, out RaycastHit hit, rayLen)) continue;

                    float h01 = Mathf.Clamp01(hit.point.y / Mathf.Max(0.0001f, heightMultiplier));
                    if (h01 < settings.minHeight01 || h01 > settings.maxHeight01) continue;
                    if (hit.point.y < (waterSurfaceY + settings.waterExclusionYOffset)) continue;
                    float slope = Vector3.Angle(hit.normal, Vector3.up);
                    if (slope > settings.maxSlopeDeg) continue;

                    Vector3 p = hit.point;
                    if (minSq > 0f)
                    {
                        bool ok = true;
                        for (int i = 0; i < placed.Count; i++)
                        {
                            Vector3 d = placed[i] - p;
                            d.y = 0f;
                            if (d.sqrMagnitude < minSq) { ok = false; break; }
                        }
                        if (!ok) continue;
                    }

                    VegetationPrefabEntry entry = enableClustering ? PickWeightedClustered(rng, entries, hit.point) : PickWeighted(rng, entries);
                    if (entry.prefab == null) continue;

                    Quaternion rot = ComputeRotation(rng, entry, hit.normal);
                    float scale = RandomRange(rng, entry.minUniformScale, entry.maxUniformScale);
                    if (scale <= 0f) scale = 1f;

                    RegisterInstance(entry.prefab, p + (hit.normal * entry.yOffset), rot, scale);
                    if (createColliders) CreateColliderProxies(entry.prefab, p + (hit.normal * entry.yOffset), rot, scale);
                    placed.Add(p);
                }
            }
        }

        private void RegisterInstance(GameObject prefab, Vector3 position, Quaternion rotation, float scale)
        {
            // Get cached parts
            if (!_prefabCache.TryGetValue(prefab, out List<PrefabPart> parts))
            {
                parts = ScanPrefab(prefab);
                _prefabCache[prefab] = parts;
            }

            if (parts == null || parts.Count == 0) return;

            Matrix4x4 instanceMat = Matrix4x4.TRS(position, rotation, Vector3.one * scale);

            for (int i = 0; i < parts.Count; i++)
            {
                PrefabPart part = parts[i];
                
                // Combine local prefab transform with instance transform
                Matrix4x4 finalMat = instanceMat * part.localMatrix;

                // Get or Create Batch
                int key = part.mesh.GetInstanceID();
                key = (key * 397) ^ part.material.GetInstanceID();
                key = (key * 397) ^ part.submeshIndex;
                key = (key * 397) ^ part.layer;
                
                if (!_batches.TryGetValue(key, out InstanceBatch batch))
                {
                    batch = new InstanceBatch
                    {
                        mesh = part.mesh,
                        material = part.material,
                        submeshIndex = part.submeshIndex,
                        layer = part.layer,
                        castShadows = ShadowCastingMode.On, // Force ON for testing
                        receiveShadows = true
                    };
                    
                    // Auto-enable instancing
                    if (batch.material != null && !batch.material.enableInstancing)
                        batch.material.enableInstancing = true;

                    _batches[key] = batch;
                }

                batch.matrices.Add(finalMat);
            }
        }

        private void CreateColliderProxies(GameObject prefab, Vector3 position, Quaternion rotation, float scale)
        {
            if (!_colliderCache.TryGetValue(prefab, out List<ColliderInfo> colliders))
            {
                colliders = ScanColliders(prefab);
                _colliderCache[prefab] = colliders;
            }

            if (colliders == null || colliders.Count == 0)
            {
                if (settings != null && settings.autoGenerateColliderWhenMissing)
                {
                    CreateAutoColliderProxy(prefab, position, rotation, scale);
                }
                return;
            }

            // Root transform for this instance
            // We can parent directly to container to save hierarchy depth, but we need world coords.
            // Or create a parent "Prop" and children colliders? 
            // Better: Just create independent collider objects parented to _container to avoid nested logic
            
            Matrix4x4 instanceMat = Matrix4x4.TRS(position, rotation, Vector3.one * scale);

            for (int i = 0; i < colliders.Count; i++)
            {
                ColliderInfo info = colliders[i];
                
                // Calculate world position/rot of the collider
                // info.localMatrix is relative to prefab root
                Matrix4x4 worldMat = instanceMat * info.localMatrix;
                
                Vector3 wPos = worldMat.GetColumn(3);
                Quaternion wRot = Quaternion.LookRotation(worldMat.GetColumn(2), worldMat.GetColumn(1));
                // Scale is trickier if non-uniform, but we assume uniform input scale. 
                // The localMatrix might have scale. instanceMat has scale.
                Vector3 wScale = worldMat.lossyScale; 
                
                GameObject go = new GameObject("Col");
                go.layer = prefab.layer; // Keep layer logic
                go.transform.position = wPos;
                go.transform.rotation = wRot;
                go.transform.localScale = wScale; 
                go.transform.SetParent(_container, true);

                if (info.type == typeof(BoxCollider))
                {
                    var c = go.AddComponent<BoxCollider>();
                    c.center = info.center;
                    c.size = info.size;
                }
                else if (info.type == typeof(SphereCollider))
                {
                    var c = go.AddComponent<SphereCollider>();
                    c.center = info.center;
                    c.radius = info.radius;
                }
                else if (info.type == typeof(CapsuleCollider))
                {
                    var c = go.AddComponent<CapsuleCollider>();
                    c.center = info.center;
                    c.radius = info.radius;
                    c.height = info.height;
                    c.direction = info.direction;
                }
                else if (info.type == typeof(MeshCollider))
                {
                    var c = go.AddComponent<MeshCollider>();
                    c.sharedMesh = info.mesh;
                    c.convex = info.convex;
                }
            }
        }

        private void CreateAutoColliderProxy(GameObject prefab, Vector3 position, Quaternion rotation, float scale)
        {
            if (prefab == null) return;

            if (!_boundsCache.TryGetValue(prefab, out Bounds b))
            {
                b = ComputePrefabLocalBounds(prefab);
                _boundsCache[prefab] = b;
            }

            if (b.size.sqrMagnitude < 1e-6f) return;

            // Create a single primitive collider from bounds
            GameObject go = new GameObject("ColAuto");
            go.layer = prefab.layer;
            go.transform.position = position;
            go.transform.rotation = rotation;
            go.transform.localScale = Vector3.one * Mathf.Max(0.001f, scale);
            go.transform.SetParent(_container, true);

            bool capsule = (b.size.y > Mathf.Max(b.size.x, b.size.z) * 1.2f);
            if (capsule)
            {
                var c = go.AddComponent<CapsuleCollider>();
                c.direction = 1;
                c.center = b.center;
                float r = Mathf.Max(0.05f, Mathf.Min(b.extents.x, b.extents.z));
                c.radius = r;
                c.height = Mathf.Max(r * 2f, b.size.y);
            }
            else
            {
                var c = go.AddComponent<BoxCollider>();
                c.center = b.center;
                c.size = b.size;
            }
        }

        private static Bounds ComputePrefabLocalBounds(GameObject prefab)
        {
            if (prefab == null) return new Bounds(Vector3.zero, Vector3.zero);
            var rs = prefab.GetComponentsInChildren<Renderer>(true);
            if (rs == null || rs.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);

            Transform root = prefab.transform;
            bool has = false;
            Bounds b = new Bounds(Vector3.zero, Vector3.zero);

            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                Bounds wb = rs[i].bounds;
                Vector3 c = root.InverseTransformPoint(wb.center);
                Vector3 e = wb.extents;
                Vector3 eLocal = new Vector3(
                    Mathf.Abs(root.InverseTransformVector(new Vector3(e.x, 0, 0)).x),
                    Mathf.Abs(root.InverseTransformVector(new Vector3(0, e.y, 0)).y),
                    Mathf.Abs(root.InverseTransformVector(new Vector3(0, 0, e.z)).z));

                Bounds lb = new Bounds(c, eLocal * 2f);
                if (!has) { b = lb; has = true; }
                else { b.Encapsulate(lb.min); b.Encapsulate(lb.max); }
            }

            return b;
        }

        private List<ColliderInfo> ScanColliders(GameObject prefab)
        {
            var list = new List<ColliderInfo>();
            var cols = prefab.GetComponentsInChildren<Collider>(true);
            foreach (var c in cols)
            {
                // Calculate local matrix relative to prefab root
                Matrix4x4 localMat = Matrix4x4.identity;
                Transform t = c.transform;
                while (t != prefab.transform && t != null)
                {
                    localMat = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * localMat;
                    t = t.parent;
                }

                if (c is BoxCollider bc)
                {
                    list.Add(new ColliderInfo { type = typeof(BoxCollider), center = bc.center, size = bc.size, localMatrix = localMat });
                }
                else if (c is SphereCollider sc)
                {
                    list.Add(new ColliderInfo { type = typeof(SphereCollider), center = sc.center, radius = sc.radius, localMatrix = localMat });
                }
                else if (c is CapsuleCollider cc)
                {
                    list.Add(new ColliderInfo { type = typeof(CapsuleCollider), center = cc.center, radius = cc.radius, height = cc.height, direction = cc.direction, localMatrix = localMat });
                }
                else if (c is MeshCollider mc)
                {
                    list.Add(new ColliderInfo { type = typeof(MeshCollider), mesh = mc.sharedMesh, convex = mc.convex, localMatrix = localMat });
                }
            }
            return list;
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

        private List<PrefabPart> ScanPrefab(GameObject prefab)
        {
            var parts = new List<PrefabPart>();
            
            // Determine which renderers to use
            // If LODGroup exists, use only LOD 0 to avoid rendering all LODs at once
            Renderer[] renderersToScan;
            LODGroup lodGroup = prefab.GetComponent<LODGroup>();
            if (lodGroup != null)
            {
                LOD[] lods = lodGroup.GetLODs();
                if (lods != null && lods.Length > 0)
                {
                    renderersToScan = lods[0].renderers;
                }
                else
                {
                    renderersToScan = prefab.GetComponentsInChildren<Renderer>(true);
                }
            }
            else
            {
                renderersToScan = prefab.GetComponentsInChildren<Renderer>(true);
            }
            
            if (renderersToScan == null) return parts;

            foreach (var r in renderersToScan)
            {
                if (r == null) continue;
                
                // We only support MeshRenderer + MeshFilter for instancing
                MeshRenderer mr = r as MeshRenderer;
                if (mr == null) continue;

                MeshFilter mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                // We need local matrix relative to prefab root
                Matrix4x4 localMat = Matrix4x4.identity;
                Transform t = mr.transform;
                
                // If the renderer is part of the prefab, calculate relative transform
                // Note: LODGroup renderers might be deep in hierarchy
                
                while (t != prefab.transform && t != null)
                {
                    localMat = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * localMat;
                    t = t.parent;
                }

                // If t is null here, it means r is NOT a child of prefab? 
                // Checks:
                if (t == null && mr.transform != prefab.transform)
                {
                     // This should not happen if we got renderers via GetComponentsInChildren or LODGroup on the same prefab
                     continue;
                }

                var mats = mr.sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] == null) continue;
                    if (mf.sharedMesh == null) continue;
                    if (m >= mf.sharedMesh.subMeshCount) continue; // safety: avoid invalid submesh index (causes "invisible" instances)
                    
                    parts.Add(new PrefabPart
                    {
                        mesh = mf.sharedMesh,
                        material = mats[m],
                        submeshIndex = m,
                        localMatrix = localMat,
                        layer = mr.gameObject.layer,
                        castShadows = mr.shadowCastingMode,
                        receiveShadows = mr.receiveShadows
                    });
                }
            }
            return parts;
        }
        
        // --- Helper Funcs ---

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

        private VegetationPrefabEntry PickWeightedClustered(System.Random rng, List<VegetationPrefabEntry> entries, Vector3 worldPos)
        {
            if (settings == null || !settings.enableTreeClustering || settings.treeClusterCellSize <= 0.01f)
            {
                return PickWeighted(rng, entries);
            }

            float total = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                float w = Mathf.Max(0f, entries[i].weight);
                if (w <= 0f) continue;
                float patch = VegetationNoise.PatchFactor(worldPos.x, worldPos.z, entries[i].prefab, globalSeed, settings);
                total += w * patch;
            }
            if (total <= 0f) return PickWeighted(rng, entries);

            float r = (float)rng.NextDouble() * total;
            float acc = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                float w = Mathf.Max(0f, entries[i].weight);
                if (w <= 0f) continue;
                float patch = VegetationNoise.PatchFactor(worldPos.x, worldPos.z, entries[i].prefab, globalSeed, settings);
                acc += w * patch;
                if (r <= acc) return entries[i];
            }
            return entries[entries.Count - 1];
        }
    }
}
