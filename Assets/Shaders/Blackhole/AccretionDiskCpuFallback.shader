Shader "OmniTwin/Blackhole/Accretion Disk CPU Mesh Fallback"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "AccretionDiskCpuMesh"
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float3 positionWS : POSITION;
                float2 uv : TEXCOORD0;
                float3 velocityWS : TEXCOORD1;
                float temperature : TEXCOORD2;
                float radius : TEXCOORD3;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 velocityWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float temperature : TEXCOORD3;
                float radius : TEXCOORD4;
            };

            float _SpeedOfLight;
            float _SchwarzschildRadius;
            float _OuterRadius;
            float3 _BlackholeOrigin;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformWorldToHClip(input.positionWS);
                output.uv = input.uv;
                output.velocityWS = input.velocityWS;
                output.positionWS = input.positionWS;
                output.temperature = input.temperature;
                output.radius = input.radius;
                return output;
            }

            float3 Blackbody(float temperature)
            {
                const float3 blackbodyLut[8] =
                {
                    float3(1.0, 0.035, 0.0), float3(1.0, 0.31, 0.06),
                    float3(1.0, 0.63, 0.36), float3(1.0, 0.84, 0.70),
                    float3(0.83, 0.87, 1.0), float3(0.67, 0.78, 1.0),
                    float3(0.55, 0.69, 1.0), float3(0.48, 0.63, 1.0)
                };
                float lutPosition = saturate((clamp(temperature, 1000.0, 20000.0) - 1000.0) / 19000.0) * 7.0;
                uint lutIndex = min((uint)lutPosition, 6u);
                return lerp(blackbodyLut[lutIndex], blackbodyLut[lutIndex + 1], frac(lutPosition));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float radiusSquared = dot(input.uv * 2.0 - 1.0, input.uv * 2.0 - 1.0);
                float alpha = exp(-radiusSquared * 4.2);
                float3 viewDirection = SafeNormalize(_WorldSpaceCameraPos - input.positionWS);
                float3 betaVec = input.velocityWS / max(_SpeedOfLight, 1e-4);
                float betaSquared = dot(betaVec, betaVec);
                float emissionRadius = max(input.radius, _SchwarzschildRadius + 1e-4);
                float alphaGr = sqrt(max(0.02, 1.0 - _SchwarzschildRadius / emissionRadius));
                float gamma = rcp(sqrt(max(0.05, 1.0 - betaSquared)));
                float g = alphaGr / (gamma * max(1.0 - dot(betaVec, viewDirection), 0.05));
                float intensityShift = clamp(pow(max(g, 1e-4), 3.2), 0.08, 6.0);
                float emittedTemperature = input.temperature < 100.0 ? input.temperature * 10000.0 : input.temperature;
                float shiftedTemperature = clamp(emittedTemperature * g, 1000.0, 20000.0);
                float innerHeat = saturate((input.temperature - 2800.0) / 9200.0);
                float baseIntensity = input.temperature < 100.0 ? 5.0 : lerp(0.55, 3.9, pow(innerHeat, 0.9));
                float3 color = Blackbody(shiftedTemperature) * (baseIntensity * intensityShift);
                return half4(color * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
