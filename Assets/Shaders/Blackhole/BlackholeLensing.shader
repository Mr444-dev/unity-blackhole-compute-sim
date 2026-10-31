Shader "OmniTwin/Blackhole/Gravitational Lensing"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "BlackholeLensing"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _BlackholeLensingCenter;   // viewport xy, shadow radius, aspect ratio
            float4 _BlackholeLensingSettings; // distortion strength, ring intensity

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float2 delta = uv - _BlackholeLensingCenter.xy;
                delta.x *= _BlackholeLensingCenter.w;
                float radius = length(delta);
                float shadowRadius = max(_BlackholeLensingCenter.z, 1e-5);
                float2 direction = delta / max(radius, 1e-5);

                // Thin-lens-inspired radial bend; the singular center is clamped to the visible shadow edge.
                float bend = _BlackholeLensingSettings.x * shadowRadius * shadowRadius / max(radius, shadowRadius * 0.85);
                float2 sampleUv = uv + float2(direction.x / _BlackholeLensingCenter.w, direction.y) * bend;
                half3 background = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, sampleUv).rgb;

                float shadowMask = smoothstep(shadowRadius * 0.94, shadowRadius * 1.02, radius);
                float ringDistance = abs(radius - shadowRadius * 1.08);
                float ring = exp(-ringDistance * ringDistance / max(shadowRadius * shadowRadius * 0.012, 1e-7));
                float3 photonRing = float3(1.0, 0.48, 0.16) * ring * _BlackholeLensingSettings.y;
                return half4(background * shadowMask + photonRing, 1.0);
            }
            ENDHLSL
        }
    }
}
