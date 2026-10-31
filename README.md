# Unity Blackhole Compute Sim

An engineering-focused black-hole accretion-disk simulation for Unity 6 and URP. The implementation is organized around the same four systems shown in the portfolio Engineering X-Ray and can switch between a Burst job backend and a GPU compute backend.

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

`ParticlePopulationGenerator` creates a persistent `NativeArray<Particle>` from a repeatable Wang-hash sequence. Radius sampling is uniform by annular area between the ISCO and the configured outer radius. The disk supports a vertical half-thickness and eccentricity perturbation. Zero eccentricity starts each particle at the Paczyński–Wiita circular speed:

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

`AccretionDiskProcedural.shader` builds camera-facing quads from `SV_VertexID`, reads each visible particle from the structured buffer, evaluates relativistic Doppler beaming, and shifts its blackbody color by the Doppler factor. The velocity scale derives `c = sqrt(2GM/r_s)` from the configured simulation units, so the Doppler ratio remains meaningful when using normalized simulation values.

`BlackholeLensingFeature` is a URP `ScriptableRendererFeature` that records a fullscreen lensing pass in RenderGraph. It projects the black-hole transform into viewport space, estimates the Schwarzschild shadow radius, bends the source image, masks the shadow, and adds a photon-ring contribution.

## Setup in Unity

1. Add a GameObject at the black-hole center. Add `BlackholeSimulationController` and `AccretionDiskController` to it.
2. Assign `BlackholeGravity.compute` to **Gravity Compute**.
3. Create a material using **OmniTwin/Blackhole/Accretion Disk Procedural** and assign it to **Particle Material**.
4. Assign the active camera to **Render Camera** or tag the camera `MainCamera`.
5. Set population and simulation values in the controller. The default normalized scale is `GM = 1`, `r_s = 0.25`, and `r_max = 12`.
6. To enable lensing, add **Blackhole Lensing Feature** to the active URP Renderer Data, assign `BlackholeLensing.shader`, set the black-hole transform and the same `r_s`, then enable the feature.
7. Select `GPUCompute` on compute-capable hardware; the component falls back to `CPUBurstJobs` if compute or indirect-argument support is unavailable.

The project version is Unity 6000.6.3f1, and the manifest pins URP 17.6.0.

## Tests

The project avoids Assembly Definition files. Run **Tools → Blackhole Simulation → Run Edit Mode Verification** in the Unity Editor to check:

- exact particle stride and field offsets,
- stable circular orbit radius and energy at `r > 3r_s`,
- horizon capture for inward sub-ISCO motion,
- deterministic seeded generation and finite state under an extreme timestep.

## Benchmark reproduction

1. Complete the Unity setup above and add `BlackholeBenchmarkRecorder` to a GameObject.
2. Assign the simulation controller. Keep **Auto Run On Start** enabled or call `RunBenchmark()` from a UI button.
3. Let the sweep finish. It warms each configuration, measures 120 frames by default, and covers 10k, 100k, and 250k particles on both backends.
4. Read `blackhole-benchmarks.md` from `Application.persistentDataPath` and paste the table into the portfolio README.

The report records frame time, CPU dispatch/step wall time, visible particle count, and bytes allocated on the simulation controller's main-thread update. Dispatch time is submission/schedule time, not GPU execution time; use Unity Profiler or a GPU capture tool for GPU timings. The harness performs one asynchronous argument-buffer readback per GPU benchmark case to capture its visible count. Gameplay rendering has no per-frame readback. GC values are measurements, not hard-coded claims: review the output on the shipping Unity version and target hardware.

## Repository map

```text
Assets/Scripts/Blackhole/Core/          particle layout, generator, gravity math, Burst job
Assets/Scripts/Blackhole/Systems/       backend orchestration
Assets/Scripts/Blackhole/Rendering/     indirect rendering and URP RenderGraph lensing
Assets/Scripts/Blackhole/Benchmarking/  automated sweep and Markdown export
Assets/Shaders/Blackhole/               compute integration, procedural disk, lensing shader
Assets/Tests/EditMode/                  deterministic edit-mode verification checks
```
