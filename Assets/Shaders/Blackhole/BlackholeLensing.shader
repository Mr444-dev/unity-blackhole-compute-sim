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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _BlackholeLensingCenter;   // viewport xy, shadow radius, aspect ratio
            float4 _BlackholeLensingSettings; // distortion strength, ring intensity

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float3 Starfield(float2 uv)
            {
                float vertical = saturate(uv.y);
                float3 gradient = lerp(float3(0.003, 0.005, 0.014), float3(0.020, 0.035, 0.085), pow(vertical, 1.7));
                float2 grid = uv * float2(480.0, 270.0);
                float2 cell = floor(grid);
                float2 local = frac(grid);
                float seed = Hash21(cell);
                float2 starOffset = float2(Hash21(cell + 17.1), Hash21(cell + 53.7));
                float distanceSquared = dot(local - starOffset, local - starOffset);
                float starShape = exp(-distanceSquared * 90.0);
                float star = starShape * step(0.9965, seed);
                float brightStar = starShape * step(0.999, seed);
                float3 starColor = lerp(float3(0.36, 0.54, 1.0), float3(1.0, 0.82, 0.62), Hash21(cell + 91.3));
                return gradient + starColor * (star * 0.9 + brightStar * 2.5);
            }

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
                float luminance = dot(background, float3(0.2126, 0.7152, 0.0722));
                float backgroundWeight = 1.0 - smoothstep(0.08, 0.4, luminance);
                background = lerp(background, Starfield(sampleUv), backgroundWeight);

                float shadowMask = smoothstep(shadowRadius * 0.94, shadowRadius * 1.02, radius);
                float ringDistance = abs(radius - shadowRadius * 1.08);
                float ring = exp(-ringDistance * ringDistance / max(shadowRadius * shadowRadius * 0.006, 1e-7));
                float halo = exp(-ringDistance * ringDistance / max(shadowRadius * shadowRadius * 0.065, 1e-7));
                float3 photonRing = (float3(1.0, 0.55, 0.25) * ring + float3(0.12, 0.24, 0.62) * halo * 0.45) * _BlackholeLensingSettings.y;
                return half4(background * shadowMask + photonRing, 1.0);
            }
            ENDHLSL
        }
    }
}
