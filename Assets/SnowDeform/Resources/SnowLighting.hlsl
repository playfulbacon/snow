#ifndef SNOW_DAYS_LIGHTING_INCLUDED
#define SNOW_DAYS_LIGHTING_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// Shared by the terrain and the displaced shell so direct light, ambient
// tint, shadows, and light falloff cannot give the two surfaces different looks.
half SnowBand(half diffuse, half width, half bands)
{
    half steps = max(bands, 2.0h) - 1.0h;
    half x = saturate(diffuse) * steps;
    return (floor(x) + smoothstep(0.5h - width, 0.5h + width, frac(x))) / steps;
}

half3 SnowDirectLight(Light light, half3 normalWS, half width, half bands)
{
    half diffuse = saturate(dot(normalWS, light.direction)) * light.shadowAttenuation;
    return light.color * light.distanceAttenuation * SnowBand(diffuse, width, bands);
}

half3 SnowLighting(InputData inputData, half3 albedo, half occlusion, half3 shadowTint, half bands)
{
    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    half diffuse = saturate(dot(inputData.normalWS, mainLight.direction)) * mainLight.shadowAttenuation;
    // Derivatives stay outside varying-iteration light loops (required on Metal).
    half width = clamp(fwidth(diffuse * (max(bands, 2.0h) - 1.0h)) * 0.75h, 0.001h, 0.45h);
    half3 lighting = inputData.bakedGI * shadowTint;
    #if defined(_SCREEN_SPACE_OCCLUSION)
        lighting *= SampleAmbientOcclusion(inputData.normalizedScreenSpaceUV);
    #endif

    uint meshRenderingLayers = GetMeshRenderingLayer();
    #if defined(_LIGHT_LAYERS)
    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
    #endif
        lighting += SnowDirectLight(mainLight, inputData.normalWS, width, bands);

    #if defined(_ADDITIONAL_LIGHTS)
        #if USE_CLUSTER_LIGHT_LOOP
        UNITY_LOOP for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
        {
            CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
            Light light = GetAdditionalLight(lightIndex, inputData.positionWS, inputData.shadowMask);
            #if defined(_LIGHT_LAYERS)
            if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
            #endif
                lighting += SnowDirectLight(light, inputData.normalWS, width, bands);
        }
        #endif

        uint pixelLightCount = GetAdditionalLightsCount();
        LIGHT_LOOP_BEGIN(pixelLightCount)
            Light light = GetAdditionalLight(lightIndex, inputData.positionWS, inputData.shadowMask);
            #if defined(_LIGHT_LAYERS)
            if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
            #endif
                lighting += SnowDirectLight(light, inputData.normalWS, width, bands);
        LIGHT_LOOP_END
    #endif
    #if defined(_ADDITIONAL_LIGHTS_VERTEX)
        lighting += inputData.vertexLighting;
    #endif

    return albedo * occlusion * lighting;
}

#endif
