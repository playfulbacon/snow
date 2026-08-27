// Footprint stamp: an oriented soft-edged ellipse drawn into the trample
// window RT. BlendOp Max means re-treading never stacks past full depth and
// overlapping prints merge cleanly. The quad is placed by a TRS matrix in
// window-space meters (SnowDeformSystem sets an ortho VP over the window).
Shader "SnowDays/SnowStamp"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex StampVertex
            #pragma fragment StampFragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // x = strength (peak trample 0..1), y = edge softness (0..1 of
            // the half extent), z = noise amount, w = unused
            float4 _StampParams;
            // 1 / window size. The object matrix places the quad in window
            // meters; clip space is derived manually so that a texel written
            // for window position y is ALWAYS the texel sampled at v = y/S -
            // relying on projection-matrix conventions here flipped the map
            // on Metal (camera-less command-buffer draws skip Unity's usual
            // flip compensation).
            float _SnowWriteInv;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings StampVertex(Attributes input)
            {
                Varyings output;
                float2 windowPos = mul(UNITY_MATRIX_M, float4(input.positionOS, 1.0)).xy;
                float2 ndc = windowPos * _SnowWriteInv * 2.0 - 1.0;
                #if UNITY_UV_STARTS_AT_TOP
                ndc.y = -ndc.y;
                #endif
                output.positionCS = float4(ndc, 0.5, 1.0);
                output.uv = input.uv;
                return output;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(234.34, 435.345));
                p += dot(p, p + 34.23);
                return frac(p.x * p.y);
            }

            half4 StampFragment(Varyings input) : SV_Target
            {
                // Elliptical distance: uv (0..1) -> centered (-1..1).
                float2 c = input.uv * 2.0 - 1.0;
                float d = length(c);
                // Ragged edge so prints don't read as perfect stadium shapes.
                d += (Hash21(input.uv * 37.7) - 0.5) * _StampParams.z;
                float soft = max(_StampParams.y, 0.01);
                float profile = 1.0 - smoothstep(1.0 - soft, 1.0, d);
                return half4(profile * _StampParams.x, 0, 0, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
