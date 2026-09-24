#ifndef SNOW_DAYS_TERRAIN_SNOW_PASSES_INCLUDED
#define SNOW_DAYS_TERRAIN_SNOW_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "SnowLighting.hlsl"

CBUFFER_START(SnowTerrainMaterial)
    half4 _SnowAlbedo;
    half4 _SnowShadowTint;
    half _SnowLightBands;
CBUFFER_END

half4 SnowTerrainFragment(InputData inputData, half3 albedo, half metallic,
    half3 specular, half smoothness, half occlusion, half3 emission, half alpha)
{
    return half4(SnowLighting(inputData, albedo * _SnowAlbedo.rgb, occlusion,
        _SnowShadowTint.rgb, _SnowLightBands) + emission, alpha);
}

// Keep URP's terrain UVs, splat weights, normals, holes, decals, GI, and fog.
// Lighting.hlsl is already included, so only the terrain fragment's final
// lighting call is redirected. Its alpha remains the terrain layer weight.
#define UniversalFragmentPBR SnowTerrainFragment
#include "Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitPasses.hlsl"
#undef UniversalFragmentPBR

#endif
