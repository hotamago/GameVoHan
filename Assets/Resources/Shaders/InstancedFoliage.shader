Shader "Custom/InstancedFoliage"
{
    Properties
    {
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200
        Cull Off // Double sided for grass

        CGPROGRAM
        // Physically based Standard lighting model
        #pragma surface surf Standard addshadow fullforwardshadows
        #pragma multi_compile_instancing
        #pragma instancing_options procedural:setup

        #pragma target 5.0

        sampler2D _MainTex;
        fixed4 _Color;
        fixed _Cutoff;

        struct Input
        {
            float2 uv_MainTex;
        };

        struct VegetationInstance {
            float3 position;
            float3 scale;
            float rotationY;
            int typeID;
        };

        #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            StructuredBuffer<VegetationInstance> vegetationBuffer;
        #endif

        void setup()
        {
            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                VegetationInstance data = vegetationBuffer[unity_InstanceID];

                float3 position = data.position;
                float3 scale = data.scale;
                float rotation = data.rotationY * 0.0174532924; // Degrees to radians

                // Create rotation matrix (Y-axis)
                float c = cos(rotation);
                float s = sin(rotation);
                float4x4 rotationMatrix = float4x4(
                    c, 0, s, 0,
                    0, 1, 0, 0,
                    -s, 0, c, 0,
                    0, 0, 0, 1
                );

                // Create scale matrix
                float4x4 scaleMatrix = float4x4(
                   scale.x, 0, 0, 0,
                   0, scale.y, 0, 0,
                   0, 0, scale.z, 0,
                   0, 0, 0, 1
                );

                // Create translation matrix
                float4x4 translationMatrix = float4x4(
                   1, 0, 0, position.x,
                   0, 1, 0, position.y,
                   0, 0, 1, position.z,
                   0, 0, 0, 1
                );

                // Combine: T * R * S
                unity_ObjectToWorld = mul(translationMatrix, mul(rotationMatrix, scaleMatrix));
            #endif
        }

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
            // Basic clip for leaves
            clip(c.a - _Cutoff);
            
            o.Albedo = c.rgb;
            o.Metallic = 0.0;
            o.Smoothness = 0.2;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
