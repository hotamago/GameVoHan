using UnityEngine;
using System.Collections;
using InfinityTerrain.Core;
using InfinityTerrain.Settings;
using InfinityTerrain.Utilities;
using InfinityTerrain.Vegetation;

namespace InfinityTerrain
{
    /// <summary>
    /// Main orchestrator for infinite terrain rendering system.
    /// Manages chunk generation, water tiles, player safety, and world origin shifting.
    /// </summary>
    public class InfinityRenderChunks : MonoBehaviour
    {
        [Header("General Settings")]
        [SerializeField] private Transform player;
        [SerializeField] private int chunkSize = 100;
        [SerializeField] private int renderDistance = 4;
        [SerializeField] private int seed = 0;
        [SerializeField] private float floatingOriginThreshold = 500f;

        [Header("Current Position")]
        [Tooltip("Realtime absolute chunk coordinate (64-bit). You can also edit these in Inspector during Play Mode to teleport.")]
        public long current_x;
        public long current_y;

        [Header("Debug Info")]
        [SerializeField] private Vector3 _debugLocalPos;

        [Header("Player Spawn")]
        [SerializeField] private bool autoPlacePlayerOnStart = true;
        [Tooltip("When the game starts, player.y will be set to (terrainHeight + spawnHeightOffset) at the current (x,z).")]
        public float spawnHeightOffset = 5.0f;

        [Header("Player Safety")]
        [SerializeField] private bool enableSafety = true;
        [SerializeField] private float safetyHeightOffset = 2.0f;
        [SerializeField] private float groundCheckDistance = 0.5f;
        [Tooltip("If player falls below this Y (local Unity space), force-teleport back above terrain even if 'jumping'.")]
        [SerializeField] private float hardVoidY = -500f;
        [Tooltip("Teleport player back when they are below (terrainHeight - this value), unless they are actively jumping up.")]
        [SerializeField] private float recoverBelowTerrain = 5.0f;

        [Header("Terrain Settings")]
        [Tooltip("Base (highest) resolution. For LOD to work correctly this MUST be 2^n+1 (e.g. 129, 257, 513).")]
        [SerializeField] private int resolution = 129;
        [SerializeField] private float heightMultiplier = 30f;
        [SerializeField] private float noiseScale = 0.005f;
        [SerializeField] private int octaves = 6;
        [SerializeField] private float persistence = 0.5f;
        [SerializeField] private float lacunarity = 2f;

        [Header("Terrain LOD")]
        [Tooltip("Enable chunk LOD: far chunks use lower mesh/heightmap resolution to reduce cost.")]
        [SerializeField] private bool enableLod = true;
        [Tooltip("Chunk distance (Chebyshev: max(|dx|,|dy|)) thresholds per LOD level. Must match 'lodResolutions' length.")]
        [SerializeField] private int[] lodChunkRadii = new int[] { 1, 2, 4, 999 };
        [Tooltip("Mesh/heightmap resolution per LOD level (must be 2^n+1 and (resolution-1) must be divisible by (lodRes-1)). Example: 129,65,33,17,9.")]
        [SerializeField] private int[] lodResolutions = new int[] { 129, 65, 33, 17 };
        [Tooltip("Optional: add a downward skirt on chunk borders to hide tiny cracks from LOD T-junctions. 0 disables.")]
        [SerializeField] private float skirtDepth = 0f;

        [Header("Far SuperChunks (×3)")]
        [Tooltip("Experimental: replace far-away base chunks with larger 'superchunks' to reduce chunk count.")]
        [SerializeField] private bool enableFarSuperChunks = false;
        [Tooltip("Superchunk covers (scale x scale) base chunks. Default 3.")]
        [SerializeField] private int superChunkScale = 3;
        [Tooltip("Chebyshev radius (in base chunks). Beyond this, superchunks are used when possible.")]
        [SerializeField] private int superChunkStartRadius = 4;
        [Tooltip("Superchunks usually should NOT have colliders (performance).")]
        [SerializeField] private bool superChunksHaveCollider = false;

        [Header("Advanced Terrain")]
        [SerializeField] private float mountainStrength = 0.4f;
        [SerializeField] private float plainStrength = 0.3f;
        [SerializeField] private float erosionStrength = 0.2f;
        [SerializeField] private float domainWarpStrength = 2.0f;

        [Header("Shader Height Thresholds")]
        [SerializeField] private float waterLevel = 0.2f;
        [SerializeField] private float beachLevel = 0.25f;
        [SerializeField] private float grassLevel = 0.55f;
        [SerializeField] private float rockLevel = 0.75f;
        [SerializeField] private float snowLevel = 0.9f;

        [Header("Water (IgniteCoders Simple Water Shader)")]
        [SerializeField] private bool enableWaterTiles = true;
        [Tooltip("If assigned, this material is used instead of loading from Resources.")]
        [SerializeField] private Material waterMaterialOverride;
        [Tooltip("Resources material name. Default exists at Assets/IgniteCoders/Simple Water Shader/Resources/Water_mat_01.mat")]
        [SerializeField] private string waterMaterialResourceName = "Water_mat_01";
        [Tooltip("How far water tiles should be created around the player (in chunks). If 0, uses Terrain renderDistance.")]
        [SerializeField] private int waterRenderDistanceOverride = 0;

        [Header("Water LOD / Resolution")]
        [Tooltip("Enable water tile LOD: far tiles use lower vertex resolution (flat mesh, cheaper).")]
        [SerializeField] private bool waterEnableLod = true;
        [Tooltip("If LOD disabled, this is the single water mesh resolution (verts per side).")]
        [SerializeField] private int waterTileResolution = 33;
        [Tooltip("Chunk distance (Chebyshev) thresholds per water LOD level. Must match 'waterLodResolutions' length.")]
        [SerializeField] private int[] waterLodChunkRadii = new int[] { 1, 2, 4, 999 };
        [Tooltip("Water mesh resolution per LOD level (verts per side). Suggested: 65,33,17,9,5.")]
        [SerializeField] private int[] waterLodResolutions = new int[] { 33, 17, 9, 5 };

        [Tooltip("Water surface height in world-space meters. If 'Use Terrain Water Level' is enabled, this is ignored.")]
        [SerializeField] private float waterSurfaceY = 0f;
        [Tooltip("If enabled, the water surface Y is computed as (waterLevel * heightMultiplier).")]
        [SerializeField] private bool waterUseTerrainWaterLevel = true;
        [SerializeField] private bool waterCastShadows = false;
        [SerializeField] private bool waterReceiveShadows = true;
        [Tooltip("Optional: raise the water a bit to avoid Z-fighting at shoreline.")]
        [SerializeField] private float waterSurfaceYOffset = 0.02f;

        [Header("Vegetation Scatter (Idyllic Fantasy Nature Prefabs)")]
        [SerializeField] private bool enableVegetationScatter = true;
        [Tooltip("Settings asset that contains prefab list + budgets. Create one via Assets/Create/InfinityTerrain/Vegetation Scatter Settings.")]
        [SerializeField] private VegetationScatterSettings vegetationScatterSettings;
        [Tooltip("Spawn vegetation only on base chunks within this radius (Chebyshev) from player chunk. Keep small for performance.")]
        [SerializeField] private int vegetationRenderDistance = 2;
        [Tooltip("If enabled, vegetation is spawned only on the highest-detail (max) LOD chunks.")]
        [SerializeField] private bool vegetationOnlyMaxLod = true;

        [Header("GPU Resources")]
        [Tooltip("REQUIRED: Assign TerrainGen.compute from Assets/Resources/Shaders/ folder directly in Inspector. This ensures it's included in builds.")]
        [SerializeField] private ComputeShader terrainComputeShader;
        [SerializeField] private Shader proceduralTerrainShader;

        // Core Managers
        private TerrainSettings terrainSettings;
        private MaterialSettings materialSettings;
        private WaterSettings waterSettings;
        private PlayerSettings playerSettings;

        private MaterialManager materialManager;
        private TerrainGenerator terrainGenerator;
        private ChunkManager chunkManager;
        private WaterManager waterManager;
        private PlayerSafety playerSafety;
        private WorldOriginManager worldOriginManager;
        private VegetationScatterManager vegetationScatterManager;

        private VegetationScatterSettings _lastVegetationSettings;
        private int _lastVegetationRenderDistance;
        private bool _lastVegetationOnlyMaxLod;

        private long _runtimeChunkX;
        private long _runtimeChunkY;
        private Coroutine _placePlayerCoroutine;

        private void Start()
        {
            if (!enabled) return;

            // Find player if not assigned
            if (player == null)
            {
                GameObject p = GameObject.FindGameObjectWithTag("Player");
                if (p != null) player = p.transform;
            }

            if (player == null)
            {
                Debug.LogError("Player Transform is not assigned and no GameObject with 'Player' tag found!");
                enabled = false;
                return;
            }

            // Initialize seed
            if (seed == 0) seed = UnityEngine.Random.Range(-10000, 10000);

            // Build settings objects
            BuildSettings();

            // Verify compute shader
            if (materialSettings.terrainComputeShader == null)
            {
                Debug.LogError("Terrain Compute Shader is not assigned! " +
                    "Please assign 'TerrainGen.compute' from Assets/Resources/Shaders/ in the Inspector. " +
                    "Terrain generation will not work without this.");
                enabled = false;
                return;
            }

            int kernelHeight = materialSettings.terrainComputeShader.FindKernel("GenerateHeightmap");
            if (kernelHeight < 0)
            {
                Debug.LogError("TerrainGen compute shader is missing required kernel 'GenerateHeightmap'! " +
                    "The shader may not have compiled correctly for the target platform.");
                enabled = false;
                return;
            }

            // Initialize managers
            InitializeManagers();

            // Teleport to starting position
            TeleportToChunk(current_x, current_y);

            // Place player above terrain if requested
            if (autoPlacePlayerOnStart)
            {
                _placePlayerCoroutine = StartCoroutine(PlacePlayerAboveTerrainWhenPlayerReady());
            }
        }

        private void BuildSettings()
        {
            terrainSettings = new TerrainSettings
            {
                chunkSize = chunkSize,
                renderDistance = renderDistance,
                seed = seed,
                floatingOriginThreshold = floatingOriginThreshold,
                resolution = resolution,
                heightMultiplier = heightMultiplier,
                noiseScale = noiseScale,
                octaves = octaves,
                persistence = persistence,
                lacunarity = lacunarity,
                enableLod = enableLod,
                lodChunkRadii = lodChunkRadii,
                lodResolutions = lodResolutions,
                skirtDepth = skirtDepth,
                enableFarSuperChunks = enableFarSuperChunks,
                superChunkScale = superChunkScale,
                superChunkStartRadius = superChunkStartRadius,
                superChunksHaveCollider = superChunksHaveCollider,
                mountainStrength = mountainStrength,
                plainStrength = plainStrength,
                erosionStrength = erosionStrength,
                domainWarpStrength = domainWarpStrength
            };

            materialSettings = new MaterialSettings
            {
                waterLevel = waterLevel,
                beachLevel = beachLevel,
                grassLevel = grassLevel,
                rockLevel = rockLevel,
                snowLevel = snowLevel,
                terrainComputeShader = terrainComputeShader,
                proceduralTerrainShader = proceduralTerrainShader
            };

            waterSettings = new WaterSettings
            {
                enableWaterTiles = enableWaterTiles,
                waterMaterialOverride = waterMaterialOverride,
                waterMaterialResourceName = waterMaterialResourceName,
                waterRenderDistanceOverride = waterRenderDistanceOverride,
                waterEnableLod = waterEnableLod,
                waterTileResolution = waterTileResolution,
                waterLodChunkRadii = waterLodChunkRadii,
                waterLodResolutions = waterLodResolutions,
                waterSurfaceY = waterSurfaceY,
                waterUseTerrainWaterLevel = waterUseTerrainWaterLevel,
                waterCastShadows = waterCastShadows,
                waterReceiveShadows = waterReceiveShadows,
                waterSurfaceYOffset = waterSurfaceYOffset
            };

            playerSettings = new PlayerSettings
            {
                autoPlacePlayerOnStart = autoPlacePlayerOnStart,
                spawnHeightOffset = spawnHeightOffset,
                enableSafety = enableSafety,
                safetyHeightOffset = safetyHeightOffset,
                groundCheckDistance = groundCheckDistance,
                hardVoidY = hardVoidY,
                recoverBelowTerrain = recoverBelowTerrain
            };
        }

        private void InitializeManagers()
        {
            // World origin manager
            worldOriginManager = new WorldOriginManager(floatingOriginThreshold, chunkSize);

            // Material manager
            materialManager = new MaterialManager(materialSettings, terrainSettings);
            materialManager.InitializeTerrainMaterial();

            // Terrain generator
            terrainGenerator = new TerrainGenerator(terrainSettings, materialSettings, materialManager);

            // Chunk manager
            chunkManager = new ChunkManager(terrainSettings, terrainGenerator, worldOriginManager, transform);

            // Water manager
            waterManager = new WaterManager(waterSettings, terrainSettings, materialSettings, worldOriginManager, transform);
            waterManager.EnsureWaterMaterialLoaded();

            // Player safety
            playerSafety = new PlayerSafety(playerSettings, terrainSettings, materialSettings, player);
        }

        private void EnsureVegetationManager()
        {
            if (!enableVegetationScatter)
            {
                vegetationScatterManager = null;
                return;
            }

            // Ensure settings are initialized (may not be if called before Start)
            if (terrainSettings == null || materialSettings == null)
            {
                return;
            }

            bool changed =
                vegetationScatterManager == null ||
                _lastVegetationSettings != vegetationScatterSettings ||
                _lastVegetationRenderDistance != vegetationRenderDistance ||
                _lastVegetationOnlyMaxLod != vegetationOnlyMaxLod;

            if (!changed) return;

            _lastVegetationSettings = vegetationScatterSettings;
            _lastVegetationRenderDistance = vegetationRenderDistance;
            _lastVegetationOnlyMaxLod = vegetationOnlyMaxLod;

            vegetationScatterManager = new VegetationScatterManager(
                terrainSettings,
                materialSettings,
                vegetationScatterSettings,
                seed,
                vegetationRenderDistance,
                vegetationOnlyMaxLod);
        }

        private float ComputeWaterSurfaceY()
        {
            // If the project uses terrain water level, water plane is at (waterLevel * heightMultiplier).
            float y = waterUseTerrainWaterLevel ? (waterLevel * heightMultiplier) : waterSurfaceY;
            y += waterSurfaceYOffset;
            return y;
        }

        private IEnumerator PlacePlayerAboveTerrainWhenPlayerReady()
        {
            const int tries = 60;
            for (int i = 0; i < tries; i++)
            {
                if (!enabled) yield break;

                if (player == null)
                {
                    GameObject p = GameObject.FindGameObjectWithTag("Player");
                    if (p != null) player = p.transform;
                }

                if (player != null)
                {
                    worldOriginManager.GetAbsoluteChunkCoords(player.position, out long pX, out long pZ);
                    worldOriginManager.GetInChunkCoords(player.position, out float inX, out float inZ);
                    playerSafety.PlacePlayerAboveTerrainAtCurrentXZ(spawnHeightOffset, pX, pZ, inX, inZ);
                    yield break;
                }

                yield return null;
            }
        }

        private void Update()
        {
            if (!enabled || player == null) return;

            // Ensure managers are initialized
            if (worldOriginManager == null || chunkManager == null || waterManager == null)
            {
                return; // Wait for Start() to initialize managers
            }

            // 1. World Shift Check
            if (worldOriginManager.TryShiftWorldOrigin(player, chunkManager.LoadedChunks, waterManager.GetLoadedWaterTiles()))
            {
                // Origin shifted, recalculate positions
            }

            // 2. Calculate Absolute Chunk Coordinate
            worldOriginManager.GetAbsoluteChunkCoords(player.position, out long pX, out long pZ);
            worldOriginManager.GetInChunkCoords(player.position, out float inChunkX, out float inChunkZ);

            // Realtime current chunk
            _runtimeChunkX = pX;
            _runtimeChunkY = pZ;
            current_x = pX;
            current_y = pZ;
            _debugLocalPos = player.position;

            // 3. Update Chunks
            chunkManager.UpdateChunks(pX, pZ);

            // 4. Update Water Tiles
            waterManager.UpdateWaterTiles(pX, pZ);

            // 4.5 Vegetation Scatter (near chunks only)
            EnsureVegetationManager();
            if (vegetationScatterManager != null)
            {
                vegetationScatterManager.Update(
                    pX, pZ,
                    chunkManager.LoadedChunks,
                    heightMultiplier,
                    ComputeWaterSurfaceY());
            }

            // 5. Player Safety
            if (enableSafety && playerSafety != null)
            {
                playerSafety.UpdatePlayerSafety(pX, pZ, inChunkX, inChunkZ);
            }
        }

        private void OnValidate()
        {
            // Let the user edit current_x/current_y in Inspector while playing to teleport instantly
            if (!Application.isPlaying) return;
            if (!enabled) return;
            if (player == null) return;

            if (current_x != _runtimeChunkX || current_y != _runtimeChunkY)
            {
                TeleportToChunk(current_x, current_y);
            }
        }

        /// <summary>
        /// Teleport the *virtual world* so the player is now in the specified chunk (64-bit safe).
        /// </summary>
        public void TeleportToChunk(long targetChunkX, long targetChunkY)
        {
            if (!enabled || player == null) return;

            // Ensure managers are initialized (may not be if called from OnValidate before Start)
            if (chunkManager == null || waterManager == null || worldOriginManager == null)
            {
                return; // Will be called again after Start() initializes managers
            }

            // Reset terrain around new location
            chunkManager.ClearAllChunks();
            waterManager.ClearAllWaterTiles();

            // Set the chunk-origin so current local chunk becomes the target
            worldOriginManager.TeleportToChunk(targetChunkX, targetChunkY, player.position);

            // Keep debug/current fields aligned
            current_x = targetChunkX;
            current_y = targetChunkY;

            // Update immediately
            worldOriginManager.GetAbsoluteChunkCoords(player.position, out long pX, out long pZ);
            chunkManager.UpdateChunks(pX, pZ);
            waterManager.UpdateWaterTiles(pX, pZ);
        }

        /// <summary>
        /// Teleport using the currently set current_x/current_y (useful when editing in Inspector).
        /// </summary>
        [ContextMenu("Terrain/Teleport To Current Chunk")]
        public void TeleportToCurrentChunk()
        {
            TeleportToChunk(current_x, current_y);
        }

        /// <summary>
        /// Exits/unloads the chunk at the current x,y position
        /// </summary>
        [ContextMenu("Terrain/Exit Current Chunk")]
        public void ExitCurrentChunk()
        {
            string key = $"{current_x}_{current_y}";
            if (chunkManager.LoadedChunks.ContainsKey(key))
            {
                // UnloadChunk is private, we'd need to expose it or add a public method
                // For now, this is a placeholder
            }
        }

        /// <summary>
        /// Exits/unloads a specific chunk by coordinates
        /// </summary>
        public void ExitChunk(long x, long y)
        {
            string key = $"{x}_{y}";
            if (chunkManager.LoadedChunks.ContainsKey(key))
            {
                // UnloadChunk is private, we'd need to expose it or add a public method
                // For now, this is a placeholder
            }
        }

        // ---- Editor / validation helpers (used by CustomEditor) ----
        public bool IsLodConfigValid(out string error)
        {
            if (!enableLod)
            {
                error = null;
                return true;
            }

            if (resolution < 9)
            {
                error = "resolution must be >= 9.";
                return false;
            }

            int baseVerts = resolution - 1;
            if (!Utilities.ChunkLodUtility.IsPow2(baseVerts))
            {
                error = $"Base resolution invalid for LOD: resolution-1 must be power-of-two. Current resolution={resolution} => baseVerts={baseVerts}. Use 2^n+1 (e.g. 129, 257, 513).";
                return false;
            }

            if (lodResolutions == null || lodResolutions.Length == 0)
            {
                error = "lodResolutions is empty.";
                return false;
            }

            if (lodChunkRadii == null || lodChunkRadii.Length == 0)
            {
                error = "lodChunkRadii is empty.";
                return false;
            }

            int levels = Mathf.Min(lodChunkRadii.Length, lodResolutions.Length);
            if (levels < 1)
            {
                error = "LOD arrays must have at least 1 level.";
                return false;
            }

            for (int i = 0; i < levels; i++)
            {
                int r = lodResolutions[i];
                if (r < 9 || r > resolution)
                {
                    error = $"lodResolutions[{i}]={r} must be in [9..resolution].";
                    return false;
                }
                if ((r & 1) == 0)
                {
                    error = $"lodResolutions[{i}]={r} must be odd (2^n+1).";
                    return false;
                }
                int v = r - 1;
                if (!Utilities.ChunkLodUtility.IsPow2(v))
                {
                    error = $"lodResolutions[{i}]={r} invalid: (lodRes-1) must be power-of-two.";
                    return false;
                }
                if (baseVerts % v != 0)
                {
                    error = $"lodResolutions[{i}]={r} invalid: (resolution-1)={baseVerts} must be divisible by (lodRes-1)={v}.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        public void FixLodConfig(bool snapUp)
        {
            resolution = Utilities.ChunkLodUtility.SnapToPow2Plus1(resolution, snapUp, 9, 2049);
            RebuildLodArraysToMatchBase();
        }

        private void RebuildLodArraysToMatchBase()
        {
            lodResolutions = Utilities.ChunkLodUtility.BuildPow2Plus1LodResolutions(resolution, 9);
            lodChunkRadii = Utilities.ChunkLodUtility.ResizeRadiiToMatchLevels(lodChunkRadii, lodResolutions.Length);
        }

        // ---- Water LOD validation helpers (used by CustomEditor) ----
        public bool IsWaterLodConfigValid(out string error)
        {
            if (!enableWaterTiles)
            {
                error = null;
                return true;
            }

            if (!waterEnableLod)
            {
                if (waterTileResolution < 2)
                {
                    error = $"waterTileResolution must be >= 2. Current: {waterTileResolution}.";
                    return false;
                }
                error = null;
                return true;
            }

            if (waterLodResolutions == null || waterLodResolutions.Length == 0)
            {
                error = "waterLodResolutions is empty when waterEnableLod is true.";
                return false;
            }

            if (waterLodChunkRadii == null || waterLodChunkRadii.Length == 0)
            {
                error = "waterLodChunkRadii is empty when waterEnableLod is true.";
                return false;
            }

            int levels = Mathf.Min(waterLodChunkRadii.Length, waterLodResolutions.Length);
            if (levels < 1)
            {
                error = "Water LOD arrays must have at least 1 level.";
                return false;
            }

            if (waterLodChunkRadii.Length != waterLodResolutions.Length)
            {
                error = $"waterLodChunkRadii.Length ({waterLodChunkRadii.Length}) != waterLodResolutions.Length ({waterLodResolutions.Length}). They must match.";
                return false;
            }

            for (int i = 0; i < levels; i++)
            {
                int r = waterLodResolutions[i];
                if (r < 2)
                {
                    error = $"waterLodResolutions[{i}]={r} must be >= 2.";
                    return false;
                }
            }

            for (int i = 1; i < waterLodChunkRadii.Length; i++)
            {
                if (waterLodChunkRadii[i] < waterLodChunkRadii[i - 1])
                {
                    error = $"waterLodChunkRadii should be ascending. Found {waterLodChunkRadii[i - 1]} >= {waterLodChunkRadii[i]} at index {i}.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        public void FixWaterLodConfig()
        {
            if (!enableWaterTiles || !waterEnableLod) return;

            if (waterLodResolutions != null && waterLodResolutions.Length > 0)
            {
                for (int i = 0; i < waterLodResolutions.Length; i++)
                {
                    int res = waterLodResolutions[i];
                    res = Mathf.Max(2, res);
                    if ((res & 1) == 0) res += 1;
                    waterLodResolutions[i] = res;
                }
            }

            if (!waterEnableLod)
            {
                int res = waterTileResolution;
                res = Mathf.Max(2, res);
                if ((res & 1) == 0) res += 1;
                waterTileResolution = res;
            }

            if (waterLodResolutions != null && waterLodChunkRadii != null)
            {
                if (waterLodChunkRadii.Length != waterLodResolutions.Length)
                {
                    int[] newR = new int[waterLodResolutions.Length];
                    for (int i = 0; i < newR.Length; i++)
                    {
                        newR[i] = (i < waterLodChunkRadii.Length) ? waterLodChunkRadii[i] : (i == newR.Length - 1 ? 999 : (i + 1));
                    }
                    waterLodChunkRadii = newR;
                }
            }
        }

        private void OnDisable()
        {
            if (_placePlayerCoroutine != null)
            {
                StopCoroutine(_placePlayerCoroutine);
                _placePlayerCoroutine = null;
            }
        }

        private void OnDestroy()
        {
            OnDisable();
        }
    }
}

