using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;
using OmniTwin.BlackholeSimulation.Core;
using OmniTwin.BlackholeSimulation.Rendering;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace OmniTwin.BlackholeSimulation.Systems
{
    /// <summary>🎛️ Owns particle lifetime and routes each frame through the selected simulation backend.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AccretionDiskController))]
    public sealed class BlackholeSimulationController : MonoBehaviour
    {
        private const int ThreadsPerGroup = 256;
        private const int TdeParticleBudget = 4096;
        private static readonly uint[] ClearTdeCaptureCounter = { 0u };

        [Header("Simulation")]
        [SerializeField] private BlackholeSimulationSettings settings = new BlackholeSimulationSettings();
        [SerializeField] private ParticleCountPreset populationPreset = ParticleCountPreset.OneHundredThousand;
        [SerializeField] private SimulationBackend backend = SimulationBackend.GPUCompute;
        [SerializeField] private ComputeShader gravityCompute;
        [SerializeField] private Camera renderCamera;
        [SerializeField] private bool simulateWhilePaused;

        private AccretionDiskController _disk;
        private NativeArray<Particle> _particles;
        private GraphicsBuffer _particleBuffer;
        private GraphicsBuffer _tdeCaptureCounter;
        private AsyncGPUReadbackRequest _tdeCaptureReadback;
        private JobHandle _jobHandle;
        private int _resetKernel = -1;
        private int _integrateKernel = -1;
        private uint _frameCount;
        private SimulationBackend _activeBackend;
        private bool _initialized;
        private bool _synchronousCpuCulling;
        private bool _tdeReadbackPending;
        private uint _tdeGeneration;
        private uint _tdeReadbackGeneration;
        private float _nextTdeReadbackTime;
        private int _tdeParticleStart;

        public BlackholeSimulationSettings Settings => settings;
        public SimulationBackend Backend => backend;
        public int ParticleCount => _particles.IsCreated ? _particles.Length : 0;
        public double LastIntegrationMilliseconds { get; private set; }
        public double LastTotalCpuStepMilliseconds { get; private set; }
        public double LastGpuCommandMilliseconds { get; private set; }
        public long LastManagedAllocatedBytes { get; private set; }
        public uint CpuVisibleCount => _disk != null ? _disk.CpuVisibleCount : 0;
        public GraphicsBuffer ParticleBuffer => _particleBuffer;
        public AccretionDiskController Disk => _disk;
        public bool GpuComputeAvailable => IsGpuSupported();
        public bool IsReadyForBenchmark => _initialized && renderCamera != null && _disk != null && _disk.IsReadyForRendering;
        public bool TdeStreamActive { get; private set; }
        public int TdeCaptureSequence { get; private set; }

        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;

            _disk = GetComponent<AccretionDiskController>();
            if (renderCamera == null)
                renderCamera = Camera.main;
            if (RequiresPortableStartup())
            {
                backend = SimulationBackend.CPUBurstJobs;
                populationPreset = ParticleCountPreset.TwentyFiveThousand;
            }
            InitializeSimulation((int)populationPreset);
            if (backend == SimulationBackend.GPUCompute && !IsGpuSupported())
            {
                backend = SimulationBackend.CPUBurstJobs;
                populationPreset = ParticleCountPreset.TwentyFiveThousand;
                InitializeSimulation((int)populationPreset);
            }
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
                _disk.RenderGpu(renderCamera, gravityCompute, _resetKernel, _integrateKernel, _particleBuffer, _tdeCaptureCounter, settings, _frameCount, deltaTime);
                PollGpuTdeCapture();
                LastGpuCommandMilliseconds = _disk.LastGpuDispatchMilliseconds;
                LastIntegrationMilliseconds = 0.0;
                LastTotalCpuStepMilliseconds = 0.0;
            }
            else
            {
                if (!_synchronousCpuCulling && _disk.HasPendingCpuCulling && !_disk.CpuCullingIsComplete)
                {
                    _disk.DrawCpuCached(renderCamera, _particles, _particleBuffer, settings);
                    LastIntegrationMilliseconds = 0.0;
                    LastTotalCpuStepMilliseconds = 0.0;
                    LastGpuCommandMilliseconds = 0.0;
                    LastManagedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    return;
                }

                long totalCpuStepStart = Stopwatch.GetTimestamp();
                if (_disk.HasPendingCpuCulling)
                    _disk.TryCompleteCpuCulling();

                var job = new ParticleIntegrationJob
                {
                    Particles = _particles,
                    Gravity = new RelativisticGravityMath(settings.gravitationalParameter, settings.schwarzschildRadius, settings.softening, settings.maximumIntegrationStep),
                    DeltaTime = deltaTime,
                    OuterRadius = settings.outerRadius,
                    DiskHalfThickness = settings.diskHalfThickness,
                    FrameCount = _frameCount
                };
                long integrationStart = Stopwatch.GetTimestamp();
                _jobHandle = job.Schedule(_particles.Length, 128);
                _jobHandle.Complete();
                CheckCpuTdeCapture();
                LastIntegrationMilliseconds = (Stopwatch.GetTimestamp() - integrationStart) * 1000.0 / Stopwatch.Frequency;
                if (_particleBuffer != null)
                    _particleBuffer.SetData(_particles);
                _disk.RenderCpu(renderCamera, _particles, _particleBuffer, settings, _synchronousCpuCulling);
                LastTotalCpuStepMilliseconds = (Stopwatch.GetTimestamp() - totalCpuStepStart) * 1000.0 / Stopwatch.Frequency;
                LastGpuCommandMilliseconds = 0.0;
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
            _synchronousCpuCulling = targetBackend == SimulationBackend.CPUBurstJobs;
        }

        public void SetBackend(SimulationBackend targetBackend) => backend = targetBackend;

        /// <summary>🌟 Replaces a reserved tail segment with a compact, plunging 4,096-particle stellar body.</summary>
        public bool TryInjectTdeStar(Vector3 worldPosition, int seed)
        {
            if (!_initialized || !_particles.IsCreated || _particles.Length == 0)
                return false;

            int count = Mathf.Min(TdeParticleBudget, _particles.Length);
            _tdeParticleStart = _particles.Length - count;
            Vector3 localCenter = transform.InverseTransformPoint(worldPosition);
            localCenter.y = 0f;
            float radius = Mathf.Clamp(localCenter.magnitude, 7f, 9f);
            if (localCenter.sqrMagnitude < 1e-6f)
                localCenter = Vector3.forward * radius;
            else
                localCenter = localCenter.normalized * radius;

            Vector3 radial = localCenter.normalized;
            Vector3 tangent = new Vector3(-radial.z, 0f, radial.x).normalized;
            float inwardSpeed = Mathf.Min(0.78f, Mathf.Sqrt(2f * settings.gravitationalParameter / Mathf.Max(radius - settings.schwarzschildRadius, settings.softening)) * 0.55f);
            uint seedValue = unchecked((uint)seed);
            for (int i = 0; i < count; i++)
            {
                uint hash = RelativisticGravityMath.WangHash(seedValue + (uint)i * 0x9e3779b9u);
                float z = RelativisticGravityMath.Hash01(hash ^ 0xa511e9b3u) * 2f - 1f;
                float angle = RelativisticGravityMath.Hash01(hash ^ 0x63d83595u) * (2f * Mathf.PI);
                float radialSample = Mathf.Pow(RelativisticGravityMath.Hash01(hash ^ 0x85ebca6bu), 1f / 3f) * 0.35f;
                float planar = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
                Vector3 offset = new Vector3(planar * Mathf.Cos(angle), z, planar * Mathf.Sin(angle)) * radialSample;
                Vector3 dispersion = new Vector3(
                    RelativisticGravityMath.Hash01(hash ^ 0xc2b2ae35u) - 0.5f,
                    RelativisticGravityMath.Hash01(hash ^ 0x27d4eb2du) - 0.5f,
                    RelativisticGravityMath.Hash01(hash ^ 0x165667b1u) - 0.5f) * 0.07f;

                _particles[i + _tdeParticleStart] = new Particle
                {
                    position = new Unity.Mathematics.float3(localCenter.x + offset.x, offset.y, localCenter.z + offset.z),
                    temperature = 1.8f,
                    velocity = new Unity.Mathematics.float3(-radial.x * inwardSpeed + tangent.x * 0.18f + dispersion.x,
                        dispersion.y,
                        -radial.z * inwardSpeed + tangent.z * 0.18f + dispersion.z),
                    flagsAndSeed = (hash & ParticleFlags.SeedMask) | ParticleFlags.TidalDisruption
                };
            }

            if (_particleBuffer != null)
                _particleBuffer.SetData(_particles, _tdeParticleStart, _tdeParticleStart, count);
            if (_tdeCaptureCounter != null)
                _tdeCaptureCounter.SetData(ClearTdeCaptureCounter);

            _tdeGeneration++;
            TdeStreamActive = true;
            return true;
        }

        /// <summary>⏱️ Synchronous mode is reserved for measuring every CPU step in the benchmark harness.</summary>
        public void SetSynchronousCpuCulling(bool enabled) => _synchronousCpuCulling = enabled;

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
            _disk.Initialize(count);
            if (_disk.RequiresStructuredParticleBuffer)
            {
                _particleBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, Particle.StrideBytes);
                _particleBuffer.SetData(_particles);
            }
            if (backend == SimulationBackend.GPUCompute && _disk.RequiresStructuredParticleBuffer)
                _tdeCaptureCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
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
            return gravityCompute != null &&
                   SystemInfo.supportsComputeShaders &&
                   SystemInfo.supportsIndirectArgumentsBuffer &&
                   _disk != null &&
                   _disk.SupportsIndirectRendering;
        }

        private static bool RequiresPortableStartup()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return true;
#else
            return !SystemInfo.supportsComputeShaders ||
                   !SystemInfo.supportsIndirectArgumentsBuffer ||
                   SystemInfo.graphicsShaderLevel < 45;
#endif
        }

        private void CheckCpuTdeCapture()
        {
            if (!TdeStreamActive)
                return;

            int end = Mathf.Min(_tdeParticleStart + TdeParticleBudget, _particles.Length);
            for (int i = _tdeParticleStart; i < end; i++)
            {
                if ((_particles[i].flagsAndSeed & ParticleFlags.TidalCaptureRegistered) == 0)
                    continue;

                RegisterTdeCapture();
                return;
            }
        }

        private void PollGpuTdeCapture()
        {
            if (_tdeReadbackPending && _tdeCaptureReadback.done)
            {
                if (!_tdeCaptureReadback.hasError && _tdeReadbackGeneration == _tdeGeneration)
                {
                    Unity.Collections.NativeArray<uint> result = _tdeCaptureReadback.GetData<uint>();
                    if (result.Length > 0 && result[0] > 0u)
                        RegisterTdeCapture();
                }
                _tdeReadbackPending = false;
            }

            if (!TdeStreamActive || _tdeCaptureCounter == null || !SystemInfo.supportsAsyncGPUReadback ||
                _tdeReadbackPending || Time.unscaledTime < _nextTdeReadbackTime)
                return;

            try
            {
                _tdeCaptureReadback = AsyncGPUReadback.Request(_tdeCaptureCounter);
                _tdeReadbackGeneration = _tdeGeneration;
                _tdeReadbackPending = true;
                _nextTdeReadbackTime = Time.unscaledTime + 0.2f;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("TDE capture readback is unavailable: " + exception.Message, this);
                _nextTdeReadbackTime = Time.unscaledTime + 1f;
            }
        }

        private void RegisterTdeCapture()
        {
            TdeStreamActive = false;
            TdeCaptureSequence++;
        }

        private void ReleaseSimulation()
        {
            if (_disk != null)
                _disk.CompletePendingCpuCulling();
            if (_jobHandle.IsCompleted == false)
                _jobHandle.Complete();
            _particleBuffer?.Dispose();
            _particleBuffer = null;
            _tdeCaptureCounter?.Dispose();
            _tdeCaptureCounter = null;
            if (_particles.IsCreated)
                _particles.Dispose();
            _initialized = false;
            _synchronousCpuCulling = false;
            TdeStreamActive = false;
            _tdeParticleStart = 0;
            _tdeGeneration++;
        }
    }
}
