using UnityEngine;

public static class GpuTerrainData
{
    // Struct to match the Compute Shader buffer for vegetation
    public struct VegetationInstance
    {
        public Vector3 position;
        public Vector3 scale;
        public float rotationY;
        public int typeID; // 0=Tree, 1=Rock, 2=Grass
    }

    // Constants for configuration
    public const int THREAD_GROUP_SIZE = 8;
}
