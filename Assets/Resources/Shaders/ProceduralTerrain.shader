Shader "Custom/ProceduralTerrain"
{
    Properties
    {
        _MainColor ("Main Color", Color) = (1,1,1,1)
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
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
        _NoiseScale ("Noise Scale For Variation", Float) = 1.0
        _RimPower ("Rim Power", Range(0.5, 8.0)) = 3.0
        _HeightMultiplier ("Height Multiplier", Float) = 30.0
    }
    
    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque" 
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
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
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 viewDirWS : TEXCOORD2;
                float height : TEXCOORD3;
                float fogCoord : TEXCOORD4;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _MainColor;
                float _Smoothness;
                float _Metallic;
                float _WaterLevel, _BeachLevel, _GrassLevel, _RockLevel, _SnowLevel;
                float4 _WaterColor, _BeachColor, _GrassColor, _RockColor, _SnowColor;
                float _NoiseScale, _RimPower, _HeightMultiplier;
            CBUFFER_END

            // Simple noise function for variation
            float random(float2 uv)
            {
                return frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453123);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);
                
                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalInput.normalWS;
                output.viewDirWS = GetWorldSpaceViewDir(vertexInput.positionWS);
                output.height = input.positionOS.y; // Local Y is height
                output.fogCoord = ComputeFogFactor(vertexInput.positionCS.z);
                
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Normalize height (assuming max height is _HeightMultiplier)
                float height = input.height / _HeightMultiplier;
                
                // Slope calculation
                float slope = 1.0 - saturate(dot(input.normalWS, float3(0, 1, 0)));
                
                // Mix slope into height for rugged look
                float mixVal = height - slope * 0.2;

                half4 c = _GrassColor;
                
                // Multi-band blending with smoothstep for soft transitions
                float beachFactor = smoothstep(_WaterLevel, _WaterLevel + 0.05, mixVal);
                float grassFactor = smoothstep(_BeachLevel, _BeachLevel + 0.05, mixVal);
                float rockFactor = smoothstep(_GrassLevel, _GrassLevel + 0.1, mixVal);
                float snowFactor = smoothstep(_SnowLevel - 0.1, _SnowLevel, mixVal);
                
                // Slopes always rock/snow
                if (slope > 0.4)
                {
                    rockFactor = 1.0;
                }

                c = lerp(_WaterColor, _BeachColor, beachFactor);
                c = lerp(c, _GrassColor, grassFactor);
                c = lerp(c, _RockColor, rockFactor);
                c = lerp(c, _SnowColor, snowFactor);
                
                // Add subtle noise
                float noise = random(input.positionWS.xz * _NoiseScale);
                c.rgb += (noise - 0.5) * 0.05;

                // Lighting
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 lightDir = mainLight.direction;
                float NdotL = saturate(dot(input.normalWS, lightDir));
                
                // Rim lighting for "magical" feel
                float3 viewDir = normalize(input.viewDirWS);
                half rim = 1.0 - saturate(dot(viewDir, input.normalWS));
                c.rgb += mainLight.color.rgb * pow(rim, _RimPower) * 0.2;
                
                // Apply main lighting
                c.rgb *= mainLight.color * NdotL + unity_AmbientSky.rgb;

                // Adjust smoothness for water
                float smoothness = _Smoothness;
                if (mixVal < _WaterLevel + 0.05)
                {
                    smoothness = 0.9;
                }

                // Apply fog
                c.rgb = MixFog(c.rgb, input.fogCoord);

                return half4(c.rgb, 1.0);
            }
            ENDHLSL
        }

        // Shadow pass
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        // Depth pass
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
