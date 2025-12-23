using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System;

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

    [Header("Player Safety")]
    [SerializeField] private bool enableSafety = true;
    [SerializeField] private float safetyHeightOffset = 2.0f;
    [SerializeField] private float groundCheckDistance = 0.5f;

    [Header("Terrain Settings")]
    [SerializeField] private int resolution = 129; 
    [SerializeField] private float heightMultiplier = 30f;
    [SerializeField] private float noiseScale = 0.005f;
    [SerializeField] private int octaves = 6;
    [SerializeField] private float persistence = 0.5f;
    [SerializeField] private float lacunarity = 2f;
    
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
    
    [Header("GPU Resources")]
    [Tooltip("REQUIRED: Assign TerrainGen.compute from Assets/Resources/Shaders/ folder directly in Inspector. This ensures it's included in builds.")]
    [SerializeField] private ComputeShader terrainComputeShader;
    [SerializeField] private Shader proceduralTerrainShader;

    // State
    // We use string keys "X_Y" for long coordinates support
    private Dictionary<string, ChunkData> loadedChunks = new Dictionary<string, ChunkData>();
    private Material terrainMaterial;

    // Native "long + float" world tracking:
    // - worldChunkOriginX/Y: absolute chunk coordinate for local chunk 0_0.
    // - Unity Transforms stay small (float) to avoid farland.
    // - Absolute world location is expressed as (worldChunkOrigin + localChunk) in long.
    private long worldChunkOriginX = 0;
    private long worldChunkOriginY = 0;

    private class ChunkData
    {
        public GameObject gameObject;
        public bool isReady;
    }

    private long _runtimeChunkX;
    private long _runtimeChunkY;

    private static void SetLongAsUInt2(ComputeShader cs, string loName, string hiName, long value)
    {
        // Preserve two's complement bit pattern so negative coords stay deterministic.
        ulong u = unchecked((ulong)value);
        uint lo = (uint)u;
        uint hi = (uint)(u >> 32);
        cs.SetInt(loName, unchecked((int)lo));
        cs.SetInt(hiName, unchecked((int)hi));
    }

    private int ComputeNoiseShift(float scale)
    {
        // Convert the old "world space scale" into a stable, power-of-two cell size in vertex-grid units.
        // cellSizeWorld ≈ 1/scale
        // stepWorld = chunkSize / (resolution-1)
        // cellSizeVerts ≈ cellSizeWorld / stepWorld
        if (scale <= 0f) return 8;

        float stepWorld = chunkSize / (float)(resolution - 1);
        float cellSizeVerts = (1f / scale) / stepWorld;
        if (cellSizeVerts <= 1f) return 0;

        int shift = Mathf.RoundToInt(Mathf.Log(cellSizeVerts, 2f));
        // Shader assumes [0..30] (uses 1u<<shift)
        return Mathf.Clamp(shift, 0, 30);
    }

    private void Start()
    {
        if (player == null)
        {
             GameObject p = GameObject.FindGameObjectWithTag("Player");
             if (p != null) player = p.transform;
        }

        if (seed == 0) seed = UnityEngine.Random.Range(-10000, 10000);
        
        InitializeMaterials();

        // Verify compute shader is assigned (required for terrain generation)
        if (terrainComputeShader == null)
        {
            Debug.LogError("Terrain Compute Shader is not assigned! " +
                "Please assign 'TerrainGen.compute' from Assets/Resources/Shaders/ in the Inspector. " +
                "Terrain generation will not work without this.");
            enabled = false;
            return;
        }
        
        // Verify compute shader has required kernels
        int kernelHeight = terrainComputeShader.FindKernel("GenerateHeightmap");
        if (kernelHeight < 0)
        {
            Debug.LogError("TerrainGen compute shader is missing required kernel 'GenerateHeightmap'! " +
                "The shader may not have compiled correctly for the target platform.");
            enabled = false;
            return;
        }
        
        // Allow setting a starting chunk from Inspector (current_x/current_y).
        // During gameplay these are still realtime values.
        TeleportToChunk(current_x, current_y);
    }

    private void OnValidate()
    {
        // Let the user edit current_x/current_y in Inspector while playing to teleport instantly.
        if (!Application.isPlaying) return;
        if (player == null) return;

        if (current_x != _runtimeChunkX || current_y != _runtimeChunkY)
        {
            TeleportToChunk(current_x, current_y);
        }
    }
    
    private void InitializeMaterials()
    {
        // Terrain Fallback Logic
        if (proceduralTerrainShader == null) proceduralTerrainShader = Shader.Find("Custom/ProceduralTerrain");
        
        // If still null or not supported, fallback to Standard
        bool useFallback = (proceduralTerrainShader == null);
        if (!useFallback && !proceduralTerrainShader.isSupported) useFallback = true;
        
        if (useFallback)
        {
            Debug.LogWarning("Custom Shader missing or not supported. Falling back to Standard.");
            proceduralTerrainShader = Shader.Find("Standard");
        }
        
        terrainMaterial = new Material(proceduralTerrainShader);
        if (useFallback) terrainMaterial.color = new Color(0.4f, 0.6f, 0.4f); // Green
        
        // Set shader properties
        UpdateMaterialProperties();
    }
    
    private void UpdateMaterialProperties()
    {
        if (terrainMaterial == null) return;
        
        // Set height multiplier
        terrainMaterial.SetFloat("_HeightMultiplier", heightMultiplier);
        
        // Set height thresholds
        terrainMaterial.SetFloat("_WaterLevel", waterLevel);
        terrainMaterial.SetFloat("_BeachLevel", beachLevel);
        terrainMaterial.SetFloat("_GrassLevel", grassLevel);
        terrainMaterial.SetFloat("_RockLevel", rockLevel);
        terrainMaterial.SetFloat("_SnowLevel", snowLevel);
    }

    private void Update()
    {
        if (player == null) return;

        // 1. World Shift Check
        if (Mathf.Abs(player.position.x) > floatingOriginThreshold || Mathf.Abs(player.position.z) > floatingOriginThreshold)
        {
            ShiftWorldOrigin();
        }

        // 2. Calculate Absolute Chunk Coordinate (native long + float local)
        int localChunkX = Mathf.FloorToInt(player.position.x / chunkSize);
        int localChunkY = Mathf.FloorToInt(player.position.z / chunkSize);
        long pX = worldChunkOriginX + localChunkX;
        long pZ = worldChunkOriginY + localChunkY;

        float inChunkX = player.position.x - (localChunkX * chunkSize);
        float inChunkZ = player.position.z - (localChunkY * chunkSize);
        
        // Realtime current chunk (always)
        _runtimeChunkX = pX;
        _runtimeChunkY = pZ;
        current_x = pX;
        current_y = pZ;
        _debugLocalPos = player.position;

        // 3. Update Chunks if changed
        UpdateChunks(pX, pZ);
        
        // 4. Safety
        if (enableSafety) UpdatePlayerSafety(pX, pZ, inChunkX, inChunkZ);
    }
    
    private void ShiftWorldOrigin()
    {
        // Shift by whole chunks so origin stays chunk-aligned (no doubles needed).
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

        // Update absolute chunk origin (native long)
        worldChunkOriginX += dxChunks;
        worldChunkOriginY += dzChunks;
    }

    private void UpdateChunks(long centerChunkX, long centerChunkY)
    {
        // Identify active chunks
        HashSet<string> activeKeys = new HashSet<string>();
        
        for (int x = -renderDistance; x <= renderDistance; x++)
        {
            for (int y = -renderDistance; y <= renderDistance; y++)
            {
                long cx = centerChunkX + x;
                long cy = centerChunkY + y;
                activeKeys.Add($"{cx}_{cy}");
            }
        }

        // Unload unused
        List<string> toRemove = new List<string>();
        foreach (var key in loadedChunks.Keys)
        {
            if (!activeKeys.Contains(key)) toRemove.Add(key);
        }
        foreach (var key in toRemove) UnloadChunk(key);

        // Load new
        foreach (var key in activeKeys)
        {
            if (!loadedChunks.ContainsKey(key))
            {
                // Parse key back to coordinates
                string[] parts = key.Split('_');
                long cx = long.Parse(parts[0]);
                long cy = long.Parse(parts[1]);
                CreateChunk(cx, cy, key);
            }
        }
    }
    
    private void UpdateChunksImmediate()
    {
        if (player == null) return;
        int localChunkX = Mathf.FloorToInt(player.position.x / chunkSize);
        int localChunkY = Mathf.FloorToInt(player.position.z / chunkSize);
        long pX = worldChunkOriginX + localChunkX;
        long pZ = worldChunkOriginY + localChunkY;
        
        _runtimeChunkX = pX;
        _runtimeChunkY = pZ;
        current_x = pX; 
        current_y = pZ;
        
        UpdateChunks(pX, pZ);
    }

    private void ClearAllChunks()
    {
        foreach (var kvp in loadedChunks)
        {
            if (kvp.Value.gameObject != null) Destroy(kvp.Value.gameObject);
        }
        loadedChunks.Clear();
    }

    /// <summary>
    /// Teleport the *virtual world* so the player is now in the specified chunk (64-bit safe).
    /// This keeps Unity Transform positions near 0 (no float farland), while chunk coords remain long.
    /// </summary>
    public void TeleportToChunk(long targetChunkX, long targetChunkY)
    {
        if (player == null) return;

        // Reset terrain around new location
        ClearAllChunks();

        // Native long+float teleport: set the chunk-origin so current local chunk becomes the target.
        int localChunkX = Mathf.FloorToInt(player.position.x / chunkSize);
        int localChunkY = Mathf.FloorToInt(player.position.z / chunkSize);
        worldChunkOriginX = targetChunkX - localChunkX;
        worldChunkOriginY = targetChunkY - localChunkY;

        // Keep debug/current fields aligned
        current_x = targetChunkX;
        current_y = targetChunkY;

        UpdateChunksImmediate();
    }

    /// <summary>
    /// Teleport using the currently set current_x/current_y (useful when editing in Inspector).
    /// </summary>
    [ContextMenu("Terrain/Teleport To Current Chunk")]
    public void TeleportToCurrentChunk()
    {
        TeleportToChunk(current_x, current_y);
    }

    private void UnloadChunk(string key)
    {
        if (loadedChunks.TryGetValue(key, out ChunkData data))
        {
            if (data.gameObject != null) Destroy(data.gameObject);
            loadedChunks.Remove(key);
        }
    }
    
    /// <summary>
    /// Exits/unloads the chunk at the current x,y position
    /// </summary>
    [ContextMenu("Terrain/Exit Current Chunk")]
    public void ExitCurrentChunk()
    {
        string key = $"{current_x}_{current_y}";
        if (loadedChunks.ContainsKey(key))
        {
            UnloadChunk(key);
        }
    }
    
    /// <summary>
    /// Exits/unloads a specific chunk by coordinates
    /// </summary>
    public void ExitChunk(long x, long y)
    {
        string key = $"{x}_{y}";
        if (loadedChunks.ContainsKey(key))
        {
            UnloadChunk(key);
        }
    }

    private void CreateChunk(long cx, long cy, string key)
    {
        if (terrainComputeShader == null)
        {
            Debug.LogWarning($"Cannot create chunk {cx}_{cy}: Terrain Compute Shader is not assigned!");
            return;
        }

        GameObject chunkObj = new GameObject($"Chunk_{cx}_{cy}");
        
        // Local position is based on relative chunk delta from the chunk-origin.
        // Delta is always small (renderDistance) so floats are safe.
        long relX = cx - worldChunkOriginX;
        long relY = cy - worldChunkOriginY;
        Vector3 localPos = new Vector3(relX * chunkSize, 0, relY * chunkSize);
        
        chunkObj.transform.position = localPos;
        chunkObj.transform.parent = transform;
        chunkObj.layer = LayerMask.NameToLayer("Default");

        ChunkData data = new ChunkData { gameObject = chunkObj, isReady = false };
        loadedChunks[key] = data;

        GenerateChunkGPU(cx, cy, data);
    }

    private void GenerateChunkGPU(long cx, long cy, ChunkData data)
    {
        // Noise Generation uses Absolute Coordinates (cx, cy) directly!
        // No virtual offsets needed, cx/cy ARE the virtual offsets.
        
        int kernelHeight = terrainComputeShader.FindKernel("GenerateHeightmap");
        int kernelErode = terrainComputeShader.FindKernel("ErodeHeightmap");
        int kernelSmooth = terrainComputeShader.FindKernel("SmoothHeightmap");
        int kernelBiome = terrainComputeShader.FindKernel("GenerateBiomeMap");
        int kernelNormal = terrainComputeShader.FindKernel("CalculateNormals");
        int kernelMesh = terrainComputeShader.FindKernel("GenerateMesh");
        
        // Verify all required kernels exist
        if (kernelHeight < 0 || kernelErode < 0 || kernelSmooth < 0 || kernelBiome < 0 || 
            kernelNormal < 0 || kernelMesh < 0)
        {
            Debug.LogError($"Compute shader kernels not found for chunk {cx}_{cy}! " +
                $"Kernels: Height={kernelHeight}, Erode={kernelErode}, Smooth={kernelSmooth}, " +
                $"Biome={kernelBiome}, Normal={kernelNormal}, Mesh={kernelMesh}. " +
                "The compute shader may not have compiled correctly for the target platform.");
            return;
        }
        
        // Buffers
        int vertCount = resolution * resolution;
        
        // Heightmap ping-pong for post process (erosion/smoothing) - avoids read/write hazards
        RenderTexture heightMapA = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat);
        heightMapA.enableRandomWrite = true;
        heightMapA.Create();

        RenderTexture heightMapB = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat);
        heightMapB.enableRandomWrite = true;
        heightMapB.Create();
        
        RenderTexture biomeMap = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat);
        biomeMap.enableRandomWrite = true;
        biomeMap.Create();

        ComputeBuffer vertBuffer = new ComputeBuffer(vertCount, sizeof(float) * 3);
        ComputeBuffer uvBuffer = new ComputeBuffer(vertCount, sizeof(float) * 2);
        ComputeBuffer normalBuffer = new ComputeBuffer(vertCount, sizeof(float) * 3);
        ComputeBuffer triBuffer = new ComputeBuffer((resolution - 1) * (resolution - 1) * 6, sizeof(int));

        // Parameters - Pass doubles as floats? 
        // Compute shader only supports float. 
        // Wait, for Perlin Noise at 1,000,000, precision issues occur with float.
        // Standard Perlin uses (float x, float y).
        // Solution: Modulo or reset origin for noise?
        // Infinite Perlin noise usually requires doubles or origin shifts.
        // For now, we just pass the large float. Floating Origin handles rendering precision.
        // But Noise precision will degrade after ~100k units.
        // Fix: Use a hash-based or tiled noise where (x,y) are hashed integers?
        // Current implementation is `float2 worldPos = float2(chunkX * chunkSize, ...)`.
        // This will jitter visually at 100k+.
        // However, for this task, we assume the user accepts noise limits or we'd need a double-precision noise library.
        // Let's rely on standard float behavior for now, it's "Infinite" enough for most games.
        
        terrainComputeShader.SetFloat("chunkSize", chunkSize);
        terrainComputeShader.SetInt("resolution", resolution);
        terrainComputeShader.SetFloat("heightMultiplier", heightMultiplier);
        terrainComputeShader.SetInt("octaves", octaves);
        terrainComputeShader.SetFloat("persistence", persistence);
        terrainComputeShader.SetFloat("lacunarity", lacunarity);
        terrainComputeShader.SetInt("seed", seed);

        // Stable-at-infinity noise configuration (no float world coordinates in shader)
        SetLongAsUInt2(terrainComputeShader, "chunkXLo", "chunkXHi", cx);
        SetLongAsUInt2(terrainComputeShader, "chunkYLo", "chunkYHi", cy);
        terrainComputeShader.SetInt("baseNoiseShift", ComputeNoiseShift(noiseScale));
        terrainComputeShader.SetInt("moistureNoiseShift", ComputeNoiseShift(0.002f));
        terrainComputeShader.SetInt("temperatureNoiseShift", ComputeNoiseShift(0.003f));
        terrainComputeShader.SetFloat("mountainStrength", mountainStrength);
        terrainComputeShader.SetFloat("plainStrength", plainStrength);
        terrainComputeShader.SetFloat("erosionStrength", erosionStrength);
        terrainComputeShader.SetFloat("domainWarpStrength", domainWarpStrength);

        // Post-process tuning: drive iterations/thresholds from erosionStrength so user doesn't need new knobs
        int erosionIterations = Mathf.RoundToInt(Mathf.Lerp(0f, 12f, Mathf.Clamp01(erosionStrength)));
        int smoothIterations = Mathf.RoundToInt(Mathf.Lerp(0f, 2f, Mathf.Clamp01(erosionStrength)));
        float talus = Mathf.Lerp(heightMultiplier * 0.02f, heightMultiplier * 0.004f, Mathf.Clamp01(erosionStrength));
        float amount = Mathf.Lerp(0f, 0.35f, Mathf.Clamp01(erosionStrength));
        float smooth = Mathf.Lerp(0f, 0.65f, Mathf.Clamp01(erosionStrength));
        terrainComputeShader.SetFloat("erosionTalus", talus);
        terrainComputeShader.SetFloat("erosionAmount", amount);
        terrainComputeShader.SetFloat("smoothStrength", smooth);

        // Dispatch
        int groups = Mathf.CeilToInt(resolution / 8f);

        try
        {
            // 1) Height
            terrainComputeShader.SetTexture(kernelHeight, "HeightMap", heightMapA);
            terrainComputeShader.Dispatch(kernelHeight, groups, groups, 1);

            // 2) Erosion (ping-pong)
            RenderTexture src = heightMapA;
            RenderTexture dst = heightMapB;
            for (int i = 0; i < erosionIterations; i++)
            {
                terrainComputeShader.SetTexture(kernelErode, "HeightMapIn", src);
                terrainComputeShader.SetTexture(kernelErode, "HeightMapOut", dst);
                terrainComputeShader.Dispatch(kernelErode, groups, groups, 1);
                RenderTexture tmp = src;
                src = dst;
                dst = tmp;
            }

            // 3) Small blur to remove remaining needle peaks while preserving features
            for (int i = 0; i < smoothIterations; i++)
            {
                terrainComputeShader.SetTexture(kernelSmooth, "HeightMapIn", src);
                terrainComputeShader.SetTexture(kernelSmooth, "HeightMapOut", dst);
                terrainComputeShader.Dispatch(kernelSmooth, groups, groups, 1);
                RenderTexture tmp = src;
                src = dst;
                dst = tmp;
            }

            // 4) Biome map from final height
            terrainComputeShader.SetTexture(kernelBiome, "HeightMap", src);
            terrainComputeShader.SetTexture(kernelBiome, "BiomeMap", biomeMap);
            terrainComputeShader.Dispatch(kernelBiome, groups, groups, 1);

            // 5) Normals/Mesh from final height
            terrainComputeShader.SetTexture(kernelNormal, "HeightMap", src);
            terrainComputeShader.SetBuffer(kernelNormal, "Normals", normalBuffer);
            terrainComputeShader.Dispatch(kernelNormal, groups, groups, 1);

            terrainComputeShader.SetTexture(kernelMesh, "HeightMap", src);
            terrainComputeShader.SetBuffer(kernelMesh, "Vertices", vertBuffer);
            terrainComputeShader.SetBuffer(kernelMesh, "UVs", uvBuffer);
            terrainComputeShader.SetBuffer(kernelMesh, "Triangles", triBuffer);
            terrainComputeShader.Dispatch(kernelMesh, groups, groups, 1);

            // Readback
            Vector3[] vertices = new Vector3[vertCount];
            vertBuffer.GetData(vertices);
            
            Vector2[] uvs = new Vector2[vertCount];
            uvBuffer.GetData(uvs);
            
            Vector3[] normals = new Vector3[vertCount];
            normalBuffer.GetData(normals);
            
            int[] triangles = new int[(resolution - 1) * (resolution - 1) * 6];
            triBuffer.GetData(triangles);

            // Validate GPU output to avoid spamming Mesh errors if a kernel failed to dispatch/compile
            for (int i = 0; i < Mathf.Min(vertices.Length, 256); i++)
            {
                Vector3 v = vertices[i];
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                    float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                {
                    Debug.LogError($"GPU mesh data invalid (NaN/Inf) for chunk {cx}_{cy}. Skipping mesh build. Check compute shader compile errors in Console.");
                    return;
                }
            }

            for (int i = 0; i < triangles.Length; i++)
            {
                int t = triangles[i];
                if ((uint)t >= (uint)vertCount)
                {
                    Debug.LogError($"GPU triangle index out of bounds for chunk {cx}_{cy} (tri[{i}]={t}, vertCount={vertCount}). Skipping mesh build. Check compute shader compile errors in Console.");
                    return;
                }
            }

            Mesh mesh = new Mesh();
            if (vertCount > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            
            MeshFilter mf = data.gameObject.AddComponent<MeshFilter>();
            MeshRenderer mr = data.gameObject.AddComponent<MeshRenderer>();
            MeshCollider mc = data.gameObject.AddComponent<MeshCollider>();
            
            mf.mesh = mesh;
            mr.material = terrainMaterial;
            // Update material properties
            UpdateMaterialProperties();
            mc.sharedMesh = mesh;
            
            data.isReady = true;
        }
        finally
        {
            // Always release GPU resources
            heightMapA.Release();
            heightMapB.Release();
            biomeMap.Release();
            vertBuffer.Release();
            uvBuffer.Release();
            normalBuffer.Release();
            triBuffer.Release();
        }
    }
    
    // CPU Noise for Safety
    private void UpdatePlayerSafety(long playerChunkX, long playerChunkY, float inChunkX, float inChunkZ)
    {
        // Get ACTUAL terrain height using raycast from above
        // Cast from high above player position down to get real terrain height
        float raycastStartHeight = Mathf.Max(player.position.y + 100f, 200f); // Start raycast from high above
        Vector3 raycastStart = new Vector3(player.position.x, raycastStartHeight, player.position.z);
        
        RaycastHit hit;
        float actualTerrainHeight = float.MinValue;
        bool hasTerrainHit = Physics.Raycast(raycastStart, Vector3.down, out hit, 500f);
        
        if (hasTerrainHit)
        {
            actualTerrainHeight = hit.point.y;
        }
        else
        {
            // Fallback to approximated height if raycast doesn't hit (terrain might not be loaded)
            actualTerrainHeight = GetTerrainHeightCPU(playerChunkX, playerChunkY, inChunkX, inChunkZ);
        }
        
        // Get player velocity to check if jumping
        Rigidbody rb = player.GetComponent<Rigidbody>();
        bool isJumping = rb != null && rb.linearVelocity.y > 2.0f; // Only consider significant upward velocity as jumping
        
        // Simple check: if player is below terrain by a significant amount, teleport
        // But don't teleport if player is actively jumping (velocity up > 2)
        float safetyThreshold = actualTerrainHeight - 15.0f;
        
        if (!isJumping && player.position.y < safetyThreshold)
        {
            // Player is below terrain and not jumping - teleport to safety
            Vector3 newPos = player.position;
            newPos.y = actualTerrainHeight + safetyHeightOffset + 5.0f; 
            player.position = newPos;
            
            if (rb != null) rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0, rb.linearVelocity.z);
        }
    }

    // CPU approximation for safety checks (stable at infinity; no float world coordinate dependence)
    private static uint Hash32(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    private static uint HashCombine(uint h, uint v)
    {
        unchecked
        {
            return Hash32(h ^ (v + 0x9e3779b9u + (h << 6) + (h >> 2)));
        }
    }

    private static float HashTo01(uint h) => (h & 0x00FFFFFFu) / 16777216.0f;

    private static float Hash2D64(ulong x, ulong y, uint salt)
    {
        uint h = Hash32(salt);
        h = HashCombine(h, (uint)x);
        h = HashCombine(h, (uint)(x >> 32));
        h = HashCombine(h, (uint)y);
        h = HashCombine(h, (uint)(y >> 32));
        return HashTo01(h);
    }

    private static float ValueNoise64(ulong gx, ulong gz, int shift, uint salt)
    {
        shift = Mathf.Clamp(shift, 0, 30);
        if (shift == 0) return Hash2D64(gx, gz, salt);

        ulong mask = (1ul << shift) - 1ul;
        float fx = (gx & mask) / (float)(1ul << shift);
        float fz = (gz & mask) / (float)(1ul << shift);
        float ux = fx * fx * (3f - 2f * fx);
        float uz = fz * fz * (3f - 2f * fz);

        ulong cellX = gx >> shift;
        ulong cellZ = gz >> shift;

        float a = Hash2D64(cellX, cellZ, salt);
        float b = Hash2D64(cellX + 1ul, cellZ, salt);
        float c = Hash2D64(cellX, cellZ + 1ul, salt);
        float d = Hash2D64(cellX + 1ul, cellZ + 1ul, salt);

        float ab = Mathf.Lerp(a, b, ux);
        float cd = Mathf.Lerp(c, d, ux);
        return Mathf.Lerp(ab, cd, uz);
    }

    private static float Fbm64(ulong gx, ulong gz, int baseShift, int oct, float pers, uint saltBase)
    {
        float value = 0f;
        float amplitude = 0.5f;
        float maxValue = 0f;

        for (int i = 0; i < oct; i++)
        {
            int s = Mathf.Max(0, baseShift - i);
            value += amplitude * ValueNoise64(gx, gz, s, saltBase + (uint)(i * 1013));
            maxValue += amplitude;
            amplitude *= pers;
        }

        return value / Mathf.Max(maxValue, 1e-6f);
    }

    private float GetTerrainHeightCPU(long chunkX, long chunkY, float inChunkX, float inChunkZ)
    {
        // Match the shader's integer vertex-grid space (approximation).
        int vertsPerChunk = resolution - 1;
        float stepWorld = chunkSize / (float)vertsPerChunk;

        int vx = Mathf.Clamp(Mathf.RoundToInt(inChunkX / stepWorld), 0, vertsPerChunk);
        int vz = Mathf.Clamp(Mathf.RoundToInt(inChunkZ / stepWorld), 0, vertsPerChunk);

        ulong gx = unchecked((ulong)chunkX) * (ulong)vertsPerChunk + (ulong)vx;
        ulong gz = unchecked((ulong)chunkY) * (ulong)vertsPerChunk + (ulong)vz;

        uint s0 = Hash32((uint)seed ^ 0xA341316Cu);
        float continents = Fbm64(gx, gz, ComputeNoiseShift(noiseScale) + 4, 3, 0.5f, s0);

        float height01 = (continents < 0.30f) ? (continents * 0.80f) : continents;
        if (continents > 0.6f) height01 += 0.2f * mountainStrength;

        return Mathf.Clamp01(height01) * heightMultiplier;
    }

    private void OnDestroy()
    {
        // Cleanup is handled by UnloadChunk when chunks are destroyed
    }
}
