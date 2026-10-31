# Unity Black Hole Compute Simulation

A Unity 6 and URP engineering prototype for a black-hole accretion disk. It explores deterministic particle populations, Paczyński–Wiita gravity, a Burst CPU backend, GPU compute integration and culling, procedural relativistic shading, and a RenderGraph lensing effect. The source is organized around the four systems in the portfolio Engineering X-Ray.

**Status:** the four systems, checks, and benchmark harness are present in source. In the live editor session used for this audit, the benchmark harness and test/menu scripts were absent from active compilation inputs. The repository also needs a configured demo scene and target-hardware runtime checks. See the [technical audit](docs/PROJECT_AUDIT.md) for findings and next steps. No unmeasured performance results are presented as facts.

## Engineering snapshot

| Property | Value |
|---|---:|
| Particle record | 32 bytes, 16-byte field groups |
| Rendering | GPU visible-index list + `RenderPrimitivesIndirect` |
| GPU culling | 6 camera frustum planes, integrated in the compute dispatch |
| CPU backend | Burst `IJobParallelFor`, `FloatMode.Fast`, `FloatPrecision.Low` |
| Gravity | Paczyński–Wiita pseudo-Newtonian potential, velocity Verlet |
| Population presets | 10k, 100k, 250k, 500k |
| Benchmark sweep | 10k, 100k, 250k × CPU Burst and GPU compute |

No representative hardware benchmark results are committed as project facts. Run the harness on the target machine to generate a measured table for the README or portfolio.

## 01 Particle population

`ParticlePopulationGenerator` creates a persistent `NativeArray<Particle>` from a repeatable Wang-hash sequence. Radius sampling is uniform by annular area between the ISCO and the configured outer radius. The disk supports a vertical half-thickness and eccentricity. Eccentric particles use pericenter/apocenter bounds inside the annulus and initial tangential/radial velocity derived from equal specific energy at those two turning points in the Paczyński–Wiita potential. Zero eccentricity starts each particle at the circular speed:

```text
r_ISCO = 3 r_s
v_circ = sqrt(GM r) / (r - r_s)
```

The `Particle` layout is explicitly packed and sized for a 32-byte stride:

| Byte offset | Field | Size |
|---:|---|---:|
| 0 | `float3 position` | 12 B |
| 12 | `float temperature` | 4 B |
| 16 | `float3 velocity` | 12 B |
| 28 | `uint flagsAndSeed` | 4 B |

This produces two 16-byte groups, matching the HLSL `Particle` declaration without implicit padding. Particle and visible-index storage is allocated once per population configuration; normal simulation frames do not resize or allocate managed particle arrays.

## 02 Gravitational attraction

`RelativisticGravityMath` and `BlackholeGravity.compute` use the Paczyński–Wiita potential

```text
Φ(r) = -GM / (r - r_s)
a(r) = -GM / (r - r_s)² · r̂
r_s = 2GM / c²
```

The acceleration denominator is bounded by a configurable softening distance near the horizon. Both backends use the same velocity-Verlet sequence: evaluate acceleration, advance position, evaluate acceleration at the new position, then update velocity. The maximum integration step clamps long editor stalls to keep float32 state finite.

The event horizon is `r ≤ r_s`. The ISCO at `3r_s` is the marginally stable circular orbit; crossing inside it makes a circular orbit unstable, but is not itself an event-horizon capture. Particles are recycled only after crossing the horizon (or when invalid state is detected). Their outer feeding-boundary position, tangent, and seed are derived from particle ID, frame count, and prior seed with the same Wang hash on CPU and GPU.

## 03 Accretion disk and GPU-driven culling

The GPU path uses `GraphicsBuffer.Target.Structured` for particles and visible indices and `GraphicsBuffer.Target.IndirectArguments` for the draw command. Each frame:

1. Reset the indirect draw argument count.
2. Integrate and recycle each particle in 256-thread groups.
3. Test world-space positions against six camera frustum planes.
4. Atomically reserve an index with `InterlockedAdd` in the indirect argument buffer and append the particle index.
5. Render six billboard vertices per visible particle through `Graphics.RenderPrimitivesIndirect`.

The render path performs no CPU visibility readback. The CPU Burst backend uses the same state and draw layout, computes frustum visibility into a preallocated native array, and uploads visible indices for rendering.

## 04 Visual effects

`AccretionDiskProcedural.shader` builds camera-facing quads from `SV_VertexID`, reads each visible particle from the structured buffer, evaluates relativistic Doppler beaming, and shifts its blackbody color through a compact temperature LUT by the Doppler factor. The velocity scale derives `c = sqrt(2GM/r_s)` from the configured simulation units, so the Doppler ratio remains meaningful when using normalized simulation values.

`BlackholeLensingFeature` is a URP `ScriptableRendererFeature` that records a fullscreen lensing pass in RenderGraph. It projects the black-hole transform into viewport space, estimates the Schwarzschild shadow radius, bends the source image, masks the shadow, and adds a photon-ring contribution.

## Setup in Unity

1. Add a GameObject at the black-hole center. Add `BlackholeSimulationController` and `AccretionDiskController` to it.
2. Assign `BlackholeGravity.compute` to **Gravity Compute**.
3. Create a material using **OmniTwin/Blackhole/Accretion Disk Procedural** and assign it to **Particle Material**.
4. Assign the active camera to **Render Camera** or tag the camera `MainCamera`.
5. Set population and simulation values in the controller. The default normalized scale is `GM = 1`, `r_s = 0.25`, and `r_max = 12`.
6. To enable lensing, add **Blackhole Lensing Feature** to the active URP Renderer Data, assign `BlackholeLensing.shader`, set the black-hole transform and the same `r_s`, then enable the feature.
7. Select `GPUCompute` on compute-capable hardware. The simulation can switch to `CPUBurstJobs`, but rendering still uses indirect draws; this is not yet a complete fallback for platforms without compute-shader or indirect-draw support.

The project version is Unity 6000.6.3f1, and the manifest pins URP 17.6.0.

## Tests

The repository contains assertion checks for particle stride, orbital stability, horizon capture, deterministic seeding, and finite state after an extreme timestep. The live editor used for this audit excluded the test file and menu runner from its active compilation inputs, so these checks were unavailable. Repair test compilation/discovery before relying on them; see the [audit](docs/PROJECT_AUDIT.md).

## Benchmark reproduction

1. Repair the benchmark script's compilation inclusion as described in the audit. Then complete the Unity setup above and add `BlackholeBenchmarkRecorder` to a GameObject.
2. Assign the simulation controller. Keep **Auto Run On Start** enabled or call `RunBenchmark()` from a UI button.
3. Let the sweep finish. It warms each configuration, measures 120 frames by default, and covers 10k, 100k, and 250k particles on both backends.
4. Read `blackhole-benchmarks.md` from `Application.persistentDataPath` and paste the table into the portfolio README.

The report records frame time, CPU dispatch/schedule wall time, visible particle count, and bytes allocated on the simulation controller's main-thread update. Dispatch time is CPU submission/schedule time, not GPU execution time; use Unity Profiler or a GPU capture tool for GPU timings. Switching backends regenerates the same seeded initial state for a fair comparison. The harness performs one asynchronous argument-buffer readback per GPU benchmark case to capture its visible count. If that readback fails, the current implementation can report zero as if it were a measured count; do not rely on the GPU visible-count column until this error case is handled. Gameplay rendering has no per-frame readback. GC values are measurements, not hard-coded claims: review the output on the shipping Unity version and target hardware.

## Repository map

```text
Assets/Scripts/Blackhole/Core/          particle layout, generator, gravity math, Burst job
Assets/Scripts/Blackhole/Systems/       backend orchestration
Assets/Scripts/Blackhole/Rendering/     indirect rendering and URP RenderGraph lensing
Assets/Scripts/Blackhole/Benchmarking/  automated sweep and Markdown export
Assets/Shaders/Blackhole/               compute integration, procedural disk, lensing shader
Assets/Tests/EditMode/                  deterministic edit-mode verification checks
```
