Shader "SnowDays/RetroSnow"
{
    Properties
    {
        _SnowTint ("Tint", Color) = (0.95, 0.97, 1, 0.95)
        _SnowFollowPos ("Follow Pos", Vector) = (0, 0, 0, 0)
        _SnowBox ("Box Size XYZ, Edge Fade Start", Vector) = (26, 16, 26, 0.65)
        _SnowMotion ("Wind XZ, Fall Min, Fall Max", Vector) = (0.5, 0.2, 0.9, 1.9)
        _SnowFlake ("Size Min, Size Max, Sway Amp, Sway Speed", Vector) = (0.05, 0.13, 0.3, 1.2)
        _SnowMisc ("Light Influence", Vector) = (0.6, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "RetroSnow"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog

            CBUFFER_START(UnityPerMaterial)
                half4 _SnowTint;
                float4 _SnowFollowPos;
                float4 _SnowBox;
                float4 _SnowMotion;
                float4 _SnowFlake;
                float4 _SnowMisc;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 corner : TEXCOORD0;
                float2 seed : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                half4 color : TEXCOORD1;
                half fogFactor : TEXCOORD2;
                // x = variant row offset into kFlakeRows, y = view distance in flake-sizes
                float2 flake : TEXCOORD3;
            };

            // 9x9 pixel-art snowflakes, one row per uint (bit i = column i).
            // Three variants: 8-arm star, hollow crystal, detached-tip star.
            static const uint kFlakeRows[27] =
            {
                273u, 146u, 84u, 56u, 511u, 56u, 84u, 146u, 273u,
                16u, 16u, 84u, 40u, 427u, 40u, 84u, 16u, 16u,
                16u, 146u, 84u, 16u, 471u, 16u, 84u, 146u, 16u
            };

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 345.45));
                p += dot(p, p + 34.345);
                return frac(p.x * p.y);
            }

            Varyings Vert(Attributes input)
            {
                Varyings o;

                float2 seed = input.seed;
                float r0 = Hash(seed);
                float r1 = Hash(seed + 1.71);
                float r2 = Hash(seed + 3.13);
                float r3 = Hash(seed + 5.57);
                float r4 = Hash(seed + 7.31);
                float r5 = Hash(seed + 9.19);

                float3 box = _SnowBox.xyz;
                float t = _Time.y;

                float fall = lerp(_SnowMotion.z, _SnowMotion.w, r3);
                float swayAmp = _SnowFlake.z * (0.5 + 0.5 * r4);
                float swaySpeed = _SnowFlake.w * (0.7 + 0.6 * r5);
                float phase = r2 * 6.2831853;

                // Unwrapped world-space path: spawn point + wind drift + fall
                // + flutter. A visible flake's motion is exactly this path,
                // fixed in the world and independent of the player.
                float3 p;
                p.x = r0 * box.x + _SnowMotion.x * t + sin(t * swaySpeed + phase) * swayAmp;
                p.z = r1 * box.z + _SnowMotion.y * t + cos(t * swaySpeed * 0.83 + phase * 1.7) * swayAmp;
                p.y = r2 * box.y - fall * t;

                // Fold the path into the box centered on the follow point. As
                // the box moves, a flake's folded position is constant until
                // an edge passes it, when it steps by a whole box stride -
                // the classic fixed-budget infinite-snow trick.
                float3 boxMin = _SnowFollowPos.xyz - box * 0.5;
                float3 center = boxMin + frac((p - boxMin) / box) * box;

                half bright = lerp(0.8, 1.0, Hash(seed + 11.3));
                // Chunky 8 Hz twinkle, quantized like a low-rate sprite anim.
                bright *= lerp(0.85, 1.0, Hash(seed + floor(t * 8.0)));

                // Hide the wraps: fade toward the box edges and right at the
                // camera so flakes never pop on screen.
                float3 rel = abs(center - _SnowFollowPos.xyz) / (box * 0.5);
                half fade = 1.0 - smoothstep(_SnowBox.w, 1.0, max(rel.x, rel.z));
                fade *= 1.0 - smoothstep(0.85, 1.0, rel.y);
                fade *= smoothstep(0.2, 0.7, distance(center, _WorldSpaceCameraPos.xyz));

                // sqrt bias: more flakes near the top of the size range, so
                // the pixel art reads on more of them.
                float size = lerp(_SnowFlake.x, _SnowFlake.y, sqrt(Hash(seed + 13.7)));
                float3 camRight = UNITY_MATRIX_V[0].xyz;
                float3 camUp = UNITY_MATRIX_V[1].xyz;
                float3 positionWS = center + (input.corner.x * camRight + input.corner.y * camUp) * (size * 0.5);

                o.positionCS = TransformWorldToHClip(positionWS);
                o.corner = input.corner;
                half3 lit = lerp(half3(1.0, 1.0, 1.0), _MainLightColor.rgb, _SnowMisc.x);
                o.color = half4(_SnowTint.rgb * lit * bright, _SnowTint.a * fade);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);

                float variant = floor(Hash(seed + 15.9) * 3.0);
                float viewZ = max(-TransformWorldToView(center).z, 0.1);
                o.flake = float2(variant * 9.0, viewZ / max(size, 0.001));
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Hard-edged 9x9 pixel-art flake, point-sampled per cell.
                float2 uv = saturate(input.corner * 0.5 + 0.5);
                uint2 cell = (uint2)min(floor(uv * 9.0), 8.0);
                uint rowMask = kFlakeRows[(uint)input.flake.x + cell.y];
                half pattern = (half)((rowMask >> cell.x) & 1u);

                // Past ~55 flake-sizes away (about 5 screen px at the 480p
                // target) the art can't resolve; cross-fade to a chunky
                // diamond dot so distant flakes stay visible instead of
                // shimmering arms.
                half core = (abs(input.corner.x) + abs(input.corner.y)) < 0.55 ? (half)1.0 : (half)0.0;
                half shape = lerp(core, pattern, saturate((55.0 - input.flake.y) / 12.0));

                half alpha = shape * input.color.a;
                half3 col = MixFog(input.color.rgb, input.fogFactor);
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
