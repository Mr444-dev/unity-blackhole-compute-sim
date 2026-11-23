# Unity Black Hole Compute Simulation

A Unity 6 URP prototype for a GPU-driven black-hole accretion disk. The project combines deterministic particle populations, Paczynski-Wiita gravity, a Burst CPU backend, GPU compute integration and culling, velocity-stretched HDR billboards, and a stylized RenderGraph lensing pass.

**Current build:** Unity 6000.6.3f1 with URP 17.6.0. The default scene uses the GPU Compute backend and 100,000 particles. All five EditMode checks pass. The six-case benchmark below was captured in the Unity Editor on the listed Windows/DX12 hardware.

## Portfolio capture

![Clean 1920 by 1080 Game View of the black-hole simulation](docs/screenshots/blackhole-game-view.png)

The PNG is a 1920 x 1080 capture of the camera output with the telemetry panel composited at its Game View position. It excludes Unity Editor chrome.

## Engineering snapshot

| Property | Value |
|---|---:|
| Particle record | 32 bytes, 16-byte field groups |
| Rendering | GPU visible-index list and indirect draw; CPU procedural draw or dynamic mesh fallback |
| GPU culling | Six camera frustum planes in the compute dispatch |
| CPU backend | Burst `IJobParallelFor`, `FloatMode.Fast`, `FloatPrecision.Low` |
| Gravity | Paczynski-Wiita pseudo-Newtonian potential, velocity Verlet |
| Population presets | 10k, 25k browser fallback, 100k, 250k, 500k |
| Default backend | GPU Compute on desktop; CPU Burst + mesh on WebGL |
| Benchmark cases | 10k, 100k, 250k on CPU Burst and GPU Compute |

## Visual setup

The demo camera sits at `(0, 3.2, -18.5)` with a 42-degree field of view. The configured Schwarzschild radius is `0.65`, the ISCO is `3 r_s = 1.95`, the outer disk radius is `11`, and the disk half-thickness is `0.10`. Particle radii are biased toward the hot inner disk with a power-law sample.

The procedural disk shader aligns each soft billboard with projected particle velocity, stretches it with speed, and applies gravitational redshift, Lorentz time dilation, and relativistic Doppler beaming to the emitted blackbody color. The same radiative-transfer model runs in the CPU dynamic-mesh fallback. The renderer feature adds screen-space gravitational bending, a dark shadow mask, and photon-ring glow. A procedural starfield skybox provides contrast. The global URP Volume uses Bloom (`threshold 0.9`, `intensity 1.25`, `scatter 0.68`) and ACES tonemapping. Lensing is a stylized screen-space effect rather than a full general-relativistic ray tracer.

During Play Mode, click the disk or press **Space** to launch a 4,096-particle stellar body on a plunging orbit. The particles integrate through the same Paczynski-Wiita potential as the disk and develop a tidal stream; crossing `1.5 r_s` triggers a brief pair of polar jet beams. Launches have a 10-second cooldown. Hold **right mouse** and drag, use **WASD** or arrow keys to orbit, and scroll to zoom. These controls are suppressed during the benchmark and screenshot capture flows.

On WebGL, or when compute and indirect drawing are unavailable, the simulation starts with the CPU Burst backend and 25,000 particles. It renders through the dynamic-mesh fallback with larger streaks. In the Editor, choose **Tools > Blackhole > 4. Build WebGL for itch.io** to switch to the WebGL target, compile the browser-specific fallback, and build `Builds/WebGL-ItchIo/` with Gzip compression, decompression fallback, and a 1280 x 720 default canvas. The Unity Editor's Web Build Support module must be installed.

## Running and Inspecting in Unity Editor

1. Open the project with Unity `6000.6.3f1` and let package import and script compilation finish.
2. Open `Assets/Scenes/SampleScene.unity`. To rebuild the demo setup, choose **Tools > Blackhole > 1. Wire & Save Demo SampleScene**.
3. Press **Play** and inspect the **Game** tab. The camera renders the simulation; the Scene tab shows the editor scene.
4. Select **Blackhole Simulation** in the Hierarchy to inspect the `BlackholeSimulationController`. Its Backend and Population Preset fields can be changed in the Inspector. Stop and restart Play Mode after changing the population preset.
5. To refresh the portfolio image, choose **Tools > Blackhole > 3. Capture 1080p Portfolio Screenshot**. The Editor waits for 60 warmup frames and writes `docs/screenshots/blackhole-game-view.png`.
6. To verify and collect measurements, choose **Tools > Blackhole > 2. Run EditMode Checks & Export Benchmark Markdown**. The menu runs the EditMode checks, then the six-case Play Mode benchmark, writes `Blackhole-Benchmark-Results.md` to the repository root, and returns to Edit Mode.

The telemetry HUD sits 14 pixels from the top and left edges and measures 310 x 164 pixels. It reports backend, population, CPU timings, GPU command submission time, main-thread GC allocation, TDE cooldown, and orbit controls. GPU visible-particle count is asynchronous; it is shown only when readback succeeds. GPU dispatch submission is CPU time spent issuing commands, not GPU execution time.

## Measured benchmark

Captured on **NVIDIA GeForce GTX 1660 SUPER (Direct3D12)** with a **13th Gen Intel Core i3-13100F**. Each case used 30 warmup frames followed by 120 measured frames in the Unity Editor.

**Capture condition:** Another Unity project was in Play Mode on the same machine during collection. Frame-time values include that concurrent editor workload and are not an isolated performance baseline.

| Backend | Particles | Frame time (ms) | CPU integration (ms) | Total CPU step (ms) | GPU dispatch submission (ms) | Visible particles | Visible-count status | Managed GC alloc / frame (B) |
|---|---:|---:|---:|---:|---:|---:|---|---:|
| CPUBurstJobs | 10,000 | 65.855 | 0.966 | 1.819 | — | 9,991 | CPU culling | 0.0 |
| CPUBurstJobs | 100,000 | 35.429 | 0.534 | 1.726 | — | 99,896 | CPU culling | 0.0 |
| CPUBurstJobs | 250,000 | 40.096 | 0.908 | 5.079 | — | 249,717 | CPU culling | 0.0 |
| GPUCompute | 10,000 | 42.807 | — | — | 0.063 | 9,989 | Valid | 0.0 |
| GPUCompute | 100,000 | 44.746 | — | — | 0.039 | 99,895 | Valid | 0.0 |
| GPUCompute | 250,000 | 36.754 | — | — | 0.047 | 249,719 | Valid | 0.0 |

CPU integration excludes culling and uploads. Total CPU step includes Burst integration, Burst culling, particle/index uploads, and draw submission. GPU dispatch submission is CPU wall time, not GPU execution time. Managed allocation measures bytes allocated on the simulation controller's main thread during each update. GPU visible counts are reported only when asynchronous readback succeeds.

The full generated report is [Blackhole-Benchmark-Results.md](Blackhole-Benchmark-Results.md). For hardware GPU timings, use Unity Profiler or a GPU capture tool.

## Simulation details

`ParticlePopulationGenerator` creates a persistent `NativeArray<Particle>` from a repeatable Wang-hash sequence. It biases radial sampling toward the ISCO, applies the configured vertical thickness and eccentricity, and initializes circular or eccentric velocities from the Paczynski-Wiita potential.

The CPU and compute backends use velocity Verlet with a bounded acceleration near the event horizon. Disk particles are recycled after crossing the horizon or when state becomes invalid; crossing inside the ISCO alone does not count as capture. The TDE segment additionally records the first inward crossing of `1.5 r_s` to trigger its capture flare. The GPU culling path appends visible particle indices without CPU readback. The CPU backend compacts visible particles in Burst jobs using preallocated native buffers.

The packed particle layout is 32 bytes: `float3 position`, `float temperature`, `float3 velocity`, and `uint flagsAndSeed`. These fields form two 16-byte groups matching the HLSL structure.

## Tests

The EditMode suite contains five checks for particle and indirect-argument layouts, deterministic finite population generation, stable circular orbits, and event-horizon capture. Run them through **Window > General > Test Runner > EditMode**, or use the combined verification and benchmark menu item above.

## Repository map

```text
Assets/Scripts/Blackhole/Core/          particle layout, generator, and gravity math
Assets/Scripts/Blackhole/Systems/       simulation orchestration, TDE interaction, and benchmark recorder
Assets/Scripts/Blackhole/Rendering/     culling, procedural/fallback rendering, orbit camera, and RenderGraph lensing
Assets/Scripts/Blackhole/UI/            runtime telemetry HUD
Assets/Shaders/Blackhole/               compute integration and rendering shaders
Assets/Tests/EditMode/                  EditMode verification checks
Assets/Editor/                          scene setup, screenshot capture, and benchmark tools
```

Original project code by Michał Rusinek (Mr444-dev) is distributed under the [MIT License](LICENSE).
