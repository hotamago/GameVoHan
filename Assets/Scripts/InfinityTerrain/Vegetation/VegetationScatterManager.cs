using System.Collections.Generic;
using InfinityTerrain.Data;
using InfinityTerrain.Settings;
using InfinityTerrain.Utilities;
using UnityEngine;

namespace InfinityTerrain.Vegetation
{
    /// <summary>
    /// Keeps vegetation spawned only on nearby high-detail base chunks.
    /// </summary>
    public class VegetationScatterManager
    {
        private readonly TerrainSettings _terrainSettings;
        private readonly MaterialSettings _materialSettings;
        private readonly VegetationScatterSettings _settings;
        private readonly int _globalSeed;

        private readonly int _renderDistanceChunks;
        private readonly bool _onlyMaxLod;

        public VegetationScatterManager(
            TerrainSettings terrainSettings,
            MaterialSettings materialSettings,
            VegetationScatterSettings settings,
            int globalSeed,
            int renderDistanceChunks,
            bool onlyMaxLod)
        {
            _terrainSettings = terrainSettings;
            _materialSettings = materialSettings;
            _settings = settings;
            _globalSeed = globalSeed;
            _renderDistanceChunks = Mathf.Max(0, renderDistanceChunks);
            _onlyMaxLod = onlyMaxLod;
        }

        public void Update(long centerChunkX, long centerChunkY, Dictionary<string, ChunkData> loadedChunks, float heightMultiplier, float waterSurfaceY)
        {
            if (_settings == null) return;
            if (loadedChunks == null) return;

            foreach (var kvp in loadedChunks)
            {
                ChunkData d = kvp.Value;
                if (d == null || d.gameObject == null) continue;
                if (!d.isReady) continue;
                if (d.isSuperChunk) continue; // skip superchunks

                int dx = ComputeShaderHelper.ClampLongToInt(d.noiseChunkX - centerChunkX);
                int dy = ComputeShaderHelper.ClampLongToInt(d.noiseChunkY - centerChunkY);
                int r = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));

                bool inRange = r <= _renderDistanceChunks;
                bool lodOk = !_onlyMaxLod || d.lodResolution == _terrainSettings.resolution;

                ChunkVegetationScatter scatter = d.gameObject.GetComponent<ChunkVegetationScatter>();

                if (!inRange || !lodOk)
                {
                    if (scatter != null) scatter.Clear();
                    continue;
                }

                if (scatter == null) scatter = d.gameObject.AddComponent<ChunkVegetationScatter>();

                scatter.settings = _settings;
                scatter.globalSeed = _globalSeed;
                scatter.heightMultiplier = Mathf.Max(0.0001f, heightMultiplier);
                scatter.waterSurfaceY = waterSurfaceY;
                scatter.EnsureGenerated(d.noiseChunkX, d.noiseChunkY, d.lodResolution);
            }
        }
    }
}


