using UnityEngine;

namespace InfinityTerrain.Settings
{
    /// <summary>
    /// Terrain generation settings.
    /// </summary>
    [System.Serializable]
    public class TerrainSettings
    {
        [Header("General Settings")]
        [Tooltip("Size of each chunk in world units.")]
        public int chunkSize = 100;
        
        [Tooltip("How many chunks to render around the player (Chebyshev distance).")]
        public int renderDistance = 4;
        
        [Tooltip("Seed for terrain generation. 0 = random.")]
        public int seed = 0;
        
        [Tooltip("Distance threshold for floating origin shift.")]
        public float floatingOriginThreshold = 500f;

        [Header("Terrain Generation")]
        [Tooltip("Base (highest) resolution. For LOD to work correctly this MUST be 2^n+1 (e.g. 129, 257, 513).")]
        public int resolution = 129;
        
        [Tooltip("Multiplier for terrain height.")]
        public float heightMultiplier = 30f;
        
        [Tooltip("Noise scale for terrain generation.")]
        public float noiseScale = 0.005f;
        
        [Tooltip("Number of octaves for noise generation.")]
        public int octaves = 6;
        
        [Tooltip("Persistence for noise generation.")]
        public float persistence = 0.5f;
        
        [Tooltip("Lacunarity for noise generation.")]
        public float lacunarity = 2f;

        [Header("Terrain LOD")]
        [Tooltip("Enable chunk LOD: far chunks use lower mesh/heightmap resolution to reduce cost.")]
        public bool enableLod = true;
        
        [Tooltip("Chunk distance (Chebyshev: max(|dx|,|dy|)) thresholds per LOD level. Must match 'lodResolutions' length.")]
        public int[] lodChunkRadii = new int[] { 1, 2, 4, 999 };
        
        [Tooltip("Mesh/heightmap resolution per LOD level (must be 2^n+1 and (resolution-1) must be divisible by (lodRes-1)). Example: 129,65,33,17,9.")]
        public int[] lodResolutions = new int[] { 129, 65, 33, 17 };
        
        [Tooltip("Optional: add a downward skirt on chunk borders to hide tiny cracks from LOD T-junctions. 0 disables.")]
        public float skirtDepth = 0f;

        [Header("Far SuperChunks")]
        [Tooltip("Experimental: replace far-away base chunks with larger 'superchunks' to reduce chunk count.")]
        public bool enableFarSuperChunks = false;
        
        [Tooltip("Superchunk covers (scale x scale) base chunks. Default 3.")]
        public int superChunkScale = 3;
        
        [Tooltip("Chebyshev radius (in base chunks). Beyond this, superchunks are used when possible.")]
        public int superChunkStartRadius = 4;
        
        [Tooltip("Superchunks usually should NOT have colliders (performance).")]
        public bool superChunksHaveCollider = false;

        [Header("Advanced Terrain")]
        public float mountainStrength = 0.4f;
        public float plainStrength = 0.3f;
        public float erosionStrength = 0.2f;
        public float domainWarpStrength = 2.0f;
    }

    /// <summary>
    /// Material and shader settings for terrain rendering.
    /// </summary>
    [System.Serializable]
    public class MaterialSettings
    {
        [Header("Shader Height Thresholds")]
        public float waterLevel = 0.2f;
        public float beachLevel = 0.25f;
        public float grassLevel = 0.55f;
        public float rockLevel = 0.75f;
        public float snowLevel = 0.9f;

        [Header("GPU Resources")]
        [Tooltip("REQUIRED: Assign TerrainGen.compute from Assets/Resources/Shaders/ folder directly in Inspector.")]
        public ComputeShader terrainComputeShader;
        
        public Shader proceduralTerrainShader;
    }

    /// <summary>
    /// Water tile settings.
    /// </summary>
    [System.Serializable]
    public class WaterSettings
    {
        [Header("Water General")]
        public bool enableWaterTiles = true;
        
        [Tooltip("If assigned, this material is used instead of loading from Resources.")]
        public Material waterMaterialOverride;
        
        [Tooltip("Resources material name. Default exists at Assets/IgniteCoders/Simple Water Shader/Resources/Water_mat_01.mat")]
        public string waterMaterialResourceName = "Water_mat_01";
        
        [Tooltip("How far water tiles should be created around the player (in chunks). If 0, uses Terrain renderDistance.")]
        public int waterRenderDistanceOverride = 0;

        [Header("Water LOD / Resolution")]
        [Tooltip("Enable water tile LOD: far tiles use lower vertex resolution (flat mesh, cheaper).")]
        public bool waterEnableLod = true;
        
        [Tooltip("If LOD disabled, this is the single water mesh resolution (verts per side).")]
        public int waterTileResolution = 33;
        
        [Tooltip("Chunk distance (Chebyshev) thresholds per water LOD level. Must match 'waterLodResolutions' length.")]
        public int[] waterLodChunkRadii = new int[] { 1, 2, 4, 999 };
        
        [Tooltip("Water mesh resolution per LOD level (verts per side). Suggested: 65,33,17,9,5.")]
        public int[] waterLodResolutions = new int[] { 33, 17, 9, 5 };

        [Tooltip("Water surface height in world-space meters. If 'Use Terrain Water Level' is enabled, this is ignored.")]
        public float waterSurfaceY = 0f;
        
        [Tooltip("If enabled, the water surface Y is computed as (waterLevel * heightMultiplier).")]
        public bool waterUseTerrainWaterLevel = true;
        
        public bool waterCastShadows = false;
        public bool waterReceiveShadows = true;
        
        [Tooltip("Optional: raise the water a bit to avoid Z-fighting at shoreline.")]
        public float waterSurfaceYOffset = 0.02f;
    }

    /// <summary>
    /// Player safety and spawn settings.
    /// </summary>
    [System.Serializable]
    public class PlayerSettings
    {
        [Header("Player Spawn")]
        public bool autoPlacePlayerOnStart = true;
        
        [Tooltip("When the game starts, player.y will be set to (terrainHeight + spawnHeightOffset) at the current (x,z).")]
        public float spawnHeightOffset = 5.0f;

        [Header("Player Safety")]
        public bool enableSafety = true;
        public float safetyHeightOffset = 2.0f;
        public float groundCheckDistance = 0.5f;
        
        [Tooltip("If player falls below this Y (local Unity space), force-teleport back above terrain even if 'jumping'.")]
        public float hardVoidY = -500f;
        
        [Tooltip("Teleport player back when they are below (terrainHeight - this value), unless they are actively jumping up.")]
        public float recoverBelowTerrain = 5.0f;
    }
}

