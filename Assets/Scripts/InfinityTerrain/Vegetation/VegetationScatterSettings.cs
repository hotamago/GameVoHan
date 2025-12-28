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

        [Header("Min Spacing (meters)")]
        [Min(0f)] public float treeMinSpacing = 6f;
        [Min(0f)] public float rockMinSpacing = 4f;
        [Min(0f)] public float plantMinSpacing = 1.75f;
        [Min(0f)] public float grassMinSpacing = 0.6f;

        [Header("Placement Quality")]
        [Tooltip("Higher = more retries to satisfy spacing/filters. Cost increases linearly.")]
        [Min(1)] public int attemptsPerSpawn = 10;

        [Header("Prefab Entries")]
        public List<VegetationPrefabEntry> prefabs = new List<VegetationPrefabEntry>();
    }
}


