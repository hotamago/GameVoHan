using System.Collections.Generic;
using InfinityTerrain.Data;
using InfinityTerrain.Settings;
using InfinityTerrain.Utilities;
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
        private readonly Material _terrainMaterialTemplate;

        public TerrainChunkManagerBuiltIn(
            TerrainSettings terrainSettings,
            TerrainGenerator terrainGenerator,
            WorldOriginManager worldOriginManager,
            Transform parentTransform,
            float heightmapPixelError = 5f,
            float basemapDistance = 1000f,
            bool drawInstanced = true,
            int groupingID = 0,
            int terrainLayer = 0,
            TerrainLayer defaultTerrainLayer = null,
            Material terrainMaterialTemplate = null)
        {
            _terrainSettings = terrainSettings;
            _terrainGenerator = terrainGenerator;
            _worldOriginManager = worldOriginManager;
            _parentTransform = parentTransform;
            _heightmapPixelError = Mathf.Max(1f, heightmapPixelError);
            _basemapDistance = Mathf.Max(0f, basemapDistance);
            _drawInstanced = drawInstanced;
            _groupingID = groupingID;
            _terrainLayer = terrainLayer;
            _defaultTerrainLayer = defaultTerrainLayer;
            _terrainMaterialTemplate = terrainMaterialTemplate;
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
                    RegenerateChunk(existing, d);
                }
                else
                {
                    // Still ensure position is correct after floating-origin shifts/teleports
                    UpdateChunkLocalPosition(existing.gameObject, d.minBaseChunkX, d.minBaseChunkY);
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
            RegenerateChunk(data, d);
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
            if (_defaultTerrainLayer == null) return;
            td.terrainLayers = new[] { _defaultTerrainLayer };
        }

        private void RegenerateChunk(ChunkData data, DesiredChunk d)
        {
            if (data == null || data.gameObject == null) return;

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

            // Generate heights (0..1)
            float[,] heights01 = _terrainGenerator.GenerateHeightmap01GPU(
                d.noiseChunkX, d.noiseChunkY, d.lodResolution, d.chunkSizeWorld, d.baseVertsPerChunk);

            if (heights01 == null)
            {
                data.isReady = false;
                return;
            }

            // Apply to TerrainData (compatible across Unity versions)
            // NOTE: Some Unity versions support SetHeightsDelayLOD, but ApplyDelayedHeightmapModification
            // is not available everywhere. Use SetHeights for maximum compatibility.
            td.SetHeights(0, 0, heights01);

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
                ReturnToPool(data);
            }
        }

        public void ClearAllChunks()
        {
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
    }
}


