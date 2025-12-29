using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace InfinityTerrain.Vegetation
{
    /// <summary>
    /// Fills Unity built-in Terrain detail layers (grass/foliage) on a Terrain chunk.
    /// Designed for InfinityTerrain built-in Terrain streaming: deterministic per chunk coords + cached.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkTerrainDetailScatter : MonoBehaviour
    {
        [NonSerialized] public VegetationScatterSettings settings;
        [NonSerialized] public int globalSeed;
        [NonSerialized] public float heightMultiplier;
        [NonSerialized] public float waterSurfaceY;

        [Tooltip("Include VegetationCategory.Grass entries as Terrain detail layers.")]
        public bool includeGrass = true;

        [Tooltip("Include VegetationCategory.Plant entries as Terrain detail layers.")]
        public bool includePlants = true;

        [Header("Performance (Jobs/Burst)")]
        [Tooltip("If enabled, uses Unity Job System + Burst to compute detail layers faster. " +
                 "TerrainData.SetDetailLayer is still applied on the main thread.")]
        public bool useJobsAndBurst = true;

        [Tooltip("Batch size for IJobParallelFor scheduling. Typical: 32..256.")]
        public int jobBatchSize = 128;

        [Tooltip("If enabled, scales Terrain detail resolution down for far LOD chunks.")]
        public bool scaleDetailResolutionWithLod = true;

        [Tooltip("Minimum detail resolution when scaling with LOD.")]
        public int minDetailResolutionWhenScaling = 64;

        private long _lastChunkX;
        private long _lastChunkY;
        private int _lastLodResolution;
        private int _lastSeed;
        private int _lastSettingsHash;

        private static readonly HashSet<int> _invalidDetailPrefabWarned = new HashSet<int>();
        private static readonly HashSet<int> _usingChildDetailPrefabWarned = new HashSet<int>();

        // Cache reflection for Unity 2022.2+ DetailScatterMode to avoid scanning AppDomain every regen.
        private static bool _scatterModeCached;
        private static MethodInfo _setDetailScatterModeMethod;
        private static object _instanceCountModeValue;
        private static object _coverageModeValue;
        private static readonly object[] _scatterModeArgs = new object[1];

        private static void TryCacheDetailScatterModeApi()
        {
            if (_scatterModeCached) return;
            _scatterModeCached = true;

            try
            {
                // "Gọn + chuẩn": lấy enum từ assembly của TerrainData (UnityEngine.TerrainModule)
                Type enumType = typeof(TerrainData).Assembly.GetType("UnityEngine.DetailScatterMode");
                if (enumType == null)
                {
                    // Fallback for some configurations
                    enumType = Type.GetType("UnityEngine.DetailScatterMode, UnityEngine.TerrainModule");
                }

                if (enumType != null)
                {
                    _instanceCountModeValue = Enum.Parse(enumType, "InstanceCountMode");
                    // Unity 2022.2+: CoverageMode uses 0..255 values in detail maps.
                    _coverageModeValue = Enum.Parse(enumType, "CoverageMode");
                }

                _setDetailScatterModeMethod = typeof(TerrainData).GetMethod(
                    "SetDetailScatterMode",
                    BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                _setDetailScatterModeMethod = null;
                _instanceCountModeValue = null;
                _coverageModeValue = null;
            }
        }

        private static void TrySetDetailScatterMode(TerrainData td, bool useCoverageMode)
        {
            if (td == null) return;
            TryCacheDetailScatterModeApi();
            if (_setDetailScatterModeMethod == null) return;

            object modeValue = useCoverageMode ? _coverageModeValue : _instanceCountModeValue;
            if (modeValue == null) return;

            _scatterModeArgs[0] = modeValue;
            _setDetailScatterModeMethod.Invoke(td, _scatterModeArgs);
        }

        public void Clear()
        {
            Terrain t = GetComponent<Terrain>();
            if (t == null || t.terrainData == null) return;
            TerrainData td = t.terrainData;

            int res = td.detailResolution;
            int layers = (td.detailPrototypes != null) ? td.detailPrototypes.Length : 0;
            if (res > 0 && layers > 0)
            {
                int[,] empty = new int[res, res]; // [y,x]
                for (int i = 0; i < layers; i++)
                {
                    td.SetDetailLayer(0, 0, i, empty);
                }
            }

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

            Regenerate(terrain, chunkX, chunkY, seed, chunkSizeWorld, heights01);
            return true;
        }

        private void Regenerate(Terrain terrain, long chunkX, long chunkY, int seed, float chunkSizeWorld, float[,] heights01)
        {
            TerrainData td = terrain.terrainData;
            if (td == null) return;

            CollectDetailEntries(
                settings,
                includeGrass,
                includePlants,
                out List<VegetationPrefabEntry> uniqueEntries,
                out List<int> grassProtoIdx,
                out List<float> grassWeights,
                out List<int> plantProtoIdx,
                out List<float> plantWeights);

            if (uniqueEntries.Count == 0 || (grassProtoIdx.Count == 0 && plantProtoIdx.Count == 0))
            {
                Clear();
                return;
            }

            // Unity requires detailResolution to be a multiple of resolutionPerPatch.
            int perPatch = Mathf.Clamp(settings.terrainDetailResolutionPerPatch, 4, 64);
            int desiredRes = Mathf.Clamp(settings.terrainDetailResolution, 32, 1024);

            if (scaleDetailResolutionWithLod)
            {
                // Tie detail density to chunk heightmap LOD to reduce CPU generation work far away.
                // Example: 129->258 (clamped), 65->130, 33->66, 17->34 (then clamped to min).
                int lodBased = Mathf.Max(minDetailResolutionWhenScaling, _lastLodResolution * 2);
                desiredRes = Mathf.Min(desiredRes, lodBased);
            }

            desiredRes = Mathf.Max(desiredRes, perPatch);
            desiredRes = (desiredRes / perPatch) * perPatch;
            if (desiredRes < perPatch) desiredRes = perPatch;

            // Avoid reallocating detail data if resolution/perPatch already match (SetDetailResolution is expensive).
            bool needSetDetailRes = true;
            try
            {
                needSetDetailRes = td.detailResolution != desiredRes || td.detailResolutionPerPatch != perPatch;
            }
            catch
            {
                needSetDetailRes = true;
            }

            if (needSetDetailRes)
            {
                try
                {
                    td.SetDetailResolution(desiredRes, perPatch);
                }
                catch
                {
                    // Rare: Unity throws if it dislikes the combo. Fall back to a safe default.
                    td.SetDetailResolution(256, 16);
                }
            }

            // Unity 2022.2+: pick scatter mode so int[,] values are interpreted correctly.
            // - InstanceCountMode => 0..16 per-cell instance count
            // - CoverageMode      => 0..255 per-cell coverage
            try
            {
                bool useCoverageMode = settings != null && settings.terrainDetailsUseCoverageMode;
                TrySetDetailScatterMode(td, useCoverageMode);
            }
            catch
            {
                // Unity version doesn't support SetDetailScatterMode (pre-2022.2), fallback to default behavior
            }

            td.detailPrototypes = BuildDetailPrototypes(uniqueEntries);
            
            // Refresh prototypes to ensure Unity reloads detail assets (fixes "no spawn" issues)
            try
            {
                td.RefreshPrototypes();
            }
            catch
            {
                // Unity version may not have RefreshPrototypes() method (pre-2021.2)
            }

            int res = td.detailResolution;
            if (res <= 0)
            {
                Clear();
                return;
            }

            int layerCount = td.detailPrototypes != null ? td.detailPrototypes.Length : 0;
            if (layerCount <= 0)
            {
                Clear();
                return;
            }

            var layers = new List<int[,]>(layerCount);
            for (int i = 0; i < layerCount; i++)
            {
                layers.Add(new int[res, res]); // [y,x]
            }

            int heightRes = heights01.GetLength(0);
            float heightStep = (heightRes > 1) ? (chunkSizeWorld / (heightRes - 1)) : chunkSizeWorld;

            float chunkArea = chunkSizeWorld * chunkSizeWorld;
            float cellArea = chunkArea / Mathf.Max(1, res * res);
            float areaScale = chunkArea / (100f * 100f);
            if (areaScale < 0.01f) areaScale = 0.01f;

            bool useCoverage = settings != null && settings.terrainDetailsUseCoverageMode;

            int grassMaxPerCell = includeGrass ? Mathf.Clamp(settings.grassMaxPerCell, 0, 16) : 0;
            int plantsMaxPerCell = includePlants ? Mathf.Clamp(settings.plantsMaxPerCell, 0, 16) : 0;

            float cellSize = chunkSizeWorld / res;

            // Jobs + Burst path
            if (useJobsAndBurst)
            {
                NativeArray<float> heightsFlat = default;
                NativeArray<int> grassIdx = default;
                NativeArray<float> grassCum = default;
                NativeArray<int> plantIdx = default;
                NativeArray<float> plantCum = default;
                NativeArray<short> outGrassLayer = default;
                NativeArray<byte> outGrassValue = default;
                NativeArray<short> outPlantLayer = default;
                NativeArray<byte> outPlantValue = default;

                try
                {
                    int totalCells = res * res;

                    // Flatten heightmap for Burst access: heights01[y,x] => flat[y*heightRes+x]
                    heightsFlat = new NativeArray<float>(heightRes * heightRes, Allocator.TempJob);
                    for (int y = 0; y < heightRes; y++)
                    {
                        int row = y * heightRes;
                        for (int x = 0; x < heightRes; x++)
                        {
                            heightsFlat[row + x] = heights01[y, x];
                        }
                    }

                    // Proto arrays + cumulative weights
                    grassIdx = new NativeArray<int>(grassProtoIdx.Count, Allocator.TempJob);
                    grassCum = new NativeArray<float>(grassWeights.Count, Allocator.TempJob);
                    float grassAcc = 0f;
                    for (int i = 0; i < grassProtoIdx.Count; i++)
                    {
                        grassIdx[i] = grassProtoIdx[i];
                        grassAcc += Mathf.Max(0f, grassWeights[i]);
                        grassCum[i] = grassAcc;
                    }

                    plantIdx = new NativeArray<int>(plantProtoIdx.Count, Allocator.TempJob);
                    plantCum = new NativeArray<float>(plantWeights.Count, Allocator.TempJob);
                    float plantAcc = 0f;
                    for (int i = 0; i < plantProtoIdx.Count; i++)
                    {
                        plantIdx[i] = plantProtoIdx[i];
                        plantAcc += Mathf.Max(0f, plantWeights[i]);
                        plantCum[i] = plantAcc;
                    }

                    outGrassLayer = new NativeArray<short>(totalCells, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                    outGrassValue = new NativeArray<byte>(totalCells, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                    outPlantLayer = new NativeArray<short>(totalCells, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                    outPlantValue = new NativeArray<byte>(totalCells, Allocator.TempJob, NativeArrayOptions.ClearMemory);

                    var job = new TerrainDetailScatterBurstJobs.DetailScatterJob
                    {
                        heights01Flat = heightsFlat,
                        heightRes = heightRes,
                        heightStep = Mathf.Max(0.0001f, heightStep),
                        heightMultiplier = Mathf.Max(0.0001f, heightMultiplier),
                        waterSurfaceY = waterSurfaceY,
                        minHeight01 = settings.minHeight01,
                        maxHeight01 = settings.maxHeight01,
                        maxSlopeDeg = settings.maxSlopeDeg,
                        waterExclusionYOffset = settings.waterExclusionYOffset,
                        detailRes = res,
                        chunkSizeWorld = chunkSizeWorld,
                        chunkX = chunkX,
                        chunkY = chunkY,
                        seed = seed,
                        useCoverage = useCoverage,
                        includeGrass = includeGrass,
                        grassDensity01 = Mathf.Clamp01(settings.grassDensityPerM2),
                        grassMaxPerCell = grassMaxPerCell,
                        grassPerChunk = Mathf.Max(0f, settings.grassPerChunk),
                        areaScale = areaScale,
                        chunkArea = chunkArea,
                        cellArea = cellArea,
                        grassProtoIdx = grassIdx,
                        grassCumWeights = grassCum,
                        includePlants = includePlants,
                        plantsDensity01 = Mathf.Clamp01(settings.plantsDensityPerM2),
                        plantsMaxPerCell = plantsMaxPerCell,
                        plantsPerChunk = Mathf.Max(0f, settings.plantsPerChunk),
                        plantProtoIdx = plantIdx,
                        plantCumWeights = plantCum,
                        outGrassLayer = outGrassLayer,
                        outGrassValue = outGrassValue,
                        outPlantLayer = outPlantLayer,
                        outPlantValue = outPlantValue
                    };

                    int batch = Mathf.Clamp(jobBatchSize, 16, 1024);
                    JobHandle h = job.Schedule(totalCells, batch);
                    h.Complete();

                    // Apply results into per-layer int[,] arrays (main thread).
                    for (int idx = 0; idx < totalCells; idx++)
                    {
                        int y = idx / res;
                        int x = idx - (y * res);

                        short gl = outGrassLayer[idx];
                        byte gv = outGrassValue[idx];
                        if (gv > 0 && gl >= 0 && gl < layerCount)
                        {
                            int cur = layers[gl][y, x];
                            if (gv > cur) layers[gl][y, x] = gv;
                        }

                        short pl = outPlantLayer[idx];
                        byte pv = outPlantValue[idx];
                        if (pv > 0 && pl >= 0 && pl < layerCount)
                        {
                            int cur = layers[pl][y, x];
                            if (pv > cur) layers[pl][y, x] = pv;
                        }
                    }

                    // Skip the old CPU loops below.
                    goto APPLY_LAYERS;
                }
                catch
                {
                    // Fallback to CPU implementation below if Burst/Jobs fail for any reason.
                }
                finally
                {
                    if (heightsFlat.IsCreated) heightsFlat.Dispose();
                    if (grassIdx.IsCreated) grassIdx.Dispose();
                    if (grassCum.IsCreated) grassCum.Dispose();
                    if (plantIdx.IsCreated) plantIdx.Dispose();
                    if (plantCum.IsCreated) plantCum.Dispose();
                    if (outGrassLayer.IsCreated) outGrassLayer.Dispose();
                    if (outGrassValue.IsCreated) outGrassValue.Dispose();
                    if (outPlantLayer.IsCreated) outPlantLayer.Dispose();
                    if (outPlantValue.IsCreated) outPlantValue.Dispose();
                }
            }

            for (int y = 0; y < res; y++)
            {
                float localZ = (y + 0.5f) * cellSize;
                for (int x = 0; x < res; x++)
                {
                    float localX = (x + 0.5f) * cellSize;

                    if (!PassPlacementFilters(heights01, localX, localZ, heightStep, out _)) continue;

                    if (useCoverage)
                    {
                        // CoverageMode: interpret density fields as normalized intensity (0..1) -> 0..255 coverage
                        if (grassProtoIdx.Count > 0 && settings.grassDensityPerM2 > 0f)
                        {
                            float rCov = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x13579BDF));
                            int cov = SampleValue01ToInt(settings.grassDensityPerM2, 255, rCov);
                            if (cov > 0)
                            {
                                float rPick = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x2468ACE0));
                                int layer = PickWeightedIndex(rPick, grassProtoIdx, grassWeights);
                                if ((uint)layer < (uint)layerCount) layers[layer][y, x] = cov;
                            }
                        }

                        if (plantProtoIdx.Count > 0 && settings.plantsDensityPerM2 > 0f)
                        {
                            float rCov = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x66778899));
                            int cov = SampleValue01ToInt(settings.plantsDensityPerM2, 255, rCov);
                            if (cov > 0)
                            {
                                float rPick = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ unchecked((int)0x99AABBCC)));
                                int layer = PickWeightedIndex(rPick, plantProtoIdx, plantWeights);
                                if ((uint)layer < (uint)layerCount)
                                {
                                    layers[layer][y, x] = Mathf.Max(layers[layer][y, x], cov);
                                }
                            }
                        }

                        continue;
                    }

                    // InstanceCountMode:
                    // - if density > 0 => treat density as normalized intensity (0..1) -> 0..16 instances per cell (no extra cap)
                    // - else => fallback to legacy per-chunk budgets (capped by *MaxPerCell)
                    if (grassProtoIdx.Count > 0)
                    {
                        float rCount = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x13579BDF));

                        int count;
                        if (settings.grassDensityPerM2 > 0f)
                        {
                            // 1.0 => 16
                            count = SampleValue01ToInt(settings.grassDensityPerM2, 16, rCount);
                        }
                        else
                        {
                            if (grassMaxPerCell <= 0) count = 0;
                            else
                            {
                            float grassLambda = ComputeLambdaPerCell(
                                0f,
                                settings.grassPerChunk * areaScale,
                                chunkArea,
                                cellArea);
                            count = SampleCount(grassLambda, grassMaxPerCell, rCount);
                            }
                        }

                        if (count > 0)
                        {
                            float rPick = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x2468ACE0));
                            int layer = PickWeightedIndex(rPick, grassProtoIdx, grassWeights);
                            if ((uint)layer < (uint)layerCount)
                            {
                                layers[layer][y, x] = count;
                            }
                        }
                    }

                    if (plantProtoIdx.Count > 0)
                    {
                        float rCount = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ 0x66778899));

                        int count;
                        if (settings.plantsDensityPerM2 > 0f)
                        {
                            // 1.0 => 16
                            count = SampleValue01ToInt(settings.plantsDensityPerM2, 16, rCount);
                        }
                        else
                        {
                            if (plantsMaxPerCell <= 0) count = 0;
                            else
                            {
                            float plantsLambda = ComputeLambdaPerCell(
                                0f,
                                settings.plantsPerChunk * areaScale,
                                chunkArea,
                                cellArea);
                            count = SampleCount(plantsLambda, plantsMaxPerCell, rCount);
                            }
                        }

                        if (count > 0)
                        {
                            float rPick = Hash01(HashCell(chunkX, chunkY, x, y, seed ^ unchecked((int)0x99AABBCC)));
                            int layer = PickWeightedIndex(rPick, plantProtoIdx, plantWeights);
                            if ((uint)layer < (uint)layerCount)
                            {
                                layers[layer][y, x] = Mathf.Max(layers[layer][y, x], count);
                            }
                        }
                    }
                }
            }

        APPLY_LAYERS:
            for (int i = 0; i < layerCount; i++)
            {
                td.SetDetailLayer(0, 0, i, layers[i]);
            }

            // Đảm bảo Terrain render settings cho phép hiển thị details
            // Nếu distance/density = 0 thì dù có data cũng không render
            terrain.detailObjectDistance = Mathf.Max(terrain.detailObjectDistance, 80f);
            terrain.detailObjectDensity = Mathf.Max(terrain.detailObjectDensity, 1f);

            terrain.Flush();
        }

        private bool PassPlacementFilters(float[,] heights01, float x, float z, float heightStep, out float h01)
        {
            h01 = SampleHeight01Bilinear(heights01, x, z, heightStep);
            float hWorld = h01 * heightMultiplier;

            if (h01 < settings.minHeight01 || h01 > settings.maxHeight01) return false;
            if (hWorld < (waterSurfaceY + settings.waterExclusionYOffset)) return false;

            float slopeDeg = SampleSlopeDeg(heights01, x, z, heightStep, heightMultiplier);
            if (slopeDeg > settings.maxSlopeDeg) return false;

            return true;
        }

        private static void CollectDetailEntries(
            VegetationScatterSettings s,
            bool includeGrass,
            bool includePlants,
            out List<VegetationPrefabEntry> uniqueEntries,
            out List<int> grassProtoIdx,
            out List<float> grassWeights,
            out List<int> plantProtoIdx,
            out List<float> plantWeights)
        {
            uniqueEntries = new List<VegetationPrefabEntry>();
            grassProtoIdx = new List<int>();
            grassWeights = new List<float>();
            plantProtoIdx = new List<int>();
            plantWeights = new List<float>();

            if (s == null || s.prefabs == null) return;

            Dictionary<int, int> prefabIdToProto = new Dictionary<int, int>();

            for (int i = 0; i < s.prefabs.Count; i++)
            {
                VegetationPrefabEntry e = s.prefabs[i];
                if (e.prefab == null || e.weight <= 0f) continue;

                bool isGrass = e.category == VegetationCategory.Grass;
                bool isPlant = e.category == VegetationCategory.Plant || e.category == VegetationCategory.Other;

                if (isGrass && !includeGrass) continue;
                if (isPlant && !includePlants) continue;
                if (!isGrass && !isPlant) continue;

                // Terrain detail prototypes are stricter than trees; we validate and may use a child MeshRenderer if root is empty.
                if (!TryGetValidDetailPrototype(e.prefab, out GameObject protoGo, out string reason))
                {
                    WarnInvalidDetailPrefabOnce(e.prefab, reason);
                    continue;
                }

                // Replace the entry's prefab with the validated prototype GameObject (may be a child).
                e.prefab = protoGo;

                int protoId = e.prefab.GetInstanceID();
                if (!prefabIdToProto.TryGetValue(protoId, out int protoIndex))
                {
                    protoIndex = uniqueEntries.Count;
                    prefabIdToProto[protoId] = protoIndex;
                    uniqueEntries.Add(e);
                }

                if (isGrass)
                {
                    grassProtoIdx.Add(protoIndex);
                    grassWeights.Add(Mathf.Max(0f, e.weight));
                }
                else
                {
                    plantProtoIdx.Add(protoIndex);
                    plantWeights.Add(Mathf.Max(0f, e.weight));
                }
            }
        }

        private static DetailPrototype[] BuildDetailPrototypes(List<VegetationPrefabEntry> entries)
        {
            DetailPrototype[] protos = new DetailPrototype[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                VegetationPrefabEntry e = entries[i];

                float minS = Mathf.Max(0.01f, e.minUniformScale);
                float maxS = Mathf.Max(0.01f, e.maxUniformScale);
                if (maxS < minS) { float t = minS; minS = maxS; maxS = t; }

                // Enable GPU Instancing for proper material/shader rendering (fixes white/blank textures in URP)
                // Unity 6 DetailPrototype supports useInstancing field
                // Quan trọng: Mesh-based details phải dùng VertexLit, không dùng Grass (Grass chỉ cho texture/billboard)
                // Unity doc: GrassBillboard => usePrototypeMesh = false, VertexLit => usePrototypeMesh = true
                protos[i] = new DetailPrototype
                {
                    usePrototypeMesh = true,
                    prototype = e.prefab,
                    minWidth = minS,
                    maxWidth = maxS,
                    minHeight = minS,
                    maxHeight = maxS,
                    noiseSpread = 0.5f,
                    healthyColor = Color.white,
                    dryColor = Color.white,
                    // Mesh-based details (usePrototypeMesh=true) phải dùng VertexLit, không dùng Grass
                    // Grass mode chỉ dành cho texture/billboard (usePrototypeMesh=false)
                    renderMode = DetailRenderMode.VertexLit
                };

                // Set useInstancing via reflection for Unity 6 compatibility (field/property exists in Unity 6+)
                // Try as property first, then as field (Unity API may vary)
                try
                {
                    var useInstancingProp = typeof(DetailPrototype).GetProperty("useInstancing");
                    if (useInstancingProp != null && useInstancingProp.CanWrite)
                    {
                        useInstancingProp.SetValue(protos[i], true, null);
                    }
                    else
                    {
                        // Try as field if property doesn't exist
                        var useInstancingField = typeof(DetailPrototype).GetField("useInstancing", 
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (useInstancingField != null)
                        {
                            useInstancingField.SetValue(protos[i], true);
                        }
                    }
                }
                catch
                {
                    // Unity version doesn't support useInstancing (pre-6.0), fallback to non-instanced
                }

                // Ensure material on prefab has instancing enabled (required for GPU instanced details)
                if (e.prefab != null)
                {
                    MeshRenderer mr = e.prefab.GetComponentInChildren<MeshRenderer>(true);
                    if (mr != null && mr.sharedMaterial != null)
                    {
                        try
                        {
                            if (!mr.sharedMaterial.enableInstancing)
                            {
                                mr.sharedMaterial.enableInstancing = true;
                            }
                        }
                        catch
                        {
                            // Material may not support instancing (e.g., legacy shader)
                        }
                    }
                }
            }
            return protos;
        }

        private static void WarnInvalidDetailPrefabOnce(GameObject prefabRoot, string reason)
        {
            if (prefabRoot == null) return;
            int id = prefabRoot.GetInstanceID();
            if (_invalidDetailPrefabWarned.Contains(id)) return;
            _invalidDetailPrefabWarned.Add(id);
            Debug.LogWarning($"[InfinityTerrain] Skipping Terrain detail prototype '{prefabRoot.name}': {reason}");
        }

        private static void WarnUsingChildDetailPrefabOnce(GameObject prefabRoot, GameObject childGo)
        {
            if (prefabRoot == null || childGo == null) return;
            int id = prefabRoot.GetInstanceID();
            if (_usingChildDetailPrefabWarned.Contains(id)) return;
            _usingChildDetailPrefabWarned.Add(id);
            Debug.LogWarning(
                $"[InfinityTerrain] Using child GameObject '{childGo.name}' as detail prototype for '{prefabRoot.name}' " +
                "(root has no MeshRenderer). This may cause rendering issues. " +
                "RECOMMENDED: Create a dedicated detail prefab with MeshRenderer+MeshFilter at the root.");
        }

        private static bool TryGetValidDetailPrototype(GameObject prefabRoot, out GameObject prototypeGo, out string reason)
        {
            prototypeGo = null;
            reason = null;
            if (prefabRoot == null) { reason = "Prefab is null."; return false; }

            // If LODGroup exists, ensure it doesn't reference missing renderers (avoids Editor TerrainTools MissingComponentException).
            LODGroup lod = prefabRoot.GetComponent<LODGroup>();
            if (lod != null)
            {
                LOD[] lods;
                try { lods = lod.GetLODs(); }
                catch (Exception e) { reason = $"LODGroup threw while reading LODs ({e.GetType().Name})."; return false; }

                if (lods != null)
                {
                    for (int i = 0; i < lods.Length; i++)
                    {
                        var rs = lods[i].renderers;
                        if (rs == null) continue;
                        for (int r = 0; r < rs.Length; r++)
                        {
                            if (rs[r] == null)
                            {
                                reason = "LODGroup has a missing renderer reference (null).";
                                return false;
                            }
                            try { _ = rs[r].sharedMaterial; }
                            catch (MissingComponentException)
                            {
                                reason = "LODGroup references a missing Renderer component.";
                                return false;
                            }
                        }
                    }
                }
            }

            // Candidate 1: root itself (preferred for Terrain details - Unity works best with root-level renderers)
            if (IsValidDetailMeshObject(prefabRoot))
            {
                prototypeGo = prefabRoot;
                return true;
            }

            // Candidate 2: any child with MeshRenderer+MeshFilter+mesh+material
            // Note: Using child renderers can cause issues with Terrain details, but we allow it as fallback
            MeshRenderer[] mrs = prefabRoot.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < mrs.Length; i++)
            {
                if (mrs[i] == null) continue;
                GameObject go = mrs[i].gameObject;
                if (go == null) continue;
                if (IsValidDetailMeshObject(go))
                {
                    prototypeGo = go;
                    // Warn once that we're using a child renderer (not ideal for Terrain details)
                    WarnUsingChildDetailPrefabOnce(prefabRoot, go);
                    return true;
                }
            }

            reason = "No valid MeshRenderer+MeshFilter with a mesh+material found (Terrain details require a valid mesh renderer).";
            return false;
        }

        private static bool IsValidDetailMeshObject(GameObject go)
        {
            if (go == null) return false;
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            MeshFilter mf = go.GetComponent<MeshFilter>();
            if (mr == null || mf == null) return false;
            if (mf.sharedMesh == null) return false;
            try
            {
                // Accessing sharedMaterial can throw MissingComponentException if the renderer ref is broken.
                if (mr.sharedMaterial == null) return false;
            }
            catch (MissingComponentException)
            {
                return false;
            }
            return true;
        }

        private static float ComputeLambdaPerCell(float densityPerM2, float perChunkTarget, float chunkArea, float cellArea)
        {
            if (chunkArea <= 0.0001f || cellArea <= 0.0001f) return 0f;

            float total;
            if (densityPerM2 > 0f)
            {
                total = densityPerM2 * chunkArea;
            }
            else
            {
                total = Mathf.Max(0f, perChunkTarget);
            }

            float effectiveDensity = total / chunkArea;
            return effectiveDensity * cellArea;
        }

        private static int SampleValue01ToInt(float value, int maxInclusive, float r01)
        {
            if (maxInclusive <= 0) return 0;
            float v01 = Mathf.Clamp01(value);
            float scaled = v01 * maxInclusive;
            int baseV = Mathf.FloorToInt(scaled);
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

            int baseCount = Mathf.FloorToInt(lambda);
            float frac = lambda - baseCount;
            int c = baseCount + ((r01 < frac) ? 1 : 0);
            if (c > maxPerCell) c = maxPerCell;
            if (c < 0) c = 0;
            return c;
        }

        private static int PickWeightedIndex(float r01, List<int> indices, List<float> weights)
        {
            if (indices == null || weights == null || indices.Count == 0) return -1;
            float total = 0f;
            for (int i = 0; i < indices.Count; i++) total += Mathf.Max(0f, weights[i]);
            if (total <= 0f) return indices[0];

            float r = Mathf.Clamp01(r01) * total;
            float acc = 0f;
            for (int i = 0; i < indices.Count; i++)
            {
                acc += Mathf.Max(0f, weights[i]);
                if (r <= acc) return indices[i];
            }
            return indices[indices.Count - 1];
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
                if (s == null) return h;

                h = (h * 31) ^ s.seedOffset;
                h = (h * 31) ^ Mathf.RoundToInt(s.minHeight01 * 10000f);
                h = (h * 31) ^ Mathf.RoundToInt(s.maxHeight01 * 10000f);
                h = (h * 31) ^ Mathf.RoundToInt(s.maxSlopeDeg * 100f);
                h = (h * 31) ^ Mathf.RoundToInt(s.waterExclusionYOffset * 100f);
                h = (h * 31) ^ s.terrainDetailsUseCoverageMode.GetHashCode();
                h = (h * 31) ^ s.terrainDetailResolution;
                h = (h * 31) ^ s.terrainDetailResolutionPerPatch;
                h = (h * 31) ^ s.grassMaxPerCell;
                h = (h * 31) ^ s.plantsMaxPerCell;
                h = (h * 31) ^ s.plantsPerChunk;
                h = (h * 31) ^ s.grassPerChunk;
                h = (h * 31) ^ Mathf.RoundToInt(s.plantsDensityPerM2 * 10000f);
                h = (h * 31) ^ Mathf.RoundToInt(s.grassDensityPerM2 * 10000f);

                if (s.prefabs != null)
                {
                    int count = Mathf.Min(128, s.prefabs.Count);
                    for (int i = 0; i < count; i++)
                    {
                        var e = s.prefabs[i];
                        if (e.prefab == null) continue;
                        bool isGrass = e.category == VegetationCategory.Grass;
                        bool isPlant = e.category == VegetationCategory.Plant || e.category == VegetationCategory.Other;
                        if (!isGrass && !isPlant) continue;
                        if (isGrass && !includeGrass) continue;
                        if (isPlant && !includePlants) continue;
                        h = (h * 31) ^ e.prefab.GetInstanceID();
                        h = (h * 31) ^ (int)e.category;
                        h = (h * 31) ^ Mathf.RoundToInt(e.weight * 1000f);
                        h = (h * 31) ^ Mathf.RoundToInt(e.minUniformScale * 1000f);
                        h = (h * 31) ^ Mathf.RoundToInt(e.maxUniformScale * 1000f);
                        h = (h * 31) ^ e.alignToNormal.GetHashCode();
                        h = (h * 31) ^ e.randomYaw.GetHashCode();
                        h = (h * 31) ^ Mathf.RoundToInt(e.yOffset * 1000f);
                    }
                }

                return h;
            }
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

        private static uint Hash32(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352d;
                x ^= x >> 15;
                x *= 0x846ca68b;
                x ^= x >> 16;
                return x;
            }
        }

        private static float Hash01(uint h)
        {
            return (h & 0x00FFFFFFu) / 16777216.0f;
        }
    }
}




