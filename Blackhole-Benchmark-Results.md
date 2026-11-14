# Blackhole Simulation Benchmark

Captured: 2026-11-01 13:08 UTC
Graphics: NVIDIA GeForce GTX 1660 SUPER (Direct3D12)
CPU: 13th Gen Intel(R) Core(TM) i3-13100F
Warmup frames: 30; measured frames: 120
Another Unity project was in Play Mode on the same machine during collection. Frame-time values include that concurrent editor workload and are not an isolated performance baseline.
GPU dispatch time is CPU submission wall time, not GPU execution time. CPU integration excludes culling and uploads; total CPU step includes Burst integration, Burst culling, particle/index uploads, and draw submission.

| Backend | Particles | Frame time (ms) | CPU integration (ms) | Total CPU step (ms) | GPU dispatch submission (ms) | Visible particles | Visible-count status | Managed GC alloc / frame (B) |
|---|---:|---:|---:|---:|---:|---:|---|---:|
| CPUBurstJobs | 10,000 | 65.855 | 0.966 | 1.819 | — | 9,991 | CPU culling | 0.0 |
| CPUBurstJobs | 100,000 | 35.429 | 0.534 | 1.726 | — | 99,896 | CPU culling | 0.0 |
| CPUBurstJobs | 250,000 | 40.096 | 0.908 | 5.079 | — | 249,717 | CPU culling | 0.0 |
| GPUCompute | 10,000 | 42.807 | — | — | 0.063 | 9,989 | Valid | 0.0 |
| GPUCompute | 100,000 | 44.746 | — | — | 0.039 | 99,895 | Valid | 0.0 |
| GPUCompute | 250,000 | 36.754 | — | — | 0.047 | 249,719 | Valid | 0.0 |

A GPU visible count is exported only when its asynchronous readback succeeds. Unsupported or failed readbacks are explicitly marked and shown as an em dash; they are never presented as a measured zero.
Managed allocation measures bytes allocated on the simulation controller's main thread during each update.
