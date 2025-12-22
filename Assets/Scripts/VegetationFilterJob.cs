using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Blittable version of VegetationInstance for Burst jobs
/// </summary>
public struct VegetationInstanceBlittable
{
    public float3 position;
    public float3 scale;
    public float rotationY;
    public int typeID;
    
    public static VegetationInstanceBlittable FromVegetationInstance(GpuTerrainData.VegetationInstance v)
    {
        return new VegetationInstanceBlittable
        {
            position = new float3(v.position.x, v.position.y, v.position.z),
            scale = new float3(v.scale.x, v.scale.y, v.scale.z),
            rotationY = v.rotationY,
            typeID = v.typeID
        };
    }
    
    public GpuTerrainData.VegetationInstance ToVegetationInstance()
    {
        return new GpuTerrainData.VegetationInstance
        {
            position = new UnityEngine.Vector3(position.x, position.y, position.z),
            scale = new UnityEngine.Vector3(scale.x, scale.y, scale.z),
            rotationY = rotationY,
            typeID = typeID
        };
    }
}

/// <summary>
/// Burst-optimized job to filter vegetation instances by type
/// </summary>
[BurstCompile]
public struct VegetationFilterJob : IJob
{
    [ReadOnly] public NativeArray<VegetationInstanceBlittable> inputVegetation;
    public NativeList<VegetationInstanceBlittable> trees;
    public NativeList<VegetationInstanceBlittable> rocks;
    public NativeList<VegetationInstanceBlittable> grasses;
    
    public void Execute()
    {
        for (int i = 0; i < inputVegetation.Length; i++)
        {
            var veg = inputVegetation[i];
            switch (veg.typeID)
            {
                case 0: // Tree
                    trees.Add(veg);
                    break;
                case 1: // Rock
                    rocks.Add(veg);
                    break;
                case 2: // Grass
                    grasses.Add(veg);
                    break;
            }
        }
    }
}

