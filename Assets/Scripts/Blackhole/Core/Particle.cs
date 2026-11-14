using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace OmniTwin.BlackholeSimulation.Core
{
    /// <summary>🧱 GPU-compatible particle record with a fixed 32-byte stride.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
    public struct Particle
    {
        public const int StrideBytes = 32;

        public float3 position;
        public float temperature;
        public float3 velocity;
        public uint flagsAndSeed;
    }

    /// <summary>⚙️ Selects where particle integration and visibility culling run.</summary>
    public enum SimulationBackend
    {
        GPUCompute,
        CPUBurstJobs
    }

    /// <summary>📊 Supported repeatable population sizes.</summary>
    public enum ParticleCountPreset
    {
        TenThousand = 10000,
        TwentyFiveThousand = 25000,
        OneHundredThousand = 100000,
        TwoHundredFiftyThousand = 250000,
        FiveHundredThousand = 500000
    }

    /// <summary>🚩 High bits in the particle seed word reserved for transient TDE state.</summary>
    public static class ParticleFlags
    {
        public const uint TidalDisruption = 0x80000000u;
        public const uint TidalCaptureRegistered = 0x40000000u;
        public const uint SeedMask = 0x3fffffffu;
    }
}
