Shader "ARDepthRefinement/DepthBilateralFilter"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalRenderPipeline" }

        Pass
        {
            Name "DepthBilateralFilter"
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
            int _Radius;
            float _DepthSigma;
            float _SpatialSigma;

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

            float SampleDepth(float2 uv)
            {
                return SAMPLE_TEXTURE2D(_InputDepth, sampler_InputDepth, uv).r;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float center = SampleDepth(i.uv);

                if (center <= 0.0)
                    return half4(0, 0, 0, 0);

                float sum = 0.0;
                float weightSum = 0.0;

                float spatialDenom = max(0.0001, 2.0 * _SpatialSigma * _SpatialSigma);
                float depthDenom = max(0.0001, 2.0 * _DepthSigma * _DepthSigma);

                [unroll]
                for (int y = -2; y <= 2; y++)
                {
                    [unroll]
                    for (int x = -2; x <= 2; x++)
                    {
                        if (abs(x) > _Radius || abs(y) > _Radius)
                            continue;

                        float2 offset = float2((float)x, (float)y) * _TexelSize.xy;
                        float d = SampleDepth(i.uv + offset);

                        if (d <= 0.0)
                            continue;

                        float spatialDist = (float)(x * x + y * y);
                        float depthDiff = d - center;

                        float spatialWeight = exp(-spatialDist / spatialDenom);
                        float depthWeight = exp(-(depthDiff * depthDiff) / depthDenom);
                        float w = spatialWeight * depthWeight;

                        sum += d * w;
                        weightSum += w;
                    }
                }

                float result = (weightSum > 0.0) ? (sum / weightSum) : center;
                return half4(result, result, result, 1);
            }

            ENDHLSL
        }
    }
}