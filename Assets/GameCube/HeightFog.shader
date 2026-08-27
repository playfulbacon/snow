Shader "Hidden/SnowDays/HeightFog"
{
    Properties
    {
        _FogColor ("Fog Color", Color) = (0.75, 0.81, 0.89, 1)
        _FogDensity ("Density", Range(0, 0.2)) = 0.012
        _FogBaseHeight ("Base Height (world Y)", Float) = 0
        _FogHeightFalloff ("Height Falloff", Range(0.001, 1)) = 0.06
        _FogStartDistance ("Start Distance", Float) = 20
        _FogMaxDistance ("Max Distance", Float) = 3000
        _FogNoiseStrength ("Noise Strength (0 = off)", Range(0, 1)) = 0.5
        _FogNoiseScale ("Noise Scale (per meter)", Float) = 0.008
        _FogNoiseVelocity ("Noise Wind (X, Z, Vertical) m/s", Vector) = (3, 1, 0.3, 0)
        _FogNoiseRange ("Near-Field Range (m)", Float) = 600
        _FogSunStrength ("Sun Scatter Strength", Range(0, 1)) = 0.6
        _FogSunPower ("Sun Scatter Tightness", Range(1, 64)) = 8
        _FogLightInfluence ("Light Color Influence", Range(0, 1)) = 0.5
        _FogShaftStrength ("Light Shaft Strength (0 = off)", Range(0, 1)) = 0.7
        _FogPointLightStrength ("Point Light Scatter (0 = off)", Range(0, 20)) = 10
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "HeightFog"

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // _MAIN_LIGHT_SHADOWS_SCREEN is deliberately not declared: the
            // screen-space shadow texture holds the receiver geometry's
            // shadows, which is wrong for points sampled mid-air along a ray.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP

            #pragma vertex Vert
            #pragma fragment Frag

            float4 _FogColor;
            float _FogDensity;
            float _FogBaseHeight;
            float _FogHeightFalloff;
            float _FogStartDistance;
            float _FogMaxDistance;
            float _FogNoiseStrength;
            float _FogNoiseScale;
            float4 _FogNoiseVelocity;
            float _FogNoiseRange;
            float _FogSunStrength;
            float _FogSunPower;
            float _FogLightInfluence;
            float _FogShaftStrength;
            float _FogPointLightStrength;
            // Set per-draw by SnowDaysFogFeature, not material properties.
            float _FogAdditionalLightsCount;
            float _FogShadowDistance;

            float Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float ValueNoise3(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = Hash31(i);
                float n100 = Hash31(i + float3(1, 0, 0));
                float n010 = Hash31(i + float3(0, 1, 0));
                float n110 = Hash31(i + float3(1, 1, 0));
                float n001 = Hash31(i + float3(0, 0, 1));
                float n101 = Hash31(i + float3(1, 0, 1));
                float n011 = Hash31(i + float3(0, 1, 1));
                float n111 = Hash31(i + float3(1, 1, 1));
                return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                            lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
            }

            // World-anchored fog bank pattern: 3D value noise advected by the
            // wind, squashed vertically so banks read as wide pancakes rather
            // than blobs.
            float FogNoise(float3 p)
            {
                float3 wind = float3(_FogNoiseVelocity.x, _FogNoiseVelocity.z, _FogNoiseVelocity.y);
                float3 q = (p - wind * _Time.y) * _FogNoiseScale;
                q.y *= 2.0;
                return ValueNoise3(q) * 0.65 + ValueNoise3(q * 2.7 + 17.3) * 0.35;
            }

            float HeightDensity(float y)
            {
                float k = max(_FogHeightFalloff, 1.0e-4);
                // Clamp the exponent, not the product: exp overflow to +inf
                // times a zero density is NaN, and Metal's min() would pass
                // the 4.0 through, turning disabled fog opaque.
                return min(_FogDensity * exp(-max(k * (y - _FogBaseHeight), -80.0)), 4.0);
            }

            // Closed-form optical depth of the pure exponential height fog
            // along a ray segment (no noise).
            float AnalyticOD(float3 p, float3 rd, float L)
            {
                L = max(L, 0.0);
                float k = max(_FogHeightFalloff, 1.0e-4);
                float kry = k * rd.y;
                // Branch on the dimensionless x = kry * L: branching on kry
                // alone leaves a ~15% step in the integral for near-horizontal
                // sky rays, a visible band at eye level.
                float x = kry * L;
                float term = abs(x) > 1.0e-3 ? (1.0 - exp(-x)) / kry : L * (1.0 - 0.5 * x);
                return HeightDensity(p.y) * term;
            }

            // Shadow attenuation of the main light at a mid-air point, faded
            // to 1 beyond the shadow distance.
            float MainLightVolumeShadow(float3 p)
            {
            #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                float atten = MainLightRealtimeShadow(TransformWorldToShadowCoord(p));
                return lerp(atten, 1.0, GetMainLightShadowFade(p));
            #else
                return 1.0;
            #endif
            }

            // In-scatter of one punctual light along a ray segment, in closed
            // form: with dist^2(s) = (s + b)^2 + h^2, the integral of
            // 1/dist^2 ds is atan((s + b) / h) / h. No sampling, so small
            // lights can never fall between march steps.
            float PointGlowIntegral(float b, float h, float s0, float s1)
            {
                return (atan((s1 + b) / h) - atan((s0 + b) / h)) / h;
            }

            // Total glow from all visible punctual lights (points and spots)
            // over the fogged segment [s0, s1]. Iterates the raw visible-light
            // arrays directly - per-object index lists and clusters are
            // meaningless for a screen ray. Halo brightness scales with the
            // local fog density (banks included), the light's range window,
            // its spot cone, and the fog transmittance in front of it.
            float3 PointLightGlow(float3 ro, float3 rd, float s0, float s1)
            {
            #if USE_CLUSTER_LIGHT_LOOP
                uint idx = URP_FP_DIRECTIONAL_LIGHTS_COUNT;
                uint end = URP_FP_PROBES_BEGIN;
                // Defensive: if the Forward+ param globals are not bound in
                // this fullscreen pass, fall back to the count the feature
                // passes in.
                if (end <= idx)
                {
                    idx = 0u;
                    end = uint(_FogAdditionalLightsCount);
                }
            #else
                // NOT _AdditionalLightsCount.x - that is the per-object cap
                // (default 4), and slots past the packed count hold stale
                // lights from earlier frames.
                uint idx = 0u;
                uint end = uint(_FogAdditionalLightsCount);
            #endif
                float3 sum = float3(0.0, 0.0, 0.0);
                [loop] for (; idx < end; ++idx)
                {
            #if USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA
                    float4 lightPos = _AdditionalLightsBuffer[idx].position;
                    half3 lightColor = _AdditionalLightsBuffer[idx].color.rgb;
                    half4 attn = _AdditionalLightsBuffer[idx].attenuation;
                    half4 spotDir = _AdditionalLightsBuffer[idx].spotDirection;
            #else
                    float4 lightPos = _AdditionalLightsPosition[idx];
                    half3 lightColor = _AdditionalLightsColor[idx].rgb;
                    half4 attn = _AdditionalLightsAttenuation[idx];
                    half4 spotDir = _AdditionalLightsSpotDir[idx];
            #endif
                    if (lightPos.w < 0.5)
                        continue; // directional entries have w = 0

                    float3 d = ro - lightPos.xyz;
                    float b = dot(rd, d);
                    float h2 = max(dot(d, d) - b * b, 0.01);
                    float h = sqrt(h2);

                    float glow = PointGlowIntegral(b, h, s0, s1);

                    // Range window (attn.x ~ 1/range^2), same shape as URP's
                    // smooth distance window, evaluated at closest approach.
                    float win = saturate(1.0 - h2 * attn.x);
                    win *= win;

                    // Spot cone factor at the ray's closest approach; for
                    // point lights URP packs zw so this returns 1.
                    float sc = clamp(-b, s0, s1);
                    float3 pc = ro + rd * sc;
                    half3 toLight = half3(normalize(lightPos.xyz - pc));
                    half cone = AngleAttenuation(spotDir.xyz, toLight, attn.zw);

                    // Local participation: denser fog -> brighter halo, and
                    // drifting banks modulate it. Saturating curve so halos
                    // stay readable where the height falloff thins the fog
                    // (lights on elevated terrain) yet vanish when density
                    // is actually zero.
                    float sigma = HeightDensity(lightPos.y);
                    if (_FogNoiseStrength > 0.0001)
                        sigma *= 1.0 + _FogNoiseStrength * (FogNoise(pc) * 2.0 - 1.0);
                    float sigmaTerm = saturate(sigma * 150.0);

                    // Fog transmittance between the camera and the halo.
                    float Tc = exp(-min(AnalyticOD(ro + rd * s0, rd, max(sc - s0, 0.0)), 60.0));

                    sum += lightColor * (glow * win * cone * sigmaTerm * Tc * 0.04);
                }
                return sum;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                if (_FogDensity <= 0.0)
                    return col;
                float rawDepth = SampleSceneDepth(uv);

            #if UNITY_REVERSED_Z
                bool isSky = rawDepth < 1.0e-7;
            #else
                bool isSky = rawDepth > 1.0 - 1.0e-7;
            #endif

                float3 worldPos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 ro = _WorldSpaceCameraPos.xyz;
                float3 delta = worldPos - ro;
                float t = length(delta);
                float3 rd = delta / max(t, 1.0e-4);

                // Skybox pixels carry no scene depth; fog them as an
                // effectively infinite ray so the horizon fills in while the
                // zenith stays clear.
                if (isSky)
                    t = _FogMaxDistance;
                t = min(t, _FogMaxDistance);
                float t0 = min(_FogStartDistance, t);

                // Directional fog color: the base tint inherits the main
                // light's color so time-of-day drives the haze, and a
                // forward-scatter lobe glows around the light direction.
                float3 lightCol = _MainLightColor.rgb;
                float3 baseCol = _FogColor.rgb * lerp(float3(1.0, 1.0, 1.0), lightCol, _FogLightInfluence);
                float sunLobe = pow(saturate(dot(rd, normalize(_MainLightPosition.xyz))), _FogSunPower) * _FogSunStrength;

                // Front-to-back scattering composite: T is transmittance so
                // far, scatter is accumulated in-scattered light.
                float T = 1.0;
                float3 scatter = float3(0.0, 0.0, 0.0);

                float noiseRange = max(_FogNoiseRange, 1.0);
                float tNearEnd = min(t, t0 + noiseRange);
                bool wantMarch = (_FogNoiseStrength > 0.0001
                    || _FogShaftStrength > 0.001) && tNearEnd > t0;

                float shadowW = 0.0;
                float shadowWSum = 0.0;

                if (wantMarch)
                {
                    // March the near field: noise banks and main-light
                    // shadowing (light shafts) resolved per step. The 12
                    // samples are warped so 8 land inside the shadow distance
                    // where shafts actually live (with shafts off the bound
                    // sits at 2/3 of the range, making the mapping uniform).
                    // Interleaved gradient noise jitters the comb: its ordered
                    // structure reads as a smooth gradient rather than white
                    // noise, and there is deliberately no temporal term - no
                    // TAA exists to resolve shimmer.
                    const int STEPS = 12;
                    float jitter = frac(52.9829189 * frac(dot(input.positionCS.xy, float2(0.06711056, 0.00583715))));
                    bool shaftsOn = _FogShaftStrength > 0.001;
                    float bound = shaftsOn
                        ? clamp(_FogShadowDistance, t0 + 1.0, tNearEnd)
                        : lerp(t0, tNearEnd, 2.0 / 3.0);
                    float lenA = bound - t0;
                    float lenB = tNearEnd - bound;

                    [loop] for (int i = 0; i < STEPS; i++)
                    {
                        float u = (float(i) + jitter) / float(STEPS);
                        bool inA = u < (2.0 / 3.0);
                        float s = inA ? t0 + lenA * (u * 1.5)
                                      : bound + lenB * ((u - 2.0 / 3.0) * 3.0);
                        float ds = inA ? lenA / 8.0 : lenB / 4.0;
                        if (ds <= 0.0)
                            continue;
                        float3 p = ro + rd * s;

                        float m = 1.0;
                        if (_FogNoiseStrength > 0.0001)
                        {
                            // Fade the modulation back to neutral near the
                            // march boundary so no seam shows against the
                            // analytic tail.
                            float fade = 1.0 - smoothstep(0.6, 1.0, (s - t0) / noiseRange);
                            m = 1.0 + _FogNoiseStrength * fade * (FogNoise(p) * 2.0 - 1.0);
                        }

                        float sigma = HeightDensity(p.y) * m;
                        float a = 1.0 - exp(-sigma * ds);

                        float shadow = 1.0;
                        if (shaftsOn && inA)
                            shadow = lerp(1.0, MainLightVolumeShadow(p), _FogShaftStrength);

                        float w = T * a;
                        shadowW += w * shadow;
                        shadowWSum += w;
                        scatter += w * lerp(baseCol, lightCol, sunLobe * shadow);
                        T *= 1.0 - a;
                    }
                }

                // Analytic tail beyond the near field (and the whole ray when
                // nothing needs marching). The transmittance-weighted average
                // of the marched shadowing extends the shafts plausibly to
                // the horizon instead of dissolving them at a fixed ring at
                // the shadow distance.
                float tTail = wantMarch ? tNearEnd : t0;
                float odTail = min(AnalyticOD(ro + rd * tTail, rd, t - tTail), 60.0);
                float aTail = 1.0 - exp(-odTail);
                float tailShadow = shadowWSum > 1.0e-4 ? shadowW / shadowWSum : 1.0;
                scatter += T * aTail * lerp(baseCol, lightCol, sunLobe * tailShadow);
                T *= 1.0 - aTail;

                if (_FogPointLightStrength > 0.001)
                    scatter += PointLightGlow(ro, rd, t0, t) * _FogPointLightStrength;

                col.rgb = col.rgb * T + scatter;
                return col;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
