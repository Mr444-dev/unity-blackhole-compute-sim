Shader "OmniTwin/Blackhole/Accretion Disk Procedural"
{
    Properties
    {
        _ParticleSize ("Particle Size", Float) = 0.045
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "AccretionDisk"
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
            #include "UnityIndirect.cginc"

            struct Particle
            {
                float3 position;
                float temperature;
                float3 velocity;
                uint flagsAndSeed;
            };

            StructuredBuffer<Particle> _Particles;
            StructuredBuffer<uint> _VisibleIndices;
            float _ParticleSize;
            float3 _BlackholeOrigin;
            float3 _CameraRight;
            float3 _CameraUp;
            float _SpeedOfLight;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 velocityWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float temperature : TEXCOORD3;
            };

            float2 QuadCorner(uint vertexIndex)
            {
                const float2 corners[6] =
                {
                    float2(-1.0, -1.0), float2(1.0, -1.0), float2(1.0, 1.0),
                    float2(-1.0, -1.0), float2(1.0, 1.0), float2(-1.0, 1.0)
                };
                return corners[vertexIndex];
            }

            Varyings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                InitIndirectDrawArgs(0);
                uint visibleInstance = GetIndirectInstanceID(instanceId);
                uint particleIndex = _VisibleIndices[visibleInstance];
                Particle particle = _Particles[particleIndex];
                float2 corner = QuadCorner(GetIndirectVertexID(vertexId));
                float3 centerWS = _BlackholeOrigin + particle.position;
                float3 billboardWS = centerWS + (_CameraRight * corner.x + _CameraUp * corner.y) * (_ParticleSize * 0.5);

                Varyings output;
                output.positionCS = TransformWorldToHClip(billboardWS);
                output.uv = corner * 0.5 + 0.5;
                output.velocityWS = particle.velocity;
                output.positionWS = centerWS;
                output.temperature = particle.temperature;
                return output;
            }

            float3 Blackbody(float temperature)
            {
                float t = clamp(temperature, 1000.0, 20000.0) / 100.0;
                float3 color;
                color.r = t <= 66.0 ? 1.0 : saturate(1.292936 * pow(t - 60.0, -0.133205));
                color.g = t <= 66.0 ? saturate(0.390082 * log(max(t, 1.0)) - 0.631841) : saturate(1.129891 * pow(t - 60.0, -0.075515));
                color.b = t >= 66.0 ? 1.0 : (t <= 19.0 ? 0.0 : saturate(0.543207 * log(max(t - 10.0, 1.0)) - 1.196254));
                return color;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float radius = length(input.uv * 2.0 - 1.0);
                float alpha = saturate((1.0 - radius) * 7.0);
                alpha *= alpha;

                float3 viewDirection = SafeNormalize(_WorldSpaceCameraPos - input.positionWS);
                float3 velocityDirection = SafeNormalize(input.velocityWS);
                float cosTheta = dot(velocityDirection, viewDirection);
                float beta = min(length(input.velocityWS) / max(_SpeedOfLight, 1e-4), 0.999);
                float gamma = rsqrt(max(1.0 - beta * beta, 1e-4));
                float doppler = rcp(max(gamma * (1.0 - beta * cosTheta), 1e-3));
                float beaming = pow(max(doppler, 0.0), 3.0);
                float shiftedTemperature = clamp(input.temperature * doppler, 1000.0, 20000.0);
                float3 color = Blackbody(shiftedTemperature) * beaming;
                return half4(color * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
