using UnityEngine;
using InfinityTerrain.Settings;
using InfinityTerrain.Utilities;

namespace InfinityTerrain.Core
{
    /// <summary>
    /// Handles player safety checks: teleporting above terrain, void protection.
    /// </summary>
    public class PlayerSafety
    {
        private readonly PlayerSettings playerSettings;
        private readonly TerrainSettings terrainSettings;
        private readonly MaterialSettings materialSettings;
        private readonly Transform player;

        public PlayerSafety(
            PlayerSettings playerSettings,
            TerrainSettings terrainSettings,
            MaterialSettings materialSettings,
            Transform player)
        {
            this.playerSettings = playerSettings;
            this.terrainSettings = terrainSettings;
            this.materialSettings = materialSettings;
            this.player = player;
        }

        /// <summary>
        /// Update player safety checks: teleport if below terrain or in void.
        /// </summary>
        public void UpdatePlayerSafety(long playerChunkX, long playerChunkY, float inChunkX, float inChunkZ)
        {
            if (!playerSettings.enableSafety || player == null) return;

            // Get actual terrain height; raycast ignores player's own colliders
            float actualTerrainHeight;
            if (!TryGetTerrainHeightRaycast(player.position, out actualTerrainHeight))
            {
                // Fallback to approximated height if raycast doesn't hit
                actualTerrainHeight = NoiseGenerator.GetTerrainHeightCPU(
                    playerChunkX, playerChunkY, inChunkX, inChunkZ,
                    terrainSettings.resolution, terrainSettings.chunkSize,
                    terrainSettings.heightMultiplier, terrainSettings.noiseScale,
                    terrainSettings.mountainStrength, terrainSettings.seed);
            }

            // Get player velocity to check if jumping
            Rigidbody rb = player.GetComponent<Rigidbody>();
            bool isJumping = rb != null && rb.linearVelocity.y > 2.0f;

            // Teleport if player is below terrain, but don't fight an active jump
            float safetyThreshold = actualTerrainHeight - Mathf.Max(0.1f, playerSettings.recoverBelowTerrain);

            bool isHardVoid = player.position.y < playerSettings.hardVoidY;
            if ((isHardVoid || !isJumping) && player.position.y < safetyThreshold)
            {
                // Player is below terrain and not jumping - teleport to safety
                Vector3 newPos = player.position;
                newPos.y = actualTerrainHeight + playerSettings.safetyHeightOffset + 5.0f;
                player.position = newPos;

                if (rb != null) rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0, rb.linearVelocity.z);
            }
        }

        /// <summary>
        /// Place player above terrain at current XZ position.
        /// </summary>
        public void PlacePlayerAboveTerrainAtCurrentXZ(float heightOffset, long playerChunkX, long playerChunkY, float inChunkX, float inChunkZ)
        {
            if (player == null) return;

            // Prefer raycast (actual collider), fallback to CPU approximation
            float terrainY;
            if (!TryGetTerrainHeightRaycast(player.position, out terrainY))
            {
                terrainY = NoiseGenerator.GetTerrainHeightCPU(
                    playerChunkX, playerChunkY, inChunkX, inChunkZ,
                    terrainSettings.resolution, terrainSettings.chunkSize,
                    terrainSettings.heightMultiplier, terrainSettings.noiseScale,
                    terrainSettings.mountainStrength, terrainSettings.seed);
            }

            Vector3 newPos = player.position;
            newPos.y = terrainY + Mathf.Max(0f, heightOffset);
            player.position = newPos;

            Rigidbody rb = player.GetComponent<Rigidbody>();
            if (rb != null) rb.linearVelocity = Vector3.zero;
        }

        private bool TryGetTerrainHeightRaycast(Vector3 positionXZ, out float terrainY)
        {
            // Raycast can easily hit the player's own collider if we cast from above at the same XZ.
            // So we raycast ALL and choose the closest hit that is NOT part of the player hierarchy.
            float startHeight = Mathf.Max(positionXZ.y + 500f, 1000f);
            Vector3 start = new Vector3(positionXZ.x, startHeight, positionXZ.z);

            RaycastHit[] hits = Physics.RaycastAll(start, Vector3.down, 200000f, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0)
            {
                terrainY = 0f;
                return false;
            }

            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            Transform playerRoot = player != null ? player : null;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null) continue;

                // Ignore player's own colliders (including children)
                if (playerRoot != null && c.transform.IsChildOf(playerRoot)) continue;

                terrainY = hits[i].point.y;
                return true;
            }

            terrainY = 0f;
            return false;
        }
    }
}

