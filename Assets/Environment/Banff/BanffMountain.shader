Shader "SnowDays/BanffMountains"
{
    Properties
    {
        [HideInInspector] _ProfileVersion ("Look Revision", Float) = 0
        _RockColor ("Limestone", Color) = (0.19, 0.24, 0.30, 1)
        _SnowColor ("Alpine Snow", Color) = (0.60, 0.68, 0.78, 1)
        _ForestColor ("Lower Forest", Color) = (0.15, 0.20, 0.25, 1)
        _HazeColor ("Distant Haze", Color) = (0.30, 0.39, 0.51, 1)
        _AmbientStrength ("Sky Ambient", Range(0, 1)) = 0.10
        _FillStrength ("Night Relief", Range(0, 2)) = 0.80
        _SnowLine ("Snowline (source metres)", Float) = 1950
        _SnowTransition ("Snowline Transition (metres)", Float) = 280
        _ForestLine ("Treeline (source metres)", Float) = 1850
        _GullyStrength ("Gully Recess", Range(0, 1)) = 0.15
        _HazeStart ("Haze Start (world metres)", Float) = 1200
        _HazeEnd ("Haze End (world metres)", Float) = 6500
        _HazeStrength ("Haze Strength", Range(0, 0.6)) = 0.10
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
        Cull Back
        ZWrite On

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _RockColor;
            half4 _SnowColor;
            half4 _ForestColor;
            half4 _HazeColor;
            half _AmbientStrength;
            half _FillStrength;
            float _SnowLine;
            float _SnowTransition;
            float _ForestLine;
            half _GullyStrength;
            float _HazeStart;
            float _HazeEnd;
            half _HazeStrength;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            // X is uncompressed DEM height; Y optionally holds DEM concavity
            // (0 on ridges/flat ground, 1 in recessed gullies).
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float elevation : TEXCOORD2;
            half gully : TEXCOORD3;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings MountainVertex(Attributes input)
        {
            Varyings output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
            output.positionCS = position.positionCS;
            output.positionWS = position.positionWS;
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.elevation = input.uv.x;
            output.gully = saturate(input.uv.y);
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex MountainVertex
            #pragma fragment MountainFragment
            #pragma multi_compile_instancing

            half4 MountainFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half3 normalWS = normalize(input.normalWS);
                float transition = max(_SnowTransition, 1.0);

                // Interpolated surface normals keep the rock/snow transition
                // continuous across triangles and scenery chunks.
                half alpine = smoothstep(_SnowLine - transition, _SnowLine + transition, input.elevation);
                half ledge = smoothstep(0.52h, 0.95h, normalWS.y);
                half snow = alpine * ledge;
                half forest = 1.0h - smoothstep(_ForestLine - 180.0, _ForestLine + 160.0, input.elevation);

                // Snow follows broad upward-facing slopes and the terrain's
                // actual gullies; there are no drawn contour lines or stripes.
                half gullySnow = input.gully * smoothstep(0.12h, 0.50h, normalWS.y) * 0.30h;
                snow = saturate(max(snow, alpine * gullySnow));
                half3 albedo = lerp(_RockColor.rgb, _ForestColor.rgb, forest * 0.86h);
                albedo = lerp(albedo, _SnowColor.rgb, snow);

                // Broad restrained fill keeps the mountain forms legible
                // while leaving the nearby snow as the brightest surface.
                half3 fillDirection = normalize(half3(-0.72h, 0.48h, -0.50h));
                half fillDiffuse = saturate(dot(normalWS, fillDirection));
                half3 fill = lerp(half3(0.20h, 0.28h, 0.40h), half3(0.85h, 0.93h, 1.02h), fillDiffuse) * _FillStrength;
                // Keep the bright sky probe from filling every cliff shadow.
                half3 ambient = min(max(SampleSH(normalWS), 0.0h) * _AmbientStrength, 0.14h);
                Light mainLight = GetMainLight();
                half direct = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = fill + ambient + mainLight.color * direct * mainLight.distanceAttenuation;
                half recess = 1.0h - input.gully * _GullyStrength * lerp(1.0h, 0.50h, snow);
                half3 color = albedo * lighting * recess;

                // Keep this distant aerial tint light: the fullscreen height
                // fog supplies the much denser valley haze using scene depth.
                float distanceToCamera = distance(input.positionWS, GetCameraPositionWS());
                half haze = smoothstep(_HazeStart, max(_HazeStart + 1.0, _HazeEnd), distanceToCamera) * _HazeStrength;
                color = lerp(color, _HazeColor.rgb, haze);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex MountainVertex
            #pragma fragment MountainDepthFragment
            #pragma multi_compile_instancing

            half MountainDepthFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return input.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormalsOnly" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex MountainVertex
            #pragma fragment MountainDepthNormalsFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            half4 MountainDepthNormalsFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 normalWS = normalize(input.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                    return half4(PackFloat2To888(remappedOctNormalWS), 0.0h);
                #else
                    return half4(normalWS, 0.0h);
                #endif
            }
            ENDHLSL
        }
    }
    Fallback Off
}
