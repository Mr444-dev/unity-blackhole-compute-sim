using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using OmniTwin.BlackholeSimulation.Core;
using OmniTwin.BlackholeSimulation.Rendering;

namespace OmniTwin.BlackholeSimulation.Systems
{
    /// <summary>🎛️ Owns particle lifetime and routes each frame through the selected simulation backend.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AccretionDiskController))]
    public sealed class BlackholeSimulationController : MonoBehaviour
    {
        private const int ThreadsPerGroup = 256;

        [Header("Simulation")]
        [SerializeField] private BlackholeSimulationSettings settings = new BlackholeSimulationSettings();
        [SerializeField] private ParticleCountPreset populationPreset = ParticleCountPreset.TenThousand;
        [SerializeField] private SimulationBackend backend = SimulationBackend.GPUCompute;
        [SerializeField] private ComputeShader gravityCompute;
        [SerializeField] private Camera renderCamera;
        [SerializeField] private bool simulateWhilePaused;

        private AccretionDiskController _disk;
        private NativeArray<Particle> _particles;
        private GraphicsBuffer _particleBuffer;
        private JobHandle _jobHandle;
        private int _resetKernel = -1;
        private int _integrateKernel = -1;
        private uint _frameCount;
        private SimulationBackend _activeBackend;
        private bool _initialized;

        public BlackholeSimulationSettings Settings => settings;
        public SimulationBackend Backend => backend;
        public int ParticleCount => _particles.IsCreated ? _particles.Length : 0;
        public double LastDispatchMilliseconds { get; private set; }
        public long LastManagedAllocatedBytes { get; private set; }
        public uint CpuVisibleCount => _disk != null ? _disk.CpuVisibleCount : 0;
        public GraphicsBuffer ParticleBuffer => _particleBuffer;
        public AccretionDiskController Disk => _disk;
        public bool GpuComputeAvailable => IsGpuSupported();
        public bool IsReadyForBenchmark => _initialized && renderCamera != null && _disk != null && _disk.IsReadyForRendering;

        private void OnEnable()
        {
            _disk = GetComponent<AccretionDiskController>();
            if (renderCamera == null)
                renderCamera = Camera.main;
            InitializeSimulation((int)populationPreset);
        }

        private void Update()
        {
            if (!_initialized || _disk == null || renderCamera == null)
                return;

            float deltaTime = (simulateWhilePaused ? Time.unscaledDeltaTime : Time.deltaTime) * settings.timeScale;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            SimulationBackend selectedBackend = backend == SimulationBackend.GPUCompute && !IsGpuSupported()
                ? SimulationBackend.CPUBurstJobs
                : backend;
            if (selectedBackend != _activeBackend)
            {
                int currentCount = _particles.Length;
                backend = selectedBackend;
                InitializeSimulation(currentCount);
            }

            if (backend == SimulationBackend.GPUCompute)
            {
                _disk.RenderGpu(renderCamera, gravityCompute, _resetKernel, _integrateKernel, _particleBuffer, settings, _frameCount, deltaTime);
                LastDispatchMilliseconds = _disk.LastGpuDispatchMilliseconds;
            }
            else
            {
                var job = new ParticleIntegrationJob
                {
                    Particles = _particles,
                    Gravity = new RelativisticGravityMath(settings.gravitationalParameter, settings.schwarzschildRadius, settings.softening, settings.maximumIntegrationStep),
                    DeltaTime = deltaTime,
                    OuterRadius = settings.outerRadius,
                    DiskHalfThickness = settings.diskHalfThickness,
                    FrameCount = _frameCount
                };
                long jobStartTicks = Stopwatch.GetTimestamp();
                _jobHandle = job.Schedule(_particles.Length, 128);
                _jobHandle.Complete();
                LastDispatchMilliseconds = (Stopwatch.GetTimestamp() - jobStartTicks) * 1000.0 / Stopwatch.Frequency;
                _particleBuffer.SetData(_particles);
                _disk.RenderCpu(renderCamera, _particles, _particleBuffer, settings);
            }

            LastManagedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            _frameCount++;
        }

        private void OnDisable() => ReleaseSimulation();
        private void OnDestroy() => ReleaseSimulation();

        /// <summary>📏 Rebuilds native and GPU storage for the benchmark harness or a population preset change.</summary>
        public void ConfigureForBenchmark(SimulationBackend targetBackend, int particleCount)
        {
            backend = targetBackend;
            InitializeSimulation(particleCount);
        }

        public void SetBackend(SimulationBackend targetBackend) => backend = targetBackend;

        private void InitializeSimulation(int particleCount)
        {
            ReleaseSimulation();
            ValidateSettings();
            int count = Mathf.Max(1, particleCount);
            settings.particleCount = count;
            _particles = ParticlePopulationGenerator.Create(
                count,
                settings.seed,
                settings.gravitationalParameter,
                settings.schwarzschildRadius,
                settings.outerRadius,
                settings.diskHalfThickness,
                settings.eccentricity,
                settings.softening,
                Allocator.Persistent);
            _particleBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, Particle.StrideBytes);
            _particleBuffer.SetData(_particles);
            _disk.Initialize(count);
            _frameCount = 0;

            if (gravityCompute != null)
            {
                _resetKernel = gravityCompute.FindKernel("ResetIndirectArgs");
                _integrateKernel = gravityCompute.FindKernel("IntegrateAndCull");
            }

            _activeBackend = backend;
            _initialized = true;
        }

        private void OnValidate() => ValidateSettings();

        private void ValidateSettings()
        {
            if (settings == null)
                settings = new BlackholeSimulationSettings();

            settings.gravitationalParameter = Mathf.Max(settings.gravitationalParameter, 1e-4f);
            settings.schwarzschildRadius = Mathf.Max(settings.schwarzschildRadius, 1e-4f);
            settings.softening = Mathf.Max(settings.softening, 1e-4f);
            settings.outerRadius = Mathf.Max(settings.outerRadius, 3f * settings.schwarzschildRadius + settings.softening);
            settings.diskHalfThickness = Mathf.Max(settings.diskHalfThickness, 0f);
            settings.maximumIntegrationStep = Mathf.Max(settings.maximumIntegrationStep, 1e-4f);
        }

        private bool IsGpuSupported()
        {
            return gravityCompute != null && SystemInfo.supportsComputeShaders && SystemInfo.supportsIndirectArgumentsBuffer;
        }

        private void ReleaseSimulation()
        {
            if (_jobHandle.IsCompleted == false)
                _jobHandle.Complete();
            _particleBuffer?.Dispose();
            _particleBuffer = null;
            if (_particles.IsCreated)
                _particles.Dispose();
            _initialized = false;
        }
    }
}
