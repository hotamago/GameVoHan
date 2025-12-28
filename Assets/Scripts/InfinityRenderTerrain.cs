using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Infinite terrain streaming using Unity built-in Terrain tiles (Terrain + TerrainCollider).
/// Keeps Unity Transform space near origin via floating-origin shifting, while chunk coordinates remain 64-bit.
/// </summary>
public class InfinityRenderTerrain : MonoBehaviour
{
    [Header("General Settings")]
    [SerializeField] private Transform player;
    [SerializeField] private int chunkSize = 200;
    [SerializeField] private int renderDistance = 6;
    [SerializeField] private int seed = 0;
    [SerializeField] private float floatingOriginThreshold = 1000f;

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
    [Tooltip("Terrain heightmap resolution per tile. Must be 2^n+1 (e.g. 129, 257, 513).")]
    [SerializeField] private int resolution = 257;
    [SerializeField] private float heightMultiplier = 1500f;
    [SerializeField] private float noiseScale = 0.005f;
    [SerializeField] private int octaves = 6;
    [SerializeField] private float persistence = 0.5f;
    [SerializeField] private float lacunarity = 2f;

    [Header("Advanced Terrain")]
    [SerializeField] private float mountainStrength = 0.4f;
    [SerializeField] private float plainStrength = 0.3f;
    [SerializeField] private float erosionStrength = 0.2f;
    [SerializeField] private float domainWarpStrength = 2.0f;

    [Header("Terrain Tiles")]
    [Tooltip("Chebyshev radius (in tiles) that keeps TerrainCollider enabled. Beyond this, collider is disabled to reduce cost.")]
    [SerializeField] private int colliderRadius = 1;
    [Tooltip("Optional material to apply to spawned Terrain tiles (URP Terrain Lit, etc.).")]
    [SerializeField] private Material terrainMaterialTemplate;
    [Tooltip("How many new/regen tiles to build per frame (coroutine). Higher = faster load, more hitches.")]
    [SerializeField] private int maxGenerationsPerFrame = 1;

    // Legacy/compat: the scene currently serializes this reference (from older compute-chunk version).
    // We keep it so the scene loads cleanly; it is unused for Terrain-based generation.
    [Header("Legacy (unused)")]
    [SerializeField] private ComputeShader terrainComputeShader;

    // Virtual world tracking (long chunk origin + float local space)
    private long worldChunkOriginX = 0;
    private long worldChunkOriginY = 0;
    private long _runtimeChunkX;
    private long _runtimeChunkY;

    private class TileData
    {
        public GameObject go;
        public Terrain terrain;
        public TerrainCollider collider;
        public TerrainData data;
        public bool isReady;
        public long chunkX;
        public long chunkY;
    }

    // key = "X_Y" for long coords
    private readonly Dictionary<string, TileData> _tiles = new Dictionary<string, TileData>(256);
    private readonly Queue<(long x, long y, int dx, int dy)> _genQueue = new Queue<(long, long, int, int)>(256);
    private readonly HashSet<string> _queued = new HashSet<string>();

    private void Start()
    {
        if (player == null)
        {
            var p = GameObject.FindGameObjectWithTag("Player");
            if (p != null) player = p.transform;
        }

        if (seed == 0) seed = UnityEngine.Random.Range(-10000, 10000);

        // Allow setting a starting chunk from Inspector (current_x/current_y).
        TeleportToChunk(current_x, current_y);

        if (autoPlacePlayerOnStart)
        {
            StartCoroutine(PlacePlayerAboveTerrainWhenPlayerReady());
        }

        StartCoroutine(TerrainGenerationWorker());
    }

    private IEnumerator PlacePlayerAboveTerrainWhenPlayerReady()
    {
        const int tries = 60;
        for (int i = 0; i < tries; i++)
        {
            if (player == null)
            {
                var p = GameObject.FindGameObjectWithTag("Player");
                if (p != null) player = p.transform;
            }

            if (player != null)
            {
                PlacePlayerAboveTerrainAtCurrentXZ(spawnHeightOffset);
                yield break;
            }

            yield return null;
        }
    }

    private void Update()
    {
        if (player == null) return;

        // 1) Floating origin shift (keeps transforms near 0)
        if (Mathf.Abs(player.position.x) > floatingOriginThreshold || Mathf.Abs(player.position.z) > floatingOriginThreshold)
        {
            ShiftWorldOrigin();
        }

        // 2) Calculate absolute chunk coordinate (native long + float local)
        int localChunkX = Mathf.FloorToInt(player.position.x / chunkSize);
        int localChunkY = Mathf.FloorToInt(player.position.z / chunkSize);
        long pX = worldChunkOriginX + localChunkX;
        long pZ = worldChunkOriginY + localChunkY;

        _runtimeChunkX = pX;
        _runtimeChunkY = pZ;
        current_x = pX;
        current_y = pZ;
        _debugLocalPos = player.position;

        // 3) Stream terrain tiles
        UpdateTiles(pX, pZ);

        // 4) Safety
        if (enableSafety)
        {
            float inChunkX = player.position.x - (localChunkX * chunkSize);
            float inChunkZ = player.position.z - (localChunkY * chunkSize);
            UpdatePlayerSafety(pX, pZ, inChunkX, inChunkZ);
        }
    }

    private void OnValidate()
    {
        if (!Application.isPlaying) return;
        if (player == null) return;

        if (current_x != _runtimeChunkX || current_y != _runtimeChunkY)
        {
            TeleportToChunk(current_x, current_y);
        }
    }

    private void ShiftWorldOrigin()
    {
        int dxChunks = Mathf.FloorToInt(player.position.x / chunkSize);
        int dzChunks = Mathf.FloorToInt(player.position.z / chunkSize);
        if (dxChunks == 0 && dzChunks == 0) return;

        Vector3 shift = new Vector3(dxChunks * chunkSize, 0, dzChunks * chunkSize);

        // Shift player
        player.position -= shift;

        // Shift tiles
        foreach (var kvp in _tiles)
        {
            if (kvp.Value?.go != null) kvp.Value.go.transform.position -= shift;
        }

        // Update absolute chunk origin
        worldChunkOriginX += dxChunks;
        worldChunkOriginY += dzChunks;
    }

    private static bool IsPow2(int v) => (v > 0) && ((v & (v - 1)) == 0);

    private int ValidateResolution(int r)
    {
        r = Mathf.Clamp(r, 33, 4097);
        if ((r & 1) == 0) r += 1; // must be odd
        int verts = r - 1;
        if (!IsPow2(verts))
        {
            // snap to nearest power-of-two+1 (up)
            int p = 1;
            while (p < verts && p < (1 << 30)) p <<= 1;
            r = Mathf.Clamp(p + 1, 33, 4097);
        }
        return r;
    }

    private void UpdateTiles(long centerChunkX, long centerChunkY)
    {
        int res = ValidateResolution(resolution);
        if (res != resolution) resolution = res;

        var desired = new HashSet<string>(256);
        for (int dx = -renderDistance; dx <= renderDistance; dx++)
        {
            for (int dy = -renderDistance; dy <= renderDistance; dy++)
            {
                long cx = centerChunkX + dx;
                long cy = centerChunkY + dy;
                string key = $"{cx}_{cy}";
                desired.Add(key);

                if (!_tiles.ContainsKey(key))
                {
                    CreateTile(cx, cy);
                }

                // Queue height generation / collider toggle update
                if (!_queued.Contains(key))
                {
                    _queued.Add(key);
                    _genQueue.Enqueue((cx, cy, dx, dy));
                }
            }
        }

        // Unload tiles outside range
        var toRemove = new List<string>();
        foreach (var key in _tiles.Keys)
        {
            if (!desired.Contains(key)) toRemove.Add(key);
        }
        foreach (var key in toRemove) UnloadTile(key);

        // Update neighbors (for seamless stitching / LOD)
        UpdateNeighbors(centerChunkX, centerChunkY);
    }

    private void UpdateNeighbors(long centerChunkX, long centerChunkY)
    {
        // Update only in the active square to keep it cheap.
        for (int dx = -renderDistance; dx <= renderDistance; dx++)
        {
            for (int dy = -renderDistance; dy <= renderDistance; dy++)
            {
                long cx = centerChunkX + dx;
                long cy = centerChunkY + dy;
                if (!_tiles.TryGetValue($"{cx}_{cy}", out var t) || t?.terrain == null) continue;

                _tiles.TryGetValue($"{cx - 1}_{cy}", out var left);
                _tiles.TryGetValue($"{cx + 1}_{cy}", out var right);
                _tiles.TryGetValue($"{cx}_{cy + 1}", out var top);
                _tiles.TryGetValue($"{cx}_{cy - 1}", out var bottom);

                t.terrain.SetNeighbors(left?.terrain, top?.terrain, right?.terrain, bottom?.terrain);
            }
        }
    }

    private void CreateTile(long chunkX, long chunkY)
    {
        string key = $"{chunkX}_{chunkY}";
        if (_tiles.ContainsKey(key)) return;

        var go = new GameObject($"Terrain_{chunkX}_{chunkY}");
        go.transform.parent = transform;

        // Place in local space relative to chunk-origin so floats remain small.
        long relX = chunkX - worldChunkOriginX;
        long relY = chunkY - worldChunkOriginY;
        go.transform.position = new Vector3(relX * chunkSize, 0f, relY * chunkSize);

        int res = ValidateResolution(resolution);
        var td = new TerrainData
        {
            heightmapResolution = res,
            size = new Vector3(chunkSize, Mathf.Max(1f, heightMultiplier), chunkSize)
        };

        var terrain = go.AddComponent<Terrain>();
        terrain.terrainData = td;
        terrain.drawInstanced = true;
        if (terrainMaterialTemplate != null) terrain.materialTemplate = terrainMaterialTemplate;

        var col = go.AddComponent<TerrainCollider>();
        col.terrainData = td;

        _tiles[key] = new TileData
        {
            go = go,
            terrain = terrain,
            collider = col,
            data = td,
            isReady = false,
            chunkX = chunkX,
            chunkY = chunkY
        };
    }

    private void UnloadTile(string key)
    {
        if (_tiles.TryGetValue(key, out var t))
        {
            if (t?.go != null) Destroy(t.go);
            _tiles.Remove(key);
            _queued.Remove(key);
        }
    }

    private IEnumerator TerrainGenerationWorker()
    {
        while (true)
        {
            int budget = Mathf.Max(1, maxGenerationsPerFrame);
            while (budget-- > 0 && _genQueue.Count > 0)
            {
                var (x, y, dx, dy) = _genQueue.Dequeue();
                string key = $"{x}_{y}";
                _queued.Remove(key);

                if (!_tiles.TryGetValue(key, out var t) || t == null || t.go == null || t.data == null) continue;

                // Collider management (near tiles only)
                int r = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
                bool wantCollider = r <= Mathf.Max(0, colliderRadius);
                if (t.collider != null) t.collider.enabled = wantCollider;

                // Generate heights once (or if parameters changed, you can force regen by clearing tiles / teleport).
                if (!t.isReady)
                {
                    ApplyHeightsToTerrain(t, x, y);
                    t.isReady = true;
                }
            }

            yield return null;
        }
    }

    private void ApplyHeightsToTerrain(TileData t, long chunkX, long chunkY)
    {
        int res = t.data.heightmapResolution;
        int verts = res - 1;
        float[,] heights = new float[res, res]; // [z,x]

        // Stable hashing based on 64-bit grid coords (no float-world dependence).
        uint s0 = Hash32((uint)seed ^ 0xA341316Cu);

        // Noise shifts are derived from the old "scale" intent but computed in vertex-grid space.
        int baseShift = ComputeNoiseShift(noiseScale);
        int warpShift = Mathf.Clamp(baseShift + 1, 0, 30);

        for (int z = 0; z < res; z++)
        {
            for (int x = 0; x < res; x++)
            {
                // Absolute grid coordinate (in vertex steps), deterministic for negative chunks too.
                ulong gx = unchecked((ulong)chunkX) * (ulong)verts + (ulong)x;
                ulong gz = unchecked((ulong)chunkY) * (ulong)verts + (ulong)z;

                // Optional domain warp in grid-space (keeps continuity across tiles).
                if (domainWarpStrength > 0.0001f)
                {
                    float wx = Fbm64(gx, gz, warpShift, 2, 0.5f, s0 ^ 0x68bc21ebu);
                    float wz = Fbm64(gx, gz, warpShift, 2, 0.5f, s0 ^ 0x02e5be93u);
                    // Map to signed offset in "vertex units"
                    float ox = (wx - 0.5f) * 2f * domainWarpStrength;
                    float oz = (wz - 0.5f) * 2f * domainWarpStrength;
                    gx = unchecked(gx + (ulong)Mathf.RoundToInt(ox));
                    gz = unchecked(gz + (ulong)Mathf.RoundToInt(oz));
                }

                float continents = Fbm64(gx, gz, baseShift + 4, 3, 0.5f, s0);
                float detail = Fbm64(gx, gz, baseShift, Mathf.Max(1, octaves), Mathf.Clamp01(persistence), s0 ^ 0x9e3779b9u);

                // Blend: keep large-scale shapes + small details; tune with strengths.
                float h01 = Mathf.Lerp(continents, detail, Mathf.Clamp01(plainStrength));
                if (continents > 0.6f) h01 += 0.2f * mountainStrength;
                h01 = Mathf.Clamp01(h01);

                // Very small erosion-ish smoothing (cheap): reduce needle peaks.
                if (erosionStrength > 0.0001f)
                {
                    // Pull heights slightly toward mid to reduce extremes.
                    float k = Mathf.Clamp01(erosionStrength) * 0.15f;
                    h01 = Mathf.Lerp(h01, h01 * (1f - k) + 0.5f * k, k);
                }

                heights[z, x] = h01;
            }
        }

        t.data.SetHeights(0, 0, heights);
        if (t.collider != null) t.collider.terrainData = t.data;
    }

    private int ComputeNoiseShift(float scale)
    {
        // Convert world-space "scale" into a stable, power-of-two cell size in vertex-grid units.
        // stepWorld = chunkSize / (resolution-1)
        if (scale <= 0f) return 8;
        int res = ValidateResolution(resolution);
        float stepWorld = chunkSize / (float)(res - 1);
        float cellSizeVerts = (1f / scale) / stepWorld;
        if (cellSizeVerts <= 1f) return 0;
        int shift = Mathf.RoundToInt(Mathf.Log(cellSizeVerts, 2f));
        return Mathf.Clamp(shift, 0, 30);
    }

    // --- Teleport / cleanup ---
    private void ClearAllTiles()
    {
        foreach (var kvp in _tiles)
        {
            if (kvp.Value?.go != null) Destroy(kvp.Value.go);
        }
        _tiles.Clear();
        _genQueue.Clear();
        _queued.Clear();
    }

    /// <summary>
    /// Teleport the virtual world so the player is now in the specified chunk (64-bit safe),
    /// while keeping Unity Transform positions near 0.
    /// </summary>
    public void TeleportToChunk(long targetChunkX, long targetChunkY)
    {
        if (player == null) return;

        ClearAllTiles();

        int localChunkX = Mathf.FloorToInt(player.position.x / chunkSize);
        int localChunkY = Mathf.FloorToInt(player.position.z / chunkSize);
        worldChunkOriginX = targetChunkX - localChunkX;
        worldChunkOriginY = targetChunkY - localChunkY;

        current_x = targetChunkX;
        current_y = targetChunkY;

        UpdateTiles(targetChunkX, targetChunkY);
    }

    [ContextMenu("Terrain/Teleport To Current Chunk")]
    public void TeleportToCurrentChunk()
    {
        TeleportToChunk(current_x, current_y);
    }

    // --- Safety / spawn height ---
    private bool TryGetTerrainHeightRaycast(Vector3 positionXZ, out float terrainY)
    {
        float startHeight = Mathf.Max(positionXZ.y + 500f, 1000f);
        Vector3 start = new Vector3(positionXZ.x, startHeight, positionXZ.z);
        RaycastHit[] hits = Physics.RaycastAll(start, Vector3.down, 200000f, ~0, QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0)
        {
            terrainY = 0f;
            return false;
        }

        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        Transform playerRoot = player != null ? player : null;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider c = hits[i].collider;
            if (c == null) continue;
            if (playerRoot != null && c.transform.IsChildOf(playerRoot)) continue;
            terrainY = hits[i].point.y;
            return true;
        }

        terrainY = 0f;
        return false;
    }

    private void PlacePlayerAboveTerrainAtCurrentXZ(float heightOffset)
    {
        if (player == null) return;

        int localChunkX = Mathf.FloorToInt(player.position.x / chunkSize);
        int localChunkY = Mathf.FloorToInt(player.position.z / chunkSize);
        long pX = worldChunkOriginX + localChunkX;
        long pZ = worldChunkOriginY + localChunkY;

        float inChunkX = player.position.x - (localChunkX * chunkSize);
        float inChunkZ = player.position.z - (localChunkY * chunkSize);

        float terrainY;
        if (!TryGetTerrainHeightRaycast(player.position, out terrainY))
        {
            terrainY = GetTerrainHeightCPU(pX, pZ, inChunkX, inChunkZ);
        }

        Vector3 newPos = player.position;
        newPos.y = terrainY + Mathf.Max(0f, heightOffset);
        player.position = newPos;

        Rigidbody rb = player.GetComponent<Rigidbody>();
        if (rb != null) rb.linearVelocity = Vector3.zero;
    }

    private void UpdatePlayerSafety(long playerChunkX, long playerChunkY, float inChunkX, float inChunkZ)
    {
        float actualTerrainHeight;
        if (!TryGetTerrainHeightRaycast(player.position, out actualTerrainHeight))
        {
            actualTerrainHeight = GetTerrainHeightCPU(playerChunkX, playerChunkY, inChunkX, inChunkZ);
        }

        Rigidbody rb = player.GetComponent<Rigidbody>();
        bool isJumping = rb != null && rb.linearVelocity.y > 2.0f;

        float safetyThreshold = actualTerrainHeight - Mathf.Max(0.1f, recoverBelowTerrain);
        bool isHardVoid = player.position.y < hardVoidY;
        if ((isHardVoid || !isJumping) && player.position.y < safetyThreshold)
        {
            Vector3 newPos = player.position;
            newPos.y = actualTerrainHeight + safetyHeightOffset + 5.0f;
            player.position = newPos;
            if (rb != null) rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0, rb.linearVelocity.z);
        }
    }

    // --- CPU noise (stable at infinity) ---
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
        int res = ValidateResolution(resolution);
        int vertsPerChunk = Mathf.Max(1, res - 1);
        float stepWorld = chunkSize / (float)vertsPerChunk;
        int vx = Mathf.Clamp(Mathf.RoundToInt(inChunkX / stepWorld), 0, vertsPerChunk);
        int vz = Mathf.Clamp(Mathf.RoundToInt(inChunkZ / stepWorld), 0, vertsPerChunk);

        ulong gx = unchecked((ulong)chunkX) * (ulong)vertsPerChunk + (ulong)vx;
        ulong gz = unchecked((ulong)chunkY) * (ulong)vertsPerChunk + (ulong)vz;

        uint s0 = Hash32((uint)seed ^ 0xA341316Cu);
        float continents = Fbm64(gx, gz, ComputeNoiseShift(noiseScale) + 4, 3, 0.5f, s0);
        float height01 = (continents < 0.30f) ? (continents * 0.80f) : continents;
        if (continents > 0.6f) height01 += 0.2f * mountainStrength;
        height01 = Mathf.Clamp01(height01);

        // TerrainData.size.y is heightMultiplier, so return world height:
        return height01 * Mathf.Max(1f, heightMultiplier);
    }
}


