Shader "Hidden/SnowDays/GameCubePost"
{
    Properties
    {
        _ColorBits ("Color Bits Per Channel", Range(3, 8)) = 5
        _DitherStrength ("Dither Strength", Range(0, 1)) = 0.6
        _ScanlineStrength ("Scanline Strength", Range(0, 1)) = 0.15
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "GameCubePost"

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            float _ColorBits;
            float _DitherStrength;
            float _ScanlineStrength;

            // Ordered 4x4 Bayer value in 0..15, computed from the recursive
            // definition so no array indexing is needed.
            float Bayer4x4(uint2 p)
            {
                uint fine   = (2u * (p.x & 1u) + 3u * (p.y & 1u)) & 3u;
                uint coarse = (2u * ((p.x >> 1) & 1u) + 3u * ((p.y >> 1) & 1u)) & 3u;
                return float(4u * fine + coarse);
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);

                // Quantize in gamma space so the banding falls where a real
                // 16-bit framebuffer would put it, hidden by ordered dither -
                // the GameCube's embedded framebuffer did exactly this.
                float3 srgb = LinearToSRGB(saturate(col.rgb));

                uint2 p = uint2(input.positionCS.xy);
                float bayer = Bayer4x4(p & 3u) / 16.0 - 0.46875;

                float levels = exp2(_ColorBits) - 1.0;
                srgb = floor(srgb * levels + 0.5 + bayer * _DitherStrength) / levels;

                float3 outRgb = SRGBToLinear(srgb);

                // 480i field lines: darken every other internal scanline; the
                // bilinear upscale afterwards melts them into CRT softness.
                outRgb *= 1.0 - _ScanlineStrength * float(p.y & 1u);

                return float4(outRgb, col.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
