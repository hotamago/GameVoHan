using System.Collections;
using InfinityTerrain.Core;
using InfinityTerrain.Settings;
using InfinityTerrain.Utilities;
using InfinityTerrain.Vegetation;
using UnityEngine;

namespace InfinityTerrain
{
    /// <summary>
    /// Infinite terrain renderer that streams Unity built-in Terrain chunks instead of Mesh chunks.
    /// Keeps floating origin, water tiles, and player safety.
    /// </summary>
    public class InfinityRenderTerrainChunksBuiltIn : MonoBehaviour
    {
        [Header("General Settings")]
        [SerializeField] private Transform player;
        [SerializeField] private int chunkSize = 100;
        [SerializeField] private int renderDistance = 4;
        [SerializeField] private int seed = 0;
        [SerializeField] private float floatingOriginThreshold = 500f;

        [Header("Current Position")]
        public long current_x;
        public long current_y;

        [Header("Player Spawn")]
        [SerializeField] private bool autoPlacePlayerOnStart = true;
        public float spawnHeightOffset = 5.0f;

        [Header("Player Safety")]
        [SerializeField] private bool enableSafety = true;
        [SerializeField] private float safetyHeightOffset = 2.0f;
        [SerializeField] private float groundCheckDistance = 0.5f;
        [SerializeField] private float hardVoidY = -500f;
        [SerializeField] private float recoverBelowTerrain = 5.0f;

        [Header("Terrain Settings")]
        [Tooltip("Base heightmap resolution. MUST be 2^n+1 (e.g. 129, 257, 513).")]
        [SerializeField] private int resolution = 129;
        [SerializeField] private float heightMultiplier = 30f;
        [SerializeField] private float noiseScale = 0.005f;
        [SerializeField] private int octaves = 6;
        [SerializeField] private float persistence = 0.5f;
        [SerializeField] private float lacunarity = 2f;

        [Header("Terrain LOD (Heightmap Resolution per distance)")]
        [SerializeField] private bool enableLod = true;
        [SerializeField] private int[] lodChunkRadii = new int[] { 1, 2, 4, 999 };
        [SerializeField] private int[] lodResolutions = new int[] { 129, 65, 33, 17 };

        [Header("Far SuperChunks (×3)")]
        [SerializeField] private bool enableFarSuperChunks = false;
        [SerializeField] private int superChunkScale = 3;
        [SerializeField] private int superChunkStartRadius = 4;
        [SerializeField] private bool superChunksHaveCollider = false;

        [Header("Advanced Terrain")]
        [SerializeField] private float mountainStrength = 0.4f;
        [SerializeField] private float plainStrength = 0.3f;
        [SerializeField] private float erosionStrength = 0.2f;
        [SerializeField] private float domainWarpStrength = 2.0f;

        [Header("Water")]
        [SerializeField] private bool enableWaterTiles = true;
        [SerializeField] private Material waterMaterialOverride;
        [SerializeField] private string waterMaterialResourceName = "Water_mat_01";
        [SerializeField] private int waterRenderDistanceOverride = 0;
        [SerializeField] private bool waterEnableLod = true;
        [SerializeField] private int waterTileResolution = 33;
        [SerializeField] private int[] waterLodChunkRadii = new int[] { 1, 2, 4, 999 };
        [SerializeField] private int[] waterLodResolutions = new int[] { 33, 17, 9, 5 };
        [SerializeField] private float waterSurfaceY = 0f;
        [SerializeField] private bool waterUseTerrainWaterLevel = true;
        [SerializeField] private bool waterCastShadows = false;
        [SerializeField] private bool waterReceiveShadows = true;
        [SerializeField] private float waterSurfaceYOffset = 0.02f;

        [Header("Built-in Terrain Runtime Quality")]
        [SerializeField] private float terrainHeightmapPixelError = 5f;
        [SerializeField] private float terrainBasemapDistance = 1000f;
        [SerializeField] private bool terrainDrawInstanced = true;
        [SerializeField] private int terrainGroupingID = 0;
        [Tooltip("If null, a runtime TerrainLayer will be created (simple gray) so URP Terrain is not invisible.")]
        [SerializeField] private TerrainLayer defaultTerrainLayer;
        [Tooltip("Optional override for the default TerrainLayer diffuse texture.")]
        [SerializeField] private Texture2D defaultTerrainDiffuse;

        [Header("Vegetation (Terrain Trees)")]
        [SerializeField] private bool enableTerrainTrees = true;
        [SerializeField] private VegetationScatterSettings vegetationScatterSettings;
        [SerializeField] private int vegetationRenderDistance = 2;
        [SerializeField] private bool vegetationOnlyMaxLod = true;
        [SerializeField] private bool vegetationIncludeNonTreesAsTrees = true;
        [SerializeField] private bool vegetationExcludeGrass = true;

        [Header("GPU Resources")]
        [Tooltip("REQUIRED: Assign TerrainGen.compute from Assets/Resources/Shaders/ folder directly in Inspector.")]
        [SerializeField] private ComputeShader terrainComputeShader;

        // Settings objects
        private TerrainSettings _terrainSettings;
        private MaterialSettings _materialSettings;
        private WaterSettings _waterSettings;
        private PlayerSettings _playerSettings;

        // Managers
        private WorldOriginManager _worldOriginManager;
        private TerrainGenerator _terrainGenerator;
        private TerrainChunkManagerBuiltIn _terrainChunkManager;
        private WaterManager _waterManager;
        private PlayerSafety _playerSafety;

        private long _runtimeChunkX;
        private long _runtimeChunkY;
        private Coroutine _placePlayerCoroutine;

        // Cache of last generated heightmaps (for vegetation); keys are loadedChunks keys.
        private readonly System.Collections.Generic.Dictionary<string, float[,]> _heightCache01 =
            new System.Collections.Generic.Dictionary<string, float[,]>();

        private void Start()
        {
            if (!enabled) return;

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

            if (seed == 0) seed = Random.Range(-10000, 10000);

            BuildSettings();

            if (_materialSettings.terrainComputeShader == null)
            {
                Debug.LogError("Terrain Compute Shader is not assigned! Assign 'TerrainGen.compute' in the Inspector.");
                enabled = false;
                return;
            }

            int kernelHeight = _materialSettings.terrainComputeShader.FindKernel("GenerateHeightmap");
            if (kernelHeight < 0)
            {
                Debug.LogError("TerrainGen compute shader is missing required kernel 'GenerateHeightmap'!");
                enabled = false;
                return;
            }

            InitializeManagers();

            TeleportToChunk(current_x, current_y);

            if (autoPlacePlayerOnStart)
            {
                _placePlayerCoroutine = StartCoroutine(PlacePlayerAboveTerrainWhenPlayerReady());
            }
        }

        private void BuildSettings()
        {
            _terrainSettings = new TerrainSettings
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
                skirtDepth = 0f,
                enableFarSuperChunks = enableFarSuperChunks,
                superChunkScale = superChunkScale,
                superChunkStartRadius = superChunkStartRadius,
                superChunksHaveCollider = superChunksHaveCollider,
                mountainStrength = mountainStrength,
                plainStrength = plainStrength,
                erosionStrength = erosionStrength,
                domainWarpStrength = domainWarpStrength
            };

            // Still used for compute shader & water level thresholds
            _materialSettings = new MaterialSettings
            {
                waterLevel = 0.2f,
                beachLevel = 0.25f,
                grassLevel = 0.55f,
                rockLevel = 0.75f,
                snowLevel = 0.9f,
                terrainComputeShader = terrainComputeShader,
                proceduralTerrainShader = null
            };

            _waterSettings = new WaterSettings
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

            _playerSettings = new PlayerSettings
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
            _worldOriginManager = new WorldOriginManager(floatingOriginThreshold, chunkSize);
            _terrainGenerator = new TerrainGenerator(_terrainSettings, _materialSettings, materialManager: null);

            EnsureDefaultTerrainLayer();
            Material terrainMat = TryCreateTerrainMaterialTemplate();

            _terrainChunkManager = new TerrainChunkManagerBuiltIn(
                _terrainSettings,
                _terrainGenerator,
                _worldOriginManager,
                transform,
                heightmapPixelError: terrainHeightmapPixelError,
                basemapDistance: terrainBasemapDistance,
                drawInstanced: terrainDrawInstanced,
                groupingID: terrainGroupingID,
                terrainLayer: Mathf.Max(0, LayerMask.NameToLayer("Default")),
                defaultTerrainLayer: defaultTerrainLayer,
                terrainMaterialTemplate: terrainMat);

            _waterManager = new WaterManager(_waterSettings, _terrainSettings, _materialSettings, _worldOriginManager, transform);
            _waterManager.EnsureWaterMaterialLoaded();

            _playerSafety = new PlayerSafety(_playerSettings, _terrainSettings, _materialSettings, player);
        }

        private void EnsureDefaultTerrainLayer()
        {
            if (defaultTerrainLayer != null) return;

            // Create a simple runtime layer so URP terrain is visible even without authoring layers in editor.
            // Note: TerrainLayer is NOT a ScriptableObject, use 'new' instead.
            defaultTerrainLayer = new TerrainLayer();
            defaultTerrainLayer.diffuseTexture = defaultTerrainDiffuse != null ? defaultTerrainDiffuse : Texture2D.grayTexture;
            defaultTerrainLayer.tileSize = new Vector2(20f, 20f);
            defaultTerrainLayer.tileOffset = Vector2.zero;
        }

        private static Material TryCreateTerrainMaterialTemplate()
        {
            // URP project: prefer URP Terrain/Lit.
            Shader s =
                Shader.Find("Universal Render Pipeline/Terrain/Lit") ??
                Shader.Find("Universal Render Pipeline/Nature/Terrain/Lit") ??
                Shader.Find("Nature/Terrain/Standard");

            if (s == null) return null;
            return new Material(s);
        }

        private float ComputeWaterSurfaceY()
        {
            float y = waterUseTerrainWaterLevel ? (0.2f * heightMultiplier) : waterSurfaceY;
            y += waterSurfaceYOffset;
            return y;
        }

        private IEnumerator PlacePlayerAboveTerrainWhenPlayerReady()
        {
            const int tries = 60;
            for (int i = 0; i < tries; i++)
            {
                if (!enabled) yield break;
                if (player != null)
                {
                    _worldOriginManager.GetAbsoluteChunkCoords(player.position, out long pX, out long pZ);
                    _worldOriginManager.GetInChunkCoords(player.position, out float inX, out float inZ);
                    _playerSafety.PlacePlayerAboveTerrainAtCurrentXZ(spawnHeightOffset, pX, pZ, inX, inZ);
                    yield break;
                }
                yield return null;
            }
        }

        private void Update()
        {
            if (!enabled || player == null) return;
            if (_worldOriginManager == null || _terrainChunkManager == null || _waterManager == null) return;

            if (_worldOriginManager.TryShiftWorldOrigin(player, _terrainChunkManager.LoadedChunks, _waterManager.GetLoadedWaterTiles()))
            {
                // Positions will be corrected by chunk update calls
            }

            _worldOriginManager.GetAbsoluteChunkCoords(player.position, out long pX, out long pZ);
            _worldOriginManager.GetInChunkCoords(player.position, out float inChunkX, out float inChunkZ);

            _runtimeChunkX = pX;
            _runtimeChunkY = pZ;
            current_x = pX;
            current_y = pZ;

            _terrainChunkManager.UpdateChunks(pX, pZ);
            _waterManager.UpdateWaterTiles(pX, pZ);

            if (enableTerrainTrees)
            {
                UpdateTerrainTrees(pX, pZ);
            }

            if (enableSafety && _playerSafety != null)
            {
                _playerSafety.UpdatePlayerSafety(pX, pZ, inChunkX, inChunkZ);
            }
        }

        private void UpdateTerrainTrees(long centerChunkX, long centerChunkY)
        {
            if (vegetationScatterSettings == null) return;

            foreach (var kvp in _terrainChunkManager.LoadedChunks)
            {
                var d = kvp.Value;
                if (d == null || d.gameObject == null) continue;
                if (!d.isReady) continue;
                if (d.isSuperChunk) continue; // optional: skip superchunks for trees

                int dx = ComputeShaderHelper.ClampLongToInt(d.noiseChunkX - centerChunkX);
                int dy = ComputeShaderHelper.ClampLongToInt(d.noiseChunkY - centerChunkY);
                int r = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
                bool inRange = r <= Mathf.Max(0, vegetationRenderDistance);
                bool lodOk = !vegetationOnlyMaxLod || d.lodResolution == _terrainSettings.resolution;

                ChunkTerrainTreeScatter scatter = d.gameObject.GetComponent<ChunkTerrainTreeScatter>();

                if (!inRange || !lodOk)
                {
                    if (scatter != null) scatter.Clear();
                    continue;
                }

                if (scatter == null) scatter = d.gameObject.AddComponent<ChunkTerrainTreeScatter>();
                scatter.settings = vegetationScatterSettings;
                scatter.globalSeed = seed;
                scatter.heightMultiplier = Mathf.Max(0.0001f, heightMultiplier);
                scatter.waterSurfaceY = ComputeWaterSurfaceY();
                scatter.includeNonTreesAsTrees = vegetationIncludeNonTreesAsTrees;
                scatter.excludeGrass = vegetationExcludeGrass;

                // Ensure we have heightmap cached for this chunk key; if not, regenerate on demand.
                // Note: TerrainChunkManagerBuiltIn internally already generated heights, but doesn't expose them.
                // For now, we compute again here only for chunks that will get vegetation (small radius).
                if (!_heightCache01.TryGetValue(kvp.Key, out float[,] heights01) || heights01 == null || heights01.GetLength(0) != d.lodResolution)
                {
                    heights01 = _terrainGenerator.GenerateHeightmap01GPU(
                        d.noiseChunkX, d.noiseChunkY, d.lodResolution, d.chunkSizeWorld, d.baseVertsPerChunk);
                    _heightCache01[kvp.Key] = heights01;
                }

                scatter.EnsureGenerated(d.noiseChunkX, d.noiseChunkY, d.lodResolution, d.chunkSizeWorld, heights01, true);
            }
        }

        private void OnValidate()
        {
            if (!Application.isPlaying) return;
            if (!enabled) return;
            if (player == null) return;

            if (current_x != _runtimeChunkX || current_y != _runtimeChunkY)
            {
                TeleportToChunk(current_x, current_y);
            }
        }

        public void TeleportToChunk(long targetChunkX, long targetChunkY)
        {
            if (!enabled || player == null) return;
            if (_terrainChunkManager == null || _waterManager == null || _worldOriginManager == null) return;

            _terrainChunkManager.ClearAllChunks();
            _waterManager.ClearAllWaterTiles();
            _heightCache01.Clear();

            _worldOriginManager.TeleportToChunk(targetChunkX, targetChunkY, player.position);

            current_x = targetChunkX;
            current_y = targetChunkY;

            _worldOriginManager.GetAbsoluteChunkCoords(player.position, out long pX, out long pZ);
            _terrainChunkManager.UpdateChunks(pX, pZ);
            _waterManager.UpdateWaterTiles(pX, pZ);
        }

        private void OnDisable()
        {
            if (_placePlayerCoroutine != null)
            {
                StopCoroutine(_placePlayerCoroutine);
                _placePlayerCoroutine = null;
            }
        }
    }
}


