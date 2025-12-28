using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using InfinityTerrain.Settings;
using InfinityTerrain.Utilities;

namespace InfinityTerrain.Core
{
    /// <summary>
    /// Manages water tile creation, LOD, and lifecycle.
    /// </summary>
    public class WaterManager
    {
        private readonly WaterSettings waterSettings;
        private readonly TerrainSettings terrainSettings;
        private readonly MaterialSettings materialSettings;
        private readonly WorldOriginManager worldOriginManager;
        private readonly Transform parentTransform;

        private readonly Dictionary<string, GameObject> loadedWaterTiles = new Dictionary<string, GameObject>(256);
        private readonly Dictionary<string, int> loadedWaterTileRes = new Dictionary<string, int>(256);
        private readonly Dictionary<long, Mesh> waterMeshCache = new Dictionary<long, Mesh>(16);
        private Material waterMaterialLoaded;

        public WaterManager(
            WaterSettings waterSettings,
            TerrainSettings terrainSettings,
            MaterialSettings materialSettings,
            WorldOriginManager worldOriginManager,
            Transform parentTransform)
        {
            this.waterSettings = waterSettings;
            this.terrainSettings = terrainSettings;
            this.materialSettings = materialSettings;
            this.worldOriginManager = worldOriginManager;
            this.parentTransform = parentTransform;
        }

        /// <summary>
        /// Ensure water material is loaded.
        /// </summary>
        public void EnsureWaterMaterialLoaded()
        {
            if (!waterSettings.enableWaterTiles) return;
            if (waterMaterialLoaded != null) return;

            Material mat = waterSettings.waterMaterialOverride != null
                ? waterSettings.waterMaterialOverride
                : Resources.Load<Material>(waterSettings.waterMaterialResourceName);

            if (mat == null)
            {
                Debug.LogWarning($"Water material not found. Assign a material override, or ensure a Resources material named '{waterSettings.waterMaterialResourceName}' exists.");
                return;
            }

            waterMaterialLoaded = mat;
        }

        /// <summary>
        /// Update water tiles around the center chunk position.
        /// </summary>
        public void UpdateWaterTiles(long centerChunkX, long centerChunkY)
        {
            if (!waterSettings.enableWaterTiles) return;

            EnsureWaterMaterialLoaded();
            if (waterMaterialLoaded == null) return;

            int rd = GetWaterRenderDistance();

            // Desired set (base tiles only; one per chunk)
            HashSet<string> desired = new HashSet<string>();
            for (int dx = -rd; dx <= rd; dx++)
            {
                for (int dy = -rd; dy <= rd; dy++)
                {
                    long cx = centerChunkX + dx;
                    long cy = centerChunkY + dy;
                    string key = $"{cx}_{cy}";
                    desired.Add(key);

                    if (!loadedWaterTiles.ContainsKey(key))
                    {
                        int res = GetWaterTileResolutionForChunkDelta(dx, dy);
                        CreateWaterTile(cx, cy, key, res);
                    }
                    else
                    {
                        var go = loadedWaterTiles[key];
                        if (go != null)
                        {
                            // Ensure Y stays correct
                            Vector3 p = go.transform.position;
                            p.y = GetWaterSurfaceY();
                            go.transform.position = p;

                            // Update LOD mesh if needed
                            int desiredRes = GetWaterTileResolutionForChunkDelta(dx, dy);
                            if (!loadedWaterTileRes.TryGetValue(key, out int currentRes) || currentRes != desiredRes)
                            {
                                MeshFilter mf = go.GetComponent<MeshFilter>();
                                if (mf != null)
                                    mf.sharedMesh = GetOrCreateWaterGridMesh(desiredRes, terrainSettings.chunkSize);
                                loadedWaterTileRes[key] = desiredRes;
                            }
                        }
                    }
                }
            }

            // Unload tiles no longer needed
            if (loadedWaterTiles.Count > 0)
            {
                List<string> remove = null;
                foreach (var kvp in loadedWaterTiles)
                {
                    if (!desired.Contains(kvp.Key))
                    {
                        remove ??= new List<string>();
                        remove.Add(kvp.Key);
                    }
                }
                if (remove != null)
                {
                    for (int i = 0; i < remove.Count; i++)
                    {
                        string k = remove[i];
                        if (loadedWaterTiles.TryGetValue(k, out GameObject go) && go != null)
                            Object.Destroy(go);
                        loadedWaterTiles.Remove(k);
                        loadedWaterTileRes.Remove(k);
                    }
                }
            }
        }

        private void CreateWaterTile(long chunkX, long chunkY, string key, int resolutionVertsPerSide)
        {
            resolutionVertsPerSide = ValidateWaterTileResolution(resolutionVertsPerSide);

            GameObject plane = new GameObject($"Water_{chunkX}_{chunkY}");
            plane.transform.SetParent(parentTransform, true);

            long relX = chunkX - worldOriginManager.WorldChunkOriginX;
            long relY = chunkY - worldOriginManager.WorldChunkOriginY;

            float half = terrainSettings.chunkSize * 0.5f;
            plane.transform.position = new Vector3(
                relX * terrainSettings.chunkSize + half,
                GetWaterSurfaceY(),
                relY * terrainSettings.chunkSize + half);
            plane.transform.rotation = Quaternion.identity;
            plane.transform.localScale = Vector3.one;

            var mf = plane.AddComponent<MeshFilter>();
            mf.sharedMesh = GetOrCreateWaterGridMesh(resolutionVertsPerSide, terrainSettings.chunkSize);

            var mr = plane.AddComponent<MeshRenderer>();
            mr.sharedMaterial = waterMaterialLoaded;
            mr.shadowCastingMode = waterSettings.waterCastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            mr.receiveShadows = waterSettings.waterReceiveShadows;

            loadedWaterTiles[key] = plane;
            loadedWaterTileRes[key] = resolutionVertsPerSide;
        }

        private Mesh GetOrCreateWaterGridMesh(int vertsPerSide, int sizeWorld)
        {
            vertsPerSide = ValidateWaterTileResolution(vertsPerSide);
            sizeWorld = Mathf.Max(1, sizeWorld);

            // Cache key: hi32 = vertsPerSide, lo32 = sizeWorld
            long key = ((long)vertsPerSide << 32) | (uint)sizeWorld;
            if (waterMeshCache.TryGetValue(key, out Mesh cached) && cached != null) return cached;

            Mesh mesh = MeshUtilities.CreateWaterGridMesh(vertsPerSide, sizeWorld);
            waterMeshCache[key] = mesh;
            return mesh;
        }

        private float GetWaterSurfaceY()
        {
            float baseY = waterSettings.waterUseTerrainWaterLevel
                ? (materialSettings.waterLevel * terrainSettings.heightMultiplier)
                : waterSettings.waterSurfaceY;
            return baseY + waterSettings.waterSurfaceYOffset;
        }

        private int GetWaterRenderDistance()
        {
            int rd = waterSettings.waterRenderDistanceOverride > 0
                ? waterSettings.waterRenderDistanceOverride
                : terrainSettings.renderDistance;
            return Mathf.Max(0, rd);
        }

        private int GetWaterTileResolutionForChunkDelta(int dx, int dy)
        {
            if (!waterSettings.waterEnableLod || waterSettings.waterLodChunkRadii == null || waterSettings.waterLodResolutions == null ||
                waterSettings.waterLodChunkRadii.Length == 0 || waterSettings.waterLodResolutions.Length == 0)
            {
                return ValidateWaterTileResolution(waterSettings.waterTileResolution);
            }

            int r = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
            int levels = Mathf.Min(waterSettings.waterLodChunkRadii.Length, waterSettings.waterLodResolutions.Length);
            for (int i = 0; i < levels; i++)
            {
                if (r <= waterSettings.waterLodChunkRadii[i])
                    return ValidateWaterTileResolution(waterSettings.waterLodResolutions[i]);
            }

            return ValidateWaterTileResolution(waterSettings.waterLodResolutions[levels - 1]);
        }

        private static int ValidateWaterTileResolution(int res)
        {
            res = Mathf.Max(2, res);
            if ((res & 1) == 0) res += 1; // Keep odd for symmetry
            return res;
        }

        /// <summary>
        /// Clear all water tiles.
        /// </summary>
        public void ClearAllWaterTiles()
        {
            foreach (var kvp in loadedWaterTiles)
            {
                if (kvp.Value != null) Object.Destroy(kvp.Value);
            }
            loadedWaterTiles.Clear();
            loadedWaterTileRes.Clear();
        }

        public Dictionary<string, GameObject> GetLoadedWaterTiles() => loadedWaterTiles;
    }
}

