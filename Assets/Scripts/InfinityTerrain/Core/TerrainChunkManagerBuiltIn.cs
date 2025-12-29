using System.Collections.Generic;
using InfinityTerrain.Data;
using InfinityTerrain.Settings;
using InfinityTerrain.Utilities;
using Unity.Collections;
using UnityEngine;

namespace InfinityTerrain.Core
{
    /// <summary>
    /// Chunk manager that uses Unity built-in Terrain (Terrain + TerrainCollider) instead of MeshRenderer.
    /// This enables Unity Terrain features (trees/details/terrain tools) while keeping infinite streaming.
    /// </summary>
    public class TerrainChunkManagerBuiltIn
    {
        private readonly TerrainSettings _terrainSettings;
        private readonly MaterialSettings _materialSettings;
        private readonly TerrainGenerator _terrainGenerator;
        private readonly WorldOriginManager _worldOriginManager;
        private readonly Transform _parentTransform;

        private readonly Dictionary<string, ChunkData> _loadedChunks = new Dictionary<string, ChunkData>();
        public Dictionary<string, ChunkData> LoadedChunks => _loadedChunks;

        // Pool by heightmap resolution (TerrainData.heightmapResolution)
        private readonly Dictionary<int, Stack<ChunkData>> _poolByResolution = new Dictionary<int, Stack<ChunkData>>();

        // Terrain tuning (perf/quality)
        private readonly float _heightmapPixelError;
        private readonly float _basemapDistance;
        private readonly bool _drawInstanced;
        private readonly int _groupingID;
        private readonly int _terrainLayer;
        private readonly TerrainLayer _defaultTerrainLayer;
        private readonly TerrainLayer[] _terrainLayersOverride;
        private readonly Material _terrainMaterialTemplate;

        // Runtime splat (alphamap) generation
        private readonly bool _enableAutoSplatmap;
        private readonly int _splatmapResolution;
        private readonly float _slopeRockStartDeg;
        private readonly float _slopeRockEndDeg;
        private readonly float _blendNoiseCellSize;
        private readonly float _blendNoiseStrength;

        // --- Async heightmap generation (prevents main-thread stalls) ---
        private class PendingHeightmap
        {
            public ChunkData chunk;
            public DesiredChunk desired;
            public Terrain terrain;
            public TerrainCollider collider;
            public TerrainData terrainData;
            public TerrainGenerator.Heightmap01AsyncTask task;
        }

        private readonly Dictionary<string, PendingHeightmap> _pendingByKey = new Dictionary<string, PendingHeightmap>(128);
        private readonly Dictionary<string, DesiredChunk> _queuedRegens = new Dictionary<string, DesiredChunk>(256);

        /// <summary>
        /// Limits how many GPU async readbacks can be in-flight at once (keeps spikes under control).
        /// </summary>
        public int maxAsyncHeightmapsInFlight = 2;

        /// <summary>
        /// Limits how many new async heightmap tasks can start per frame.
        /// </summary>
        public int startAsyncHeightmapsPerFrame = 1;

        /// <summary>
        /// Limits how many finished async heightmaps are applied to TerrainData per frame.
        /// </summary>
        public int applyAsyncHeightmapsPerFrame = 1;

        /// <summary>
        /// If false, heightmaps are generated synchronously (legacy behavior, can stutter).
        /// </summary>
        public bool enableAsyncHeightmapStreaming = true;

        public TerrainChunkManagerBuiltIn(
            TerrainSettings terrainSettings,
            MaterialSettings materialSettings,
            TerrainGenerator terrainGenerator,
            WorldOriginManager worldOriginManager,
            Transform parentTransform,
            float heightmapPixelError = 5f,
            float basemapDistance = 1000f,
            bool drawInstanced = true,
            int groupingID = 0,
            int terrainLayer = 0,
            TerrainLayer defaultTerrainLayer = null,
            TerrainLayer[] terrainLayersOverride = null,
            Material terrainMaterialTemplate = null,
            bool enableAutoSplatmap = false,
            int splatmapResolution = 128,
            float slopeRockStartDeg = 30f,
            float slopeRockEndDeg = 45f,
            float blendNoiseCellSize = 25f,
            float blendNoiseStrength = 0.12f)
        {
            _terrainSettings = terrainSettings;
            _materialSettings = materialSettings;
            _terrainGenerator = terrainGenerator;
            _worldOriginManager = worldOriginManager;
            _parentTransform = parentTransform;
            _heightmapPixelError = Mathf.Max(1f, heightmapPixelError);
            _basemapDistance = Mathf.Max(0f, basemapDistance);
            _drawInstanced = drawInstanced;
            _groupingID = groupingID;
            _terrainLayer = terrainLayer;
            _defaultTerrainLayer = defaultTerrainLayer;
            _terrainLayersOverride = terrainLayersOverride;
            _terrainMaterialTemplate = terrainMaterialTemplate;

            _enableAutoSplatmap = enableAutoSplatmap;
            _splatmapResolution = Mathf.Clamp(splatmapResolution, 16, 1024);
            _slopeRockStartDeg = Mathf.Clamp(slopeRockStartDeg, 0f, 89f);
            _slopeRockEndDeg = Mathf.Clamp(slopeRockEndDeg, 0f, 89f);
            if (_slopeRockEndDeg < _slopeRockStartDeg) _slopeRockEndDeg = _slopeRockStartDeg;
            _blendNoiseCellSize = Mathf.Max(1f, blendNoiseCellSize);
            _blendNoiseStrength = Mathf.Clamp01(blendNoiseStrength);
        }

        public void UpdateChunks(long centerChunkX, long centerChunkY)
        {
            Dictionary<string, DesiredChunk> desired = new Dictionary<string, DesiredChunk>(256);

            int scale = Mathf.Max(2, _terrainSettings.superChunkScale);
            int baseVertsBase = Mathf.Max(1, _terrainSettings.resolution - 1);

            long nearMinX = centerChunkX - _terrainSettings.superChunkStartRadius;
            long nearMaxX = centerChunkX + _terrainSettings.superChunkStartRadius;
            long nearMinY = centerChunkY - _terrainSettings.superChunkStartRadius;
            long nearMaxY = centerChunkY + _terrainSettings.superChunkStartRadius;

            for (int dx = -_terrainSettings.renderDistance; dx <= _terrainSettings.renderDistance; dx++)
            {
                for (int dy = -_terrainSettings.renderDistance; dy <= _terrainSettings.renderDistance; dy++)
                {
                    long cx = centerChunkX + dx;
                    long cy = centerChunkY + dy;
                    int r = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));

                    bool trySuper = _terrainSettings.enableFarSuperChunks && r > _terrainSettings.superChunkStartRadius;
                    if (trySuper)
                    {
                        long sx = ComputeShaderHelper.FloorDiv(cx, scale);
                        long sy = ComputeShaderHelper.FloorDiv(cy, scale);
                        long minBaseX = sx * scale;
                        long minBaseY = sy * scale;
                        long maxBaseX = minBaseX + (scale - 1);
                        long maxBaseY = minBaseY + (scale - 1);

                        bool intersectsNear =
                            !(maxBaseX < nearMinX || minBaseX > nearMaxX || maxBaseY < nearMinY || minBaseY > nearMaxY);

                        if (!intersectsNear)
                        {
                            string skey = $"S_{scale}_{sx}_{sy}";
                            if (!desired.ContainsKey(skey))
                            {
                                long centerX = minBaseX + (scale / 2);
                                long centerY2 = minBaseY + (scale / 2);
                                int sdx = ComputeShaderHelper.ClampLongToInt(centerX - centerChunkX);
                                int sdy = ComputeShaderHelper.ClampLongToInt(centerY2 - centerChunkY);

                                int lodRes = GetLodResolutionForChunkDelta(sdx, sdy);
                                desired[skey] = new DesiredChunk
                                {
                                    key = skey,
                                    isSuper = true,
                                    superScale = scale,
                                    noiseChunkX = sx,
                                    noiseChunkY = sy,
                                    minBaseChunkX = minBaseX,
                                    minBaseChunkY = minBaseY,
                                    lodResolution = lodRes,
                                    chunkSizeWorld = _terrainSettings.chunkSize * scale,
                                    baseVertsPerChunk = baseVertsBase * scale,
                                    wantCollider = _terrainSettings.superChunksHaveCollider
                                };
                            }

                            continue;
                        }
                    }

                    string key = $"{cx}_{cy}";
                    if (!desired.ContainsKey(key))
                    {
                        int lodRes = GetLodResolutionForChunkDelta(dx, dy);
                        desired[key] = new DesiredChunk
                        {
                            key = key,
                            isSuper = false,
                            superScale = 1,
                            noiseChunkX = cx,
                            noiseChunkY = cy,
                            minBaseChunkX = cx,
                            minBaseChunkY = cy,
                            lodResolution = lodRes,
                            chunkSizeWorld = _terrainSettings.chunkSize,
                            baseVertsPerChunk = baseVertsBase,
                            wantCollider = true
                        };
                    }
                }
            }

            // Unload unused
            List<string> toRemove = new List<string>();
            foreach (var key in _loadedChunks.Keys)
            {
                if (!desired.ContainsKey(key)) toRemove.Add(key);
            }
            foreach (var key in toRemove) UnloadChunk(key);

            // Create/update desired
            foreach (var kvp in desired)
            {
                DesiredChunk d = kvp.Value;
                if (!_loadedChunks.TryGetValue(d.key, out ChunkData existing) || existing == null || existing.gameObject == null)
                {
                    CreateChunk(d);
                    continue;
                }

                bool needsRegen =
                    existing.lodResolution != d.lodResolution ||
                    existing.isSuperChunk != d.isSuper ||
                    existing.superScale != d.superScale ||
                    existing.noiseChunkX != d.noiseChunkX ||
                    existing.noiseChunkY != d.noiseChunkY ||
                    existing.baseVertsPerChunk != d.baseVertsPerChunk ||
                    Mathf.Abs(existing.chunkSizeWorld - d.chunkSizeWorld) > 0.0001f;

                if (needsRegen)
                {
                    QueueRegenerateChunk(existing, d);
                }
                else
                {
                    // Still ensure position is correct after floating-origin shifts/teleports
                    UpdateChunkLocalPosition(existing.gameObject, d.minBaseChunkX, d.minBaseChunkY);
                }
            }
        }

        /// <summary>
        /// Must be called every frame by the owner (e.g., InfinityRenderTerrainChunksBuiltIn).
        /// Starts a limited number of async heightmap tasks and applies completed ones.
        /// </summary>
        public void TickAsyncHeightmaps()
        {
            // Clamp configuration defensively
            int maxInFlight = Mathf.Clamp(maxAsyncHeightmapsInFlight, 0, 64);
            int startBudget = Mathf.Clamp(startAsyncHeightmapsPerFrame, 0, 64);
            int applyBudget = Mathf.Clamp(applyAsyncHeightmapsPerFrame, 0, 64);

            // Apply completed first (frees in-flight slots ASAP)
            if (applyBudget > 0 && _pendingByKey.Count > 0)
            {
                var keys = new List<string>(_pendingByKey.Count);
                foreach (var kvp in _pendingByKey) keys.Add(kvp.Key);

                int applied = 0;
                for (int i = 0; i < keys.Count && applied < applyBudget; i++)
                {
                    string key = keys[i];
                    if (!_pendingByKey.TryGetValue(key, out PendingHeightmap p) || p == null) continue;
                    if (p.task == null) { _pendingByKey.Remove(key); continue; }
                    if (!p.task.IsDone) continue;

                    bool ok = TryApplyCompletedHeightmap(p);
                    p.task.Dispose();
                    p.task = null;
                    _pendingByKey.Remove(key);

                    if (!ok)
                    {
                        // Re-queue the desired regen to retry later
                        _queuedRegens[key] = p.desired;
                    }
                    applied++;
                }
            }

            // Start new tasks within budget & in-flight cap
            if (startBudget <= 0 || maxInFlight <= 0) return;
            int startedCount = 0;

            if (_queuedRegens.Count > 0 && _pendingByKey.Count < maxInFlight)
            {
                // We iterate a snapshot to allow removals while starting
                var keys = new List<string>(_queuedRegens.Count);
                foreach (var kvp in _queuedRegens) keys.Add(kvp.Key);

                for (int i = 0; i < keys.Count && startedCount < startBudget && _pendingByKey.Count < maxInFlight; i++)
                {
                    string key = keys[i];
                    if (!_queuedRegens.TryGetValue(key, out DesiredChunk d)) continue;
                    if (!_loadedChunks.TryGetValue(key, out ChunkData chunk) || chunk == null || chunk.gameObject == null)
                    {
                        _queuedRegens.Remove(key);
                        continue;
                    }
                    if (_pendingByKey.ContainsKey(key))
                    {
                        // Already running; keep latest desired queued so it can regen again if needed
                        continue;
                    }

                    bool started = StartAsyncHeightmap(chunk, d);
                    _queuedRegens.Remove(key);
                    if (started) startedCount++;
                }
            }
        }

        private int GetLodResolutionForChunkDelta(int dx, int dy)
        {
            return ChunkLodUtility.GetPow2Plus1LodResolutionForChunkDelta(
                dx, dy, _terrainSettings.enableLod, _terrainSettings.resolution,
                _terrainSettings.lodChunkRadii, _terrainSettings.lodResolutions, 9);
        }

        private void CreateChunk(DesiredChunk d)
        {
            ChunkData data = TryGetFromPool(d.lodResolution);
            if (data == null || data.gameObject == null)
            {
                data = new ChunkData
                {
                    gameObject = CreateTerrainGameObject(d),
                };
            }
            else
            {
                data.gameObject.name = d.isSuper
                    ? $"TerrainSuperChunk_{d.superScale}_{d.noiseChunkX}_{d.noiseChunkY}"
                    : $"TerrainChunk_{d.noiseChunkX}_{d.noiseChunkY}";
                data.gameObject.SetActive(true);
            }

            data.isReady = false;
            data.lodResolution = 0;
            data.isSuperChunk = d.isSuper;
            data.superScale = d.superScale;
            data.noiseChunkX = d.noiseChunkX;
            data.noiseChunkY = d.noiseChunkY;
            data.baseVertsPerChunk = d.baseVertsPerChunk;
            data.chunkSizeWorld = d.chunkSizeWorld;

            _loadedChunks[d.key] = data;
            UpdateChunkLocalPosition(data.gameObject, d.minBaseChunkX, d.minBaseChunkY);
            QueueRegenerateChunk(data, d);
        }

        private GameObject CreateTerrainGameObject(DesiredChunk d)
        {
            GameObject go = new GameObject(d.isSuper
                ? $"TerrainSuperChunk_{d.superScale}_{d.noiseChunkX}_{d.noiseChunkY}"
                : $"TerrainChunk_{d.noiseChunkX}_{d.noiseChunkY}");

            go.transform.SetParent(_parentTransform, worldPositionStays: true);
            go.layer = _terrainLayer;

            Terrain terrain = go.AddComponent<Terrain>();
            TerrainCollider tc = go.AddComponent<TerrainCollider>();

            // Create TerrainData now; it may be replaced on regen if resolution changes.
            TerrainData td = new TerrainData();
            td.heightmapResolution = d.lodResolution;
            td.size = new Vector3(d.chunkSizeWorld, Mathf.Max(0.0001f, _terrainSettings.heightMultiplier), d.chunkSizeWorld);
            EnsureTerrainLayers(td);

            terrain.terrainData = td;
            tc.terrainData = td;

            ApplyTerrainRuntimeSettings(terrain);
            return go;
        }

        private void ApplyTerrainRuntimeSettings(Terrain terrain)
        {
            if (terrain == null) return;
            terrain.drawInstanced = _drawInstanced;
            terrain.heightmapPixelError = _heightmapPixelError;
            terrain.basemapDistance = _basemapDistance;
            terrain.groupingID = _groupingID;
            terrain.allowAutoConnect = false; // We are chunk streaming manually
            terrain.drawTreesAndFoliage = true;

            // Ensure runtime-created terrains actually render trees/details at reasonable distances.
            // Defaults can be surprisingly low depending on project settings / pipeline.
            terrain.treeDistance = Mathf.Max(terrain.treeDistance, _terrainSettings.chunkSize * 6f);
            terrain.treeBillboardDistance = Mathf.Max(terrain.treeBillboardDistance, _terrainSettings.chunkSize * 3f);
            terrain.detailObjectDistance = Mathf.Max(terrain.detailObjectDistance, _terrainSettings.chunkSize * 4f);
            terrain.detailObjectDensity = Mathf.Max(terrain.detailObjectDensity, 0.75f);

            // URP/HDRP: providing a custom material template avoids "invisible/transparent" terrain when created at runtime.
            if (_terrainMaterialTemplate != null)
            {
                terrain.materialTemplate = _terrainMaterialTemplate;
            }
        }

        private void EnsureTerrainLayers(TerrainData td)
        {
            if (td == null) return;
            // URP terrain requires TerrainLayers to render properly; runtime TerrainData has none by default.
            if (td.terrainLayers != null && td.terrainLayers.Length > 0) return;

            if (_terrainLayersOverride != null && _terrainLayersOverride.Length > 0)
            {
                td.terrainLayers = _terrainLayersOverride;
                return;
            }

            if (_defaultTerrainLayer == null) return;
            td.terrainLayers = new[] { _defaultTerrainLayer };
        }

        private void QueueRegenerateChunk(ChunkData data, DesiredChunk d)
        {
            if (data == null || data.gameObject == null) return;

            if (!enableAsyncHeightmapStreaming)
            {
                // Legacy synchronous path (kept for debugging / compatibility)
                RegenerateChunkBlocking(data, d);
                return;
            }

            // Cancel any pending task for this key by overwriting the queued desired (latest wins)
            _queuedRegens[d.key] = d;

            // Clear cached heights immediately if chunk coords changed, so vegetation doesn't use wrong data.
            if (data.noiseChunkX != d.noiseChunkX || data.noiseChunkY != d.noiseChunkY || data.lodResolution != d.lodResolution)
            {
                data.heights01 = null;
                data.isReady = false;
            }
        }

        private void RegenerateChunkBlocking(ChunkData data, DesiredChunk d)
        {
            if (data == null || data.gameObject == null) return;

            // Cancel/cleanup async state for this chunk key
            if (_pendingByKey.TryGetValue(d.key, out PendingHeightmap p) && p != null && p.task != null)
            {
                p.task.Dispose();
            }
            _pendingByKey.Remove(d.key);
            _queuedRegens.Remove(d.key);

            Terrain terrain = data.gameObject.GetComponent<Terrain>();
            TerrainCollider tc = data.gameObject.GetComponent<TerrainCollider>();
            if (terrain == null) terrain = data.gameObject.AddComponent<Terrain>();
            if (tc == null) tc = data.gameObject.AddComponent<TerrainCollider>();

            TerrainData td = terrain.terrainData;
            bool needNewData = (td == null) || td.heightmapResolution != d.lodResolution;
            if (needNewData)
            {
                td = new TerrainData();
                td.heightmapResolution = d.lodResolution;
            }
            td.size = new Vector3(d.chunkSizeWorld, Mathf.Max(0.0001f, _terrainSettings.heightMultiplier), d.chunkSizeWorld);
            EnsureTerrainLayers(td);

            float[,] heights01 = _terrainGenerator.GenerateHeightmap01GPU(
                d.noiseChunkX, d.noiseChunkY, d.lodResolution, d.chunkSizeWorld, d.baseVertsPerChunk);

            if (heights01 == null)
            {
                data.isReady = false;
                data.heights01 = null;
                return;
            }

            td.SetHeights(0, 0, heights01);
            data.heights01 = heights01;

            if (_enableAutoSplatmap && td.terrainLayers != null && td.terrainLayers.Length >= 2 && _materialSettings != null)
            {
                ApplyAutoSplatmap(td, heights01, d.noiseChunkX, d.noiseChunkY, d.chunkSizeWorld);
            }

            terrain.terrainData = td;
            tc.terrainData = td;
            tc.enabled = d.wantCollider;
            ApplyTerrainRuntimeSettings(terrain);
            terrain.Flush();

            data.isReady = true;
            data.lodResolution = d.lodResolution;
            data.isSuperChunk = d.isSuper;
            data.superScale = d.superScale;
            data.noiseChunkX = d.noiseChunkX;
            data.noiseChunkY = d.noiseChunkY;
            data.baseVertsPerChunk = d.baseVertsPerChunk;
            data.chunkSizeWorld = d.chunkSizeWorld;
        }

        private bool StartAsyncHeightmap(ChunkData data, DesiredChunk d)
        {
            if (data == null || data.gameObject == null) return false;

            Terrain terrain = data.gameObject.GetComponent<Terrain>();
            TerrainCollider tc = data.gameObject.GetComponent<TerrainCollider>();
            if (terrain == null) terrain = data.gameObject.AddComponent<Terrain>();
            if (tc == null) tc = data.gameObject.AddComponent<TerrainCollider>();

            // Ensure TerrainData matches LOD resolution & size
            TerrainData td = terrain.terrainData;
            bool needNewData = (td == null) || td.heightmapResolution != d.lodResolution;
            if (needNewData)
            {
                td = new TerrainData();
                td.heightmapResolution = d.lodResolution;
            }
            td.size = new Vector3(d.chunkSizeWorld, Mathf.Max(0.0001f, _terrainSettings.heightMultiplier), d.chunkSizeWorld);
            EnsureTerrainLayers(td);

            // Assign now (so the terrain exists immediately, even if heights arrive next frames)
            terrain.terrainData = td;
            tc.terrainData = td;
            tc.enabled = d.wantCollider;
            ApplyTerrainRuntimeSettings(terrain);

            // Start async task
            TerrainGenerator.Heightmap01AsyncTask task = _terrainGenerator.BeginGenerateHeightmap01GPUAsync(
                d.noiseChunkX, d.noiseChunkY, d.lodResolution, d.chunkSizeWorld, d.baseVertsPerChunk);

            if (task == null)
            {
                data.isReady = false;
                return false;
            }

            data.isReady = false;
            data.heights01 = null;

            _pendingByKey[d.key] = new PendingHeightmap
            {
                chunk = data,
                desired = d,
                terrain = terrain,
                collider = tc,
                terrainData = td,
                task = task
            };

            return true;
        }

        private bool TryApplyCompletedHeightmap(PendingHeightmap p)
        {
            if (p == null || p.chunk == null || p.chunk.gameObject == null) return false;
            if (p.task == null) return false;
            if (p.task.HasError) return false;

            TerrainData td = p.terrainData;
            Terrain terrain = p.terrain != null ? p.terrain : p.chunk.gameObject.GetComponent<Terrain>();
            TerrainCollider tc = p.collider != null ? p.collider : p.chunk.gameObject.GetComponent<TerrainCollider>();
            if (terrain == null || td == null) return false;

            // Convert pixels -> float[,] and apply
            NativeArray<float> pixels = p.task.GetData();
            float[,] heights01 = TerrainGenerator.ConvertReadbackToHeights01(pixels, p.task.resolution, _terrainSettings.heightMultiplier);
            if (heights01 == null) return false;

            td.SetHeights(0, 0, heights01);

            // Cache heights for vegetation/VFX
            p.chunk.heights01 = heights01;

            // Optional runtime texture splatmap based on height/slope/noise
            if (_enableAutoSplatmap && td.terrainLayers != null && td.terrainLayers.Length >= 2 && _materialSettings != null)
            {
                ApplyAutoSplatmap(td, heights01, p.desired.noiseChunkX, p.desired.noiseChunkY, p.desired.chunkSizeWorld);
            }

            terrain.terrainData = td;
            if (tc != null) tc.terrainData = td;
            if (tc != null) tc.enabled = p.desired.wantCollider;

            ApplyTerrainRuntimeSettings(terrain);
            terrain.Flush();

            p.chunk.isReady = true;
            p.chunk.lodResolution = p.desired.lodResolution;
            p.chunk.isSuperChunk = p.desired.isSuper;
            p.chunk.superScale = p.desired.superScale;
            p.chunk.noiseChunkX = p.desired.noiseChunkX;
            p.chunk.noiseChunkY = p.desired.noiseChunkY;
            p.chunk.baseVertsPerChunk = p.desired.baseVertsPerChunk;
            p.chunk.chunkSizeWorld = p.desired.chunkSizeWorld;

            return true;
        }

        private void UpdateChunkLocalPosition(GameObject chunkObj, long minBaseChunkX, long minBaseChunkY)
        {
            if (chunkObj == null) return;
            long relX = minBaseChunkX - _worldOriginManager.WorldChunkOriginX;
            long relY = minBaseChunkY - _worldOriginManager.WorldChunkOriginY;
            chunkObj.transform.position = new Vector3(relX * _terrainSettings.chunkSize, 0, relY * _terrainSettings.chunkSize);
        }

        private void UnloadChunk(string key)
        {
            if (_loadedChunks.TryGetValue(key, out ChunkData data))
            {
                _loadedChunks.Remove(key);
                if (_pendingByKey.TryGetValue(key, out PendingHeightmap p) && p != null && p.task != null)
                {
                    p.task.Dispose();
                }
                _pendingByKey.Remove(key);
                _queuedRegens.Remove(key);
                ReturnToPool(data);
            }
        }

        public void ClearAllChunks()
        {
            foreach (var kvp in _pendingByKey)
            {
                if (kvp.Value != null && kvp.Value.task != null) kvp.Value.task.Dispose();
            }
            _pendingByKey.Clear();
            _queuedRegens.Clear();
            foreach (var kvp in _loadedChunks)
            {
                ReturnToPool(kvp.Value);
            }
            _loadedChunks.Clear();
        }

        private ChunkData TryGetFromPool(int lodResolution)
        {
            if (lodResolution <= 0) return null;
            if (_poolByResolution.TryGetValue(lodResolution, out Stack<ChunkData> stack) && stack != null && stack.Count > 0)
            {
                return stack.Pop();
            }
            return null;
        }

        private void ReturnToPool(ChunkData data)
        {
            if (data == null || data.gameObject == null) return;

            Terrain t = data.gameObject.GetComponent<Terrain>();
            int res = (t != null && t.terrainData != null) ? t.terrainData.heightmapResolution : data.lodResolution;
            res = Mathf.Max(0, res);

            data.isReady = false;
            data.noiseChunkX = 0;
            data.noiseChunkY = 0;
            data.heights01 = null;

            data.gameObject.SetActive(false);
            data.gameObject.transform.SetParent(_parentTransform, worldPositionStays: true);

            if (res <= 0) res = _terrainSettings.resolution;
            if (!_poolByResolution.TryGetValue(res, out Stack<ChunkData> stack) || stack == null)
            {
                stack = new Stack<ChunkData>();
                _poolByResolution[res] = stack;
            }
            stack.Push(data);
        }

        private void ApplyAutoSplatmap(TerrainData td, float[,] heights01, long chunkX, long chunkY, float chunkSizeWorld)
        {
            int layers = td.terrainLayers.Length;
            int res = _splatmapResolution;
            td.alphamapResolution = res;

            float[,,] alpha = new float[res, res, layers];

            int hmRes = heights01.GetLength(0);
            float invHm = 1f / Mathf.Max(1, hmRes - 1);

            float water = _materialSettings.waterLevel;
            float beach = _materialSettings.beachLevel;
            float grass = _materialSettings.grassLevel;
            float rock = _materialSettings.rockLevel;
            float snow = _materialSettings.snowLevel;

            float slopeStart = _slopeRockStartDeg;
            float slopeEnd = Mathf.Max(slopeStart + 0.001f, _slopeRockEndDeg);

            for (int y = 0; y < res; y++)
            {
                float v = (res <= 1) ? 0f : (float)y / (res - 1);
                for (int x = 0; x < res; x++)
                {
                    float u = (res <= 1) ? 0f : (float)x / (res - 1);

                    // Sample height from heightmap
                    float h01 = SampleHeight01Bilinear(heights01, u, v);

                    // Approximate slope in degrees from heightmap neighborhood
                    float slopeDeg = SampleSlopeDeg(heights01, u, v, _terrainSettings.heightMultiplier, _terrainSettings.chunkSize);

                    // Blend noise (stable across infinity)
                    double wx = (chunkX * (double)chunkSizeWorld) + (u * (double)chunkSizeWorld);
                    double wz = (chunkY * (double)chunkSizeWorld) + (v * (double)chunkSizeWorld);
                    float n = InfinityTerrain.Vegetation.VegetationNoise.ValueNoise2D(wx, wz, _blendNoiseCellSize, _terrainSettings.seed ^ 0x3a12f9d);
                    float hn = Mathf.Clamp01(h01 + (n - 0.5f) * _blendNoiseStrength);

                    float beachW = SmoothBand(hn, water, beach);
                    float grassW = SmoothBand(hn, beach, grass);
                    float rockW = SmoothBand(hn, grass, rock);
                    float snowW = SmoothStep01(hn, rock, snow);

                    // Force rock on steep slopes
                    float slope01 = Mathf.InverseLerp(slopeStart, slopeEnd, slopeDeg);
                    slope01 = Mathf.Clamp01(slope01);
                    rockW = Mathf.Max(rockW, slope01);

                    // Normalize the 4-core weights then map to N layers:
                    // Layer 0..3 = beach/grass/rock/snow by convention; extra layers get 0.
                    float sum = beachW + grassW + rockW + snowW;
                    if (sum <= 1e-6f) sum = 1f;
                    beachW /= sum;
                    grassW /= sum;
                    rockW /= sum;
                    snowW /= sum;

                    for (int l = 0; l < layers; l++)
                    {
                        float w = 0f;
                        if (l == 0) w = beachW;
                        else if (l == 1) w = grassW;
                        else if (l == 2) w = rockW;
                        else if (l == 3) w = snowW;
                        alpha[y, x, l] = w;
                    }
                }
            }

            td.SetAlphamaps(0, 0, alpha);
        }

        private static float SampleHeight01Bilinear(float[,] h, float u, float v)
        {
            int res = h.GetLength(0);
            if (res <= 1) return Mathf.Clamp01(h[0, 0]);

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

        private static float SampleSlopeDeg(float[,] h, float u, float v, float heightMultiplier, float chunkSizeWorld)
        {
            int res = h.GetLength(0);
            if (res <= 2) return 0f;

            float fx = u * (res - 1);
            float fz = v * (res - 1);
            int ix = Mathf.Clamp(Mathf.RoundToInt(fx), 1, res - 2);
            int iz = Mathf.Clamp(Mathf.RoundToInt(fz), 1, res - 2);

            float step = chunkSizeWorld / Mathf.Max(1, res - 1);

            float hL = h[iz, ix - 1] * heightMultiplier;
            float hR = h[iz, ix + 1] * heightMultiplier;
            float hD = h[iz - 1, ix] * heightMultiplier;
            float hU = h[iz + 1, ix] * heightMultiplier;

            float dx = (hR - hL) / (2f * Mathf.Max(0.0001f, step));
            float dz = (hU - hD) / (2f * Mathf.Max(0.0001f, step));

            Vector3 n = Vector3.Normalize(new Vector3(-dx, 1f, -dz));
            return Vector3.Angle(n, Vector3.up);
        }

        private static float SmoothStep01(float x, float a, float b)
        {
            if (Mathf.Abs(a - b) < 1e-6f) return x >= b ? 1f : 0f;
            float t = Mathf.InverseLerp(a, b, x);
            return t * t * (3f - 2f * t);
        }

        private static float SmoothBand(float x, float a, float b)
        {
            // Weight is high between a..b with soft edges.
            float up = SmoothStep01(x, a, b);
            float down = 1f - SmoothStep01(x, b, b + 0.08f);
            return Mathf.Clamp01(up * down);
        }
    }
}


