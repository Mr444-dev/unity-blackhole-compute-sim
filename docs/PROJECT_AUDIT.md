# Project audit

- **Reviewed:** 2026-11-01
- **Base revision:** `c55ab0f`
- **Project:** Unity 6000.6.3f1, URP 17.6.0

This follow-up implements the P1/P2/P3 findings from the review of revision `328e3a5`. The active Unity Editor compiled the assemblies without errors, all five discovered EditMode tests passed, and the scene setup menu saved SampleScene with its simulation and material references assigned. The six-case CPU/GPU hardware benchmark is intentionally left for the Editor menu; no hardware measurements are claimed here.

## Finding status

| Priority | Finding | Implementation status |
|---|---|---|
| P1 | The indirect argument buffer was missing `GraphicsBuffer.Target.Raw` while bound as HLSL `RWByteAddressBuffer`. | Fixed: allocates `IndirectArguments | Raw`, checks 4-byte stride and 16-byte/four-uint layout, initializes all fields, and resets the compute arguments at the start of each GPU frame. |
| P2 | CPU frustum culling ran in a serial main-thread loop, benchmark timing hid its cost, and partitioned Burst writes violated Job Safety's default index restriction. | Fixed: Burst jobs cull, prefix, and scatter visible indices. `NativeDisableParallelForRestriction` is applied only to the scratch/output arrays whose disjoint ranges are established by the block prefix. Normal play polls the job and reuses the cached list while it is pending. Benchmark rows report integration-only and total CPU-step timing separately; the latter includes culling, uploads, and draw submission. |
| P2 | CPU fallback still depended on indirect rendering. | Fixed: capability selection requires compute plus indirect support for the GPU backend. When indirect rendering is unavailable, the CPU path submits a direct procedural `Graphics.RenderPrimitives` draw. |
| P2 | Draw bounds used a fixed radius. | Fixed: bounds use outer radius, disk half-thickness, particle size, and absolute lossy scale. CPU and GPU frustum tests also scale their particle margin. |
| P2 | Failed or unsupported async GPU readback could be exported as a valid zero. | Fixed: readback errors and unsupported requests are labelled unavailable/failed, and the count is emitted as an em dash. |
| P2 | Tests and benchmark code were not discoverable through Unity assemblies. | Fixed in source: benchmark code is part of the runtime asmdef; NUnit tests have `[Test]` attributes in an EditMode test asmdef; Editor menus invoke `TestRunnerApi` against that assembly. |
| P3 | The checked-in SampleScene lacked a complete simulation setup. | Fixed and verified: the Editor menu creates/wires the camera, lights, URP feature, materials, simulation, compute shader, event horizon, and HUD, then saves SampleScene with both disk materials and compute/camera references serialized. GPUCompute Play Mode was visually captured; the horizon/ring are low contrast and the HUD overlaps the disk, so visual polish remains. |
| P3 | The repository had no one-click CPU/GPU benchmark export or MIT license. | Implemented: the second Editor menu runs discovered EditMode tests, then a six-case CPU/GPU sweep and exports `Blackhole-Benchmark-Results.md` with processor/GPU names and explicit metric status. Root `LICENSE` contains MIT terms. The hardware sweep has not been run. |

## Validation status

- `.cs` and `.asmdef` assets under `Assets` have matching `.meta` files.
- Unity Editor `6000.6.3f1` reports compilation complete with `failed=false` and no current Console errors or warnings.
- `Blackhole.Tests.EditMode` is discovered by Unity Test Runner and passed all 5 tests (0 failed, 0 skipped), both through the Test Runner command and **Tools > Blackhole Simulation > Run Edit Mode Verification**.
- A 10,000-particle CPUBurstJobs Play Mode run completed culling with all particles visible and no current Job Safety/Console errors; the scene backend was restored to GPUCompute afterwards.
- `Tools > Blackhole > 1. Wire & Save Demo SampleScene` ran successfully. The saved scene contains references to the procedural disk material, CPU fallback material, compute shader, render camera, simulation, and HUD.
- GPUCompute + indirect draw rendered the 10,000-particle scene in Unity Game View without current Console errors or warnings. The full Editor capture is `docs/screenshots/blackhole-game-view-editor.jpg`.
- No target-hardware benchmark values, GPU capture, or player-build result are claimed.

## Remaining verification

1. Run **Tools > Blackhole > 2. Run EditMode Checks & Export Benchmark Markdown** in the Editor when ready to capture machine-specific CPU/GPU timings. It starts Play Mode for the six-case benchmark and writes `Blackhole-Benchmark-Results.md` after completion.

Unity documents [`GraphicsBuffer.Target.Raw`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/GraphicsBuffer.Target.Raw.html) for byte-address HLSL buffers and notes that [`Graphics.RenderPrimitivesIndirect`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Graphics.RenderPrimitivesIndirect.html) requires compute-shader support.
