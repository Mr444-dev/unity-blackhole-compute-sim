using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using OmniTwin.BlackholeSimulation.Core;

namespace OmniTwin.BlackholeSimulation.Rendering
{
    /// <summary>🧵 Burst-culls fixed-size particle blocks into disjoint scratch ranges.</summary>
    [BurstCompile]
    internal struct CpuFrustumCullingJob : IJobParallelFor
    {
        internal const int BlockSize = 64;

        [ReadOnly] public NativeArray<Particle> Particles;
        [ReadOnly] public NativeArray<float4> FrustumPlanes;
        [WriteOnly] public NativeArray<int> BlockCounts;
        [NativeDisableParallelForRestriction] public NativeArray<uint> VisibilityScratch;
        public float3 Origin;
        public float3 Scale;
        public float ParticleCullRadius;

        public void Execute(int blockIndex)
        {
            int firstParticle = blockIndex * BlockSize;
            int endParticle = math.min(firstParticle + BlockSize, Particles.Length);
            int visibleCount = 0;

            for (int particleIndex = firstParticle; particleIndex < endParticle; particleIndex++)
            {
                float3 worldPosition = Particles[particleIndex].position * Scale + Origin;
                bool visible = true;
                for (int planeIndex = 0; planeIndex < 6; planeIndex++)
                {
                    float4 plane = FrustumPlanes[planeIndex];
                    if (math.dot(plane.xyz, worldPosition) + plane.w < -ParticleCullRadius)
                    {
                        visible = false;
                        break;
                    }
                }

                if (visible)
                    VisibilityScratch[firstParticle + visibleCount++] = (uint)particleIndex;
            }

            BlockCounts[blockIndex] = visibleCount;
        }
    }

    /// <summary>🔢 Builds stable output offsets for the block-culling results.</summary>
    [BurstCompile]
    internal struct CpuFrustumPrefixSumJob : IJob
    {
        [ReadOnly] public NativeArray<int> BlockCounts;
        [WriteOnly] public NativeArray<int> BlockOffsets;
        [WriteOnly] public NativeArray<int> TotalVisibleCount;

        public void Execute()
        {
            int total = 0;
            for (int blockIndex = 0; blockIndex < BlockCounts.Length; blockIndex++)
            {
                BlockOffsets[blockIndex] = total;
                total += BlockCounts[blockIndex];
            }

            TotalVisibleCount[0] = total;
        }
    }

    /// <summary>📦 Copies visible block ranges into the contiguous draw-index list in parallel.</summary>
    [BurstCompile]
    internal struct CpuFrustumScatterJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<uint> VisibilityScratch;
        [ReadOnly] public NativeArray<int> BlockCounts;
        [ReadOnly] public NativeArray<int> BlockOffsets;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<uint> VisibleIndices;

        public void Execute(int blockIndex)
        {
            int sourceOffset = blockIndex * CpuFrustumCullingJob.BlockSize;
            int destinationOffset = BlockOffsets[blockIndex];
            int count = BlockCounts[blockIndex];
            for (int i = 0; i < count; i++)
                VisibleIndices[destinationOffset + i] = VisibilityScratch[sourceOffset + i];
        }
    }
}
