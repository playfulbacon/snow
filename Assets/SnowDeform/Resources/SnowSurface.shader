// Deformable snow surface: a player-following grid mesh draped over the
// terrain in the vertex shader. Depth comes from _SnowHeightTex (CPU-sampled
// terrain heights in a sliding world-space window) plus the undisturbed snow
// depth, minus trampling read from _SnowTrampleTex (stamped by footsteps).
// Deformation is real vertex displacement; per-pixel normals are rebuilt
// from the same fields so prints stay crisp beyond the vertex density.
Shader "SnowDays/SnowSurface"
{
    Properties
    {
        _SnowBaseMap("Snow Texture", 2D) = "white" {}
        _SnowAlbedo("Snow Albedo", Color) = (0.93, 0.95, 0.99, 1)
        [HideInInspector] _SnowDiffuseRemap("Terrain Diffuse Remap", Vector) = (1, 1, 1, 1)
        _SnowShadowTint("Shadow Tint", Color) = (0.72, 0.82, 1, 1)
        _SnowLightBands("Light Bands", Range(2, 6)) = 3
        _SnowTrenchAlbedo("Trench Albedo", Color) = (0.72, 0.78, 0.90, 1)
        _SnowTrenchAO("Trench Darkening", Range(0, 1)) = 0.45
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Globals shared with SnowDeformSystem (Shader.SetGlobal*).
        TEXTURE2D(_SnowTrampleTex);
        TEXTURE2D(_SnowHeightTex);
        SAMPLER(sampler_linear_clamp);

        // xy = window min corner (world XZ), z = 1/windowSize, w = windowSize
        float4 _SnowWindow;
        // x = depth, y = compression (0..1), z = rim height, w = min clearance
        float4 _SnowShape;
        // x = fade start dist, y = fade end dist, zw = mesh center (world XZ)
        float4 _SnowFade;
        // x = trample texel size (world m), y = height texel size (world m),
        // z = skirt depth (m), w = unused
        float4 _SnowTexels;

        // Same diffuse, tiling, and UV origin as the terrain's snow layer.
        TEXTURE2D(_SnowBaseMap);
        SAMPLER(sampler_SnowBaseMap);
        TEXTURE2D(_SnowTerrainNormal0);
        TEXTURE2D(_SnowTerrainNormal1);
        TEXTURE2D(_SnowTerrainNormal2);
        TEXTURE2D(_SnowTerrainNormal3);
        TEXTURE2D(_SnowTerrainNormal4);
        TEXTURE2D(_SnowTerrainNormal5);
        TEXTURE2D(_SnowTerrainNormal6);
        TEXTURE2D(_SnowTerrainNormal7);

        CBUFFER_START(UnityPerMaterial)
        half4 _SnowAlbedo;
        half4 _SnowTrenchAlbedo;
        half4 _SnowDiffuseRemap;
        half4 _SnowShadowTint;
        half _SnowLightBands;
        half _SnowTrenchAO;
        float4 _SnowBaseMap_ST;
        float4 _SnowTerrainNormalRects[8];
        float4 _SnowTerrainNormalST[8];
        float _SnowTerrainNormalCount;
        CBUFFER_END

        float2 SnowUV(float2 worldXZ)
        {
            return (worldXZ - _SnowWindow.xy) * _SnowWindow.z;
        }

        float SnowGround(float2 worldXZ)
        {
            return SAMPLE_TEXTURE2D_LOD(_SnowHeightTex, sampler_linear_clamp, SnowUV(worldXZ), 0).r;
        }

        float SnowTrample(float2 worldXZ)
        {
            return saturate(SAMPLE_TEXTURE2D_LOD(_SnowTrampleTex, sampler_linear_clamp, SnowUV(worldXZ), 0).r);
        }

        float SnowFadeAt(float2 worldXZ)
        {
            return 1.0 - smoothstep(_SnowFade.x, _SnowFade.y, distance(worldXZ, _SnowFade.zw));
        }

        // Fade coverage as well as displacement. Otherwise the lowered shell
        // still ends in an opaque square, exposing small normal/height
        // differences at the edge. Depth and visible coverage stay identical.
        void SnowClip(float2 worldXZ, float2 pixelPosition)
        {
            float coverage = SnowFadeAt(worldXZ);
            clip(coverage - max(0.0001, InterleavedGradientNoise(pixelPosition, 0)));
        }

        // Raised ridge of pushed-aside snow around prints: driven by the
        // trample gradient, masked off inside the print itself.
        float SnowRim(float2 worldXZ, float trample)
        {
            float e = _SnowTexels.x * 2.0;
            float tx = SnowTrample(worldXZ + float2(e, 0)) - SnowTrample(worldXZ - float2(e, 0));
            float tz = SnowTrample(worldXZ + float2(0, e)) - SnowTrample(worldXZ - float2(0, e));
            float grad = length(float2(tx, tz)) / (2.0 * e);
            return saturate(grad * 0.12) * (1.0 - trample) * _SnowShape.z;
        }

        // Vertical offset of the snow surface above the sampled ground.
        float SnowOffset(float trample, float fade)
        {
            return _SnowShape.x * (1.0 - _SnowShape.y * trample) * fade + _SnowShape.w;
        }

        // Full displaced surface height at a world XZ. Skirt verts hang below
        // by skirtY (0 for surface verts, -1 for skirt bottoms).
        float3 SnowDisplace(float3 positionOS, out float trample, out float fade)
        {
            float3 worldPos = TransformObjectToWorld(float3(positionOS.x, 0, positionOS.z));
            trample = SnowTrample(worldPos.xz);
            fade = SnowFadeAt(worldPos.xz);
            float y = SnowGround(worldPos.xz)
                + SnowOffset(trample, fade)
                + SnowRim(worldPos.xz, trample) * fade
                + positionOS.y * _SnowTexels.z;
            return float3(worldPos.x, y, worldPos.z);
        }

        float3 SampleTerrainNormal(int tile, float2 uv)
        {
            float3 packedNormal;
            switch (tile)
            {
                case 0: packedNormal = SAMPLE_TEXTURE2D_LOD(_SnowTerrainNormal0, sampler_linear_clamp, uv, 0).rgb; break;
                case 1: packedNormal = SAMPLE_TEXTURE2D_LOD(_SnowTerrainNormal1, sampler_linear_clamp, uv, 0).rgb; break;
                case 2: packedNormal = SAMPLE_TEXTURE2D_LOD(_SnowTerrainNormal2, sampler_linear_clamp, uv, 0).rgb; break;
                case 3: packedNormal = SAMPLE_TEXTURE2D_LOD(_SnowTerrainNormal3, sampler_linear_clamp, uv, 0).rgb; break;
                case 4: packedNormal = SAMPLE_TEXTURE2D_LOD(_SnowTerrainNormal4, sampler_linear_clamp, uv, 0).rgb; break;
                case 5: packedNormal = SAMPLE_TEXTURE2D_LOD(_SnowTerrainNormal5, sampler_linear_clamp, uv, 0).rgb; break;
                case 6: packedNormal = SAMPLE_TEXTURE2D_LOD(_SnowTerrainNormal6, sampler_linear_clamp, uv, 0).rgb; break;
                default: packedNormal = SAMPLE_TEXTURE2D_LOD(_SnowTerrainNormal7, sampler_linear_clamp, uv, 0).rgb; break;
            }
            return normalize(packedNormal * 2.0 - 1.0);
        }

        // Use Unity's own terrain normal field. Differentiating the resampled
        // height window makes small cell slopes jump between lighting bands.
        // Per-pixel tile selection also keeps the basis aligned at tile seams.
        float3 SnowGroundNormal(float2 worldXZ)
        {
            if (_SnowTerrainNormalCount > 0)
            {
                int closestTile = 0;
                float closestDistance = 1e20;
                [unroll] for (int tile = 0; tile < 8; tile++)
                {
                    if (tile >= _SnowTerrainNormalCount) break;
                    float4 rect = _SnowTerrainNormalRects[tile];
                    float2 delta = max(max(rect.xy - worldXZ, worldXZ - rect.zw), 0);
                    float distanceSquared = dot(delta, delta);
                    if (distanceSquared < closestDistance)
                    {
                        closestTile = tile;
                        closestDistance = distanceSquared;
                    }
                }
                float4 st = _SnowTerrainNormalST[closestTile];
                return SampleTerrainNormal(closestTile, worldXZ * st.xy + st.zw);
            }

            float eh = _SnowTexels.y;
            float gx = (SnowGround(worldXZ + float2(eh, 0)) - SnowGround(worldXZ - float2(eh, 0))) / (2.0 * eh);
            float gz = (SnowGround(worldXZ + float2(0, eh)) - SnowGround(worldXZ - float2(0, eh))) / (2.0 * eh);
            return normalize(float3(-gx, 1.0, -gz));
        }

        // Add the print's slope to the same base normal used by the terrain.
        float3 SnowNormal(float2 worldXZ, float fade)
        {
            float3 groundNormal = SnowGroundNormal(worldXZ);
            float et = _SnowTexels.x * 1.5;
            float tx = (SnowTrample(worldXZ + float2(et, 0)) - SnowTrample(worldXZ - float2(et, 0))) / (2.0 * et);
            float tz = (SnowTrample(worldXZ + float2(0, et)) - SnowTrample(worldXZ - float2(0, et))) / (2.0 * et);
            float k = -_SnowShape.x * _SnowShape.y * fade * groundNormal.y;
            return normalize(groundNormal - float3(k * tx, 0, k * tz));
        }

        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SnowVertex
            #pragma fragment SnowFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "SnowLighting.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half fogFactor : TEXCOORD1;
                half3 vertexLighting : TEXCOORD2;
            };

            Varyings SnowVertex(Attributes input)
            {
                Varyings output;
                float trample, fade;
                float3 positionWS = SnowDisplace(input.positionOS, trample, fade);
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                output.vertexLighting = VertexLighting(positionWS, SnowNormal(positionWS.xz, fade));
                return output;
            }

            half4 SnowFragment(Varyings input) : SV_Target
            {
                SnowClip(input.positionWS.xz, input.positionCS.xy);
                float fade = SnowFadeAt(input.positionWS.xz);
                float trample = SnowTrample(input.positionWS.xz) * fade;
                float3 positionWS = input.positionWS;
                float3 normalWS = SnowNormal(positionWS.xz, fade);

                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                float4 shadowCoord = ComputeScreenPos(TransformWorldToHClip(positionWS));
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
                #else
                float4 shadowCoord = float4(0, 0, 0, 0);
                #endif

                // Minimal InputData: the clustered light loop reads the
                // screen UV and position from a variable with this name.
                InputData inputData = (InputData)0;
                inputData.positionWS = positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
                inputData.shadowCoord = shadowCoord;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                inputData.bakedGI = SampleSH(normalWS);
                inputData.vertexLighting = input.vertexLighting;
                inputData.fogCoord = InitializeInputDataFog(float4(positionWS, 1), input.fogFactor);

                // Preserve the snow's stylized palette and light bands.
                // The terrain now calls the very same lighting function.
                half4 tex = SAMPLE_TEXTURE2D(_SnowBaseMap, sampler_SnowBaseMap,
                    positionWS.xz * _SnowBaseMap_ST.xy + _SnowBaseMap_ST.zw);
                half3 albedo = tex.rgb * _SnowDiffuseRemap.rgb *
                    lerp(_SnowAlbedo.rgb, _SnowTrenchAlbedo.rgb, trample);
                half occlusion = 1.0 - trample * _SnowTrenchAO;
                half3 color = SnowLighting(inputData, albedo, occlusion, _SnowShadowTint.rgb, _SnowLightBands);
                return half4(MixFog(color, inputData.fogCoord), 1);
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
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SnowShadowVertex
            #pragma fragment SnowShadowFragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 worldXZ : TEXCOORD0;
            };

            Varyings SnowShadowVertex(Attributes input)
            {
                Varyings output;
                float trample, fade;
                float3 positionWS = SnowDisplace(input.positionOS, trample, fade);
                float3 normalWS = SnowGroundNormal(positionWS.xz);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                output.positionCS = ApplyShadowClamping(positionCS);
                output.worldXZ = positionWS.xz;
                return output;
            }

            half4 SnowShadowFragment(Varyings input) : SV_Target
            {
                // The terrain supplies shadows in the transition. Casting
                // from the shell onto terrain exposed by its coverage fade
                // produces an artificial dark ring around the window.
                clip(SnowFadeAt(input.worldXZ) - 0.9999);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SnowDepthVertex
            #pragma fragment SnowDepthFragment

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 worldXZ : TEXCOORD0;
            };

            Varyings SnowDepthVertex(Attributes input)
            {
                Varyings output;
                float trample, fade;
                float3 positionWS = SnowDisplace(input.positionOS, trample, fade);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.worldXZ = positionWS.xz;
                return output;
            }

            half SnowDepthFragment(Varyings input) : SV_Target
            {
                SnowClip(input.worldXZ, input.positionCS.xy);
                return input.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SnowDepthNormalsVertex
            #pragma fragment SnowDepthNormalsFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float fade : TEXCOORD1;
            };

            Varyings SnowDepthNormalsVertex(Attributes input)
            {
                Varyings output;
                float trample, fade;
                float3 positionWS = SnowDisplace(input.positionOS, trample, fade);
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.fade = fade;
                return output;
            }

            half4 SnowDepthNormalsFragment(Varyings input) : SV_Target
            {
                SnowClip(input.positionWS.xz, input.positionCS.xy);
                float3 normalWS = SnowNormal(input.positionWS.xz, SnowFadeAt(input.positionWS.xz));
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                    return half4(PackFloat2To888(remappedOctNormalWS), 0);
                #else
                    return half4(normalWS, 0);
                #endif
            }
            ENDHLSL
        }
    }
    Fallback Off
}
