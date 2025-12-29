using System.Collections.Generic;
using UnityEngine;

namespace InfinityTerrain.Vegetation
{
    [CreateAssetMenu(menuName = "InfinityTerrain/Vegetation Scatter Settings", fileName = "VegetationScatterSettings")]
    public class VegetationScatterSettings : ScriptableObject
    {
        [Header("General")]
        public int seedOffset = 12345;
        [Tooltip("Skip spawning below this normalized height (0..1). Helps avoid water/beach.")]
        [Range(0f, 1f)] public float minHeight01 = 0.28f;
        [Tooltip("Skip spawning above this normalized height (0..1). Helps avoid snow peaks.")]
        [Range(0f, 1f)] public float maxHeight01 = 0.95f;
        [Tooltip("Skip spawning on slopes above this angle (degrees).")]
        [Range(0f, 89f)] public float maxSlopeDeg = 35f;
        [Tooltip("Extra margin above water surface to keep vegetation from touching water.")]
        public float waterExclusionYOffset = 0.5f;

        [Header("Per-Chunk Budgets (base chunk ~100x100)")]
        [Min(0)] public int treesPerChunk = 18;
        [Min(0)] public int rocksPerChunk = 10;
        [Min(0)] public int plantsPerChunk = 30;
        [Min(0)] public int grassPerChunk = 60;

        [Header("Density (optional, per m²)")]
        [Tooltip("If > 0, Plants will be spawned using a probability/density approach instead of a fixed per-chunk count.\n" +
                 "NOTE (Terrain Details): when using built-in Terrain detail layers, these values are treated as a normalized intensity (0..1):\n" +
                 "- CoverageMode: 1.0 => 255 coverage\n" +
                 "- InstanceCountMode: 1.0 => 16 instances per cell")]
        [Min(0f)] public float plantsDensityPerM2 = 0f;
        [Tooltip("If > 0, Grass will be spawned using a probability/density approach instead of a fixed per-chunk count.\n" +
                 "NOTE (Terrain Details): when using built-in Terrain detail layers, these values are treated as a normalized intensity (0..1):\n" +
                 "- CoverageMode: 1.0 => 255 coverage\n" +
                 "- InstanceCountMode: 1.0 => 16 instances per cell")]
        [Min(0f)] public float grassDensityPerM2 = 0f;

        [Header("Terrain Detail Layers (Built-in Terrain)")]
        [Tooltip("Unity 2022.2+: Use Terrain DetailScatterMode.CoverageMode (0..255) instead of InstanceCountMode (0..16). " +
                 "CoverageMode is generally more scalable for high-density detail rendering.")]
        public bool terrainDetailsUseCoverageMode = true;

        [Tooltip("Detail map resolution per chunk. Higher = denser/finer, but slower to generate. Typical: 128..512.")]
        [Range(32, 1024)] public int terrainDetailResolution = 256;
        [Tooltip("Detail resolution per patch. Typical: 8..32.")]
        [Range(4, 64)] public int terrainDetailResolutionPerPatch = 16;
        [Tooltip("InstanceCountMode: max number of Grass details per cell (0..16). " +
                 "NOTE: If 'grassDensityPerM2' > 0, Terrain details use the density normalized mapping and this cap is ignored.\n" +
                 "CoverageMode: this is used as a generation cap and mapped to 0..255 coverage values.")]
        [Range(0, 16)] public int grassMaxPerCell = 4;
        [Tooltip("InstanceCountMode: max number of Plant details per cell (0..16). " +
                 "NOTE: If 'plantsDensityPerM2' > 0, Terrain details use the density normalized mapping and this cap is ignored.\n" +
                 "CoverageMode: this is used as a generation cap and mapped to 0..255 coverage values.")]
        [Range(0, 16)] public int plantsMaxPerCell = 2;

        [Header("Min Spacing (meters)")]
        [Min(0f)] public float treeMinSpacing = 6f;
        [Min(0f)] public float rockMinSpacing = 4f;
        [Min(0f)] public float plantMinSpacing = 1.75f;
        [Min(0f)] public float grassMinSpacing = 0.6f;

        [Header("Placement Quality")]
        [Tooltip("Higher = more retries to satisfy spacing/filters. Cost increases linearly.")]
        [Min(1)] public int attemptsPerSpawn = 10;

        [Header("Clustering (Trees)")]
        [Tooltip("If enabled, the same tree species will tend to appear in patches (more natural).")]
        public bool enableTreeClustering = true;
        [Tooltip("World-space cell size (meters) for tree clustering noise. Larger => bigger patches.")]
        [Min(1f)] public float treeClusterCellSize = 35f;
        [Tooltip("Higher => fewer places where a tree species is allowed to spawn.")]
        [Range(0f, 1f)] public float treeClusterThreshold = 0.55f;
        [Tooltip("Higher => sharper patch edges.")]
        [Range(0.25f, 8f)] public float treeClusterSharpness = 2.5f;

        [Header("Colliders (Trees/Rocks)")]
        [Tooltip("Create lightweight collider proxy GameObjects for Trees/Rocks so they have physics even when rendered via Terrain trees / GPU instancing.")]
        public bool createColliderProxies = true;
        [Tooltip("If a prefab has no colliders, generate a simple Box/Capsule collider proxy from renderer bounds.")]
        public bool autoGenerateColliderWhenMissing = true;

        [Header("Prefab Entries")]
        public List<VegetationPrefabEntry> prefabs = new List<VegetationPrefabEntry>();
    }
}


