Shader "OmniTwin/Blackhole/Procedural Starfield Skybox"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
        }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float4 clipPosition = TransformObjectToHClip(input.positionOS);
                output.positionCS = clipPosition.xyww;
                output.direction = input.positionOS.xyz;
                return output;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.direction);
                float vertical = saturate(direction.y * 0.5 + 0.5);
                float3 gradient = lerp(float3(0.002, 0.003, 0.010), float3(0.012, 0.024, 0.065), pow(vertical, 1.35));

                float longitude = atan2(direction.z, direction.x) * (0.5 / PI) + 0.5;
                float latitude = asin(clamp(direction.y, -1.0, 1.0)) * (1.0 / PI) + 0.5;
                float2 grid = float2(longitude, latitude) * float2(640.0, 320.0);
                float2 cell = floor(grid);
                float2 local = frac(grid);
                float2 offset = float2(Hash21(cell + 9.7), Hash21(cell + 38.2));
                float starShape = exp(-dot(local - offset, local - offset) * 100.0);
                float seed = Hash21(cell);
                float star = starShape * step(0.9968, seed);
                float brightStar = starShape * step(0.9993, seed);
                float3 starColor = lerp(float3(0.45, 0.62, 1.0), float3(1.0, 0.82, 0.64), Hash21(cell + 71.4));
                float3 color = gradient + starColor * (star * 0.8 + brightStar * 2.2);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
