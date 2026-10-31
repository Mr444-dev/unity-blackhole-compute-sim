using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Assertions;
using OmniTwin.BlackholeSimulation.Core;

namespace OmniTwin.BlackholeSimulation.Tests
{
    /// <summary>🧪 Deterministic edit-mode verification checks without adding an assembly definition.</summary>
    public static class BlackholeSimulationTests
    {
        public static void ParticleStrideIsGpuAligned()
        {
            Assert.AreEqual(Particle.StrideBytes, Marshal.SizeOf(typeof(Particle)), "C# and HLSL particle stride must remain 32 bytes.");
            Assert.AreEqual(0, Marshal.OffsetOf(typeof(Particle), nameof(Particle.position)).ToInt32());
            Assert.AreEqual(12, Marshal.OffsetOf(typeof(Particle), nameof(Particle.temperature)).ToInt32());
            Assert.AreEqual(16, Marshal.OffsetOf(typeof(Particle), nameof(Particle.velocity)).ToInt32());
            Assert.AreEqual(28, Marshal.OffsetOf(typeof(Particle), nameof(Particle.flagsAndSeed)).ToInt32());
        }

        public static void StableCircularOrbitConservesRadiusAndEnergy()
        {
            const float gm = 1f;
            const float rs = 0.25f;
            const float radius = 5f * rs;
            var gravity = new RelativisticGravityMath(gm, rs, 0.01f, 0.001f);
            var particle = new Particle
            {
                position = new float3(radius, 0f, 0f),
                velocity = new float3(0f, 0f, gravity.CircularSpeed(radius)),
                temperature = 5000f
            };
            float initialEnergy = gravity.SpecificEnergy(particle.position, particle.velocity);

            for (int step = 0; step < 20000; step++)
                Assert.IsFalse(gravity.Integrate(ref particle, 0.001f), "A stable orbit outside the ISCO must not cross the event horizon.");

            float finalRadius = math.length(particle.position);
            float finalEnergy = gravity.SpecificEnergy(particle.position, particle.velocity);
            Assert.IsTrue(math.abs(finalRadius - radius) < 0.01f, "Circular orbital radius drift exceeded tolerance.");
            Assert.IsTrue(math.abs(finalEnergy - initialEnergy) < 0.01f, "Velocity-Verlet orbital energy drift exceeded tolerance.");
        }

        public static void SubIscoInfallCrossesTheEventHorizon()
        {
            const float rs = 0.25f;
            var gravity = new RelativisticGravityMath(1f, rs, 0.005f, 0.001f);
            var particle = new Particle
            {
                position = new float3(2f * rs, 0f, 0f),
                velocity = new float3(-0.5f, 0f, 0f)
            };
            bool captured = false;
            for (int step = 0; step < 2000 && !captured; step++)
                captured = gravity.Integrate(ref particle, 0.001f);

            Assert.IsTrue(captured, "An inward radial orbit inside the ISCO should eventually cross r_s.");
        }

        public static void PopulationGenerationIsDeterministicAndFinite()
        {
            NativeArray<Particle> first = ParticlePopulationGenerator.Create(4096, 81, 1f, 0.25f, 8f, 0.2f, 0.12f, 0.01f, Allocator.Temp);
            NativeArray<Particle> second = ParticlePopulationGenerator.Create(4096, 81, 1f, 0.25f, 8f, 0.2f, 0.12f, 0.01f, Allocator.Temp);
            try
            {
                for (int i = 0; i < first.Length; i++)
                {
                    Assert.AreEqual(first[i].position, second[i].position, "Seeded positions must repeat exactly.");
                    Assert.AreEqual(first[i].velocity, second[i].velocity, "Seeded velocities must repeat exactly.");
                    Assert.IsTrue(math.all(math.isfinite(first[i].position)) && math.all(math.isfinite(first[i].velocity)), "Generated particle state must remain finite.");
                }

                var gravity = new RelativisticGravityMath(1f, 0.25f, 0.01f, 0.05f);
                Particle extremeStepParticle = first[0];
                gravity.Integrate(ref extremeStepParticle, float.MaxValue);
                Assert.IsTrue(math.all(math.isfinite(extremeStepParticle.position)) && math.all(math.isfinite(extremeStepParticle.velocity)), "An extreme time step must not introduce NaN or infinity.");
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        public static void RunAll()
        {
            ParticleStrideIsGpuAligned();
            StableCircularOrbitConservesRadiusAndEnergy();
            SubIscoInfallCrossesTheEventHorizon();
            PopulationGenerationIsDeterministicAndFinite();
        }
    }
}
