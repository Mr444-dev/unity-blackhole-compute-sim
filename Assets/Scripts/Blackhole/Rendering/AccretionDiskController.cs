using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using OmniTwin.BlackholeSimulation.Core;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace OmniTwin.BlackholeSimulation.Rendering
{
    /// <summary>🪐 GPU-driven visibility list, portable CPU fallback, and billboard rendering for the accretion disk.</summary>
    [DisallowMultipleComponent]
    public sealed class AccretionDiskController : MonoBehaviour
    {
        private const int IndirectArgumentCount = 4;

        private static readonly int ParticleBufferId = Shader.PropertyToID("_Particles");
        private static readonly int VisibleIndicesId = Shader.PropertyToID("_VisibleIndices");
        private static readonly int IndirectArgsId = Shader.PropertyToID("_IndirectArgs");
        private static readonly int TdeCaptureCounterId = Shader.PropertyToID("_TdeCaptureCounter");
        private static readonly int ParticleCountId = Shader.PropertyToID("_ParticleCount");
        private static readonly int FrameCountId = Shader.PropertyToID("_FrameCount");
        private static readonly int GravitationalParameterId = Shader.PropertyToID("_GM");
        private static readonly int SchwarzschildRadiusId = Shader.PropertyToID("_Rs");
        private static readonly int SofteningId = Shader.PropertyToID("_Softening");
        private static readonly int DeltaTimeId = Shader.PropertyToID("_DeltaTime");
        private static readonly int MaximumStepId = Shader.PropertyToID("_MaximumStep");
        private static readonly int OuterRadiusId = Shader.PropertyToID("_OuterRadius");
        private static readonly int DiskHalfThicknessId = Shader.PropertyToID("_DiskHalfThickness");
        private static readonly int OriginId = Shader.PropertyToID("_BlackholeOrigin");
        private static readonly int DiskScaleId = Shader.PropertyToID("_DiskScale");
        private static readonly int FrustumPlanesId = Shader.PropertyToID("_CameraFrustumPlanes");
        private static readonly int ParticleSizeId = Shader.PropertyToID("_ParticleSize");
        private static readonly int ParticleCullRadiusId = Shader.PropertyToID("_ParticleCullRadius");
        private static readonly int CameraRightId = Shader.PropertyToID("_CameraRight");
        private static readonly int CameraUpId = Shader.PropertyToID("_CameraUp");
        private static readonly int SpeedOfLightId = Shader.PropertyToID("_SpeedOfLight");
        private static readonly int DrawSchwarzschildRadiusId = Shader.PropertyToID("_SchwarzschildRadius");
        private static readonly int DrawOuterRadiusId = Shader.PropertyToID("_OuterRadius");
        private static readonly int UseIndirectArgumentsId = Shader.PropertyToID("_UseIndirectArguments");
        private static readonly Vector2[] CpuQuadCorners =
        {
            new(-1f, -1f), new(1f, -1f), new(1f, 1f),
            new(-1f, -1f), new(1f, 1f), new(-1f, 1f)
        };

        [Header("Rendering")]
        [SerializeField] private Material particleMaterial;
        [SerializeField] private Material cpuMeshFallbackMaterial;
        [SerializeField, Min(0.001f)] private float particleSize = 0.045f;

        private MaterialPropertyBlock _propertyBlock;
        private readonly Plane[] _planes = new Plane[6];
        private readonly Vector4[] _frustumPlanes = new Vector4[6];
        private GraphicsBuffer _visibleIndexBuffer;
        private GraphicsBuffer _indirectArguments;
        private Mesh _cpuFallbackMesh;
        private NativeArray<CpuMeshVertex> _cpuMeshVertices;
        private NativeArray<uint> _cpuMeshIndices;
        private NativeArray<uint> _cpuVisibilityScratch;
        private NativeArray<uint> _cpuVisibleIndices;
        private NativeArray<int> _cpuBlockCounts;
        private NativeArray<int> _cpuBlockOffsets;
        private NativeArray<int> _cpuVisibleCount;
        private NativeArray<float4> _cpuFrustumPlanes;
        private JobHandle _cpuCullingHandle;
        private GraphicsBuffer.IndirectDrawArgs[] _argumentsData;
        private int _capacity;
        private bool _useIndirectRendering;
        private bool _usesCpuMeshFallback;
        private bool _cpuCullingScheduled;
        private uint _cpuVisibleCountValue;
        private float _speedOfLight = 1f;

        private float EffectiveParticleSize
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return particleSize * 1.35f;
#else
                return particleSize;
#endif
            }
        }

        /// <summary>🖥️ CPU wall time spent submitting the reset and compute dispatches.</summary>
        public double LastGpuDispatchMilliseconds { get; private set; }

        /// <summary>📈 Visible count produced by CPU culling, without a GPU readback.</summary>
        public uint CpuVisibleCount => _cpuVisibleCountValue;

        /// <summary>🧾 GPU indirect arguments, when the active platform supports indirect rendering.</summary>
        public GraphicsBuffer IndirectArguments => _indirectArguments;

        /// <summary>🧭 True when indirect draws are available; otherwise CPU rendering uses a direct procedural draw.</summary>
        public bool SupportsIndirectRendering => _useIndirectRendering;
        public bool RequiresStructuredParticleBuffer => !_usesCpuMeshFallback;
        public bool UsesCpuMeshFallback => _usesCpuMeshFallback;
        public bool HasPendingCpuCulling => _cpuCullingScheduled;
        public bool CpuCullingIsComplete => !_cpuCullingScheduled || _cpuCullingHandle.IsCompleted;

        public bool IsReadyForRendering =>
            _usesCpuMeshFallback
                ? cpuMeshFallbackMaterial != null && _cpuFallbackMesh != null && _cpuMeshVertices.IsCreated
                : particleMaterial != null && _visibleIndexBuffer != null && (!_useIndirectRendering || _indirectArguments != null);

        private void OnDestroy() => ReleaseBuffers();

        /// <summary>🧰 Allocates CPU culling scratch space and platform-supported GPU buffers.</summary>
        public void Initialize(int particleCount)
        {
            ReleaseBuffers();
            _capacity = Mathf.Max(1, particleCount);
            int blockCount = Mathf.CeilToInt(_capacity / (float)CpuFrustumCullingJob.BlockSize);

            _usesCpuMeshFallback = !SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45;
            if (_usesCpuMeshFallback)
            {
                InitializeCpuMeshFallback(_capacity);
            }
            else
            {
                _visibleIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _capacity, sizeof(uint));
            }
            _cpuVisibilityScratch = new NativeArray<uint>(_capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _cpuVisibleIndices = new NativeArray<uint>(_capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _cpuBlockCounts = new NativeArray<int>(blockCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _cpuBlockOffsets = new NativeArray<int>(blockCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _cpuVisibleCount = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _cpuFrustumPlanes = new NativeArray<float4>(6, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _argumentsData = new GraphicsBuffer.IndirectDrawArgs[1];

            if (!_usesCpuMeshFallback && SystemInfo.supportsComputeShaders && SystemInfo.supportsIndirectArgumentsBuffer)
            {
                int argumentBytes = GraphicsBuffer.IndirectDrawArgs.size;
                int expectedBytes = IndirectArgumentCount * sizeof(uint);
                if (argumentBytes != expectedBytes)
                {
                    Debug.LogError($"IndirectDrawArgs is {argumentBytes} bytes on this platform; this shader requires four uints ({expectedBytes} bytes). GPU indirect rendering is disabled.", this);
                }
                else
                {
                    try
                    {
                        _indirectArguments = new GraphicsBuffer(
                            GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw,
                            IndirectArgumentCount,
                            sizeof(uint));
                        _useIndirectRendering = true;
                        ResetArguments();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning($"Raw indirect arguments are unavailable; using the direct CPU render path. {exception.Message}", this);
                        _indirectArguments?.Dispose();
                        _indirectArguments = null;
                        _useIndirectRendering = false;
                    }
                }
            }
        }

        /// <summary>🚀 Integrates and culls on the GPU, then submits an indirect draw on compute-capable platforms.</summary>
        public void RenderGpu(
            Camera camera,
            ComputeShader compute,
            int resetKernel,
            int integrateKernel,
            GraphicsBuffer particleBuffer,
            GraphicsBuffer tdeCaptureCounter,
            BlackholeSimulationSettings settings,
            uint frameCount,
            float deltaTime)
        {
            if (_usesCpuMeshFallback || !ReadyToRender(camera, particleBuffer) || compute == null || !_useIndirectRendering || tdeCaptureCounter == null)
            {
                LastGpuDispatchMilliseconds = 0.0;
                return;
            }

            UpdateFrustum(camera);
            Vector3 scale = transform.lossyScale;
            float worldCullRadius = EffectiveParticleSize * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            _speedOfLight = Mathf.Sqrt(2f * settings.gravitationalParameter / Mathf.Max(settings.schwarzschildRadius, 1e-6f));
            compute.SetInt(ParticleCountId, _capacity);
            compute.SetInt(FrameCountId, unchecked((int)frameCount));
            compute.SetFloat(GravitationalParameterId, settings.gravitationalParameter);
            compute.SetFloat(SchwarzschildRadiusId, settings.schwarzschildRadius);
            compute.SetFloat(SofteningId, settings.softening);
            compute.SetFloat(DeltaTimeId, deltaTime);
            compute.SetFloat(MaximumStepId, settings.maximumIntegrationStep);
            compute.SetFloat(OuterRadiusId, settings.outerRadius);
            compute.SetFloat(DiskHalfThicknessId, settings.diskHalfThickness);
            compute.SetVector(OriginId, transform.position);
            compute.SetVector(DiskScaleId, scale);
            compute.SetFloat(ParticleCullRadiusId, worldCullRadius);
            compute.SetVectorArray(FrustumPlanesId, _frustumPlanes);

            compute.SetBuffer(resetKernel, IndirectArgsId, _indirectArguments);
            long dispatchStart = Stopwatch.GetTimestamp();
            compute.Dispatch(resetKernel, 1, 1, 1);

            compute.SetBuffer(integrateKernel, ParticleBufferId, particleBuffer);
            compute.SetBuffer(integrateKernel, VisibleIndicesId, _visibleIndexBuffer);
            compute.SetBuffer(integrateKernel, IndirectArgsId, _indirectArguments);
            compute.SetBuffer(integrateKernel, TdeCaptureCounterId, tdeCaptureCounter);
            compute.Dispatch(integrateKernel, Mathf.CeilToInt(_capacity / 256f), 1, 1);
            LastGpuDispatchMilliseconds = (Stopwatch.GetTimestamp() - dispatchStart) * 1000.0 / Stopwatch.Frequency;
            _cpuVisibleCountValue = 0;
            Draw(camera, particleBuffer, settings);
        }

        /// <summary>🧵 Schedules Burst frustum culling and optionally waits for benchmark-grade timing.</summary>
        public void RenderCpu(
            Camera camera,
            NativeArray<Particle> particles,
            GraphicsBuffer particleBuffer,
            BlackholeSimulationSettings settings,
            bool completeCullingImmediately)
        {
            if (!ReadyForCpuRendering(camera, particleBuffer) || !particles.IsCreated || particles.Length < _capacity)
            {
                _cpuVisibleCountValue = 0;
                return;
            }

            if (!_cpuCullingScheduled)
                ScheduleCpuCulling(camera, particles);

            if (completeCullingImmediately && _cpuCullingScheduled)
                CompleteCpuCulling();

            DrawCpuCached(camera, particles, particleBuffer, settings);
        }

        /// <summary>✅ Completes a culling job only after its handle reports ready, then uploads the compact index list.</summary>
        public bool TryCompleteCpuCulling()
        {
            if (!_cpuCullingScheduled || !_cpuCullingHandle.IsCompleted)
                return false;

            CompleteCpuCulling();
            return true;
        }

        /// <summary>⏳ Waits for pending culling only during shutdown or storage reconfiguration.</summary>
        public void CompletePendingCpuCulling()
        {
            if (_cpuCullingScheduled)
                CompleteCpuCulling();
        }

        /// <summary>🖼️ Redraws the most recently uploaded CPU visibility list without waiting on a pending job.</summary>
        public void DrawCpuCached(Camera camera, NativeArray<Particle> particles, GraphicsBuffer particleBuffer, BlackholeSimulationSettings settings)
        {
            if (!ReadyForCpuRendering(camera, particleBuffer) || !particles.IsCreated || particles.Length < _capacity)
                return;

            _speedOfLight = Mathf.Sqrt(2f * settings.gravitationalParameter / Mathf.Max(settings.schwarzschildRadius, 1e-6f));
            if (_usesCpuMeshFallback)
                DrawCpuMeshFallback(camera, particles, settings);
            else if (_useIndirectRendering)
                Draw(camera, particleBuffer, settings);
            else if (_cpuVisibleCountValue > 0)
                DrawDirect(camera, particleBuffer, settings, (int)_cpuVisibleCountValue);
        }

        private void ScheduleCpuCulling(Camera camera, NativeArray<Particle> particles)
        {
            UpdateFrustum(camera);
            Vector3 scaleVector = transform.lossyScale;
            float worldCullRadius = EffectiveParticleSize * Mathf.Max(Mathf.Abs(scaleVector.x), Mathf.Abs(scaleVector.y), Mathf.Abs(scaleVector.z));
            var scale = new float3(scaleVector.x, scaleVector.y, scaleVector.z);
            var originVector = transform.position;
            var origin = new float3(originVector.x, originVector.y, originVector.z);
            int blockCount = _cpuBlockCounts.Length;

            var cullJob = new CpuFrustumCullingJob
            {
                Particles = particles,
                FrustumPlanes = _cpuFrustumPlanes,
                VisibilityScratch = _cpuVisibilityScratch,
                BlockCounts = _cpuBlockCounts,
                Origin = origin,
                Scale = scale,
                ParticleCullRadius = worldCullRadius
            }.Schedule(blockCount, 1);

            var prefixJob = new CpuFrustumPrefixSumJob
            {
                BlockCounts = _cpuBlockCounts,
                BlockOffsets = _cpuBlockOffsets,
                TotalVisibleCount = _cpuVisibleCount
            }.Schedule(cullJob);

            _cpuCullingHandle = new CpuFrustumScatterJob
            {
                VisibilityScratch = _cpuVisibilityScratch,
                BlockCounts = _cpuBlockCounts,
                BlockOffsets = _cpuBlockOffsets,
                VisibleIndices = _cpuVisibleIndices
            }.Schedule(blockCount, 1, prefixJob);
            _cpuCullingScheduled = true;
        }

        private void CompleteCpuCulling()
        {
            _cpuCullingHandle.Complete();
            _cpuCullingScheduled = false;
            _cpuVisibleCountValue = (uint)_cpuVisibleCount[0];
            if (_cpuVisibleCountValue > 0 && _visibleIndexBuffer != null)
                _visibleIndexBuffer.SetData(_cpuVisibleIndices, 0, 0, (int)_cpuVisibleCountValue);
            if (_useIndirectRendering)
                SetArguments(_cpuVisibleCountValue);
        }

        /// <summary>🔄 Initializes all four indirect-draw fields, including a zero instance count.</summary>
        public void ResetArguments()
        {
            if (_indirectArguments == null)
                return;

            SetArguments(0);
        }

        private void SetArguments(uint instanceCount)
        {
            _argumentsData ??= new GraphicsBuffer.IndirectDrawArgs[1];
            _argumentsData[0].vertexCountPerInstance = 6;
            _argumentsData[0].instanceCount = instanceCount;
            _argumentsData[0].startVertex = 0;
            _argumentsData[0].startInstance = 0;
            _indirectArguments.SetData(_argumentsData);
        }

        private bool ReadyToRender(Camera camera, GraphicsBuffer particleBuffer)
        {
            return camera != null && particleBuffer != null && _visibleIndexBuffer != null && IsReadyForRendering;
        }

        private bool ReadyForCpuRendering(Camera camera, GraphicsBuffer particleBuffer)
        {
            return camera != null && IsReadyForRendering && (_usesCpuMeshFallback || (particleBuffer != null && _visibleIndexBuffer != null));
        }

        private void UpdateFrustum(Camera camera)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            for (int i = 0; i < 6; i++)
            {
                Vector4 plane = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);
                _frustumPlanes[i] = plane;
                _cpuFrustumPlanes[i] = new float4(plane.x, plane.y, plane.z, plane.w);
            }
        }

        private void Draw(Camera camera, GraphicsBuffer particleBuffer, BlackholeSimulationSettings settings)
        {
            SetDrawProperties(camera, particleBuffer, settings);
            Graphics.RenderPrimitivesIndirect(CreateRenderParams(camera, settings), MeshTopology.Triangles, _indirectArguments);
        }

        private void DrawDirect(Camera camera, GraphicsBuffer particleBuffer, BlackholeSimulationSettings settings, int instanceCount)
        {
            SetDrawProperties(camera, particleBuffer, settings);
            Graphics.RenderPrimitives(CreateRenderParams(camera, settings), MeshTopology.Triangles, 6, instanceCount);
        }

        private void SetDrawProperties(Camera camera, GraphicsBuffer particleBuffer, BlackholeSimulationSettings settings)
        {
            EnsurePropertyBlock();
            _propertyBlock.Clear();
            _propertyBlock.SetBuffer(ParticleBufferId, particleBuffer);
            _propertyBlock.SetBuffer(VisibleIndicesId, _visibleIndexBuffer);
            _propertyBlock.SetFloat(ParticleSizeId, EffectiveParticleSize);
            _propertyBlock.SetVector(OriginId, transform.position);
            _propertyBlock.SetVector(DiskScaleId, transform.lossyScale);
            _propertyBlock.SetVector(CameraRightId, camera.transform.right);
            _propertyBlock.SetVector(CameraUpId, camera.transform.up);
            _propertyBlock.SetFloat(SpeedOfLightId, _speedOfLight);
            _propertyBlock.SetFloat(DrawSchwarzschildRadiusId, settings.schwarzschildRadius);
            _propertyBlock.SetFloat(DrawOuterRadiusId, settings.outerRadius);
            _propertyBlock.SetInt(UseIndirectArgumentsId, _useIndirectRendering ? 1 : 0);
        }

        private RenderParams CreateRenderParams(Camera camera, BlackholeSimulationSettings settings)
        {
            return CreateRenderParams(camera, settings, particleMaterial);
        }

        private RenderParams CreateRenderParams(Camera camera, BlackholeSimulationSettings settings, Material material)
        {
            Vector3 scale = transform.lossyScale;
            float effectiveParticleSize = EffectiveParticleSize;
            float horizontalExtent = settings.outerRadius + effectiveParticleSize * 0.5f;
            float verticalExtent = settings.diskHalfThickness + effectiveParticleSize * 0.5f;
            var extents = new Vector3(
                horizontalExtent * Mathf.Abs(scale.x) + effectiveParticleSize,
                verticalExtent * Mathf.Abs(scale.y) + effectiveParticleSize,
                horizontalExtent * Mathf.Abs(scale.z) + effectiveParticleSize);

            return new RenderParams(material)
            {
                camera = camera,
                matProps = _propertyBlock,
                worldBounds = new Bounds(transform.position, extents * 2f),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                layer = gameObject.layer
            };
        }

        private void InitializeCpuMeshFallback(int particleCapacity)
        {
            int vertexCapacity = checked(particleCapacity * 6);
            _cpuFallbackMesh = new Mesh { name = "Blackhole CPU Fallback Disk" };
            _cpuFallbackMesh.MarkDynamic();
            _cpuFallbackMesh.SetVertexBufferParams(vertexCapacity,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 1));
            _cpuFallbackMesh.SetIndexBufferParams(vertexCapacity, IndexFormat.UInt32);

            _cpuMeshVertices = new NativeArray<CpuMeshVertex>(vertexCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _cpuMeshIndices = new NativeArray<uint>(vertexCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int index = 0; index < vertexCapacity; index++)
                _cpuMeshIndices[index] = (uint)index;

            _cpuFallbackMesh.SetIndexBufferData(_cpuMeshIndices, 0, 0, vertexCapacity, MeshUpdateFlags.DontRecalculateBounds);
            _cpuFallbackMesh.subMeshCount = 1;
            _cpuFallbackMesh.SetSubMesh(0, new SubMeshDescriptor(0, 0, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
        }

        private void DrawCpuMeshFallback(Camera camera, NativeArray<Particle> particles, BlackholeSimulationSettings settings)
        {
            int visibleCount = (int)_cpuVisibleCountValue;
            if (visibleCount <= 0)
                return;

            Vector3 origin = transform.position;
            Vector3 scale = transform.lossyScale;
            Vector3 right = camera.transform.right;
            Vector3 cameraFromHole = (camera.transform.position - origin).normalized;
            float effectiveParticleSize = EffectiveParticleSize;
            float halfWidth = effectiveParticleSize * 0.22f;

            for (int particleOffset = 0; particleOffset < visibleCount; particleOffset++)
            {
                Particle particle = particles[(int)_cpuVisibleIndices[particleOffset]];
                Vector3 center = origin + Vector3.Scale(new Vector3(particle.position.x, particle.position.y, particle.position.z), scale);
                Vector3 velocity = Vector3.Scale(new Vector3(particle.velocity.x, particle.velocity.y, particle.velocity.z), scale);
                Vector3 relative = center - origin;
                float emissionRadius = Mathf.Max(relative.magnitude, settings.schwarzschildRadius + 1e-4f);
                float depth = Vector3.Dot(relative, cameraFromHole);
                if (depth < 0f)
                {
                    Vector3 projected = relative - cameraFromHole * depth;
                    float projectedRadius = projected.magnitude;
                    float shadowRadius = 1.5f * Mathf.Sqrt(3f) * settings.schwarzschildRadius;
                    float ringRadius = shadowRadius * 1.08f;
                    float bendWeight = 1f - Mathf.SmoothStep(ringRadius * 1.15f, ringRadius * 4f, projectedRadius);
                    Vector3 bendDirection = projectedRadius > 1e-4f ? projected / projectedRadius : Vector3.Cross(cameraFromHole, velocity).normalized;
                    float bentRadius = Mathf.Lerp(projectedRadius, Mathf.Max(projectedRadius, ringRadius * 1.04f), bendWeight * 0.9f);
                    center = origin + cameraFromHole * depth + bendDirection * bentRadius;
                }
                Vector3 viewDirection = (camera.transform.position - center).normalized;
                Vector3 tangent = Vector3.ProjectOnPlane(velocity, viewDirection).normalized;
                if (tangent.sqrMagnitude < 1e-6f)
                    tangent = right;
                Vector3 minor = Vector3.Cross(viewDirection, tangent).normalized;
                float speed = velocity.magnitude;
                float streakLength = effectiveParticleSize * Mathf.Clamp(1f + speed * 2.5f, 1f, 4.5f);
                int vertexOffset = particleOffset * 6;
                for (int cornerIndex = 0; cornerIndex < 6; cornerIndex++)
                {
                    Vector2 corner = CpuQuadCorners[cornerIndex];
                    _cpuMeshVertices[vertexOffset + cornerIndex] = new CpuMeshVertex
                    {
                        position = center + tangent * corner.x * (streakLength * 0.5f) + minor * corner.y * halfWidth,
                        uv = corner * 0.5f + Vector2.one * 0.5f,
                        velocity = velocity,
                        temperature = particle.temperature,
                        radius = emissionRadius
                    };
                }
            }

            int vertexCount = visibleCount * 6;
            _cpuFallbackMesh.SetVertexBufferData(_cpuMeshVertices, 0, 0, vertexCount, 0, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            EnsurePropertyBlock();
            _propertyBlock.Clear();
            _propertyBlock.SetFloat(SpeedOfLightId, _speedOfLight);
            _propertyBlock.SetFloat(DrawSchwarzschildRadiusId, settings.schwarzschildRadius);
            _propertyBlock.SetFloat(DrawOuterRadiusId, settings.outerRadius);
            _propertyBlock.SetVector(OriginId, origin);
            RenderParams renderParams = CreateRenderParams(camera, settings, cpuMeshFallbackMaterial);
            Bounds bounds = renderParams.worldBounds;
            _cpuFallbackMesh.bounds = bounds;
            _cpuFallbackMesh.SetSubMesh(0, new SubMeshDescriptor(0, vertexCount, MeshTopology.Triangles) { bounds = bounds }, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            Graphics.RenderMesh(renderParams, _cpuFallbackMesh, 0, Matrix4x4.identity);
        }

        private void ReleaseBuffers()
        {
            if (_cpuCullingScheduled)
            {
                _cpuCullingHandle.Complete();
                _cpuCullingScheduled = false;
            }
            _visibleIndexBuffer?.Dispose();
            _visibleIndexBuffer = null;
            _indirectArguments?.Dispose();
            _indirectArguments = null;
            DisposeIfCreated(ref _cpuVisibilityScratch);
            DisposeIfCreated(ref _cpuVisibleIndices);
            DisposeIfCreated(ref _cpuMeshVertices);
            DisposeIfCreated(ref _cpuMeshIndices);
            DisposeIfCreated(ref _cpuBlockCounts);
            DisposeIfCreated(ref _cpuBlockOffsets);
            DisposeIfCreated(ref _cpuVisibleCount);
            DisposeIfCreated(ref _cpuFrustumPlanes);
            _argumentsData = null;
            _capacity = 0;
            _useIndirectRendering = false;
            _cpuCullingHandle = default;
            _cpuVisibleCountValue = 0;
            if (_cpuFallbackMesh != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_cpuFallbackMesh);
                else
                    UnityEngine.Object.DestroyImmediate(_cpuFallbackMesh);
                _cpuFallbackMesh = null;
            }
            _usesCpuMeshFallback = false;
        }

        private void EnsurePropertyBlock()
        {
            if (_propertyBlock == null)
                _propertyBlock = new MaterialPropertyBlock();
        }

        private static void DisposeIfCreated<T>(ref NativeArray<T> array) where T : struct
        {
            if (array.IsCreated)
                array.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CpuMeshVertex
        {
            public Vector3 position;
            public Vector2 uv;
            public Vector3 velocity;
            public float temperature;
            public float radius;
        }
    }
}
