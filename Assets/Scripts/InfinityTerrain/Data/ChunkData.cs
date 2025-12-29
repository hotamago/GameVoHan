using UnityEngine;

namespace InfinityTerrain.Data
{
    /// <summary>
    /// Runtime data for a loaded terrain chunk.
    /// </summary>
    public class ChunkData
    {
        public GameObject gameObject;
        public bool isReady;
        public int lodResolution;
        public bool isSuperChunk;
        public int superScale;
        public long noiseChunkX;
        public long noiseChunkY;
        public int baseVertsPerChunk;
        public float chunkSizeWorld;

        /// <summary>
        /// Optional cached heightmap (0..1) for this chunk, primarily for vegetation sampling.
        /// Note: Stored as [y,x] matching Unity TerrainData.SetHeights convention.
        /// </summary>
        public float[,] heights01;
    }

    /// <summary>
    /// Desired chunk configuration for chunk management system.
    /// </summary>
    public struct DesiredChunk
    {
        public string key;
        public bool isSuper;
        public int superScale;
        public long noiseChunkX;
        public long noiseChunkY;
        public long minBaseChunkX;
        public long minBaseChunkY;
        public int lodResolution;
        public float chunkSizeWorld;
        public int baseVertsPerChunk;
        public bool wantCollider;
    }
}

