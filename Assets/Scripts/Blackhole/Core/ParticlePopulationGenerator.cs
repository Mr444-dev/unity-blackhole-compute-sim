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
            float maximumFeasibleEccentricity = (maxRadius - innerRadius) / (maxRadius + innerRadius);
            e = math.min(e, math.max(maximumFeasibleEccentricity, 0f));
            var particles = new NativeArray<Particle>(safeCount, allocator, NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < safeCount; i++)
            {
                uint baseHash = RelativisticGravityMath.WangHash((uint)i ^ (uint)seed);
                float radialSample = RelativisticGravityMath.Hash01(baseHash);
                float angle = RelativisticGravityMath.Hash01(baseHash ^ 0x9e3779b9u) * (2f * math.PI);
                float verticalSample = (RelativisticGravityMath.Hash01(baseHash ^ 0x85ebca6bu) * 2f - 1f) * thickness;
                float anomaly = RelativisticGravityMath.Hash01(baseHash ^ 0xc2b2ae35u) * (2f * math.PI);

                // Uniform area density over feasible semimajor axes keeps each turning point inside the annulus.
                float minimumSemiMajorAxis = innerRadius / (1f - e);
                float maximumSemiMajorAxis = maxRadius / (1f + e);
                float semiMajorAxis = math.sqrt(math.lerp(minimumSemiMajorAxis * minimumSemiMajorAxis, maximumSemiMajorAxis * maximumSemiMajorAxis, radialSample));
                float pericenter = semiMajorAxis * (1f - e);
                float apocenter = semiMajorAxis * (1f + e);
                float orbitalRadius = semiMajorAxis * (1f - e * math.cos(anomaly));
                float sin = math.sin(angle);
                float cos = math.cos(angle);
                float angularMomentumSquared = 2f * gravitationalParameter * pericenter * pericenter * apocenter * apocenter /
                                               ((pericenter + apocenter) * (pericenter - rs) * (apocenter - rs));
                float orbitalEnergy = 0.5f * angularMomentumSquared / (pericenter * pericenter) - gravitationalParameter / (pericenter - rs);
                float tangentialSpeed = math.sqrt(angularMomentumSquared) / orbitalRadius;
                float radialSpeedSquared = math.max(0f, 2f * (orbitalEnergy + gravitationalParameter / (orbitalRadius - rs)) - tangentialSpeed * tangentialSpeed);
                float radialSpeed = math.sqrt(radialSpeedSquared) * math.sign(sin);
                float3 radial = new float3(cos, 0f, sin);
                float3 tangent = new float3(-sin, 0f, cos);

                particles[i] = new Particle
                {
                    position = new float3(radial.x * orbitalRadius, verticalSample, radial.z * orbitalRadius),
                    temperature = math.lerp(12000f, 2800f, math.saturate((orbitalRadius - innerRadius) / (maxRadius - innerRadius))),
                    velocity = tangent * tangentialSpeed + radial * radialSpeed,
                    flagsAndSeed = baseHash & 0x3fffffffu
                };
            }

            return particles;
        }
    }
}
