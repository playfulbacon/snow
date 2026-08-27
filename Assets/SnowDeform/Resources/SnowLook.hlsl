#ifndef SNOWDAYS_SNOW_LOOK_INCLUDED
#define SNOWDAYS_SNOW_LOOK_INCLUDED

// The shared look of every snow surface in the game. SnowSurface (the ground
// shell) and SnowSculpt (voxel sculptures) both sample the SAME tiling diffuse
// in world space and run the SAME banded lighting, so a snowman reads as the
// same material as the field it stands in. Anything that decides "what snow
// looks like" belongs here; anything about deformation or meshing does not.
//
// Include this inside a ForwardLit pass (it pulls in URP lighting).

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// The terrain's snow layer diffuse. SnowDeformSystem binds it on the ground
// material at runtime; the sculpture material carries the same asset, and
// MainSceneSetup keeps the two in sync with the scene's terrain.
TEXTURE2D(_SnowBaseMap);
SAMPLER(sampler_SnowBaseMap);

// Flat top-down projection: what the ground uses. Tiling is the world size of
// one texture repeat in metres (the terrain layer's tile size).
half3 SnowTexPlanar(float2 worldXZ, float tiling)
{
    return SAMPLE_TEXTURE2D(_SnowBaseMap, sampler_SnowBaseMap, worldXZ / max(tiling, 0.01)).rgb;
}

// Sculptures have no UVs (marching cubes emits position + gradient normal
// only) and steep sides, so they project the same texture on all three axes
// and blend by normal. The Y plane uses the ground's exact mapping, so a flat
// patch of sculpted snow lines up with the field under it.
half3 SnowTexTriplanar(float3 worldPos, half3 normalWS, float tiling)
{
    float3 uvw = worldPos / max(tiling, 0.01);
    half3 cx = SAMPLE_TEXTURE2D(_SnowBaseMap, sampler_SnowBaseMap, uvw.zy).rgb;
    half3 cy = SAMPLE_TEXTURE2D(_SnowBaseMap, sampler_SnowBaseMap, uvw.xz).rgb;
    half3 cz = SAMPLE_TEXTURE2D(_SnowBaseMap, sampler_SnowBaseMap, uvw.xy).rgb;
    half3 w = abs(normalWS);
    w *= w; w *= w; // ^4: narrow blend zones, so faces read as one projection
    w /= max(w.x + w.y + w.z, 0.0001);
    return cx * w.x + cy * w.y + cz * w.z;
}

// Diffuse term quantized into hard bands, edges antialiased by a caller-
// supplied width so they don't crawl at 480p. Thresholds sit at band
// midpoints; d=0 -> 0 and d=1 -> 1 always.
half SnowBand(half d, half w, half bands)
{
    bands = max(bands, 2.0);
    half x = saturate(d) * (bands - 1.0);
    return (floor(x) + smoothstep(0.5 - w, 0.5 + w, frac(x))) / (bands - 1.0);
}

// Band edge width from screen derivatives. Must be computed OUTSIDE any
// varying-iteration light loop - derivatives are undefined there on Metal -
// so it is taken from the main light and reused for the rest.
half SnowBandWidth(half d, half bands)
{
    return clamp(fwidth(d * (max(bands, 2.0) - 1.0)) * 0.75, 0.001, 0.45);
}

// Banded sun over a cold ambient. The band level multiplies the LIVE light
// colour, so noon is white, sunset amber, night dark - only the stepping is
// stylized. inputData must be named `inputData`: the clustered light loop
// macro reads it by name.
half3 SnowShade(InputData inputData, half3 albedo, half occlusion, half3 shadowTint, half bands)
{
    half3 ambient = SampleSH(inputData.normalWS) * shadowTint;
    #if defined(_SCREEN_SPACE_OCCLUSION)
    ambient *= SampleAmbientOcclusion(inputData.normalizedScreenSpaceUV);
    #endif

    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    half dMain = saturate(dot(inputData.normalWS, mainLight.direction)) * mainLight.shadowAttenuation;
    half bandAA = SnowBandWidth(dMain, bands);
    half3 color = albedo * occlusion * (ambient + mainLight.color * SnowBand(dMain, bandAA, bands));

    #if defined(_ADDITIONAL_LIGHTS)
    uint pixelLightCount = GetAdditionalLightsCount();
    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, inputData.positionWS, half4(1, 1, 1, 1));
        half d = saturate(dot(inputData.normalWS, light.direction)) * light.shadowAttenuation;
        color += albedo * occlusion * light.color * light.distanceAttenuation * SnowBand(d, bandAA, bands);
    LIGHT_LOOP_END
    #endif

    return color;
}

#endif // SNOWDAYS_SNOW_LOOK_INCLUDED
