using Unity.Burst;
using Unity.Mathematics;

namespace OmniTwin.BlackholeSimulation.Core
{
    /// <summary>🕳️ Pure Paczyński–Wiita gravity and symplectic integration shared by CPU jobs and GPU kernels.</summary>
    [BurstCompile]
    public struct RelativisticGravityMath
    {
        public float GravitationalParameter;
        public float SchwarzschildRadius;
        public float Softening;
        public float MaximumStep;

        public RelativisticGravityMath(float gravitationalParameter, float schwarzschildRadius, float softening, float maximumStep)
        {
            GravitationalParameter = math.max(gravitationalParameter, 1e-6f);
            SchwarzschildRadius = math.max(schwarzschildRadius, 1e-6f);
            Softening = math.max(softening, 1e-6f);
            MaximumStep = math.max(maximumStep, 1e-6f);
        }

        /// <summary>🧮 Evaluates acceleration from Φ(r) = -GM / (r - rs), softened at the horizon.</summary>
        [BurstCompile]
        public float3 Acceleration(float3 position)
        {
            float radiusSquared = math.max(math.dot(position, position), 1e-12f);
            float radius = math.sqrt(radiusSquared);
            float denominator = math.max(radius - SchwarzschildRadius, Softening);
            float magnitude = GravitationalParameter / (denominator * denominator);
            return -(position * math.rsqrt(radiusSquared)) * magnitude;
        }

        /// <summary>🪐 Returns the Paczyński–Wiita specific orbital energy.</summary>
        [BurstCompile]
        public float SpecificEnergy(float3 position, float3 velocity)
        {
            float radius = math.sqrt(math.max(math.dot(position, position), 1e-12f));
            float denominator = math.max(radius - SchwarzschildRadius, Softening);
            return 0.5f * math.dot(velocity, velocity) - GravitationalParameter / denominator;
        }

        /// <summary>🛰️ Integrates one particle with velocity Verlet and reports horizon capture.</summary>
        [BurstCompile]
        public bool Integrate(ref Particle particle, float deltaTime)
        {
            if (!math.all(math.isfinite(particle.position)) || !math.all(math.isfinite(particle.velocity)))
                return true;

            if (!math.isfinite(deltaTime) || deltaTime <= 0f)
                return IsCaptured(particle.position);

            // A bounded substep keeps extreme editor stalls from overflowing float32 state.
            float dt = math.min(deltaTime, MaximumStep);
            float dtSquared = dt * dt;
            float3 acceleration0 = Acceleration(particle.position);
            particle.position += particle.velocity * dt + 0.5f * acceleration0 * dtSquared;
            float3 acceleration1 = Acceleration(particle.position);
            particle.velocity += 0.5f * (acceleration0 + acceleration1) * dt;

            if (!math.all(math.isfinite(particle.position)) || !math.all(math.isfinite(particle.velocity)))
                return true;

            return IsCaptured(particle.position);
        }

        [BurstCompile]
        public bool IsCaptured(float3 position)
        {
            return math.dot(position, position) <= SchwarzschildRadius * SchwarzschildRadius;
        }

        /// <summary>🔭 Relativistic circular speed for a circular orbit in the pseudo-Newtonian potential.</summary>
        [BurstCompile]
        public float CircularSpeed(float radius)
        {
            float safeRadius = math.max(radius, SchwarzschildRadius + Softening);
            return math.sqrt(GravitationalParameter * safeRadius) / math.max(safeRadius - SchwarzschildRadius, Softening);
        }

        /// <summary>🎲 Wang hash shared byte-for-byte with the HLSL recycling kernel.</summary>
        [BurstCompile]
        public static uint WangHash(uint value)
        {
            value = (value ^ 61u) ^ (value >> 16);
            value *= 9u;
            value ^= value >> 4;
            value *= 0x27d4eb2du;
            value ^= value >> 15;
            return value;
        }

        [BurstCompile]
        public static float Hash01(uint value)
        {
            return (WangHash(value) & 0x00ffffffu) * (1f / 16777216f);
        }
    }
}
