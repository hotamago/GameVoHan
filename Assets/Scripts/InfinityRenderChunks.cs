using UnityEngine;
using System.Collections.Generic;

public class InfinityRenderChunks : MonoBehaviour
{
    [Header("Chunk Settings")]
    [SerializeField] private int chunkSize = 100; // Kích thước mỗi chunk (Unity units)
    [SerializeField] private int renderDistance = 3; // Số chunks render xung quanh player
    [SerializeField] private int seed = 0; // Seed cho terrain generation
    
    [Header("References")]
    [SerializeField] private Transform player; // Transform của player
    [SerializeField] private GameObject chunkPrefab; // Prefab tile terrain object cho chunk
    
    [Header("Terrain Generation Settings")]
    [SerializeField] private int terrainResolution = 129; // Resolution cho heightmap (nên là 2^n + 1)
    [SerializeField] private float heightMultiplier = 20f; // Độ cao tối đa của terrain
    [SerializeField] private float noiseScale = 0.05f; // Scale cho Perlin noise
    [SerializeField] private int octaves = 4; // Số octaves cho fractal noise
    [SerializeField] private float persistence = 0.5f; // Persistence cho fractal noise
    [SerializeField] private float lacunarity = 2f; // Lacunarity cho fractal noise
    
    [Header("Biome Settings")]
    [SerializeField] private float biomeScale = 0.01f; // Scale cho biome distribution
    [SerializeField] private float temperatureNoiseScale = 0.02f;
    [SerializeField] private float moistureNoiseScale = 0.02f;
    
    [Header("Object Spawning")]
    [SerializeField] private bool enableObjectSpawning = true;
    [SerializeField] private int treesPerChunk = 20;
    [SerializeField] private int rocksPerChunk = 15;
    [SerializeField] private int grassPatchesPerChunk = 50;
    [SerializeField] private GameObject[] treePrefabs; // Array các tree prefabs
    [SerializeField] private GameObject[] rockPrefabs; // Array các rock prefabs
    [SerializeField] private GameObject[] grassPrefabs; // Array các grass/foliage prefabs
    [SerializeField] private GameObject[] flowerPrefabs; // Array các flower prefabs
    [SerializeField] private float objectSpawnRadius = 5f; // Khoảng cách tối thiểu giữa các objects
    
    // Chunk coordinates sử dụng int64 để tránh tràn số
    private long currentChunkX = 0;
    private long currentChunkY = 0;
    
    // Dictionary lưu trữ các chunks đã được tạo
    private Dictionary<string, ChunkData> loadedChunks = new Dictionary<string, ChunkData>();
    
    // World offset để dịch chuyển toàn bộ thế giới khi cần
    private Vector3 worldOffset = Vector3.zero;
    
    // Enum cho các loại biome
    private enum BiomeType
    {
        Ocean,
        Beach,
        Grassland,
        Forest,
        Desert,
        Mountain,
        Snow,
        Swamp
    }
    
    // Cache cho object spawning để tránh spawn quá gần nhau
    private Dictionary<string, List<Vector3>> chunkSpawnedObjects = new Dictionary<string, List<Vector3>>();
    
    // Lớp để lưu trữ thông tin chunk
    private class ChunkData
    {
        public long chunkX;
        public long chunkY;
        public GameObject chunkObject;
        public Vector3 worldPosition;
        
        public ChunkData(long x, long y, GameObject obj, Vector3 pos)
        {
            chunkX = x;
            chunkY = y;
            chunkObject = obj;
            worldPosition = pos;
        }
    }
    
    void Start()
    {
        if (player == null)
        {
            // Tự động tìm player nếu không được gán
            GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
            if (playerObj != null)
            {
                player = playerObj.transform;
            }
            else
            {
                Debug.LogError("InfinityRenderChunks: Không tìm thấy Player! Vui lòng gán Transform của player.");
                return;
            }
        }
        
        // Khởi tạo seed
        if (seed == 0)
        {
            seed = Random.Range(int.MinValue, int.MaxValue);
        }
        Random.InitState(seed);
        
        // Load chunks ban đầu
        UpdateChunks();
    }
    
    void Update()
    {
        if (player == null) return;
        
        // Tính toán chunk hiện tại của player
        Vector3 playerWorldPos = player.position + worldOffset;
        long newChunkX = WorldToChunkCoord(playerWorldPos.x);
        long newChunkY = WorldToChunkCoord(playerWorldPos.z);
        
        // Kiểm tra xem player có di chuyển sang chunk mới không
        if (newChunkX != currentChunkX || newChunkY != currentChunkY)
        {
            // Player đã di chuyển sang chunk mới
            long deltaX = newChunkX - currentChunkX;
            long deltaY = newChunkY - currentChunkY;
            
            // Dịch chuyển world offset để chunk mới có gốc tại (0,0)
            worldOffset.x -= deltaX * chunkSize;
            worldOffset.z -= deltaY * chunkSize;
            
            // Cập nhật chunk hiện tại
            currentChunkX = newChunkX;
            currentChunkY = newChunkY;
            
            // Dịch chuyển tất cả các chunks đã load
            ShiftAllChunks(-deltaX * chunkSize, -deltaY * chunkSize);
            
            // Cập nhật chunks
            UpdateChunks();
        }
    }
    
    // Chuyển đổi tọa độ world sang chunk coordinate (int64)
    private long WorldToChunkCoord(float worldPos)
    {
        if (worldPos < 0)
        {
            return (long)((worldPos - chunkSize + 1) / chunkSize);
        }
        return (long)(worldPos / chunkSize);
    }
    
    // Chuyển đổi chunk coordinate sang tọa độ world (gốc của chunk)
    private Vector3 ChunkToWorldPosition(long chunkX, long chunkY)
    {
        return new Vector3(
            chunkX * chunkSize - worldOffset.x,
            0,
            chunkY * chunkSize - worldOffset.z
        );
    }
    
    // Dịch chuyển tất cả chunks khi player di chuyển sang chunk mới
    private void ShiftAllChunks(float deltaX, float deltaZ)
    {
        foreach (var chunk in loadedChunks.Values)
        {
            if (chunk.chunkObject != null)
            {
                chunk.chunkObject.transform.position += new Vector3(deltaX, 0, deltaZ);
                chunk.worldPosition = chunk.chunkObject.transform.position;
            }
        }
    }
    
    // Cập nhật danh sách chunks cần render
    private void UpdateChunks()
    {
        // Tạo set các chunks cần load
        HashSet<string> chunksToLoad = new HashSet<string>();
        
        for (long x = currentChunkX - renderDistance; x <= currentChunkX + renderDistance; x++)
        {
            for (long y = currentChunkY - renderDistance; y <= currentChunkY + renderDistance; y++)
            {
                string chunkKey = GetChunkKey(x, y);
                chunksToLoad.Add(chunkKey);
                
                // Nếu chunk chưa được load, tạo mới
                if (!loadedChunks.ContainsKey(chunkKey))
                {
                    CreateChunk(x, y);
                }
            }
        }
        
        // Unload các chunks nằm ngoài render distance
        List<string> chunksToUnload = new List<string>();
        foreach (var kvp in loadedChunks)
        {
            if (!chunksToLoad.Contains(kvp.Key))
            {
                chunksToUnload.Add(kvp.Key);
            }
        }
        
        foreach (string key in chunksToUnload)
        {
            UnloadChunk(key);
        }
    }
    
    // Tạo chunk mới
    private void CreateChunk(long chunkX, long chunkY)
    {
        Vector3 chunkWorldPos = ChunkToWorldPosition(chunkX, chunkY);
        
        GameObject chunkObj;
        if (chunkPrefab != null)
        {
            // Sử dụng chunkPrefab như tile terrain object
            chunkObj = Instantiate(chunkPrefab, chunkWorldPos, Quaternion.identity, transform);
            chunkObj.name = $"Chunk_{chunkX}_{chunkY}";
        }
        else
        {
            // Tạo chunk đơn giản nếu không có prefab
            chunkObj = new GameObject($"Chunk_{chunkX}_{chunkY}");
            chunkObj.transform.position = chunkWorldPos;
            chunkObj.transform.parent = transform;
            
            // Thêm mesh renderer đơn giản
            MeshFilter meshFilter = chunkObj.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = chunkObj.AddComponent<MeshRenderer>();
            
            // Generate mesh cho chunk (có thể customize)
            meshFilter.mesh = GenerateChunkMesh(chunkX, chunkY);
            
            // Material mặc định
            meshRenderer.material = new Material(Shader.Find("Standard"));
        }
        
        ChunkData chunkData = new ChunkData(chunkX, chunkY, chunkObj, chunkWorldPos);
        loadedChunks[GetChunkKey(chunkX, chunkY)] = chunkData;
        
        // Gọi hàm generate terrain dựa trên seed
        GenerateTerrainForChunk(chunkObj, chunkX, chunkY);
    }
    
    // Unload chunk
    private void UnloadChunk(string chunkKey)
    {
        if (loadedChunks.TryGetValue(chunkKey, out ChunkData chunkData))
        {
            if (chunkData.chunkObject != null)
            {
                Destroy(chunkData.chunkObject);
            }
            loadedChunks.Remove(chunkKey);
            
            // Clean up spawned objects cache
            if (chunkSpawnedObjects.ContainsKey(chunkKey))
            {
                chunkSpawnedObjects.Remove(chunkKey);
            }
        }
    }
    
    // Tạo key cho chunk từ tọa độ
    private string GetChunkKey(long x, long y)
    {
        return $"{x}_{y}";
    }
    
    // Generate mesh đơn giản cho chunk (có thể customize)
    private Mesh GenerateChunkMesh(long chunkX, long chunkY)
    {
        Mesh mesh = new Mesh();
        mesh.name = $"ChunkMesh_{chunkX}_{chunkY}";
        
        // Tạo plane đơn giản
        int resolution = 10;
        Vector3[] vertices = new Vector3[resolution * resolution];
        int[] triangles = new int[(resolution - 1) * (resolution - 1) * 6];
        Vector2[] uv = new Vector2[vertices.Length];
        
        float step = chunkSize / (float)(resolution - 1);
        
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                int index = z * resolution + x;
                float worldX = x * step;
                float worldZ = z * step;
                
                // Có thể thêm noise/heightmap ở đây
                float height = GetHeightAt(worldX, worldZ, chunkX, chunkY);
                
                vertices[index] = new Vector3(worldX, height, worldZ);
                uv[index] = new Vector2(x / (float)(resolution - 1), z / (float)(resolution - 1));
            }
        }
        
        int triIndex = 0;
        for (int z = 0; z < resolution - 1; z++)
        {
            for (int x = 0; x < resolution - 1; x++)
            {
                int i = z * resolution + x;
                
                triangles[triIndex] = i;
                triangles[triIndex + 1] = i + resolution;
                triangles[triIndex + 2] = i + 1;
                
                triangles[triIndex + 3] = i + 1;
                triangles[triIndex + 4] = i + resolution;
                triangles[triIndex + 5] = i + resolution + 1;
                
                triIndex += 6;
            }
        }
        
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uv;
        mesh.RecalculateNormals();
        
        return mesh;
    }
    
    // Lấy height tại vị trí (fallback cho GenerateChunkMesh cũ)
    private float GetHeightAt(float localX, float localZ, long chunkX, long chunkY)
    {
        // Tính world position
        float worldX = chunkX * chunkSize + localX;
        float worldZ = chunkY * chunkSize + localZ;
        
        // Lấy biome (không gây vòng lặp vì GetBiomeAt không gọi GetHeightAt nữa)
        BiomeType biome = GetBiomeAt(worldX, worldZ);
        
        // Sử dụng GetAdvancedHeightAt với biome
        return GetAdvancedHeightAt(localX, localZ, chunkX, chunkY, biome);
    }
    
    // Generate terrain cho chunk - Hệ thống terrain generation chuyên nghiệp
    private void GenerateTerrainForChunk(GameObject chunkObj, long chunkX, long chunkY)
    {
        string chunkKey = GetChunkKey(chunkX, chunkY);
        
        // Tính toán world position thực tế của chunk
        float worldX = chunkX * chunkSize;
        float worldZ = chunkY * chunkSize;
        
        // Xác định biome cho chunk này
        BiomeType biome = GetBiomeAt(worldX, worldZ);
        
        // Tạo container cho các objects được spawn
        Transform objectsContainer = null;
        if (enableObjectSpawning)
        {
            GameObject containerObj = new GameObject("Objects");
            containerObj.transform.SetParent(chunkObj.transform);
            containerObj.transform.localPosition = Vector3.zero;
            objectsContainer = containerObj.transform;
            chunkSpawnedObjects[chunkKey] = new List<Vector3>();
        }
        
        // Generate terrain mesh nếu chunk không có Terrain component
        Terrain terrain = chunkObj.GetComponent<Terrain>();
        if (terrain == null)
        {
            MeshFilter meshFilter = chunkObj.GetComponent<MeshFilter>();
            if (meshFilter != null)
            {
                meshFilter.mesh = GenerateAdvancedChunkMesh(chunkX, chunkY, biome);
                
                // Áp dụng material dựa trên biome
                MeshRenderer meshRenderer = chunkObj.GetComponent<MeshRenderer>();
                if (meshRenderer != null)
                {
                    meshRenderer.material = GetBiomeMaterial(biome);
                }
            }
        }
        else
        {
            // Nếu có Terrain component, generate heightmap
            GenerateTerrainHeightmap(terrain, chunkX, chunkY, biome);
            ApplyTerrainTextures(terrain, biome);
        }
        
        // Spawn objects dựa trên biome
        if (enableObjectSpawning && objectsContainer != null)
        {
            SpawnTerrainObjects(chunkObj, chunkX, chunkY, biome, objectsContainer);
        }
        
        // Thêm collider nếu chưa có
        if (chunkObj.GetComponent<Collider>() == null && chunkObj.GetComponent<TerrainCollider>() == null)
        {
            MeshCollider meshCollider = chunkObj.AddComponent<MeshCollider>();
            MeshFilter mf = chunkObj.GetComponent<MeshFilter>();
            if (mf != null && mf.mesh != null)
            {
                meshCollider.sharedMesh = mf.mesh;
            }
        }
    }
    
    // Tính height từ noise mà không cần biome (để tránh vòng lặp đệ quy)
    private float GetRawHeightFromNoise(float worldX, float worldZ)
    {
        // Fractal noise với nhiều octaves (giống GetAdvancedHeightAt nhưng không có biome adjustment)
        float height = 0f;
        float amplitude = 1f;
        float frequency = noiseScale;
        
        for (int i = 0; i < octaves; i++)
        {
            float sampleX = (worldX + seed * 1000f) * frequency;
            float sampleZ = (worldZ + seed * 1000f) * frequency;
            
            float noiseValue = Mathf.PerlinNoise(sampleX, sampleZ) * 2f - 1f;
            height += noiseValue * amplitude;
            
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        
        // Normalize height
        height = (height + 1f) * 0.5f;
        
        return height * heightMultiplier;
    }
    
    // Xác định biome tại vị trí
    private BiomeType GetBiomeAt(float worldX, float worldZ)
    {
        // Sử dụng noise để tạo temperature và moisture
        float tempNoise = Mathf.PerlinNoise(
            (worldX + seed * 1000f) * temperatureNoiseScale,
            (worldZ + seed * 1000f) * temperatureNoiseScale
        );
        
        float moistureNoise = Mathf.PerlinNoise(
            (worldX + seed * 2000f) * moistureNoiseScale,
            (worldZ + seed * 2000f) * moistureNoiseScale
        );
        
        // Biome distribution noise
        float biomeNoise = Mathf.PerlinNoise(
            (worldX + seed * 500f) * biomeScale,
            (worldZ + seed * 500f) * biomeScale
        );
        
        // Tính height trực tiếp từ noise (không gọi GetHeightAt để tránh vòng lặp)
        float height = GetRawHeightFromNoise(worldX, worldZ);
        float normalizedHeight = height / heightMultiplier;
        
        // Xác định biome dựa trên height, temperature, moisture
        if (normalizedHeight < 0.1f)
        {
            return BiomeType.Ocean;
        }
        else if (normalizedHeight < 0.15f)
        {
            return BiomeType.Beach;
        }
        else if (normalizedHeight > 0.7f)
        {
            if (tempNoise < 0.3f)
                return BiomeType.Snow;
            else
                return BiomeType.Mountain;
        }
        else if (moistureNoise < 0.2f && tempNoise > 0.6f)
        {
            return BiomeType.Desert;
        }
        else if (moistureNoise > 0.7f && tempNoise < 0.5f)
        {
            return BiomeType.Swamp;
        }
        else if (moistureNoise > 0.5f && tempNoise > 0.4f)
        {
            return BiomeType.Forest;
        }
        else
        {
            return BiomeType.Grassland;
        }
    }
    
    // Generate mesh nâng cao với height variation
    private Mesh GenerateAdvancedChunkMesh(long chunkX, long chunkY, BiomeType biome)
    {
        Mesh mesh = new Mesh();
        mesh.name = $"ChunkMesh_{chunkX}_{chunkY}";
        
        int resolution = terrainResolution;
        Vector3[] vertices = new Vector3[resolution * resolution];
        int[] triangles = new int[(resolution - 1) * (resolution - 1) * 6];
        Vector2[] uv = new Vector2[vertices.Length];
        Vector3[] normals = new Vector3[vertices.Length];
        
        float step = chunkSize / (float)(resolution - 1);
        
        // Generate vertices với height variation
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                int index = z * resolution + x;
                float localX = x * step;
                float localZ = z * step;
                
                float height = GetAdvancedHeightAt(localX, localZ, chunkX, chunkY, biome);
                
                vertices[index] = new Vector3(localX, height, localZ);
                uv[index] = new Vector2(x / (float)(resolution - 1), z / (float)(resolution - 1));
            }
        }
        
        // Generate triangles
        int triIndex = 0;
        for (int z = 0; z < resolution - 1; z++)
        {
            for (int x = 0; x < resolution - 1; x++)
            {
                int i = z * resolution + x;
                
                triangles[triIndex] = i;
                triangles[triIndex + 1] = i + resolution;
                triangles[triIndex + 2] = i + 1;
                
                triangles[triIndex + 3] = i + 1;
                triangles[triIndex + 4] = i + resolution;
                triangles[triIndex + 5] = i + resolution + 1;
                
                triIndex += 6;
            }
        }
        
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uv;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        
        return mesh;
    }
    
    // Lấy height nâng cao với fractal noise
    private float GetAdvancedHeightAt(float localX, float localZ, long chunkX, long chunkY, BiomeType biome)
    {
        float worldX = chunkX * chunkSize + localX;
        float worldZ = chunkY * chunkSize + localZ;
        
        // Fractal noise với nhiều octaves
        float height = 0f;
        float amplitude = 1f;
        float frequency = noiseScale;
        
        for (int i = 0; i < octaves; i++)
        {
            float sampleX = (worldX + seed * 1000f) * frequency;
            float sampleZ = (worldZ + seed * 1000f) * frequency;
            
            float noiseValue = Mathf.PerlinNoise(sampleX, sampleZ) * 2f - 1f;
            height += noiseValue * amplitude;
            
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        
        // Normalize height
        height = (height + 1f) * 0.5f;
        
        // Điều chỉnh height dựa trên biome
        switch (biome)
        {
            case BiomeType.Ocean:
                height *= 0.1f;
                break;
            case BiomeType.Beach:
                height = height * 0.15f + 0.1f;
                break;
            case BiomeType.Mountain:
                height = height * 0.5f + 0.5f;
                break;
            case BiomeType.Snow:
                height = height * 0.6f + 0.4f;
                break;
            case BiomeType.Desert:
                height = height * 0.2f + 0.15f;
                break;
            default:
                height = height * 0.3f + 0.15f;
                break;
        }
        
        return height * heightMultiplier;
    }
    
    // Generate heightmap cho Unity Terrain
    private void GenerateTerrainHeightmap(Terrain terrain, long chunkX, long chunkY, BiomeType biome)
    {
        TerrainData terrainData = terrain.terrainData;
        if (terrainData == null) return;
        
        int resolution = terrainData.heightmapResolution;
        float[,] heights = new float[resolution, resolution];
        
        float step = chunkSize / (float)(resolution - 1);
        
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                float localX = x * step;
                float localZ = z * step;
                float height = GetAdvancedHeightAt(localX, localZ, chunkX, chunkY, biome);
                heights[z, x] = height / heightMultiplier;
            }
        }
        
        terrainData.SetHeights(0, 0, heights);
    }
    
    // Áp dụng textures cho Terrain dựa trên biome
    private void ApplyTerrainTextures(Terrain terrain, BiomeType biome)
    {
        TerrainData terrainData = terrain.terrainData;
        if (terrainData == null) return;
        
        // Có thể thêm logic để set terrain textures/layers dựa trên biome
        // Đây là nơi bạn có thể customize textures cho từng biome
    }
    
    // Lấy material dựa trên biome
    private Material GetBiomeMaterial(BiomeType biome)
    {
        Material mat = new Material(Shader.Find("Standard"));
        
        // Set color dựa trên biome
        switch (biome)
        {
            case BiomeType.Ocean:
                mat.color = new Color(0.1f, 0.3f, 0.5f);
                break;
            case BiomeType.Beach:
                mat.color = new Color(0.9f, 0.85f, 0.7f);
                break;
            case BiomeType.Grassland:
                mat.color = new Color(0.4f, 0.7f, 0.3f);
                break;
            case BiomeType.Forest:
                mat.color = new Color(0.2f, 0.5f, 0.2f);
                break;
            case BiomeType.Desert:
                mat.color = new Color(0.9f, 0.8f, 0.6f);
                break;
            case BiomeType.Mountain:
                mat.color = new Color(0.5f, 0.5f, 0.5f);
                break;
            case BiomeType.Snow:
                mat.color = new Color(0.9f, 0.9f, 0.95f);
                break;
            case BiomeType.Swamp:
                mat.color = new Color(0.3f, 0.4f, 0.3f);
                break;
        }
        
        return mat;
    }
    
    // Spawn objects trên terrain
    private void SpawnTerrainObjects(GameObject chunkObj, long chunkX, long chunkY, BiomeType biome, Transform container)
    {
        string chunkKey = GetChunkKey(chunkX, chunkY);
        List<Vector3> spawnedPositions = chunkSpawnedObjects[chunkKey];
        
        // Spawn trees
        if (treePrefabs != null && treePrefabs.Length > 0)
        {
            int treeCount = GetObjectCountForBiome(biome, treesPerChunk, true);
            SpawnObjects(treePrefabs, treeCount, chunkX, chunkY, biome, container, spawnedPositions, 2f);
        }
        
        // Spawn rocks
        if (rockPrefabs != null && rockPrefabs.Length > 0)
        {
            int rockCount = GetObjectCountForBiome(biome, rocksPerChunk, false);
            SpawnObjects(rockPrefabs, rockCount, chunkX, chunkY, biome, container, spawnedPositions, 1.5f);
        }
        
        // Spawn grass/foliage
        if (grassPrefabs != null && grassPrefabs.Length > 0)
        {
            int grassCount = GetObjectCountForBiome(biome, grassPatchesPerChunk, true);
            SpawnObjects(grassPrefabs, grassCount, chunkX, chunkY, biome, container, spawnedPositions, 0.5f);
        }
        
        // Spawn flowers (chỉ ở một số biome)
        if (flowerPrefabs != null && flowerPrefabs.Length > 0 && 
            (biome == BiomeType.Grassland || biome == BiomeType.Forest))
        {
            int flowerCount = Random.Range(5, 15);
            SpawnObjects(flowerPrefabs, flowerCount, chunkX, chunkY, biome, container, spawnedPositions, 0.3f);
        }
    }
    
    // Spawn objects với kiểm tra khoảng cách
    private void SpawnObjects(GameObject[] prefabs, int count, long chunkX, long chunkY, 
        BiomeType biome, Transform container, List<Vector3> spawnedPositions, float minDistance)
    {
        if (prefabs == null || prefabs.Length == 0) return;
        
        int attempts = 0;
        int spawned = 0;
        int maxAttempts = count * 10; // Giới hạn số lần thử
        
        while (spawned < count && attempts < maxAttempts)
        {
            attempts++;
            
            // Random position trong chunk
            float x = Random.Range(5f, chunkSize - 5f);
            float z = Random.Range(5f, chunkSize - 5f);
            
            // Lấy height tại vị trí này
            float y = GetAdvancedHeightAt(x, z, chunkX, chunkY, biome);
            Vector3 spawnPos = new Vector3(x, y, z);
            
            // Kiểm tra khoảng cách với các objects đã spawn
            bool tooClose = false;
            foreach (Vector3 existingPos in spawnedPositions)
            {
                if (Vector3.Distance(spawnPos, existingPos) < minDistance)
                {
                    tooClose = true;
                    break;
                }
            }
            
            if (!tooClose)
            {
                // Chọn random prefab
                GameObject prefab = prefabs[Random.Range(0, prefabs.Length)];
                if (prefab != null)
                {
                    GameObject obj = Instantiate(prefab, spawnPos, Quaternion.identity, container);
                    obj.transform.localRotation = Quaternion.Euler(0, Random.Range(0f, 360f), 0);
                    
                    // Random scale variation
                    float scale = Random.Range(0.8f, 1.2f);
                    obj.transform.localScale = Vector3.one * scale;
                    
                    spawnedPositions.Add(spawnPos);
                    spawned++;
                }
            }
        }
    }
    
    // Lấy số lượng objects dựa trên biome
    private int GetObjectCountForBiome(BiomeType biome, int baseCount, bool isVegetation)
    {
        float multiplier = 1f;
        
        switch (biome)
        {
            case BiomeType.Forest:
                multiplier = isVegetation ? 2f : 0.5f;
                break;
            case BiomeType.Grassland:
                multiplier = isVegetation ? 1.5f : 1f;
                break;
            case BiomeType.Desert:
                multiplier = isVegetation ? 0.2f : 1.5f;
                break;
            case BiomeType.Mountain:
            case BiomeType.Snow:
                multiplier = isVegetation ? 0.1f : 2f;
                break;
            case BiomeType.Ocean:
            case BiomeType.Beach:
                multiplier = 0f;
                break;
            case BiomeType.Swamp:
                multiplier = isVegetation ? 1.2f : 0.8f;
                break;
        }
        
        return Mathf.RoundToInt(baseCount * multiplier);
    }
    
    // Public methods để truy cập thông tin
    public Vector2Int GetCurrentChunk()
    {
        return new Vector2Int((int)currentChunkX, (int)currentChunkY);
    }
    
    public long GetCurrentChunkX() => currentChunkX;
    public long GetCurrentChunkY() => currentChunkY;
    
    public int GetChunkSize() => chunkSize;
    
    public void SetSeed(int newSeed)
    {
        seed = newSeed;
        Random.InitState(seed);
        
        // Reload tất cả chunks với seed mới
        foreach (var chunk in loadedChunks.Values)
        {
            if (chunk.chunkObject != null)
            {
                Destroy(chunk.chunkObject);
            }
        }
        loadedChunks.Clear();
        chunkSpawnedObjects.Clear();
        UpdateChunks();
    }
    
    void OnDrawGizmos()
    {
        // Vẽ gizmos để visualize chunks trong editor
        if (Application.isPlaying && loadedChunks != null)
        {
            Gizmos.color = Color.yellow;
            foreach (var chunk in loadedChunks.Values)
            {
                if (chunk.chunkObject != null)
                {
                    Vector3 center = chunk.worldPosition + new Vector3(chunkSize / 2f, 0, chunkSize / 2f);
                    Gizmos.DrawWireCube(center, new Vector3(chunkSize, 1, chunkSize));
                }
            }
        }
    }
}
