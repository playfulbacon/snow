// Trample-window upkeep, ping-ponged between two RTs. One pass handles both
// jobs: shift the window when it scrolls under the player (texels that slide
// in from outside the old window start untrampled) and decay everything
// toward zero so falling snow slowly refills old prints. Drawn as a
// window-sized quad through the same ortho VP as SnowStamp so every write to
// the trample RT shares one orientation convention.
Shader "SnowDays/SnowMaintenance"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            Blend One Zero

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex MaintVertex
            #pragma fragment MaintFragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_SnowPrevTrample);
            SAMPLER(sampler_SnowPrevTrample);

            // xy = uv offset of the scroll (newUV + offset = oldUV), z = decay
            // amount this pass, w = unused
            float4 _SnowMaint;
            // 1 / window size; see SnowStamp - manual clip mapping keeps the
            // write orientation locked to the sampling orientation, so the
            // ping-pong copy is a true identity (a flip here mirrors the
            // whole trample map on every decay tick).
            float _SnowWriteInv;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings MaintVertex(Attributes input)
            {
                Varyings output;
                float2 windowPos = mul(UNITY_MATRIX_M, float4(input.positionOS.xyz, 1.0)).xy;
                float2 ndc = windowPos * _SnowWriteInv * 2.0 - 1.0;
                #if UNITY_UV_STARTS_AT_TOP
                ndc.y = -ndc.y;
                #endif
                output.positionCS = float4(ndc, 0.5, 1.0);
                output.uv = input.uv;
                return output;
            }

            half4 MaintFragment(Varyings input) : SV_Target
            {
                float2 prevUV = input.uv + _SnowMaint.xy;
                float inside = all(prevUV >= 0.0) && all(prevUV <= 1.0) ? 1.0 : 0.0;
                float v = SAMPLE_TEXTURE2D(_SnowPrevTrample, sampler_SnowPrevTrample, prevUV).r * inside;
                return half4(max(v - _SnowMaint.z, 0.0), 0, 0, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
