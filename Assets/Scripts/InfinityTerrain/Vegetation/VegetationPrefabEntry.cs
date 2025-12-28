using System;
using UnityEngine;

namespace InfinityTerrain.Vegetation
{
    [Serializable]
    public struct VegetationPrefabEntry
    {
        public string name;
        public VegetationCategory category;
        public GameObject prefab;

        [Min(0f)] public float weight;

        [Header("Transform")]
        [Min(0f)] public float minUniformScale;
        [Min(0f)] public float maxUniformScale;
        public bool alignToNormal;
        public bool randomYaw;
        public float yOffset;
    }
}


