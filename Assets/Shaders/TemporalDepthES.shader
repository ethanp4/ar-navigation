Shader "ARDepthRefinement/TemporalDepthES"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalRenderPipeline" }

        Pass
        {
            Name "TemporalDepthES"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_CurrentDepth);
            SAMPLER(sampler_CurrentDepth);

            TEXTURE2D(_HistoryDepth);
            SAMPLER(sampler_HistoryDepth);

            float _Alpha;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float current = SAMPLE_TEXTURE2D(_CurrentDepth, sampler_CurrentDepth, i.uv).r;
                float history = SAMPLE_TEXTURE2D(_HistoryDepth, sampler_HistoryDepth, i.uv).r;

                if (history <= 0.0)
                    return half4(current, current, current, 1);

                if (current <= 0.0)
                    return half4(history, history, history, 1);

                float result = lerp(history, current, _Alpha);

                return half4(result, result, result, 1);
            }

            ENDHLSL
        }
    }
}