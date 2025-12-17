using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Linq;

public class InfinityRenderChunks : MonoBehaviour
{
    [Header("General Settings")]
    [SerializeField] private Transform player;
    [SerializeField] private int chunkSize = 100;
    [SerializeField] private int renderDistance = 4;
    [SerializeField] private int seed = 0;

    [Header("Terrain Settings")]
    [SerializeField] private int resolution = 129; 
    [SerializeField] private float heightMultiplier = 30f;
    [SerializeField] private float noiseScale = 0.005f;
    [SerializeField] private int octaves = 6;
    [SerializeField] private float persistence = 0.5f;
    [SerializeField] private float lacunarity = 2f;
    
    [Header("GPU Resources")]
    [SerializeField] private ComputeShader terrainComputeShader;
    [SerializeField] private Shader proceduralTerrainShader;
    [SerializeField] private Shader instancedFoliageShader;
    
    [Header("Vegetation Mesh")]
    [SerializeField] private Mesh treeMesh;
    [SerializeField] private Mesh rockMesh;
    [SerializeField] private Mesh grassMesh;
    [SerializeField] private Texture2D vegetationTexture;

    // Private State
    private Vector2Int currentChunkCoord;
    private Dictionary<Vector2Int, ChunkData> loadedChunks = new Dictionary<Vector2Int, ChunkData>();
    private Material terrainMaterial;
    private Material foliageMaterial;
    
    private ComputeBuffer argsBufferTree;
    private ComputeBuffer argsBufferRock;
    private ComputeBuffer argsBufferGrass;
    private uint[] args = new uint[5] { 0, 0, 0, 0, 0 };

    private class ChunkData
    {
        public GameObject gameObject;
        // Separate buffers for each type
        public ComputeBuffer treeBuffer;
        public ComputeBuffer rockBuffer;
        public ComputeBuffer grassBuffer;
        public int treeCount;
        public int rockCount;
        public int grassCount;
        public bool isReady;
    }

    private void Start()
    {
        if (player == null)
        {
             GameObject p = GameObject.FindGameObjectWithTag("Player");
             if (p != null) player = p.transform;
        }

        if (seed == 0) seed = Random.Range(-10000, 10000);
        
        // Initialize Materials
        if (proceduralTerrainShader == null) proceduralTerrainShader = Shader.Find("Custom/ProceduralTerrain");
        if (terrainMaterial == null) 
        {
            if (proceduralTerrainShader != null) terrainMaterial = new Material(proceduralTerrainShader);
            else Debug.LogError("ProceduralTerrain Shader not found!");
        }
        
        if (instancedFoliageShader == null) instancedFoliageShader = Shader.Find("Custom/InstancedFoliage");
        if (foliageMaterial == null) 
        {
             if (instancedFoliageShader != null)
             {
                foliageMaterial = new Material(instancedFoliageShader);
                if (vegetationTexture != null) foliageMaterial.mainTexture = vegetationTexture;
             }
             else Debug.LogError("InstancedFoliage Shader not found!");
        }

        // Load Default Meshes as fallbacks
        if (treeMesh == null) { GameObject p = GameObject.CreatePrimitive(PrimitiveType.Cylinder); treeMesh = p.GetComponent<MeshFilter>().sharedMesh; Destroy(p); }
        if (rockMesh == null) { GameObject p = GameObject.CreatePrimitive(PrimitiveType.Cube); rockMesh = p.GetComponent<MeshFilter>().sharedMesh; Destroy(p); }
        if (grassMesh == null) { GameObject p = GameObject.CreatePrimitive(PrimitiveType.Quad); grassMesh = p.GetComponent<MeshFilter>().sharedMesh; Destroy(p); }

        if (terrainComputeShader == null) terrainComputeShader = Resources.Load<ComputeShader>("Shaders/TerrainGen");

        UpdateChunksImmediate();
    }

    private void Update()
    {
        if (player == null) return;

        Vector3 playerPos = player.position;
        int pX = Mathf.FloorToInt(playerPos.x / chunkSize);
        int pZ = Mathf.FloorToInt(playerPos.z / chunkSize);
        Vector2Int playerChunk = new Vector2Int(pX, pZ);

        if (playerChunk != currentChunkCoord)
        {
            currentChunkCoord = playerChunk;
            UpdateChunks();
        }

        RenderVegetation();
    }

    private void UpdateChunks()
    {
        List<Vector2Int> activeCoords = new List<Vector2Int>();

        for (int x = -renderDistance; x <= renderDistance; x++)
        {
            for (int y = -renderDistance; y <= renderDistance; y++)
            {
                activeCoords.Add(currentChunkCoord + new Vector2Int(x, y));
            }
        }

        // Unload old
        List<Vector2Int> toRemove = new List<Vector2Int>();
        foreach (var kvp in loadedChunks)
        {
            if (!activeCoords.Contains(kvp.Key))
                toRemove.Add(kvp.Key);
        }

        foreach (var coord in toRemove)
        {
            UnloadChunk(coord);
        }

        // Load new
        foreach (var coord in activeCoords)
        {
            if (!loadedChunks.ContainsKey(coord))
            {
                CreateChunk(coord);
            }
        }
    }
    
    private void UpdateChunksImmediate()
    {
        if (player == null) return;
        Vector3 playerPos = player.position;
        int pX = Mathf.FloorToInt(playerPos.x / chunkSize);
        int pZ = Mathf.FloorToInt(playerPos.z / chunkSize);
        currentChunkCoord = new Vector2Int(pX, pZ);
        UpdateChunks();
    }

    private void UnloadChunk(Vector2Int coord)
    {
        if (loadedChunks.TryGetValue(coord, out ChunkData data))
        {
            if (data.treeBuffer != null) data.treeBuffer.Release();
            if (data.rockBuffer != null) data.rockBuffer.Release();
            if (data.grassBuffer != null) data.grassBuffer.Release();
            
            if (data.gameObject != null) Destroy(data.gameObject);
            loadedChunks.Remove(coord);
        }
    }

    private void CreateChunk(Vector2Int coord)
    {
        if (terrainComputeShader == null) return;

        GameObject chunkObj = new GameObject($"Chunk_{coord.x}_{coord.y}");
        chunkObj.transform.position = new Vector3(coord.x * chunkSize, 0, coord.y * chunkSize);
        chunkObj.transform.parent = transform;
        // Terrain needs to be on a layer?
        chunkObj.layer = LayerMask.NameToLayer("Default");

        ChunkData data = new ChunkData
        {
            gameObject = chunkObj,
            isReady = false
        };
        loadedChunks[coord] = data;

        // Run Generation
        GenerateChunkGPU(coord, data);
    }

    private void GenerateChunkGPU(Vector2Int coord, ChunkData data)
    {
        int kernelHeight = terrainComputeShader.FindKernel("GenerateHeightmap");
        int kernelNormal = terrainComputeShader.FindKernel("CalculateNormals");
        int kernelMesh = terrainComputeShader.FindKernel("GenerateMesh");
        int kernelVeg = terrainComputeShader.FindKernel("PlaceVegetation");

        // Buffers
        int vertCount = resolution * resolution;
        
        RenderTexture heightMap = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RFloat);
        heightMap.enableRandomWrite = true;
        heightMap.Create();
        
        RenderTexture biomeMap = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat);
        biomeMap.enableRandomWrite = true;
        biomeMap.Create();

        ComputeBuffer vertBuffer = new ComputeBuffer(vertCount, sizeof(float) * 3);
        ComputeBuffer uvBuffer = new ComputeBuffer(vertCount, sizeof(float) * 2);
        ComputeBuffer normalBuffer = new ComputeBuffer(vertCount, sizeof(float) * 3);
        ComputeBuffer triBuffer = new ComputeBuffer((resolution - 1) * (resolution - 1) * 6, sizeof(int));
        ComputeBuffer vegBuffer = new ComputeBuffer(20000, 32, ComputeBufferType.Append); // Increased max
        vegBuffer.SetCounterValue(0);

        // Set Parameters
        terrainComputeShader.SetFloat("chunkX", coord.x);
        terrainComputeShader.SetFloat("chunkY", coord.y);
        terrainComputeShader.SetFloat("chunkSize", chunkSize);
        terrainComputeShader.SetInt("resolution", resolution);
        terrainComputeShader.SetFloat("heightMultiplier", heightMultiplier);
        terrainComputeShader.SetFloat("noiseScale", noiseScale);
        terrainComputeShader.SetInt("octaves", octaves);
        terrainComputeShader.SetFloat("persistence", persistence);
        terrainComputeShader.SetFloat("lacunarity", lacunarity);
        terrainComputeShader.SetFloat("seed", seed);
        terrainComputeShader.SetFloat("moistureNoiseScale", 0.002f);
        terrainComputeShader.SetFloat("temperatureNoiseScale", 0.003f);

        // Dispatch Geometry
        terrainComputeShader.SetTexture(kernelHeight, "HeightMap", heightMap);
        terrainComputeShader.SetTexture(kernelHeight, "BiomeMap", biomeMap);
        terrainComputeShader.Dispatch(kernelHeight, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

        terrainComputeShader.SetTexture(kernelNormal, "HeightMap", heightMap);
        terrainComputeShader.SetBuffer(kernelNormal, "Normals", normalBuffer);
        terrainComputeShader.Dispatch(kernelNormal, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

        terrainComputeShader.SetTexture(kernelMesh, "HeightMap", heightMap);
        terrainComputeShader.SetBuffer(kernelMesh, "Vertices", vertBuffer);
        terrainComputeShader.SetBuffer(kernelMesh, "UVs", uvBuffer);
        terrainComputeShader.SetBuffer(kernelMesh, "Triangles", triBuffer);
        terrainComputeShader.Dispatch(kernelMesh, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

        // Vegetation
        terrainComputeShader.SetTexture(kernelVeg, "HeightMap", heightMap);
        terrainComputeShader.SetTexture(kernelVeg, "BiomeMap", biomeMap);
        terrainComputeShader.SetBuffer(kernelVeg, "Normals", normalBuffer);
        terrainComputeShader.SetBuffer(kernelVeg, "Vertices", vertBuffer);
        terrainComputeShader.SetBuffer(kernelVeg, "VegetationBuffer", vegBuffer);
        terrainComputeShader.Dispatch(kernelVeg, Mathf.CeilToInt(resolution / 8f), Mathf.CeilToInt(resolution / 8f), 1);

        // Readback Mesh Data (Blocking for stability)
        Vector3[] vertices = new Vector3[vertCount];
        vertBuffer.GetData(vertices);
        
        Vector2[] uvs = new Vector2[vertCount];
        uvBuffer.GetData(uvs);
        
        Vector3[] normals = new Vector3[vertCount];
        normalBuffer.GetData(normals);
        
        int[] triangles = new int[(resolution - 1) * (resolution - 1) * 6];
        triBuffer.GetData(triangles);

        // Apply to Mesh
        Mesh mesh = new Mesh();
        // Since we are using 65k+ vertices likely (129*129 = 16k, safe), for higher res use IndexFormat.UInt32
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
        mc.sharedMesh = mesh;

        // Process Vegetation
        ComputeBuffer countBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.IndirectArguments);
        int[] countArr = new int[1] { 0 };
        ComputeBuffer.CopyCount(vegBuffer, countBuffer, 0);
        countBuffer.GetData(countArr);
        int totalVeg = countArr[0];
        
        if (totalVeg > 0)
        {
             GpuTerrainData.VegetationInstance[] allVeg = new GpuTerrainData.VegetationInstance[totalVeg];
             vegBuffer.GetData(allVeg, 0, 0, totalVeg);
             
             // Filter by type
             var trees = allVeg.Where(v => v.typeID == 0).ToArray();
             var rocks = allVeg.Where(v => v.typeID == 1).ToArray();
             var grasses = allVeg.Where(v => v.typeID == 2).ToArray();
             
             if (trees.Length > 0)
             {
                 data.treeCount = trees.Length;
                 data.treeBuffer = new ComputeBuffer(trees.Length, 32);
                 data.treeBuffer.SetData(trees);
             }
             
             if (rocks.Length > 0)
             {
                 data.rockCount = rocks.Length;
                 data.rockBuffer = new ComputeBuffer(rocks.Length, 32);
                 data.rockBuffer.SetData(rocks);
             }
             
             if (grasses.Length > 0)
             {
                 data.grassCount = grasses.Length;
                 data.grassBuffer = new ComputeBuffer(grasses.Length, 32);
                 data.grassBuffer.SetData(grasses);
             }
        }

        // Cleanup
        heightMap.Release();
        biomeMap.Release();
        vertBuffer.Release();
        uvBuffer.Release();
        normalBuffer.Release();
        triBuffer.Release();
        vegBuffer.Release();
        countBuffer.Release();
        
        data.isReady = true;
    }

    private void RenderVegetation()
    {
        if (foliageMaterial == null) return;
        
        // Initialize args buffers if needed
        if (argsBufferTree == null) argsBufferTree = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        if (argsBufferRock == null) argsBufferRock = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        if (argsBufferGrass == null) argsBufferGrass = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);

        foreach (var kvp in loadedChunks)
        {
            ChunkData data = kvp.Value;
            if (!data.isReady) continue;
            
            // Draw Trees
            if (data.treeCount > 0 && treeMesh != null)
            {
                foliageMaterial.SetBuffer("vegetationBuffer", data.treeBuffer);
                foliageMaterial.SetColor("_Color", new Color(0.1f, 0.4f, 0.1f)); // Dark Green
                
                args[0] = (uint)treeMesh.GetIndexCount(0);
                args[1] = (uint)data.treeCount;
                args[2] = (uint)treeMesh.GetIndexStart(0);
                args[3] = (uint)treeMesh.GetBaseVertex(0);
                argsBufferTree.SetData(args);
                
                Graphics.DrawMeshInstancedIndirect(treeMesh, 0, foliageMaterial, new Bounds(Vector3.zero, Vector3.one * 10000), argsBufferTree);
            }
            
            // Draw Rocks
            if (data.rockCount > 0 && rockMesh != null)
            {
                foliageMaterial.SetBuffer("vegetationBuffer", data.rockBuffer);
                foliageMaterial.SetColor("_Color", new Color(0.5f, 0.5f, 0.5f)); // Gray
                
                args[0] = (uint)rockMesh.GetIndexCount(0);
                args[1] = (uint)data.rockCount;
                args[2] = (uint)rockMesh.GetIndexStart(0);
                args[3] = (uint)rockMesh.GetBaseVertex(0);
                argsBufferRock.SetData(args);
                
                Graphics.DrawMeshInstancedIndirect(rockMesh, 0, foliageMaterial, new Bounds(Vector3.zero, Vector3.one * 10000), argsBufferRock);
            }
            
            // Draw Grass
            if (data.grassCount > 0 && grassMesh != null)
            {
                foliageMaterial.SetBuffer("vegetationBuffer", data.grassBuffer);
                foliageMaterial.SetColor("_Color", new Color(0.3f, 0.7f, 0.2f)); // Green
                
                args[0] = (uint)grassMesh.GetIndexCount(0);
                args[1] = (uint)data.grassCount;
                args[2] = (uint)grassMesh.GetIndexStart(0);
                args[3] = (uint)grassMesh.GetBaseVertex(0);
                argsBufferGrass.SetData(args);
                
                Graphics.DrawMeshInstancedIndirect(grassMesh, 0, foliageMaterial, new Bounds(Vector3.zero, Vector3.one * 10000), argsBufferGrass);
            }
        }
    }

    private void OnDestroy()
    {
        foreach (var kvp in loadedChunks)
        {
            if (kvp.Value.treeBuffer != null) kvp.Value.treeBuffer.Release();
            if (kvp.Value.rockBuffer != null) kvp.Value.rockBuffer.Release();
            if (kvp.Value.grassBuffer != null) kvp.Value.grassBuffer.Release();
        }
        if (argsBufferTree != null) argsBufferTree.Release();
        if (argsBufferRock != null) argsBufferRock.Release();
        if (argsBufferGrass != null) argsBufferGrass.Release();
    }
}
