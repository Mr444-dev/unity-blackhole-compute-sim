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
    /// <summary>📊 Sweeps CPU Burst and GPU compute at portfolio population sizes and exports Markdown metrics.</summary>
    [DefaultExecutionOrder(10000)]
    public sealed class BlackholeBenchmarkRecorder : MonoBehaviour
    {
        private const int PopulationCount = 3;
        private const int BackendCount = 2;

        [SerializeField] private BlackholeSimulationController simulation;
        [SerializeField] private bool autoRunOnStart = true;
        [SerializeField, Min(1)] private int warmupFrames = 30;
        [SerializeField, Min(1)] private int sampleFrames = 120;
        [SerializeField] private string outputFileName = "blackhole-benchmarks.md";

        private readonly int[] _populationSizes =
        {
            (int)ParticleCountPreset.TenThousand,
            (int)ParticleCountPreset.OneHundredThousand,
            (int)ParticleCountPreset.TwoHundredFiftyThousand
        };
        private BenchmarkRow[] _rows;

        /// <summary>📄 Absolute location of the most recently exported benchmark report.</summary>
        public string LastExportPath { get; private set; }

        private void Start()
        {
            if (autoRunOnStart)
                StartCoroutine(RunSweep());
        }

        public void RunBenchmark() => StartCoroutine(RunSweep());

        private IEnumerator RunSweep()
        {
            if (simulation == null)
            {
                Debug.LogError("Blackhole benchmark requires a BlackholeSimulationController reference.", this);
                yield break;
            }

            if (!simulation.IsReadyForBenchmark)
            {
                Debug.LogError("Assign the simulation camera and accretion-disk material before running the benchmark.", this);
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
                        _rows[rowIndex++] = BenchmarkRow.Unsupported(backend, particleCount);
                        continue;
                    }

                    simulation.ConfigureForBenchmark(backend, particleCount);
                    for (int frame = 0; frame < warmupFrames; frame++)
                        yield return null;

                    double frameMilliseconds = 0.0;
                    double dispatchMilliseconds = 0.0;
                    long managedBytes = 0;
                    for (int frame = 0; frame < sampleFrames; frame++)
                    {
                        yield return null;
                        frameMilliseconds += Time.unscaledDeltaTime * 1000.0;
                        dispatchMilliseconds += simulation.LastDispatchMilliseconds;
                        managedBytes += simulation.LastManagedAllocatedBytes;
                    }

                    uint visibleCount = simulation.CpuVisibleCount;
                    if (backend == SimulationBackend.GPUCompute)
                    {
                        AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(simulation.Disk.IndirectArguments);
                        while (!request.done)
                            yield return null;

                        if (!request.hasError)
                        {
                            NativeArray<uint> arguments = request.GetData<uint>();
                            if (arguments.Length > 1)
                                visibleCount = arguments[1];
                        }
                    }

                    _rows[rowIndex++] = new BenchmarkRow(
                        backend,
                        particleCount,
                        frameMilliseconds / sampleFrames,
                        dispatchMilliseconds / sampleFrames,
                        visibleCount,
                        (double)managedBytes / sampleFrames,
                        true);
                }
            }

            ExportMarkdown(_rows);
        }

        private void ExportMarkdown(BenchmarkRow[] rows)
        {
            var markdown = new StringBuilder(2048);
            markdown.AppendLine("# Blackhole Simulation Benchmark");
            markdown.AppendLine();
            markdown.Append("Captured: ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)).AppendLine();
            markdown.Append("Graphics: ").Append(SystemInfo.graphicsDeviceName).Append(" ( ").Append(SystemInfo.graphicsDeviceType).AppendLine(" )");
            markdown.Append("CPU: ").Append(SystemInfo.processorType).AppendLine();
            markdown.Append("Warmup frames: ").Append(warmupFrames).Append("; measured frames: ").Append(sampleFrames).AppendLine();
            markdown.AppendLine("Dispatch/step time is CPU wall time for dispatch submission or Burst schedule+completion; GPU execution time requires a platform GPU profiler.");
            markdown.AppendLine();
            markdown.AppendLine("| Backend | Particles | Frame time (ms) | Dispatch / step CPU time (ms) | Visible particles | Managed GC alloc / frame (B) |");
            markdown.AppendLine("|---|---:|---:|---:|---:|---:|");

            for (int i = 0; i < rows.Length; i++)
            {
                BenchmarkRow row = rows[i];
                if (!row.Available)
                {
                    markdown.Append("| ").Append(row.Backend).Append(" | ").Append(row.ParticleCount.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" | Unsupported | Unsupported | — | — |");
                    continue;
                }

                markdown.Append("| ").Append(row.Backend)
                    .Append(" | ").Append(row.ParticleCount.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(row.FrameMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(row.DispatchMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(row.VisibleCount.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(row.ManagedBytesPerFrame.ToString("F1", CultureInfo.InvariantCulture)).AppendLine(" |");
            }

            markdown.AppendLine();
            markdown.AppendLine("Managed bytes are measured around the simulation controller update on the main thread. The visible GPU count is captured once per benchmark case with asynchronous readback; normal rendering performs no readback.");

            LastExportPath = Path.Combine(Application.persistentDataPath, outputFileName);
            try
            {
                File.WriteAllText(LastExportPath, markdown.ToString());
                Debug.Log("Blackhole benchmark Markdown exported to: " + LastExportPath, this);
            }
            catch (IOException exception)
            {
                Debug.LogError("Could not write the benchmark report: " + exception.Message, this);
            }
            catch (UnauthorizedAccessException exception)
            {
                Debug.LogError("Could not write the benchmark report: " + exception.Message, this);
            }
        }

        private readonly struct BenchmarkRow
        {
            internal readonly SimulationBackend Backend;
            internal readonly int ParticleCount;
            internal readonly double FrameMilliseconds;
            internal readonly double DispatchMilliseconds;
            internal readonly uint VisibleCount;
            internal readonly double ManagedBytesPerFrame;
            internal readonly bool Available;

            internal BenchmarkRow(SimulationBackend backend, int particleCount, double frameMilliseconds, double dispatchMilliseconds, uint visibleCount, double managedBytesPerFrame, bool available)
            {
                Backend = backend;
                ParticleCount = particleCount;
                FrameMilliseconds = frameMilliseconds;
                DispatchMilliseconds = dispatchMilliseconds;
                VisibleCount = visibleCount;
                ManagedBytesPerFrame = managedBytesPerFrame;
                Available = available;
            }

            internal static BenchmarkRow Unsupported(SimulationBackend backend, int particleCount)
            {
                return new BenchmarkRow(backend, particleCount, 0, 0, 0, 0, false);
            }
        }
    }
}
