using Unity.Collections;
using Unity.Mathematics;

namespace OmniTwin.BlackholeSimulation.Core
{
    /// <summary>🌌 Creates deterministic particles in an ISCO-bounded disk annulus.</summary>
    public static class ParticlePopulationGenerator
    {
        public static NativeArray<Particle> Create(
            int count,
            int seed,
            float gravitationalParameter,
            float schwarzschildRadius,
            float outerRadius,
            float diskHalfThickness,
            float eccentricity,
            float softening,
            Allocator allocator)
        {
            int safeCount = math.max(count, 1);
            float rs = math.max(schwarzschildRadius, 1e-4f);
            float innerRadius = 3f * rs;
            float maxRadius = math.max(outerRadius, innerRadius + rs * 0.1f);
            float thickness = math.max(diskHalfThickness, 0f);
            float e = math.clamp(eccentricity, 0f, 0.8f);
            var gravity = new RelativisticGravityMath(gravitationalParameter, rs, softening, 0.05f);
            var particles = new NativeArray<Particle>(safeCount, allocator, NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < safeCount; i++)
            {
                uint baseHash = RelativisticGravityMath.WangHash((uint)i ^ (uint)seed);
                float radialSample = RelativisticGravityMath.Hash01(baseHash);
                float angle = RelativisticGravityMath.Hash01(baseHash ^ 0x9e3779b9u) * (2f * math.PI);
                float verticalSample = (RelativisticGravityMath.Hash01(baseHash ^ 0x85ebca6bu) * 2f - 1f) * thickness;
                float anomaly = RelativisticGravityMath.Hash01(baseHash ^ 0xc2b2ae35u) * (2f * math.PI);

                // Uniform area density in the annulus; e=0 is an exact circular P-W orbit.
                float radius = math.sqrt(math.lerp(innerRadius * innerRadius, maxRadius * maxRadius, radialSample));
                float radialOffset = e * math.cos(anomaly);
                float orbitalRadius = math.clamp(radius * (1f - radialOffset), innerRadius, maxRadius);
                float sin = math.sin(angle);
                float cos = math.cos(angle);
                float circularSpeed = gravity.CircularSpeed(orbitalRadius);
                float tangentialScale = math.sqrt(math.max(1f - e * e, 0f));
                float radialSpeed = e * circularSpeed * math.sin(anomaly);
                float3 radial = new float3(cos, 0f, sin);
                float3 tangent = new float3(-sin, 0f, cos);

                particles[i] = new Particle
                {
                    position = new float3(radial.x * orbitalRadius, verticalSample, radial.z * orbitalRadius),
                    temperature = math.lerp(12000f, 2800f, math.saturate((orbitalRadius - innerRadius) / (maxRadius - innerRadius))),
                    velocity = tangent * (circularSpeed * tangentialScale) + radial * radialSpeed,
                    flagsAndSeed = baseHash & 0x3fffffffu
                };
            }

            return particles;
        }
    }
}
