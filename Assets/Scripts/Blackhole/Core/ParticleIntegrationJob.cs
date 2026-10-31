using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace OmniTwin.BlackholeSimulation.Core
{
    /// <summary>🧵 Burst velocity-Verlet integration and deterministic feeding-boundary recycling.</summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
    public struct ParticleIntegrationJob : IJobParallelFor
    {
        public NativeArray<Particle> Particles;
        public RelativisticGravityMath Gravity;
        public float DeltaTime;
        public float OuterRadius;
        public float DiskHalfThickness;
        public uint FrameCount;

        public void Execute(int index)
        {
            Particle particle = Particles[index];
            bool captured = Gravity.Integrate(ref particle, DeltaTime);
            if (captured)
                Recycle(ref particle, (uint)index);
            Particles[index] = particle;
        }

        private void Recycle(ref Particle particle, uint id)
        {
            uint hash = RelativisticGravityMath.WangHash(id + FrameCount + particle.flagsAndSeed);
            float angle = RelativisticGravityMath.Hash01(hash ^ 0xa511e9b3u) * (2f * math.PI);
            float radius = OuterRadius * math.lerp(0.96f, 1f, RelativisticGravityMath.Hash01(hash));
            float vertical = (RelativisticGravityMath.Hash01(hash ^ 0x63d83595u) * 2f - 1f) * DiskHalfThickness;
            float sin = math.sin(angle);
            float cos = math.cos(angle);
            float3 radial = new float3(cos, 0f, sin);
            float3 tangent = new float3(-sin, 0f, cos);

            particle.position = new float3(radial.x * radius, vertical, radial.z * radius);
            particle.velocity = tangent * Gravity.CircularSpeed(radius);
            particle.temperature = 2800f;
            particle.flagsAndSeed = hash & 0x3fffffffu;
        }
    }
}
