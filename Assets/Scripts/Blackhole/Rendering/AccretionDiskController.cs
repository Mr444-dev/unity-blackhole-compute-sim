using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using OmniTwin.BlackholeSimulation.Core;

namespace OmniTwin.BlackholeSimulation.Rendering
{
    /// <summary>🪐 GPU-driven visibility list, indirect arguments, and billboard rendering for the accretion disk.</summary>
    [DisallowMultipleComponent]
    public sealed class AccretionDiskController : MonoBehaviour
    {
        private static readonly int ParticleBufferId = Shader.PropertyToID("_Particles");
        private static readonly int VisibleIndicesId = Shader.PropertyToID("_VisibleIndices");
        private static readonly int IndirectArgsId = Shader.PropertyToID("_IndirectArgs");
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
        private static readonly int FrustumPlanesId = Shader.PropertyToID("_CameraFrustumPlanes");
        private static readonly int ParticleSizeId = Shader.PropertyToID("_ParticleSize");
        private static readonly int CameraRightId = Shader.PropertyToID("_CameraRight");
        private static readonly int CameraUpId = Shader.PropertyToID("_CameraUp");
        private static readonly int SpeedOfLightId = Shader.PropertyToID("_SpeedOfLight");

        [Header("Rendering")]
        [SerializeField] private Material particleMaterial;
        [SerializeField, Min(0.001f)] private float particleSize = 0.045f;
        [SerializeField, Min(1f)] private float renderBoundsRadius = 30f;

        private readonly MaterialPropertyBlock _propertyBlock = new MaterialPropertyBlock();
        private readonly Plane[] _planes = new Plane[6];
        private readonly Vector4[] _frustumPlanes = new Vector4[6];
        private GraphicsBuffer _visibleIndexBuffer;
        private GraphicsBuffer _indirectArguments;
        private NativeArray<uint> _cpuVisibleIndices;
        private GraphicsBuffer.IndirectDrawArgs[] _argumentsData;
        private int _capacity;
        private uint _cpuVisibleCount;
        private float _speedOfLight = 1f;

        /// <summary>📈 Visible count known without readback on the CPU backend.</summary>
        public uint CpuVisibleCount => _cpuVisibleCount;

        /// <summary>🧾 GPU indirect argument buffer; benchmark snapshots may read it asynchronously.</summary>
        public GraphicsBuffer IndirectArguments => _indirectArguments;

        private void OnDestroy() => ReleaseBuffers();

        public void Initialize(int particleCount)
        {
            ReleaseBuffers();
            _capacity = Mathf.Max(1, particleCount);
            _visibleIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _capacity, sizeof(uint));
            _indirectArguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawArgs.size);
            _cpuVisibleIndices = new NativeArray<uint>(_capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _argumentsData = new GraphicsBuffer.IndirectDrawArgs[1];
            ResetArguments();
        }

        public void RenderGpu(
            Camera camera,
            ComputeShader compute,
            int resetKernel,
            int integrateKernel,
            GraphicsBuffer particleBuffer,
            BlackholeSimulationSettings settings,
            uint frameCount,
            float deltaTime)
        {
            if (!ReadyToRender(camera, particleBuffer) || compute == null)
                return;

            UpdateFrustum(camera);
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
            compute.SetVectorArray(FrustumPlanesId, _frustumPlanes);

            compute.SetBuffer(resetKernel, IndirectArgsId, _indirectArguments);
            compute.Dispatch(resetKernel, 1, 1, 1);

            compute.SetBuffer(integrateKernel, ParticleBufferId, particleBuffer);
            compute.SetBuffer(integrateKernel, VisibleIndicesId, _visibleIndexBuffer);
            compute.SetBuffer(integrateKernel, IndirectArgsId, _indirectArguments);
            compute.Dispatch(integrateKernel, Mathf.CeilToInt(_capacity / 256f), 1, 1);
            _cpuVisibleCount = 0;
            Draw(camera, particleBuffer);
        }

        public void RenderCpu(Camera camera, NativeArray<Particle> particles, GraphicsBuffer particleBuffer, BlackholeSimulationSettings settings)
        {
            if (!ReadyToRender(camera, particleBuffer) || !particles.IsCreated)
                return;

            UpdateFrustum(camera);
            _speedOfLight = Mathf.Sqrt(2f * settings.gravitationalParameter / Mathf.Max(settings.schwarzschildRadius, 1e-6f));
            float3 origin = transform.position;
            uint visible = 0;
            for (int i = 0; i < _capacity; i++)
            {
                float3 worldPosition = particles[i].position + origin;
                bool inside = true;
                for (int planeIndex = 0; planeIndex < 6; planeIndex++)
                {
                    Plane plane = _planes[planeIndex];
                    float distance = plane.normal.x * worldPosition.x + plane.normal.y * worldPosition.y + plane.normal.z * worldPosition.z + plane.distance;
                    if (distance < 0f)
                    {
                        inside = false;
                        break;
                    }
                }

                if (inside)
                    _cpuVisibleIndices[(int)visible++] = (uint)i;
            }

            if (visible > 0)
                _visibleIndexBuffer.SetData(_cpuVisibleIndices, 0, 0, (int)visible);

            _argumentsData[0].vertexCountPerInstance = 6;
            _argumentsData[0].instanceCount = visible;
            _argumentsData[0].startVertex = 0;
            _argumentsData[0].startInstance = 0;
            _indirectArguments.SetData(_argumentsData);
            _cpuVisibleCount = visible;
            Draw(camera, particleBuffer);
        }

        public void ResetArguments()
        {
            if (_indirectArguments == null)
                return;

            _argumentsData ??= new GraphicsBuffer.IndirectDrawArgs[1];
            _argumentsData[0].vertexCountPerInstance = 6;
            _argumentsData[0].instanceCount = 0;
            _argumentsData[0].startVertex = 0;
            _argumentsData[0].startInstance = 0;
            _indirectArguments.SetData(_argumentsData);
            _cpuVisibleCount = 0;
        }

        private bool ReadyToRender(Camera camera, GraphicsBuffer particleBuffer)
        {
            return camera != null && particleBuffer != null && _visibleIndexBuffer != null && _indirectArguments != null && particleMaterial != null;
        }

        private void UpdateFrustum(Camera camera)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            for (int i = 0; i < 6; i++)
                _frustumPlanes[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);
        }

        private void Draw(Camera camera, GraphicsBuffer particleBuffer)
        {
            _propertyBlock.Clear();
            _propertyBlock.SetBuffer(ParticleBufferId, particleBuffer);
            _propertyBlock.SetBuffer(VisibleIndicesId, _visibleIndexBuffer);
            _propertyBlock.SetFloat(ParticleSizeId, particleSize);
            _propertyBlock.SetVector(OriginId, transform.position);
            _propertyBlock.SetVector(CameraRightId, camera.transform.right);
            _propertyBlock.SetVector(CameraUpId, camera.transform.up);
            _propertyBlock.SetFloat(SpeedOfLightId, _speedOfLight);

            var renderParams = new RenderParams(particleMaterial)
            {
                camera = camera,
                matProps = _propertyBlock,
                worldBounds = new Bounds(transform.position, Vector3.one * (renderBoundsRadius * 2f)),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                layer = gameObject.layer
            };
            Graphics.RenderPrimitivesIndirect(renderParams, MeshTopology.Triangles, _indirectArguments);
        }

        private void ReleaseBuffers()
        {
            _visibleIndexBuffer?.Dispose();
            _visibleIndexBuffer = null;
            _indirectArguments?.Dispose();
            _indirectArguments = null;
            if (_cpuVisibleIndices.IsCreated)
                _cpuVisibleIndices.Dispose();
            _argumentsData = null;
            _capacity = 0;
            _cpuVisibleCount = 0;
        }
    }
}
