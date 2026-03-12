Shader "ARDepthRefinement/DepthConfidenceFilter"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalRenderPipeline" }

        Pass
        {
            Name "DepthConfidenceFilter"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_InputDepth);
            SAMPLER(sampler_InputDepth);

            float4 _TexelSize;
            float _GradientThreshold;

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

            float sampleDepth(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_InputDepth, sampler_InputDepth, uv).r;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float center = sampleDepth(i.uv);

                if (center <= 0.0)
                    return half4(0,0,0,0);

                float right = sampleDepth(i.uv + float2(_TexelSize.x,0));
                float down = sampleDepth(i.uv + float2(0,_TexelSize.y));

                float gradient = abs(center - right) + abs(center - down);

                if (gradient > _GradientThreshold)
                    return half4(0,0,0,1);

                return half4(center, center, center, 1);
            }

            ENDHLSL
        }
    }
}