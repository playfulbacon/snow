#ifndef SNOWDAYS_SNOW_LOOK_INCLUDED
#define SNOWDAYS_SNOW_LOOK_INCLUDED

// The shared look of every snow surface in the game. SnowSurface (the ground
// shell) and SnowSculpt (voxel sculptures) both sample the SAME tiling diffuse
// in world space and run the SAME banded lighting, so a snowman reads as the
// same material as the field it stands in. Anything that decides "what snow
// looks like" belongs here; anything about deformation or meshing does not.
//
// Include this inside a ForwardLit pass (it pulls in URP lighting).

#include "SnowLighting.hlsl"

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

// Preserve the sculpture shading entry point while sharing the terrain and
// deformable snow lighting implementation.
half3 SnowShade(InputData inputData, half3 albedo, half occlusion, half3 shadowTint, half bands)
{
    inputData.bakedGI = SampleSH(inputData.normalWS);
    return SnowLighting(inputData, albedo, occlusion, shadowTint, bands);
}

#endif // SNOWDAYS_SNOW_LOOK_INCLUDED
