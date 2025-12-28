using UnityEngine;
using System.Collections.Generic;
using InfinityTerrain.Data;
using InfinityTerrain.Settings;
using InfinityTerrain.Utilities;

namespace InfinityTerrain.Core
{
    /// <summary>
    /// Manages terrain chunk lifecycle: creation, updates, and unloading.
    /// </summary>
    public class ChunkManager
    {
        private readonly TerrainSettings terrainSettings;
        private readonly TerrainGenerator terrainGenerator;
        private readonly WorldOriginManager worldOriginManager;
        private readonly Dictionary<string, ChunkData> loadedChunks = new Dictionary<string, ChunkData>();
        private readonly Transform parentTransform;

        public Dictionary<string, ChunkData> LoadedChunks => loadedChunks;

        public ChunkManager(
            TerrainSettings terrainSettings,
            TerrainGenerator terrainGenerator,
            WorldOriginManager worldOriginManager,
            Transform parentTransform)
        {
            this.terrainSettings = terrainSettings;
            this.terrainGenerator = terrainGenerator;
            this.worldOriginManager = worldOriginManager;
            this.parentTransform = parentTransform;
        }

        /// <summary>
        /// Update chunks around the center chunk position.
        /// </summary>
        public void UpdateChunks(long centerChunkX, long centerChunkY)
        {
            // Build desired chunks (base + optional superchunks)
            Dictionary<string, DesiredChunk> desired = new Dictionary<string, DesiredChunk>(256);

            int scale = Mathf.Max(2, terrainSettings.superChunkScale);
            int baseVertsBase = Mathf.Max(1, terrainSettings.resolution - 1);

            long nearMinX = centerChunkX - terrainSettings.superChunkStartRadius;
            long nearMaxX = centerChunkX + terrainSettings.superChunkStartRadius;
            long nearMinY = centerChunkY - terrainSettings.superChunkStartRadius;
            long nearMaxY = centerChunkY + terrainSettings.superChunkStartRadius;

            for (int dx = -terrainSettings.renderDistance; dx <= terrainSettings.renderDistance; dx++)
            {
                for (int dy = -terrainSettings.renderDistance; dy <= terrainSettings.renderDistance; dy++)
                {
                    long cx = centerChunkX + dx;
                    long cy = centerChunkY + dy;
                    int r = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));

                    bool trySuper = terrainSettings.enableFarSuperChunks && r > terrainSettings.superChunkStartRadius;
                    if (trySuper)
                    {
                        long sx = ComputeShaderHelper.FloorDiv(cx, scale);
                        long sy = ComputeShaderHelper.FloorDiv(cy, scale);
                        long minBaseX = sx * scale;
                        long minBaseY = sy * scale;
                        long maxBaseX = minBaseX + (scale - 1);
                        long maxBaseY = minBaseY + (scale - 1);

                        // If this superchunk touches the near square, fall back to base chunks
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
                                DesiredChunk d = new DesiredChunk
                                {
                                    key = skey,
                                    isSuper = true,
                                    superScale = scale,
                                    noiseChunkX = sx,
                                    noiseChunkY = sy,
                                    minBaseChunkX = minBaseX,
                                    minBaseChunkY = minBaseY,
                                    lodResolution = lodRes,
                                    chunkSizeWorld = terrainSettings.chunkSize * scale,
                                    baseVertsPerChunk = baseVertsBase * scale,
                                    wantCollider = terrainSettings.superChunksHaveCollider
                                };
                                desired[skey] = d;
                            }

                            continue;
                        }
                    }

                    // Base chunk
                    string key = $"{cx}_{cy}";
                    if (!desired.ContainsKey(key))
                    {
                        int lodRes = GetLodResolutionForChunkDelta(dx, dy);
                        DesiredChunk d = new DesiredChunk
                        {
                            key = key,
                            isSuper = false,
                            superScale = 1,
                            noiseChunkX = cx,
                            noiseChunkY = cy,
                            minBaseChunkX = cx,
                            minBaseChunkY = cy,
                            lodResolution = lodRes,
                            chunkSizeWorld = terrainSettings.chunkSize,
                            baseVertsPerChunk = baseVertsBase,
                            wantCollider = true
                        };
                        desired[key] = d;
                    }
                }
            }

            // Unload unused
            List<string> toRemove = new List<string>();
            foreach (var key in loadedChunks.Keys)
            {
                if (!desired.ContainsKey(key)) toRemove.Add(key);
            }
            foreach (var key in toRemove) UnloadChunk(key);

            // Create/update desired
            foreach (var kvp in desired)
            {
                DesiredChunk d = kvp.Value;
                if (!loadedChunks.TryGetValue(d.key, out ChunkData existing) || existing == null || existing.gameObject == null)
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
                    terrainGenerator.GenerateChunkGPU(
                        d.noiseChunkX, d.noiseChunkY, existing,
                        d.lodResolution, d.chunkSizeWorld, d.baseVertsPerChunk, d.wantCollider);
                }
            }
        }

        private int GetLodResolutionForChunkDelta(int dx, int dy)
        {
            return ChunkLodUtility.GetPow2Plus1LodResolutionForChunkDelta(
                dx, dy, terrainSettings.enableLod, terrainSettings.resolution,
                terrainSettings.lodChunkRadii, terrainSettings.lodResolutions, 9);
        }

        private void CreateChunk(DesiredChunk d)
        {
            // Compute shader validation is handled by TerrainGenerator

            GameObject chunkObj = new GameObject(d.isSuper
                ? $"SuperChunk_{d.superScale}_{d.noiseChunkX}_{d.noiseChunkY}"
                : $"Chunk_{d.noiseChunkX}_{d.noiseChunkY}");

            // Local position based on relative chunk delta from chunk-origin
            long relX = d.minBaseChunkX - worldOriginManager.WorldChunkOriginX;
            long relY = d.minBaseChunkY - worldOriginManager.WorldChunkOriginY;
            Vector3 localPos = new Vector3(relX * terrainSettings.chunkSize, 0, relY * terrainSettings.chunkSize);

            chunkObj.transform.position = localPos;
            chunkObj.transform.parent = parentTransform;
            chunkObj.layer = LayerMask.NameToLayer("Default");

            ChunkData data = new ChunkData
            {
                gameObject = chunkObj,
                isReady = false,
                lodResolution = 0,
                isSuperChunk = d.isSuper,
                superScale = d.superScale,
                noiseChunkX = d.noiseChunkX,
                noiseChunkY = d.noiseChunkY,
                baseVertsPerChunk = d.baseVertsPerChunk,
                chunkSizeWorld = d.chunkSizeWorld
            };
            loadedChunks[d.key] = data;

            terrainGenerator.GenerateChunkGPU(
                d.noiseChunkX, d.noiseChunkY, data,
                d.lodResolution, d.chunkSizeWorld, d.baseVertsPerChunk, d.wantCollider);
        }

        private void UnloadChunk(string key)
        {
            if (loadedChunks.TryGetValue(key, out ChunkData data))
            {
                if (data.gameObject != null) Object.Destroy(data.gameObject);
                loadedChunks.Remove(key);
            }
        }

        /// <summary>
        /// Clear all loaded chunks.
        /// </summary>
        public void ClearAllChunks()
        {
            foreach (var kvp in loadedChunks)
            {
                if (kvp.Value.gameObject != null) Object.Destroy(kvp.Value.gameObject);
            }
            loadedChunks.Clear();
        }
    }
}

