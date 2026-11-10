Shader "Snowfield/SnowBurst"
{
    // The spray thrown off an impact. Deliberately textureless: the particle is a soft round blob drawn from its
    // own UV, so there is no sprite to author, import or keep in step with the snow look. Lives in a Resources
    // folder for the same reason RetroSnow does - nothing serialises a reference to it, so build stripping would
    // otherwise take it.
    Properties
    {
        _Softness ("Edge softness", Range(0.01, 1)) = 0.75
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "SnowBurst"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            float _Softness;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color = IN.color;
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Round blob from the quad's own UV: 1 at the middle, 0 at the inscribed circle.
                float2 d = IN.uv * 2.0 - 1.0;
                float disc = saturate(1.0 - dot(d, d));
                float alpha = IN.color.a * smoothstep(0.0, max(_Softness, 0.01), disc);
                return half4(IN.color.rgb, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
