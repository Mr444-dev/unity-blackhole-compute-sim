# Project audit

**Reviewed:** 2026-10-31

**Code revision reviewed:** `328e3a5`

**Project:** Unity 6000.6.3f1, URP 17.6.0

This audit combines source inspection with a Bugbot functional review. It is not a target-hardware runtime certification. The benchmark sweep was not run, and the sample scene is not wired to the simulation components and materials.

## Findings

| Priority | Location | Finding | Recommended follow-up |
|---|---|---|---|
| P1 | `Assets/Scripts/Blackhole/Rendering/AccretionDiskController.cs:65`; `Assets/Shaders/Blackhole/BlackholeGravity.compute:15` | The indirect-arguments buffer is created with `IndirectArguments` only, then bound to an HLSL `RWByteAddressBuffer`. Unity maps raw byte-address HLSL buffers to `GraphicsBuffer.Target.Raw`; the current target combination does not request that usage. | Allocate the argument buffer with both indirect-argument and raw targets, then validate the reset and atomic count writes on the supported graphics APIs. |
| P2 | `Assets/Scripts/Blackhole/Systems/BlackholeSimulationController.cs:159-161`; `Assets/Scripts/Blackhole/Rendering/AccretionDiskController.cs:64-65,203` | The CPU backend still renders with `Graphics.RenderPrimitivesIndirect`. The backend selector falls back when compute or indirect-argument support is missing, while indirect primitive rendering itself requires compute-shader support. This does not provide rendering on every device selected for fallback. | Add a supported CPU rendering path or narrow the fallback claim and capability checks. |
| P2 | `Assets/Scripts/Blackhole/Rendering/AccretionDiskController.cs:36,198` | Draw bounds use a fixed radius (default 30) and do not follow `settings.outerRadius`. Larger configured disks can be culled by Unity before their visible particles are drawn. | Derive the bounds from the outer radius, thickness, particle size, and black-hole transform, or validate the configured relationship. |
| P2 | `Assets/Scripts/Blackhole/Benchmarking/BlackholeBenchmarkRecorder.cs:92-101` | If asynchronous GPU readback fails, the GPU row keeps the default visible count of zero and is still marked available. | Mark the count unavailable or fail the benchmark case when readback reports an error or is unsupported. |
| P2 | `Assets/Tests/EditMode/BlackholeSimulationTests.cs`; `Assets/Editor/BlackholeSimulationTestRunner.cs`; `Assets/Scripts/Blackhole/Benchmarking/BlackholeBenchmarkRecorder.cs` | The checks are plain assertion methods without Unity Test Framework test attributes or a discoverable test assembly. In the live editor used for this audit, the test file, menu runner, and benchmark harness were absent from `CompilationPipeline.GetAssemblies()` source lists; the menu item was not registered and the test type was not loaded. | Repair asset/assembly inclusion and configure a supported test-discovery path, then confirm both checks and benchmark harness are present in the active editor assemblies. |
| P3 | `Assets/Scenes/SampleScene.unity`; `README.md` | The checked-in sample scene is not a ready-to-play simulation. The README requires manual controller, compute shader, material, camera, and renderer-feature setup. | Wire a reproducible demo scene and capture a real Unity screenshot after the runtime path is verified. |
| P3 | `Assets/Scripts/Blackhole/Rendering/AccretionDiskController.cs:117-146`; `Assets/Scripts/Blackhole/Systems/BlackholeSimulationController.cs:84-101` | CPU frustum culling is a serial main-thread loop after Burst integration. The reported CPU dispatch time measures job schedule plus completion, not that culling loop or the subsequent upload/draw submission. | Keep the frame-time column for whole-frame comparisons; label step time as integration-only or include culling and upload in the CPU measurement. |

## Validation evidence

- The source and HLSL paths are present, and a previous validation pass compiled the C# assemblies and shader stages directly.
- The final live editor sample reported `compilationFailed: false` and `compiling: false`. Its `Assembly-CSharp` source list contained the core simulation and rendering scripts, but omitted the benchmark harness; `Assembly-CSharp-Editor` did not include the test-menu script. A standard Unity Test Runner run was not completed.
- Unity logs contain earlier failed script-compilation attempts followed by successful Tundra builds. The successful editor status does not replace a clean project import and player build.
- No scene playback, GPU capture, target-hardware benchmark, or measured performance result is claimed here.

## Suggested fix order

1. Make the argument buffer usable as both indirect arguments and raw byte-address storage; validate the GPU culling count.
2. Make platform capability selection match the actual rendering path.
3. Fix draw bounds and make GPU benchmark readback errors explicit.
4. Restore test discovery and verify the assertions in Unity.
5. Add a configured demo scene, then run the CPU/GPU sweep on named hardware and commit only measured results.

Unity documents that [`GraphicsBuffer.Target.Raw`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/GraphicsBuffer.Target.Raw.html) maps to `ByteAddressBuffer` and `RWByteAddressBuffer`. The [`Graphics.RenderPrimitivesIndirect`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Graphics.RenderPrimitivesIndirect.html) documentation also notes that the call requires compute-shader support.
