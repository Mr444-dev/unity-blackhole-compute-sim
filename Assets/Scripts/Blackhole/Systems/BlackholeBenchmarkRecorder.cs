using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using OmniTwin.BlackholeSimulation.Core;
using OmniTwin.BlackholeSimulation.Systems;

namespace OmniTwin.BlackholeSimulation.Benchmarking
{
    /// <summary>📊 Sweeps CPU Burst and GPU compute at portfolio population sizes and exports honest Markdown metrics.</summary>
    [DefaultExecutionOrder(10000)]
    public sealed class BlackholeBenchmarkRecorder : MonoBehaviour
    {
        private const int PopulationCount = 3;
        private const int BackendCount = 2;

        [SerializeField] private BlackholeSimulationController simulation;
        [SerializeField] private bool autoRunOnStart;
        [SerializeField, Min(1)] private int warmupFrames = 30;
        [SerializeField, Min(1)] private int sampleFrames = 120;
        [SerializeField] private string outputFileName = "Blackhole-Benchmark-Results.md";

        private readonly int[] _populationSizes =
        {
            (int)ParticleCountPreset.TenThousand,
            (int)ParticleCountPreset.OneHundredThousand,
            (int)ParticleCountPreset.TwoHundredFiftyThousand
        };
        private BenchmarkRow[] _rows;
        private Coroutine _runningCoroutine;
        private string _outputPathOverride;

        /// <summary>📄 Absolute location of the most recently exported benchmark report.</summary>
        public string LastExportPath { get; private set; }

        public bool IsRunning { get; private set; }
        public bool HasCompleted { get; private set; }
        public string LastError { get; private set; }

        private void Start()
        {
            if (autoRunOnStart)
                RunBenchmark();
        }

        private void OnDisable()
        {
            if (simulation != null)
                simulation.SetSynchronousCpuCulling(false);
            if (_runningCoroutine != null)
            {
                StopCoroutine(_runningCoroutine);
                _runningCoroutine = null;
                IsRunning = false;
                HasCompleted = true;
                LastError = "Benchmark was cancelled because its GameObject was disabled.";
                BlackholeInteractionState.DeterministicMode = false;
            }
        }

        /// <summary>📍 Sets the destination for the next report, used by the Editor menu runner.</summary>
        public void SetOutputPathOverride(string absolutePath)
        {
            _outputPathOverride = string.IsNullOrWhiteSpace(absolutePath) ? null : Path.GetFullPath(absolutePath);
        }

        /// <summary>▶️ Starts one benchmark sweep if a sweep is not already running.</summary>
        public void RunBenchmark()
        {
            if (IsRunning)
                return;

            BlackholeInteractionState.DeterministicMode = true;
            IsRunning = true;
            HasCompleted = false;
            LastError = null;
            _rows = null;
            _runningCoroutine = StartCoroutine(RunSweep());
        }

        private IEnumerator RunSweep()
        {
            if (simulation == null)
            {
                CompleteWithError("Blackhole benchmark requires a BlackholeSimulationController reference.");
                yield break;
            }

            if (!simulation.IsReadyForBenchmark)
            {
                CompleteWithError("Assign the simulation camera and accretion-disk material before running the benchmark.");
                yield break;
            }

            _rows = new BenchmarkRow[PopulationCount * BackendCount];
            int rowIndex = 0;
            for (int backendIndex = 0; backendIndex < BackendCount; backendIndex++)
            {
                SimulationBackend backend = backendIndex == 0 ? SimulationBackend.CPUBurstJobs : SimulationBackend.GPUCompute;
                for (int populationIndex = 0; populationIndex < PopulationCount; populationIndex++)
                {
                    int particleCount = _populationSizes[populationIndex];
                    if (backend == SimulationBackend.GPUCompute && !simulation.GpuComputeAvailable)
                    {
                        _rows[rowIndex++] = BenchmarkRow.Unsupported(backend, particleCount, "Compute plus indirect arguments are unavailable.");
                        continue;
                    }

                    simulation.ConfigureForBenchmark(backend, particleCount);
                    for (int frame = 0; frame < warmupFrames; frame++)
                        yield return null;

                    double frameMilliseconds = 0.0;
                    double integrationMilliseconds = 0.0;
                    double totalCpuStepMilliseconds = 0.0;
                    double gpuDispatchMilliseconds = 0.0;
                    long managedBytes = 0;
                    for (int frame = 0; frame < sampleFrames; frame++)
                    {
                        yield return null;
                        frameMilliseconds += Time.unscaledDeltaTime * 1000.0;
                        integrationMilliseconds += simulation.LastIntegrationMilliseconds;
                        totalCpuStepMilliseconds += simulation.LastTotalCpuStepMilliseconds;
                        gpuDispatchMilliseconds += simulation.LastGpuCommandMilliseconds;
                        managedBytes += simulation.LastManagedAllocatedBytes;
                    }

                    uint visibleCount = simulation.CpuVisibleCount;
                    bool visibleCountAvailable = backend == SimulationBackend.CPUBurstJobs;
                    string visibleCountStatus = visibleCountAvailable ? "CPU culling" : "Not read";
                    if (backend == SimulationBackend.GPUCompute)
                    {
                        visibleCount = 0;
                        visibleCountAvailable = false;
                        if (!SystemInfo.supportsAsyncGPUReadback)
                        {
                            visibleCountStatus = "Unavailable: async GPU readback is unsupported.";
                        }
                        else if (simulation.Disk == null || simulation.Disk.IndirectArguments == null)
                        {
                            visibleCountStatus = "Unavailable: indirect argument buffer is missing.";
                        }
                        else
                        {
                            AsyncGPUReadbackRequest request = default;
                            bool requestStarted = false;
                            try
                            {
                                request = AsyncGPUReadback.Request(simulation.Disk.IndirectArguments);
                                requestStarted = true;
                            }
                            catch (Exception exception)
                            {
                                visibleCountStatus = "Failed: " + exception.GetType().Name;
                            }

                            if (requestStarted)
                            {
                                while (!request.done)
                                    yield return null;

                                if (request.hasError)
                                {
                                    visibleCountStatus = "Failed: Unity reported an async readback error.";
                                }
                                else
                                {
                                    NativeArray<uint> arguments = request.GetData<uint>();
                                    if (arguments.Length < 2)
                                    {
                                        visibleCountStatus = $"Failed: expected at least two uint arguments, received {arguments.Length}.";
                                    }
                                    else
                                    {
                                        visibleCount = arguments[1];
                                        visibleCountAvailable = true;
                                        visibleCountStatus = "Valid";
                                    }
                                }
                            }
                        }
                    }

                    _rows[rowIndex++] = new BenchmarkRow(
                        backend,
                        particleCount,
                        frameMilliseconds / sampleFrames,
                        integrationMilliseconds / sampleFrames,
                        totalCpuStepMilliseconds / sampleFrames,
                        gpuDispatchMilliseconds / sampleFrames,
                        visibleCount,
                        visibleCountAvailable,
                        visibleCountStatus,
                        (double)managedBytes / sampleFrames,
                        true);
                }
            }

            if (ExportMarkdown(_rows))
                CompleteSuccessfully();
            else
                CompleteWithError("The benchmark sweep finished, but the Markdown report could not be written.");
        }

        private bool ExportMarkdown(BenchmarkRow[] rows)
        {
            var markdown = new StringBuilder(3072);
            markdown.AppendLine("# Blackhole Simulation Benchmark");
            markdown.AppendLine();
            markdown.Append("Captured: ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)).AppendLine();
            markdown.Append("Graphics: ").Append(SystemInfo.graphicsDeviceName).Append(" (").Append(SystemInfo.graphicsDeviceType).AppendLine(")");
            markdown.Append("CPU: ").Append(SystemInfo.processorType).AppendLine();
            markdown.Append("Warmup frames: ").Append(warmupFrames).Append("; measured frames: ").Append(sampleFrames).AppendLine();
            markdown.AppendLine("GPU dispatch time is CPU submission wall time, not GPU execution time. CPU integration excludes culling and uploads; total CPU step includes Burst integration, Burst culling, particle/index uploads, and draw submission.");
            markdown.AppendLine();
            markdown.AppendLine("| Backend | Particles | Frame time (ms) | CPU integration (ms) | Total CPU step (ms) | GPU dispatch submission (ms) | Visible particles | Visible-count status | Managed GC alloc / frame (B) |");
            markdown.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|---:|");

            for (int i = 0; i < rows.Length; i++)
            {
                BenchmarkRow row = rows[i];
                if (!row.Available)
                {
                    markdown.Append("| ").Append(row.Backend)
                        .Append(" | ").Append(row.ParticleCount.ToString("N0", CultureInfo.InvariantCulture))
                        .Append(" | Unsupported | — | — | — | — | ").Append(row.VisibleCountStatus)
                        .AppendLine(" | — |");
                    continue;
                }

                markdown.Append("| ").Append(row.Backend)
                    .Append(" | ").Append(row.ParticleCount.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(row.FrameMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(FormatBackendMetric(row.Backend, SimulationBackend.CPUBurstJobs, row.IntegrationMilliseconds))
                    .Append(" | ").Append(FormatBackendMetric(row.Backend, SimulationBackend.CPUBurstJobs, row.TotalCpuStepMilliseconds))
                    .Append(" | ").Append(FormatBackendMetric(row.Backend, SimulationBackend.GPUCompute, row.GpuDispatchMilliseconds))
                    .Append(" | ").Append(row.VisibleCountAvailable ? row.VisibleCount.ToString("N0", CultureInfo.InvariantCulture) : "—")
                    .Append(" | ").Append(row.VisibleCountStatus)
                    .Append(" | ").Append(row.ManagedBytesPerFrame.ToString("F1", CultureInfo.InvariantCulture)).AppendLine(" |");
            }

            markdown.AppendLine();
            markdown.AppendLine("A GPU visible count is exported only when its asynchronous readback succeeds. Unsupported or failed readbacks are explicitly marked and shown as an em dash; they are never presented as a measured zero.");
            markdown.AppendLine("Managed allocation measures bytes allocated on the simulation controller's main thread during each update.");

            try
            {
                LastExportPath = _outputPathOverride == null
                    ? Path.Combine(Application.persistentDataPath, outputFileName)
                    : _outputPathOverride;
                string fullPath = Path.GetFullPath(LastExportPath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(fullPath, markdown.ToString());
                LastExportPath = fullPath;
                Debug.Log("Blackhole benchmark Markdown exported to: " + LastExportPath, this);
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is NotSupportedException)
            {
                LastError = "Could not write the benchmark report: " + exception.Message;
                Debug.LogError(LastError, this);
                return false;
            }
        }

        private void CompleteSuccessfully()
        {
            BlackholeInteractionState.DeterministicMode = false;
            if (simulation != null)
                simulation.SetSynchronousCpuCulling(false);
            _runningCoroutine = null;
            IsRunning = false;
            HasCompleted = true;
        }

        private void CompleteWithError(string error)
        {
            BlackholeInteractionState.DeterministicMode = false;
            if (simulation != null)
                simulation.SetSynchronousCpuCulling(false);
            LastError = error;
            Debug.LogError(error, this);
            _runningCoroutine = null;
            IsRunning = false;
            HasCompleted = true;
        }

        private static string FormatBackendMetric(SimulationBackend rowBackend, SimulationBackend metricBackend, double value)
        {
            return rowBackend == metricBackend
                ? value.ToString("F3", CultureInfo.InvariantCulture)
                : "—";
        }

        private readonly struct BenchmarkRow
        {
            internal readonly SimulationBackend Backend;
            internal readonly int ParticleCount;
            internal readonly double FrameMilliseconds;
            internal readonly double IntegrationMilliseconds;
            internal readonly double TotalCpuStepMilliseconds;
            internal readonly double GpuDispatchMilliseconds;
            internal readonly uint VisibleCount;
            internal readonly bool VisibleCountAvailable;
            internal readonly string VisibleCountStatus;
            internal readonly double ManagedBytesPerFrame;
            internal readonly bool Available;

            internal BenchmarkRow(
                SimulationBackend backend,
                int particleCount,
                double frameMilliseconds,
                double integrationMilliseconds,
                double totalCpuStepMilliseconds,
                double gpuDispatchMilliseconds,
                uint visibleCount,
                bool visibleCountAvailable,
                string visibleCountStatus,
                double managedBytesPerFrame,
                bool available)
            {
                Backend = backend;
                ParticleCount = particleCount;
                FrameMilliseconds = frameMilliseconds;
                IntegrationMilliseconds = integrationMilliseconds;
                TotalCpuStepMilliseconds = totalCpuStepMilliseconds;
                GpuDispatchMilliseconds = gpuDispatchMilliseconds;
                VisibleCount = visibleCount;
                VisibleCountAvailable = visibleCountAvailable;
                VisibleCountStatus = visibleCountStatus;
                ManagedBytesPerFrame = managedBytesPerFrame;
                Available = available;
            }

            internal static BenchmarkRow Unsupported(SimulationBackend backend, int particleCount, string reason)
            {
                return new BenchmarkRow(backend, particleCount, 0, 0, 0, 0, 0, false, reason, 0, false);
            }
        }
    }
}
