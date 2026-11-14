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
            float _SchwarzschildRadius;
            float _OuterRadius;
            float3 _BlackholeOrigin;
            float3 _DiskScale;
            float3 _CameraRight;
            float3 _CameraUp;
            float _SpeedOfLight;
            int _UseIndirectArguments;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 velocityWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float temperature : TEXCOORD3;
                float radialFraction : TEXCOORD4;
                float radius : TEXCOORD5;
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
                uint visibleInstance = instanceId;
                uint vertexIndex = vertexId;
                if (_UseIndirectArguments != 0)
                {
                    InitIndirectDrawArgs(0);
                    visibleInstance = GetIndirectInstanceID(instanceId);
                    vertexIndex = GetIndirectVertexID(vertexId);
                }

                uint particleIndex = _VisibleIndices[visibleInstance];
                Particle particle = _Particles[particleIndex];
                float3 relativeWS = particle.position * _DiskScale;
                float3 centerWS = _BlackholeOrigin + relativeWS;
                float3 velocityWS = particle.velocity * _DiskScale;
                float3 cameraFromHole = SafeNormalize(_WorldSpaceCameraPos - _BlackholeOrigin);
                float depthFromHole = dot(relativeWS, cameraFromHole);
                bool isBehindBlackhole = depthFromHole < 0.0;

                // A thin-lens image-space approximation lifts far-side particles around the shadow.
                float3 projectedOffset = relativeWS - cameraFromHole * depthFromHole;
                float projectedRadius = length(projectedOffset);
                float3 bendDirection = projectedOffset / max(projectedRadius, 1e-4);
                if (projectedRadius < 1e-4)
                    bendDirection = SafeNormalize(cross(cameraFromHole, velocityWS));
                float shadowWorldRadius = 1.5 * sqrt(3.0) * _SchwarzschildRadius;
                float photonRingRadius = shadowWorldRadius * 1.08;
                float bendWeight = (isBehindBlackhole ? 1.0 : 0.0) *
                    (1.0 - smoothstep(photonRingRadius * 1.15, photonRingRadius * 4.0, projectedRadius));
                float bentRadius = lerp(projectedRadius, max(projectedRadius, photonRingRadius * 1.04), bendWeight * 0.9);
                centerWS = _BlackholeOrigin + bendDirection * bentRadius + cameraFromHole * depthFromHole;

                float3 viewDirection = SafeNormalize(_WorldSpaceCameraPos - centerWS);
                float3 tangentWS = SafeNormalize(velocityWS - viewDirection * dot(velocityWS, viewDirection));
                if (dot(tangentWS, tangentWS) < 1e-4)
                    tangentWS = SafeNormalize(_CameraRight);
                float3 minorWS = SafeNormalize(cross(viewDirection, tangentWS));
                float speed = length(velocityWS);
                float streakLength = _ParticleSize * clamp(1.0 + speed * 2.5, 1.0, 4.5);
                float halfWidth = _ParticleSize * 0.27;
                float2 corner = QuadCorner(vertexIndex);
                float3 billboardWS = centerWS + tangentWS * corner.x * (streakLength * 0.5) + minorWS * corner.y * halfWidth;

                Varyings output;
                output.positionCS = TransformWorldToHClip(billboardWS);
                output.uv = corner;
                output.velocityWS = velocityWS;
                output.positionWS = centerWS;
                output.temperature = particle.temperature;
                output.radialFraction = saturate((length(relativeWS.xz) - 3.0 * _SchwarzschildRadius) /
                    max(_OuterRadius - 3.0 * _SchwarzschildRadius, 1e-3));
                output.radius = max(length(relativeWS), _SchwarzschildRadius + 1e-4);
                return output;
            }

            float3 Blackbody(float temperature)
            {
                const float3 blackbodyLut[8] =
                {
                    float3(1.0, 0.035, 0.0),
                    float3(1.0, 0.31, 0.06),
                    float3(1.0, 0.63, 0.36),
                    float3(1.0, 0.84, 0.70),
                    float3(0.83, 0.87, 1.0),
                    float3(0.67, 0.78, 1.0),
                    float3(0.55, 0.69, 1.0),
                    float3(0.48, 0.63, 1.0)
                };
                float lutPosition = saturate((clamp(temperature, 1000.0, 20000.0) - 1000.0) / 19000.0) * 7.0;
                uint lutIndex = min((uint)lutPosition, 6u);
                return lerp(blackbodyLut[lutIndex], blackbodyLut[lutIndex + 1], frac(lutPosition));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float radiusSquared = dot(input.uv, input.uv);
                float softParticle = exp(-radiusSquared * 4.2);
                float3 viewDirection = SafeNormalize(_WorldSpaceCameraPos - input.positionWS);
                float3 betaVec = input.velocityWS / max(_SpeedOfLight, 1e-4);
                float betaSquared = dot(betaVec, betaVec);
                float gamma = rcp(sqrt(max(0.05, 1.0 - betaSquared)));
                float alpha = sqrt(max(0.02, 1.0 - _SchwarzschildRadius / input.radius));
                float g = alpha / (gamma * max(1.0 - dot(betaVec, viewDirection), 0.05));
                float intensityShift = clamp(pow(max(g, 1e-4), 3.2), 0.08, 6.0);
                float emittedTemperature = input.temperature < 100.0 ? input.temperature * 10000.0 : input.temperature;
                float shiftedTemperature = clamp(emittedTemperature * g, 1000.0, 20000.0);
                float innerHeat = 1.0 - smoothstep(0.0, 1.0, input.radialFraction);
                float baseIntensity = input.temperature < 100.0
                    ? 5.0
                    : lerp(0.55, 3.9, pow(innerHeat, 0.9));
                float3 color = Blackbody(shiftedTemperature) * (baseIntensity * intensityShift);
                return half4(color * softParticle, softParticle);
            }
            ENDHLSL
        }
    }
}
