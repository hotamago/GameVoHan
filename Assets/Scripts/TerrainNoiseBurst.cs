using Unity.Burst;
using Unity.Mathematics;
using Unity.Collections;

/// <summary>
/// Burst-compiled noise functions for high-performance terrain height calculations
/// </summary>
[BurstCompile]
public static class TerrainNoiseBurst
{
    [BurstCompile]
    public static float GetTerrainHeight(
        float worldX, 
        float worldZ, 
        float noiseScale, 
        float heightMultiplier, 
        float mountainStrength, 
        int seed)
    {
        float2 worldPos = new float2(worldX, worldZ);
        float2 noisePos = (worldPos + new float2(seed * 100.0f, seed * 100.0f)) * noiseScale;
        
        float continents = Fbm(noisePos * 0.1f, 3, 0.5f, 2.0f);
        
        float height = 0;
        if (continents < 0.3f) 
            height = continents * 0.8f;
        else 
            height = continents;
        
        // Add a small buffer for mountains approximation
        if (continents > 0.6f) 
            height += 0.2f * mountainStrength;
        
        return height * heightMultiplier;
    }
    
    [BurstCompile]
    private static float Fbm(float2 st, int oct, float pers, float lac)
    {
        float value = 0.0f;
        float amplitude = 0.5f;
        float frequency = 1.0f;
        float maxValue = 0.0f;
        
        for (int i = 0; i < oct; i++)
        {
            value += amplitude * Noise(st * frequency);
            maxValue += amplitude;
            st += new float2(100.0f, 100.0f);
            frequency *= lac;
            amplitude *= pers;
        }
        return value / maxValue;
    }
    
    [BurstCompile]
    private static float Noise(float2 st)
    {
        float2 i = math.floor(st);
        float2 f = st - i;
        
        float a = RandomNoise(i);
        float b = RandomNoise(i + new float2(1.0f, 0.0f));
        float c = RandomNoise(i + new float2(0.0f, 1.0f));
        float d = RandomNoise(i + new float2(1.0f, 1.0f));
        
        float2 u = new float2(
            f.x * f.x * (3.0f - 2.0f * f.x), 
            f.y * f.y * (3.0f - 2.0f * f.y)
        );
        
        return math.lerp(
            math.lerp(a, b, u.x),
            math.lerp(c, d, u.x),
            u.y
        );
    }
    
    [BurstCompile]
    private static float RandomNoise(float2 st)
    {
        return Frac(math.sin(math.dot(st, new float2(12.9898f, 78.233f))) * 43758.5453123f);
    }
    
    [BurstCompile]
    private static float Frac(float v)
    {
        return v - math.floor(v);
    }
}


