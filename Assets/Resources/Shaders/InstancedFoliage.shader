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
                #pragma vertex shadow_vert
                #pragma fragment shadow_frag
                #pragma multi_compile_instancing
                #pragma multi_compile _ _CASTING_PUNCTUAL_LIGHT_SHADOW

                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

                // Same inputs as Forward pass
                struct Attributes
                {
                    float4 positionOS : POSITION;
                    float3 normalOS : NORMAL;
                    float2 uv : TEXCOORD0;
                    UNITY_VERTEX_INPUT_INSTANCE_ID
                };

                struct Varyings
                {
                    float4 positionCS : SV_POSITION;
                    float2 uv : TEXCOORD0;
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

                struct VegetationInstance
                {
                    float3 position;
                    float3 scale;
                    float rotationY;
                    int typeID;
                };

                StructuredBuffer<VegetationInstance> _VegetationBuffer;

                float3 _LightDirection;
                float3 _LightPosition;

                Varyings shadow_vert(Attributes input, uint instanceID : SV_InstanceID)
                {
                    Varyings output;
                    UNITY_SETUP_INSTANCE_ID(input);
                    UNITY_TRANSFER_INSTANCE_ID(input, output);

                    VegetationInstance data = _VegetationBuffer[instanceID];

                    // Rotation
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
                    float3 normalWS = mul(rotMatrix, input.normalOS);

                    // Calculate shadow position with bias
                    float3 positionWS = worldPos;
                    float3 normalWS_Bias = normalWS;

                    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS_Bias, _LightDirection));

                    #if UNITY_REVERSED_Z
                        positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                    #else
                        positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                    #endif

                    output.positionCS = positionCS;
                    output.uv = TRANSFORM_TEX(input.uv, _MainTex);

                    return output;
                }

                half4 shadow_frag(Varyings input) : SV_Target
                {
                    UNITY_SETUP_INSTANCE_ID(input);
                    half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Color;
                    clip(c.a - _Cutoff);
                    return 0;
                }
                ENDHLSL
            }
    }
    FallBack "Universal Render Pipeline/Lit"
}
