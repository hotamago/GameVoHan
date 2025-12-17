Shader "Custom/ProceduralTerrain"
{
    Properties
    {
        _MainColor ("Main Color", Color) = (1,1,1,1)
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
        
        [Header(Heights)]
        _WaterLevel ("Water Level", Float) = 0.2
        _BeachLevel ("Beach Level", Float) = 0.25
        _GrassLevel ("Grass Level", Float) = 0.55
        _RockLevel ("Rock Level", Float) = 0.75
        _SnowLevel ("Snow Level", Float) = 0.9

        [Header(Colors)]
        _WaterColor ("Water Color", Color) = (0.2, 0.4, 0.8, 1)
        _BeachColor ("Beach Color", Color) = (0.9, 0.8, 0.6, 1)
        _GrassColor ("Grass Color", Color) = (0.2, 0.6, 0.2, 1)
        _RockColor ("Rock Color", Color) = (0.4, 0.4, 0.4, 1)
        _SnowColor ("Snow Color", Color) = (0.95, 0.95, 1.0, 1)
        
        [Header(Noise)]
        _NoiseScale ("Noise ScaleFor Variation", Float) = 1.0
        _RimPower ("Rim Power", Range(0.5, 8.0)) = 3.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard fullforwardshadows vertex:vert

        // Use shader model 3.0 target, to get nicer looking lighting
        #pragma target 3.0

        struct Input
        {
            float3 worldPos;
            float3 worldNormal;
            float3 viewDir;
            float height; // Passed from vertex
        };

        half _Glossiness;
        half _Metallic;
        fixed4 _MainColor;
        
        float _WaterLevel, _BeachLevel, _GrassLevel, _RockLevel, _SnowLevel;
        fixed4 _WaterColor, _BeachColor, _GrassColor, _RockColor, _SnowColor;
        float _NoiseScale, _RimPower;

        // Simple noise function for variation
        float random(float2 uv)
        {
            return frac(sin(dot(uv,float2(12.9898,78.233)))*43758.5453123);
        }

        void vert (inout appdata_full v, out Input o) {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.height = v.vertex.y; // Assume local Y is height, valid for our flat chunks
        }

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            float height = IN.height / 20.0; // Normalize approx (assuming max height 20)
            
            // Slope calculation
            float slope = 1.0 - IN.worldNormal.y;
            
            // Mix slope into height for rugged look
            float mixVal = height - slope * 0.2;

            fixed4 c = _GrassColor;
            
            // Multi-band blending
            // Use smoothstep for soft transitions
            float beachFactor = smoothstep(_WaterLevel, _WaterLevel + 0.05, mixVal);
            float grassFactor = smoothstep(_BeachLevel, _BeachLevel + 0.05, mixVal);
            float rockFactor  = smoothstep(_GrassLevel, _GrassLevel + 0.1, mixVal);
            float snowFactor  = smoothstep(_SnowLevel - 0.1, _SnowLevel, mixVal);
            
            // Slopes always rock/snow
            if (slope > 0.4) {
                 rockFactor = 1.0;
            }

            c = lerp(_WaterColor, _BeachColor, beachFactor);
            c = lerp(c, _GrassColor, grassFactor);
            c = lerp(c, _RockColor, rockFactor);
            c = lerp(c, _SnowColor, snowFactor);
            
            // Add subtle noise
            float noise = random(IN.worldPos.xz * _NoiseScale);
            c.rgb += (noise - 0.5) * 0.05;

            // Rim lighting for "magical" feel
            half rim = 1.0 - saturate(dot (normalize(IN.viewDir), IN.worldNormal));
            c.rgb += _LightColor0.rgb * pow(rim, _RimPower) * 0.2;

            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            
            // Adjust smoothness for water
            if (mixVal < _WaterLevel + 0.05) {
                o.Smoothness = 0.9;
                o.Alpha = 0.8; 
            } else {
                o.Alpha = 1.0;
            }
        }
        ENDCG
    }
    FallBack "Diffuse"
}
