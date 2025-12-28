using UnityEngine;
using System.Collections.Generic;
using InfinityTerrain.Data;

namespace InfinityTerrain.Utilities
{
    /// <summary>
    /// Manages floating origin system to prevent float precision issues at large distances.
    /// </summary>
    public class WorldOriginManager
    {
        private long worldChunkOriginX = 0;
        private long worldChunkOriginY = 0;
        private readonly float floatingOriginThreshold;
        private readonly int chunkSize;

        public long WorldChunkOriginX => worldChunkOriginX;
        public long WorldChunkOriginY => worldChunkOriginY;

        public WorldOriginManager(float floatingOriginThreshold, int chunkSize)
        {
            this.floatingOriginThreshold = floatingOriginThreshold;
            this.chunkSize = chunkSize;
        }

        /// <summary>
        /// Check if world origin should be shifted and perform the shift if needed.
        /// </summary>
        public bool TryShiftWorldOrigin(Transform player, Dictionary<string, ChunkData> loadedChunks, Dictionary<string, GameObject> loadedWaterTiles)
        {
            if (Mathf.Abs(player.position.x) > floatingOriginThreshold || Mathf.Abs(player.position.z) > floatingOriginThreshold)
            {
                ShiftWorldOrigin(player, loadedChunks, loadedWaterTiles);
                return true;
            }
            return false;
        }

        private void ShiftWorldOrigin(Transform player, Dictionary<string, ChunkData> loadedChunks, Dictionary<string, GameObject> loadedWaterTiles)
        {
            // Shift by whole chunks so origin stays chunk-aligned
            int dxChunks = Mathf.FloorToInt(player.position.x / chunkSize);
            int dzChunks = Mathf.FloorToInt(player.position.z / chunkSize);
            if (dxChunks == 0 && dzChunks == 0) return;

            Vector3 shift = new Vector3(dxChunks * chunkSize, 0, dzChunks * chunkSize);

            // Shift Player
            player.position -= shift;

            // Shift Chunks
            foreach (var kvp in loadedChunks)
            {
                if (kvp.Value.gameObject != null)
                    kvp.Value.gameObject.transform.position -= shift;
            }

            // Shift Water Tiles
            foreach (var kvp in loadedWaterTiles)
            {
                if (kvp.Value != null)
                    kvp.Value.transform.position -= shift;
            }

            // Update absolute chunk origin
            worldChunkOriginX += dxChunks;
            worldChunkOriginY += dzChunks;
        }

        /// <summary>
        /// Calculate absolute chunk coordinates from local Unity position.
        /// </summary>
        public void GetAbsoluteChunkCoords(Vector3 localPosition, out long chunkX, out long chunkY)
        {
            int localChunkX = Mathf.FloorToInt(localPosition.x / chunkSize);
            int localChunkY = Mathf.FloorToInt(localPosition.z / chunkSize);
            chunkX = worldChunkOriginX + localChunkX;
            chunkY = worldChunkOriginY + localChunkY;
        }

        /// <summary>
        /// Get in-chunk local coordinates (0 to chunkSize).
        /// </summary>
        public void GetInChunkCoords(Vector3 localPosition, out float inChunkX, out float inChunkZ)
        {
            int localChunkX = Mathf.FloorToInt(localPosition.x / chunkSize);
            int localChunkY = Mathf.FloorToInt(localPosition.z / chunkSize);
            inChunkX = localPosition.x - (localChunkX * chunkSize);
            inChunkZ = localPosition.z - (localChunkY * chunkSize);
        }

        /// <summary>
        /// Teleport world origin to align with target chunk coordinates.
        /// </summary>
        public void TeleportToChunk(long targetChunkX, long targetChunkY, Vector3 playerPosition)
        {
            int localChunkX = Mathf.FloorToInt(playerPosition.x / chunkSize);
            int localChunkY = Mathf.FloorToInt(playerPosition.z / chunkSize);
            worldChunkOriginX = targetChunkX - localChunkX;
            worldChunkOriginY = targetChunkY - localChunkY;
        }
    }
}

