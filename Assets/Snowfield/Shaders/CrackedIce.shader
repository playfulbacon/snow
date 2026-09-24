// World-space layered cracked ice for URP. A view ray is intersected with
// three planes beneath the visible surface, so their relative motion reveals
// real parallax. Triplanar sampling makes mesh UVs and object scale irrelevant.
Shader "Snowfield/Cracked Ice"
{
    Properties
    {
        [MainTexture] [NoScaleOffset] _BaseMap("Ice Surface", 2D) = "white" {}
        _SurfaceScale("Surface Pattern Size (m)", Range(0.05, 100)) = 2
        [MainColor] _BaseColor("Shallow Ice", Color) = (0.54, 0.84, 0.92, 1)
        _DeepColor("Deep Ice", Color) = (0.035, 0.20, 0.30, 1)
        _FrostAmount("Cloudiness", Range(0, 1)) = 0.28
        _FrostScale("Cloud Size (m)", Range(0.05, 100)) = 3.5
        _TextureVariation("Surface De-Tiling", Range(0, 1)) = 0.8
        _VariationScale("Variation Patch Size (m)", Range(0.1, 100)) = 7

        [Normal] [NoScaleOffset] _NormalMap("Surface Normal", 2D) = "bump" {}
        _NormalStrength("Normal Strength", Range(0, 2)) = 0.45
        _NormalDistortion("Crack Distortion", Range(0, 0.15)) = 0.025
        [NoScaleOffset] _RoughnessMap("Surface Roughness", 2D) = "black" {}
        _RoughnessStrength("Roughness Strength", Range(0, 1)) = 0.4

        [NoScaleOffset] _CrackMap("Packed Cracks (RGB)", 2D) = "black" {}
        [Toggle] _UsePackedCracks("Use Packed Crack Texture", Float) = 0
        _CrackTiling("Crack Pattern Size (m)", Range(0.05, 100)) = 0.75
        _CrackWidth("Procedural Crack Width", Range(0.002, 0.2)) = 0.045
        _CrackFeather("Procedural Crack Softness", Range(0.001, 0.15)) = 0.018
        _LayerScale("Layer Scale (near, middle, deep)", Vector) = (0.85, 1.25, 1.8, 0)
        _LayerStrength("Layer Strength (near, middle, deep)", Vector) = (1, 0.72, 0.48, 0)
        _ParallaxDepth("Crack Depth (m)", Range(0, 2)) = 0.18
        _ParallaxClamp("Grazing Angle Clamp", Range(0.05, 1)) = 0.22

        _SurfaceCrackColor("Surface Crack", Color) = (0.78, 0.96, 1, 1)
        _DeepCrackColor("Deep Crack", Color) = (0.015, 0.075, 0.12, 1)
        _CrackContrast("Crack Contrast", Range(0, 2)) = 1.15
        _CrackGlow("Crack Glow", Range(0, 1)) = 0.08

        _Smoothness("Base Smoothness", Range(0, 1)) = 0.92
        _FrostRoughness("Frost Roughness", Range(0, 1)) = 0.72
        _CrackRoughness("Cracked Area Roughness", Range(0, 1)) = 0.58
        _IceIOR("Ice Index of Refraction", Range(1.0, 2.0)) = 1.31
        _SpecularAA("Specular Anti-Aliasing", Range(0, 1)) = 0.65
        _Occlusion("Occlusion", Range(0, 1)) = 1
        _FresnelStrength("Edge Frost", Range(0, 1)) = 0.24
        _FresnelPower("Edge Frost Power", Range(0.5, 12)) = 4

        [HideInInspector] _Cutoff("Cutoff", Range(0, 1)) = 0.5
        [HideInInspector] _Cull("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
        }
        LOD 350

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull [_Cull]
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex IceVertex
            #pragma fragment IceFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            // Ice is a dielectric. Use URP's specular workflow so its Fresnel
            // reflectance can come from the physical ice IOR instead of the
            // metallic workflow's fixed 4% dielectric value.
            #define _SPECULAR_SETUP 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_RoughnessMap);
            SAMPLER(sampler_RoughnessMap);
            TEXTURE2D(_CrackMap);
            SAMPLER(sampler_CrackMap);

            CBUFFER_START(UnityPerMaterial)
                half _SurfaceScale;
                half4 _BaseColor;
                half4 _DeepColor;
                half4 _SurfaceCrackColor;
                half4 _DeepCrackColor;
                half _FrostAmount;
                half _FrostScale;
                half _TextureVariation;
                half _VariationScale;
                half _NormalStrength;
                half _NormalDistortion;
                half _RoughnessStrength;
                half _UsePackedCracks;
                half _CrackTiling;
                half _CrackWidth;
                half _CrackFeather;
                half4 _LayerScale;
                half4 _LayerStrength;
                half _ParallaxDepth;
                half _ParallaxClamp;
                half _CrackContrast;
                half _CrackGlow;
                half _Smoothness;
                half _FrostRoughness;
                half _CrackRoughness;
                half _IceIOR;
                half _SpecularAA;
                half _Occlusion;
                half _FresnelStrength;
                half _FresnelPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 staticLightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half4 fogAndVertexLight : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 4);
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float2 Hash22(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.xx + p3.yz) * p3.zy);
            }

            float ValueNoise(float2 p)
            {
                float2 cell = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash22(cell).x;
                float b = Hash22(cell + float2(1, 0)).x;
                float c = Hash22(cell + float2(0, 1)).x;
                float d = Hash22(cell + float2(1, 1)).x;
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Distance between the two closest Voronoi sites becomes a thin,
            // naturally branching cell boundary: a useful texture-free crack.
            float ProceduralCracks(float2 p, float seed)
            {
                float2 cell = floor(p);
                float2 f = frac(p);
                float nearest = 8.0;
                float secondNearest = 8.0;

                [unroll] for (int y = -1; y <= 1; y++)
                {
                    [unroll] for (int x = -1; x <= 1; x++)
                    {
                        float2 neighbour = float2(x, y);
                        float2 featurePoint = Hash22(cell + neighbour + seed * 19.19);
                        float distanceToPoint = length(neighbour + featurePoint - f);
                        if (distanceToPoint < nearest)
                        {
                            secondNearest = nearest;
                            nearest = distanceToPoint;
                        }
                        else
                        {
                            secondNearest = min(secondNearest, distanceToPoint);
                        }
                    }
                }

                float edgeDistance = secondNearest - nearest;
                return 1.0 - smoothstep(_CrackWidth, _CrackWidth + _CrackFeather, edgeDistance);
            }

            float2 RotateUV(float2 uv, float2 rotation)
            {
                uv -= 0.5;
                return float2(
                    uv.x * rotation.x - uv.y * rotation.y,
                    uv.x * rotation.y + uv.y * rotation.x) + 0.5;
            }

            float3 TriplanarWeights(float3 normalWS)
            {
                float3 weights = pow(abs(normalWS), 8.0);
                return weights / max(weights.x + weights.y + weights.z, 0.0001);
            }

            float4 SampleTextureTriplanar(
                TEXTURE2D_PARAM(textureMap, sampler_textureMap),
                float3 positionWS,
                float3 weights,
                float scale)
            {
                float inverseScale = rcp(max(scale, 0.0001));
                float4 sampleX = SAMPLE_TEXTURE2D(textureMap, sampler_textureMap, positionWS.zy * inverseScale);
                float4 sampleY = SAMPLE_TEXTURE2D(textureMap, sampler_textureMap, positionWS.xz * inverseScale);
                float4 sampleZ = SAMPLE_TEXTURE2D(textureMap, sampler_textureMap, positionWS.xy * inverseScale);
                return sampleX * weights.x + sampleY * weights.y + sampleZ * weights.z;
            }

            float WorldNoise(float3 positionWS, float3 weights, float scale)
            {
                float inverseScale = rcp(max(scale, 0.0001));
                return ValueNoise(positionWS.zy * inverseScale) * weights.x
                    + ValueNoise(positionWS.xz * inverseScale) * weights.y
                    + ValueNoise(positionWS.xy * inverseScale) * weights.z;
            }

            // A second, rigidly rotated copy of the texture is blended in
            // broad world-space patches. The two lattices only realign over a
            // very large distance, removing the obvious repeated square tile
            // without changing the material's metre-based scale.
            float3 RotateTextureSpace(float3 positionWS)
            {
                return float3(
                    dot(positionWS, float3( 0.5938, -0.3284,  0.7346)),
                    dot(positionWS, float3( 0.7346,  0.5938, -0.3284)),
                    dot(positionWS, float3(-0.3284,  0.7346,  0.5938)));
            }

            float4 SampleTextureTriplanarDetiled(
                TEXTURE2D_PARAM(textureMap, sampler_textureMap),
                float3 positionWS,
                float3 weights,
                float scale,
                float variationBlend)
            {
                float4 primary = SampleTextureTriplanar(
                    TEXTURE2D_ARGS(textureMap, sampler_textureMap),
                    positionWS, weights, scale);
                float3 alternatePosition = RotateTextureSpace(positionWS)
                    + float3(11.73, -7.41, 5.29) * scale;
                float4 alternate = SampleTextureTriplanar(
                    TEXTURE2D_ARGS(textureMap, sampler_textureMap),
                    alternatePosition, weights, scale);
                return lerp(primary, alternate, variationBlend);
            }

            float ProceduralCracksTriplanar(float3 positionWS, float3 weights, float layerScale, float seed)
            {
                float coordinateScale = layerScale / max(_CrackTiling, 0.0001);
                float2 rotation = seed < 1.5
                    ? float2(0.9848, 0.1736)
                    : (seed < 2.5 ? float2(0.8192, -0.5736) : float2(0.4226, 0.9063));
                float2 offset = seed * float2(7.31, 13.77);
                float crackX = ProceduralCracks(RotateUV(positionWS.zy * coordinateScale + offset, rotation), seed);
                float crackY = ProceduralCracks(RotateUV(positionWS.xz * coordinateScale + offset, rotation), seed);
                float crackZ = ProceduralCracks(RotateUV(positionWS.xy * coordinateScale + offset, rotation), seed);
                return dot(float3(crackX, crackY, crackZ), weights);
            }

            float PackedCracksTriplanar(float3 positionWS, float3 weights, float layerScale, int channel)
            {
                float scale = _CrackTiling / max(layerScale, 0.0001);
                float3 packed = SampleTextureTriplanar(
                    TEXTURE2D_ARGS(_CrackMap, sampler_CrackMap), positionWS, weights, scale).rgb;
                return channel == 0 ? packed.r : (channel == 1 ? packed.g : packed.b);
            }

            // Intersect the eye ray with three parallel planes below the real
            // surface. Only the tangential displacement is needed for texture
            // lookup. Depth and world coordinates are both measured in metres.
            float3 SampleCrackLayers(
                float3 positionWS,
                float3 geometricNormalWS,
                float3 viewDirectionWS,
                float3 weights,
                float3 distortionWS)
            {
                float viewNormal = dot(viewDirectionWS, geometricNormalWS);
                float facing = max(abs(viewNormal), _ParallaxClamp);
                float3 viewTangent = viewDirectionWS - geometricNormalWS * viewNormal;
                float3 depths = float3(0.28, 0.62, 1.0) * _ParallaxDepth;
                float3 positionNear = positionWS - viewTangent * (depths.x / facing) + distortionWS;
                float3 positionMiddle = positionWS - viewTangent * (depths.y / facing) + distortionWS * 1.4;
                float3 positionDeep = positionWS - viewTangent * (depths.z / facing) + distortionWS * 1.9;
                float3 layers;

                if (_UsePackedCracks > 0.5)
                {
                    layers = float3(
                        PackedCracksTriplanar(positionNear, weights, _LayerScale.x, 0),
                        PackedCracksTriplanar(positionMiddle, weights, _LayerScale.y, 1),
                        PackedCracksTriplanar(positionDeep, weights, _LayerScale.z, 2));
                }
                else
                {
                    layers = float3(
                        ProceduralCracksTriplanar(positionNear, weights, _LayerScale.x, 1.0),
                        ProceduralCracksTriplanar(positionMiddle, weights, _LayerScale.y, 2.0),
                        ProceduralCracksTriplanar(positionDeep, weights, _LayerScale.z, 3.0));
                }

                return layers * _LayerStrength.xyz;
            }

            Varyings IceVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.shadowCoord = GetShadowCoord(positionInputs);
                output.fogAndVertexLight.x = ComputeFogFactor(positionInputs.positionCS.z);
                output.fogAndVertexLight.yzw = VertexLighting(positionInputs.positionWS, normalInputs.normalWS);
                OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
                OUTPUT_SH(normalInputs.normalWS, output.vertexSH);
                return output;
            }

            half4 IceFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 geometricNormalWS = NormalizeNormalPerPixel(input.normalWS);
                float3 triplanarWeights = TriplanarWeights(geometricNormalWS);
                half3 referenceAxis = abs(geometricNormalWS.y) < 0.999h
                    ? half3(0, 1, 0) : half3(1, 0, 0);
                half3 tangentWS = normalize(cross(referenceAxis, geometricNormalWS));
                half3 bitangentWS = cross(geometricNormalWS, tangentWS);
                half3x3 tangentToWorld = half3x3(tangentWS, bitangentWS, geometricNormalWS);
                half3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                float variationNoise = WorldNoise(
                    input.positionWS + float3(19.1, -7.7, 31.3),
                    triplanarWeights, _VariationScale);
                float variationBlend = _TextureVariation
                    * lerp(0.2, 0.8, smoothstep(0.15, 0.85, variationNoise));

                half4 packedNormal = SampleTextureTriplanarDetiled(
                    TEXTURE2D_ARGS(_NormalMap, sampler_NormalMap),
                    input.positionWS, triplanarWeights, _SurfaceScale,
                    variationBlend);
                half3 normalTS = UnpackNormalScale(packedNormal, _NormalStrength);
                half3 normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tangentToWorld));

                float3 distortionWS = (normalWS - geometricNormalWS) * _NormalDistortion;
                float3 layers = SampleCrackLayers(
                    input.positionWS, geometricNormalWS, viewDirectionWS,
                    triplanarWeights, distortionWS);
                float crackMask = saturate(max(layers.x, max(layers.y, layers.z)) * _CrackContrast);
                float layerTotal = max(layers.x + layers.y + layers.z, 0.0001);
                float crackDepth = saturate(dot(layers, float3(0.15, 0.55, 1.0)) / layerTotal);

                half3 surfaceTexture = SampleTextureTriplanarDetiled(
                    TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap),
                    input.positionWS, triplanarWeights, _SurfaceScale,
                    variationBlend).rgb;
                half surfaceRoughness = SampleTextureTriplanarDetiled(
                    TEXTURE2D_ARGS(_RoughnessMap, sampler_RoughnessMap),
                    input.positionWS, triplanarWeights, _SurfaceScale,
                    variationBlend).r;
                float cloud = WorldNoise(input.positionWS, triplanarWeights, _FrostScale) * 0.7
                    + WorldNoise(input.positionWS + 11.7, triplanarWeights, _FrostScale / 2.37) * 0.3;
                half3 iceColor = lerp(_DeepColor.rgb, _BaseColor.rgb, saturate(cloud * _FrostAmount + 0.32));
                iceColor *= surfaceTexture;

                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirectionWS)), _FresnelPower);
                iceColor = lerp(iceColor, _BaseColor.rgb, fresnel * _FresnelStrength);
                half3 crackColor = lerp(_SurfaceCrackColor.rgb, _DeepCrackColor.rgb, crackDepth);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = lerp(iceColor, crackColor, crackMask);
                surfaceData.metallic = 0;
                half iorFresnel = PositivePow(
                    (_IceIOR - 1.0h) / max(_IceIOR + 1.0h, 0.0001h), 2.0h);
                surfaceData.specular = iorFresnel.xxx;
                half clearIceRoughness = 1.0h - _Smoothness;
                half authoredRoughness = max(clearIceRoughness, surfaceRoughness);
                half materialRoughness = lerp(
                    clearIceRoughness, authoredRoughness, _RoughnessStrength);

                // Frost and fractured ice are distributions of many tiny
                // differently oriented surfaces. Blend toward roughness
                // targets so highlights spread out instead of merely losing
                // a small fixed amount of smoothness.
                half frostCoverage = saturate(
                    _FrostAmount * lerp(0.45h, 1.0h, cloud));
                materialRoughness = lerp(
                    materialRoughness,
                    max(materialRoughness, _FrostRoughness),
                    frostCoverage);
                materialRoughness = lerp(
                    materialRoughness,
                    max(materialRoughness, _CrackRoughness),
                    crackMask);
                materialRoughness = saturate(materialRoughness);
                half filteredSmoothness = 1.0h - materialRoughness;
                surfaceData.smoothness = GeometricNormalFiltering(
                    filteredSmoothness, normalWS, 0.25h * _SpecularAA, 0.2h);
                surfaceData.normalTS = normalTS;
                surfaceData.emission = crackColor * crackMask * _CrackGlow;
                surfaceData.occlusion = lerp(_Occlusion, _Occlusion * 0.55, crackMask * crackDepth);
                surfaceData.alpha = 1;
                surfaceData.clearCoatMask = 0;
                surfaceData.clearCoatSmoothness = 1;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDirectionWS;
                inputData.shadowCoord = input.shadowCoord;
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1), input.fogAndVertexLight.x);
                inputData.vertexLighting = input.fogAndVertexLight.yzw;
                inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1;
                return color;
            }
            ENDHLSL
        }

        // The surface itself remains opaque and undisplaced, so URP's stock
        // geometry passes are the exact match for shadows, depth, and baking.
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
        UsePass "Universal Render Pipeline/Lit/Meta"
    }

    Fallback "Universal Render Pipeline/Lit"
}
