Shader "Custom/InstancedFoliage"
{
    Properties
    {
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        _Smoothness ("Smoothness", Range(0,1)) = 0.2
        _Metallic ("Metallic", Range(0,1)) = 0.0
    }
    
    SubShader
    {
        Tags 
        { 
            "RenderType" = "TransparentCutout" 
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
        }
        LOD 200
        Cull Off // Double sided for grass

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float fogCoord : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float _Cutoff;
                float _Smoothness;
                float _Metallic;
            CBUFFER_END

            // Vegetation instance data structure
            struct VegetationInstance
            {
                float3 position;
                float3 scale;
                float rotationY;
                int typeID;
            };

            // Set via MaterialPropertyBlock in C# code
            StructuredBuffer<VegetationInstance> _VegetationBuffer;

            Varyings vert(Attributes input, uint instanceID : SV_InstanceID)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VegetationInstance data = _VegetationBuffer[instanceID];
                
                // Apply rotation (Y-axis)
                float angle = data.rotationY * 0.0174532924; // Degrees to radians
                float c = cos(angle);
                float s = sin(angle);
                float3x3 rotMatrix = float3x3(
                    c, 0, s,
                    0, 1, 0,
                    -s, 0, c
                );
                
                // Apply scale and rotation
                float3 localPos = mul(rotMatrix, input.positionOS.xyz * data.scale);
                // Apply translation
                float3 worldPos = localPos + data.position;

                VertexPositionInputs vertexInput = GetVertexPositionInputs(worldPos);
                VertexNormalInputs normalInput = GetVertexNormalInputs(mul(rotMatrix, float3(0, 1, 0))); // Rotate normal
                
                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalInput.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.fogCoord = ComputeFogFactor(vertexInput.positionCS.z);
                
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Color;
                
                // Alpha cutoff
                clip(c.a - _Cutoff);
                
                // Lighting
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float NdotL = saturate(dot(input.normalWS, mainLight.direction));
                
                c.rgb *= mainLight.color * NdotL + unity_AmbientSky.rgb;
                
                // Apply fog
                c.rgb = MixFog(c.rgb, input.fogCoord);
                
                return half4(c.rgb, c.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex DepthOnlyVert
            #pragma fragment DepthOnlyFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct VegetationInstance
            {
                float3 position;
                float3 scale;
                float rotationY;
                int typeID;
            };

            StructuredBuffer<VegetationInstance> _VegetationBuffer;

            Varyings DepthOnlyVert(Attributes input, uint instanceID : SV_InstanceID)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VegetationInstance data = _VegetationBuffer[instanceID];
                
                float angle = data.rotationY * 0.0174532924;
                float c = cos(angle);
                float s = sin(angle);
                float3x3 rotMatrix = float3x3(
                    c, 0, s,
                    0, 1, 0,
                    -s, 0, c
                );
                
                float3 localPos = mul(rotMatrix, input.positionOS.xyz * data.scale);
                float3 worldPos = localPos + data.position;
                
                output.positionCS = TransformWorldToHClip(worldPos);
                return output;
            }

            half4 DepthOnlyFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
