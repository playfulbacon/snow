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
        _SnowTexTiling("Snow Texture Tile Size (m)", Float) = 32
        _SnowAlbedo("Snow Albedo", Color) = (0.93, 0.95, 0.99, 1)
        _SnowTrenchAlbedo("Trench Albedo", Color) = (0.72, 0.78, 0.90, 1)
        _SnowTrenchAO("Trench Darkening", Range(0, 1)) = 0.45
        // Multiplies the ambient probe so shadowed snow reads cold.
        _SnowShadowTint("Shadow Tint", Color) = (0.72, 0.82, 1.0, 1)
        _SnowLightBands("Light Bands", Range(2, 6)) = 3
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

        CBUFFER_START(UnityPerMaterial)
        half4 _SnowAlbedo;
        half4 _SnowTrenchAlbedo;
        half4 _SnowShadowTint;
        half _SnowLightBands;
        half _SnowTrenchAO;
        float _SnowTexTiling;
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

        // Per-pixel normal from central differences of the surface fields.
        // Ground uses the height texel, trampling a slightly wider kernel so
        // print walls light softly instead of aliasing.
        float3 SnowNormal(float2 worldXZ, float fade)
        {
            float eh = _SnowTexels.y;
            float et = _SnowTexels.x * 1.5;
            float gx = (SnowGround(worldXZ + float2(eh, 0)) - SnowGround(worldXZ - float2(eh, 0))) / (2.0 * eh);
            float gz = (SnowGround(worldXZ + float2(0, eh)) - SnowGround(worldXZ - float2(0, eh))) / (2.0 * eh);
            float tx = (SnowTrample(worldXZ + float2(et, 0)) - SnowTrample(worldXZ - float2(et, 0))) / (2.0 * et);
            float tz = (SnowTrample(worldXZ + float2(0, et)) - SnowTrample(worldXZ - float2(0, et))) / (2.0 * et);
            float k = -_SnowShape.x * _SnowShape.y * fade;
            return normalize(float3(-(gx + k * tx), 1.0, -(gz + k * tz)));
        }

        // Ground-slope-only normal, cheap enough for shadow bias.
        float3 SnowGroundNormal(float2 worldXZ)
        {
            float eh = _SnowTexels.y;
            float gx = (SnowGround(worldXZ + float2(eh, 0)) - SnowGround(worldXZ - float2(eh, 0))) / (2.0 * eh);
            float gz = (SnowGround(worldXZ + float2(0, eh)) - SnowGround(worldXZ - float2(0, eh))) / (2.0 * eh);
            return normalize(float3(-gx, 1.0, -gz));
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
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            // Snow albedo + banded lighting shared with SnowSculpt, so the ground
            // and the sculptures standing on it are the same material.
            #include "SnowLook.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 vertexData : TEXCOORD1; // x = trample, y = fade, z = fogFactor
            };

            Varyings SnowVertex(Attributes input)
            {
                Varyings output;
                float trample, fade;
                float3 positionWS = SnowDisplace(input.positionOS, trample, fade);
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.vertexData = float3(trample, fade, ComputeFogFactor(output.positionCS.z));
                return output;
            }

            half4 SnowFragment(Varyings input) : SV_Target
            {
                float trample = SnowTrample(input.positionWS.xz);
                float fade = input.vertexData.y;
                float3 positionWS = input.positionWS;
                float3 normalWS = SnowNormal(positionWS.xz, fade);

                #if defined(MAIN_LIGHT_CALCULATE_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
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

                // Flat projection here: the ground is the reference mapping
                // that sculptures triplanar-match. Compressed snow in prints
                // reads darker and bluer.
                half3 texCol = SnowTexPlanar(positionWS.xz, _SnowTexTiling);
                half3 albedo = texCol * lerp(_SnowAlbedo.rgb, _SnowTrenchAlbedo.rgb, trample);
                half occlusion = 1.0 - trample * _SnowTrenchAO;

                half3 color = SnowShade(inputData, albedo, occlusion, _SnowShadowTint.rgb, _SnowLightBands);

                color = MixFog(color, input.vertexData.z);
                return half4(color, 1);
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
                return output;
            }

            half4 SnowShadowFragment(Varyings input) : SV_Target
            {
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
            };

            Varyings SnowDepthVertex(Attributes input)
            {
                Varyings output;
                float trample, fade;
                output.positionCS = TransformWorldToHClip(SnowDisplace(input.positionOS, trample, fade));
                return output;
            }

            half SnowDepthFragment(Varyings input) : SV_Target
            {
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
                return half4(SnowNormal(input.positionWS.xz, input.fade), 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
