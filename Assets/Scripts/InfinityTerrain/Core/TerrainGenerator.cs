using UnityEngine;
using UnityEngine.Rendering;
using InfinityTerrain.Data;
using InfinityTerrain.Settings;
using InfinityTerrain.Utilities;
using Unity.Collections;

namespace InfinityTerrain.Core
{
    /// <summary>
    /// Handles GPU-based terrain generation using compute shaders.
    /// </summary>
    public class TerrainGenerator
    {
        private readonly TerrainSettings terrainSettings;
        private readonly MaterialSettings materialSettings;
        private readonly MaterialManager materialManager;

        public TerrainGenerator(
            TerrainSettings terrainSettings,
            MaterialSettings materialSettings,
            MaterialManager materialManager)
        {
            this.terrainSettings = terrainSettings;
            this.materialSettings = materialSettings;
            this.materialManager = materialManager;
        }

        /// <summary>
        /// Generate a terrain chunk using GPU compute shader.
        /// </summary>
        public void GenerateChunkGPU(
            long noiseChunkX,
            long noiseChunkY,
            ChunkData data,
            int lodResolution,
            float chunkSizeWorld,
            int baseVertsPerChunkOverride,
            bool wantCollider)
        {
            lodResolution = ChunkLodUtility.ValidatePow2Plus1LodResolution(
                terrainSettings.resolution, lodResolution, 9);
            data.isReady = false;
            data.lodResolution = lodResolution;
            data.noiseChunkX = noiseChunkX;
            data.noiseChunkY = noiseChunkY;
            data.baseVertsPerChunk = Mathf.Max(1, baseVertsPerChunkOverride);
            data.chunkSizeWorld = chunkSizeWorld;

            ComputeShader cs = materialSettings.terrainComputeShader;
            if (cs == null)
            {
                Debug.LogError($"Cannot generate chunk {noiseChunkX}_{noiseChunkY}: Compute shader is null!");
                return;
            }

            int kernelHeight = cs.FindKernel("GenerateHeightmap");
            int kernelErode = cs.FindKernel("ErodeHeightmap");
            int kernelSmooth = cs.FindKernel("SmoothHeightmap");
            int kernelBiome = cs.FindKernel("GenerateBiomeMap");
            int kernelNormal = cs.FindKernel("CalculateNormals");
            int kernelMesh = cs.FindKernel("GenerateMesh");

            // Verify all required kernels exist
            if (kernelHeight < 0 || kernelErode < 0 || kernelSmooth < 0 || kernelBiome < 0 ||
                kernelNormal < 0 || kernelMesh < 0)
            {
                Debug.LogError($"Compute shader kernels not found for chunk {noiseChunkX}_{noiseChunkY}!");
                return;
            }

            // Buffers
            int vertCount = lodResolution * lodResolution;

            // Heightmap ping-pong for post process (erosion/smoothing)
            RenderTexture heightMapA = new RenderTexture(lodResolution, lodResolution, 0, RenderTextureFormat.RFloat);
            heightMapA.enableRandomWrite = true;
            heightMapA.Create();

            RenderTexture heightMapB = new RenderTexture(lodResolution, lodResolution, 0, RenderTextureFormat.RFloat);
            heightMapB.enableRandomWrite = true;
            heightMapB.Create();

            RenderTexture biomeMap = new RenderTexture(lodResolution, lodResolution, 0, RenderTextureFormat.ARGBFloat);
            biomeMap.enableRandomWrite = true;
            biomeMap.Create();

            ComputeBuffer vertBuffer = new ComputeBuffer(vertCount, sizeof(float) * 3);
            ComputeBuffer uvBuffer = new ComputeBuffer(vertCount, sizeof(float) * 2);
            ComputeBuffer normalBuffer = new ComputeBuffer(vertCount, sizeof(float) * 3);
            ComputeBuffer triBuffer = new ComputeBuffer((lodResolution - 1) * (lodResolution - 1) * 6, sizeof(int));

            // Set parameters
            cs.SetFloat("chunkSize", chunkSizeWorld);
            cs.SetInt("resolution", lodResolution);
            cs.SetInt("baseVertsPerChunk", Mathf.Max(1, baseVertsPerChunkOverride));
            cs.SetFloat("heightMultiplier", terrainSettings.heightMultiplier);
            cs.SetInt("octaves", terrainSettings.octaves);
            cs.SetFloat("persistence", terrainSettings.persistence);
            cs.SetFloat("lacunarity", terrainSettings.lacunarity);
            cs.SetInt("seed", terrainSettings.seed);

            // Stable-at-infinity noise configuration
            ComputeShaderHelper.SetLongAsUInt2(cs, "chunkXLo", "chunkXHi", noiseChunkX);
            ComputeShaderHelper.SetLongAsUInt2(cs, "chunkYLo", "chunkYHi", noiseChunkY);
            cs.SetInt("baseNoiseShift", NoiseGenerator.ComputeNoiseShift(
                terrainSettings.noiseScale, terrainSettings.resolution, terrainSettings.chunkSize));
            cs.SetInt("moistureNoiseShift", NoiseGenerator.ComputeNoiseShift(0.002f, terrainSettings.resolution, terrainSettings.chunkSize));
            cs.SetInt("temperatureNoiseShift", NoiseGenerator.ComputeNoiseShift(0.003f, terrainSettings.resolution, terrainSettings.chunkSize));
            cs.SetFloat("mountainStrength", terrainSettings.mountainStrength);
            cs.SetFloat("plainStrength", terrainSettings.plainStrength);
            cs.SetFloat("erosionStrength", terrainSettings.erosionStrength);
            cs.SetFloat("domainWarpStrength", terrainSettings.domainWarpStrength);

            // Post-process tuning
            int erosionIterations = Mathf.RoundToInt(Mathf.Lerp(0f, 12f, Mathf.Clamp01(terrainSettings.erosionStrength)));
            int smoothIterations = Mathf.RoundToInt(Mathf.Lerp(0f, 2f, Mathf.Clamp01(terrainSettings.erosionStrength)));
            float talus = Mathf.Lerp(terrainSettings.heightMultiplier * 0.02f, terrainSettings.heightMultiplier * 0.004f, Mathf.Clamp01(terrainSettings.erosionStrength));
            float amount = Mathf.Lerp(0f, 0.35f, Mathf.Clamp01(terrainSettings.erosionStrength));
            float smooth = Mathf.Lerp(0f, 0.65f, Mathf.Clamp01(terrainSettings.erosionStrength));
            cs.SetFloat("erosionTalus", talus);
            cs.SetFloat("erosionAmount", amount);
            cs.SetFloat("smoothStrength", smooth);

            // Dispatch
            int groups = Mathf.CeilToInt(lodResolution / 8f);

            try
            {
                // 1) Height
                cs.SetTexture(kernelHeight, "HeightMap", heightMapA);
                cs.Dispatch(kernelHeight, groups, groups, 1);

                // 2) Erosion (ping-pong)
                RenderTexture src = heightMapA;
                RenderTexture dst = heightMapB;
                for (int i = 0; i < erosionIterations; i++)
                {
                    cs.SetTexture(kernelErode, "HeightMapIn", src);
                    cs.SetTexture(kernelErode, "HeightMapOut", dst);
                    cs.Dispatch(kernelErode, groups, groups, 1);
                    RenderTexture tmp = src;
                    src = dst;
                    dst = tmp;
                }

                // 3) Small blur
                for (int i = 0; i < smoothIterations; i++)
                {
                    cs.SetTexture(kernelSmooth, "HeightMapIn", src);
                    cs.SetTexture(kernelSmooth, "HeightMapOut", dst);
                    cs.Dispatch(kernelSmooth, groups, groups, 1);
                    RenderTexture tmp = src;
                    src = dst;
                    dst = tmp;
                }

                // 4) Biome map
                cs.SetTexture(kernelBiome, "HeightMap", src);
                cs.SetTexture(kernelBiome, "BiomeMap", biomeMap);
                cs.Dispatch(kernelBiome, groups, groups, 1);

                // 5) Normals/Mesh
                cs.SetTexture(kernelNormal, "HeightMap", src);
                cs.SetBuffer(kernelNormal, "Normals", normalBuffer);
                cs.Dispatch(kernelNormal, groups, groups, 1);

                cs.SetTexture(kernelMesh, "HeightMap", src);
                cs.SetBuffer(kernelMesh, "Vertices", vertBuffer);
                cs.SetBuffer(kernelMesh, "UVs", uvBuffer);
                cs.SetBuffer(kernelMesh, "Triangles", triBuffer);
                cs.Dispatch(kernelMesh, groups, groups, 1);

                // Readback
                Vector3[] vertices = new Vector3[vertCount];
                vertBuffer.GetData(vertices);

                Vector2[] uvs = new Vector2[vertCount];
                uvBuffer.GetData(uvs);

                Vector3[] normals = new Vector3[vertCount];
                normalBuffer.GetData(normals);

                int[] triangles = new int[(lodResolution - 1) * (lodResolution - 1) * 6];
                triBuffer.GetData(triangles);

                // Validate GPU output
                for (int i = 0; i < Mathf.Min(vertices.Length, 256); i++)
                {
                    Vector3 v = vertices[i];
                    if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                        float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                    {
                        Debug.LogError($"GPU mesh data invalid (NaN/Inf) for chunk {noiseChunkX}_{noiseChunkY}.");
                        return;
                    }
                }

                for (int i = 0; i < triangles.Length; i++)
                {
                    int t = triangles[i];
                    if ((uint)t >= (uint)vertCount)
                    {
                        Debug.LogError($"GPU triangle index out of bounds for chunk {noiseChunkX}_{noiseChunkY}.");
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

                // Optional skirt
                if (terrainSettings.skirtDepth > 0.001f)
                {
                    MeshUtilities.AddSkirt(ref mesh, lodResolution, terrainSettings.skirtDepth);
                }

                // Apply mesh to GameObject
                MeshFilter mf = data.gameObject.GetComponent<MeshFilter>();
                if (mf == null) mf = data.gameObject.AddComponent<MeshFilter>();
                MeshRenderer mr = data.gameObject.GetComponent<MeshRenderer>();
                if (mr == null) mr = data.gameObject.AddComponent<MeshRenderer>();
                MeshCollider mc = data.gameObject.GetComponent<MeshCollider>();

                mf.mesh = mesh;
                mr.material = materialManager.TerrainMaterial;
                materialManager.UpdateMaterialProperties();

                if (wantCollider)
                {
                    if (mc == null) mc = data.gameObject.AddComponent<MeshCollider>();
                    if (mc != null) mc.sharedMesh = mesh;
                }
                else
                {
                    if (mc != null) Object.Destroy(mc);
                }

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

        /// <summary>
        /// Generate a heightmap (normalized 0..1) using GPU compute shader.
        /// Intended for Unity built-in Terrain chunks (TerrainData.SetHeights).
        /// </summary>
        public float[,] GenerateHeightmap01GPU(
            long noiseChunkX,
            long noiseChunkY,
            int lodResolution,
            float chunkSizeWorld,
            int baseVertsPerChunkOverride)
        {
            lodResolution = ChunkLodUtility.ValidatePow2Plus1LodResolution(
                terrainSettings.resolution, lodResolution, 9);

            ComputeShader cs = materialSettings.terrainComputeShader;
            if (cs == null)
            {
                Debug.LogError($"Cannot generate heightmap for chunk {noiseChunkX}_{noiseChunkY}: Compute shader is null!");
                return null;
            }

            int kernelHeight = cs.FindKernel("GenerateHeightmap");
            int kernelErode = cs.FindKernel("ErodeHeightmap");
            int kernelSmooth = cs.FindKernel("SmoothHeightmap");

            if (kernelHeight < 0 || kernelErode < 0 || kernelSmooth < 0)
            {
                Debug.LogError($"Compute shader kernels not found for heightmap chunk {noiseChunkX}_{noiseChunkY}!");
                return null;
            }

            RenderTexture heightMapA = new RenderTexture(lodResolution, lodResolution, 0, RenderTextureFormat.RFloat)
            {
                enableRandomWrite = true
            };
            heightMapA.Create();

            RenderTexture heightMapB = new RenderTexture(lodResolution, lodResolution, 0, RenderTextureFormat.RFloat)
            {
                enableRandomWrite = true
            };
            heightMapB.Create();

            // Set parameters (match GenerateChunkGPU)
            cs.SetFloat("chunkSize", chunkSizeWorld);
            cs.SetInt("resolution", lodResolution);
            cs.SetInt("baseVertsPerChunk", Mathf.Max(1, baseVertsPerChunkOverride));
            cs.SetFloat("heightMultiplier", terrainSettings.heightMultiplier);
            cs.SetInt("octaves", terrainSettings.octaves);
            cs.SetFloat("persistence", terrainSettings.persistence);
            cs.SetFloat("lacunarity", terrainSettings.lacunarity);
            cs.SetInt("seed", terrainSettings.seed);

            ComputeShaderHelper.SetLongAsUInt2(cs, "chunkXLo", "chunkXHi", noiseChunkX);
            ComputeShaderHelper.SetLongAsUInt2(cs, "chunkYLo", "chunkYHi", noiseChunkY);
            cs.SetInt("baseNoiseShift", NoiseGenerator.ComputeNoiseShift(
                terrainSettings.noiseScale, terrainSettings.resolution, terrainSettings.chunkSize));
            cs.SetInt("moistureNoiseShift", NoiseGenerator.ComputeNoiseShift(0.002f, terrainSettings.resolution, terrainSettings.chunkSize));
            cs.SetInt("temperatureNoiseShift", NoiseGenerator.ComputeNoiseShift(0.003f, terrainSettings.resolution, terrainSettings.chunkSize));
            cs.SetFloat("mountainStrength", terrainSettings.mountainStrength);
            cs.SetFloat("plainStrength", terrainSettings.plainStrength);
            cs.SetFloat("erosionStrength", terrainSettings.erosionStrength);
            cs.SetFloat("domainWarpStrength", terrainSettings.domainWarpStrength);

            int erosionIterations = Mathf.RoundToInt(Mathf.Lerp(0f, 12f, Mathf.Clamp01(terrainSettings.erosionStrength)));
            int smoothIterations = Mathf.RoundToInt(Mathf.Lerp(0f, 2f, Mathf.Clamp01(terrainSettings.erosionStrength)));
            float talus = Mathf.Lerp(terrainSettings.heightMultiplier * 0.02f, terrainSettings.heightMultiplier * 0.004f, Mathf.Clamp01(terrainSettings.erosionStrength));
            float amount = Mathf.Lerp(0f, 0.35f, Mathf.Clamp01(terrainSettings.erosionStrength));
            float smooth = Mathf.Lerp(0f, 0.65f, Mathf.Clamp01(terrainSettings.erosionStrength));
            cs.SetFloat("erosionTalus", talus);
            cs.SetFloat("erosionAmount", amount);
            cs.SetFloat("smoothStrength", smooth);

            int groups = Mathf.CeilToInt(lodResolution / 8f);

            try
            {
                // 1) Height
                cs.SetTexture(kernelHeight, "HeightMap", heightMapA);
                cs.Dispatch(kernelHeight, groups, groups, 1);

                // 2) Erosion (ping-pong)
                RenderTexture src = heightMapA;
                RenderTexture dst = heightMapB;
                for (int i = 0; i < erosionIterations; i++)
                {
                    cs.SetTexture(kernelErode, "HeightMapIn", src);
                    cs.SetTexture(kernelErode, "HeightMapOut", dst);
                    cs.Dispatch(kernelErode, groups, groups, 1);
                    RenderTexture tmp = src;
                    src = dst;
                    dst = tmp;
                }

                // 3) Smooth (ping-pong)
                for (int i = 0; i < smoothIterations; i++)
                {
                    cs.SetTexture(kernelSmooth, "HeightMapIn", src);
                    cs.SetTexture(kernelSmooth, "HeightMapOut", dst);
                    cs.Dispatch(kernelSmooth, groups, groups, 1);
                    RenderTexture tmp = src;
                    src = dst;
                    dst = tmp;
                }

                // Readback to CPU (blocking; called only when (re)generating chunks)
                AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(src, 0, TextureFormat.RFloat);
                req.WaitForCompletion();
                if (req.hasError)
                {
                    Debug.LogError($"AsyncGPUReadback failed for heightmap chunk {noiseChunkX}_{noiseChunkY}");
                    return null;
                }

                NativeArray<float> pixels = req.GetData<float>();
                int res = lodResolution;
                float invHm = 1.0f / Mathf.Max(0.0001f, terrainSettings.heightMultiplier);

                float[,] heights = new float[res, res]; // [y,x] for Unity Terrain
                for (int y = 0; y < res; y++)
                {
                    int row = y * res;
                    for (int x = 0; x < res; x++)
                    {
                        float h = pixels[row + x] * invHm;
                        heights[y, x] = Mathf.Clamp01(h);
                    }
                }

                return heights;
            }
            finally
            {
                heightMapA.Release();
                heightMapB.Release();
            }
        }
    }
}

