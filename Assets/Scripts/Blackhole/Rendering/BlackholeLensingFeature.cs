using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace OmniTwin.BlackholeSimulation.Rendering
{
    /// <summary>🌀 URP RenderGraph fullscreen lensing around the projected Schwarzschild shadow.</summary>
    public sealed class BlackholeLensingFeature : ScriptableRendererFeature
    {
        [SerializeField] private Shader lensingShader;
        [SerializeField] private Transform blackhole;
        [SerializeField, Min(0.0001f)] private float schwarzschildRadius = 0.25f;
        [SerializeField, Range(0f, 2f)] private float lensingStrength = 0.7f;
        [SerializeField, Range(0f, 3f)] private float photonRingIntensity = 1f;
        [SerializeField] private RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingTransparents;

        private Material _material;
        private LensingPass _pass;

        public override void Create()
        {
            CoreUtils.Destroy(_material);
            _material = lensingShader != null ? CoreUtils.CreateEngineMaterial(lensingShader) : null;
            _pass = new LensingPass { renderPassEvent = injectionPoint };
            _pass.requiresIntermediateTexture = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_material == null || blackhole == null)
                return;

            Camera camera = renderingData.cameraData.camera;
            if (camera == null || camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection)
                return;

            Vector3 viewport = camera.WorldToViewportPoint(blackhole.position);
            if (viewport.z <= camera.nearClipPlane)
                return;

            float shadowWorldRadius = 1.5f * Mathf.Sqrt(3f) * schwarzschildRadius;
            float shadowViewportRadius = shadowWorldRadius / viewport.z * camera.projectionMatrix.m11 * 0.5f;
            _pass.Setup(
                _material,
                new Vector4(viewport.x, viewport.y, shadowViewportRadius, (float)camera.pixelWidth / Mathf.Max(1, camera.pixelHeight)),
                new Vector4(lensingStrength, photonRingIntensity, 0f, 0f));
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }

        private sealed class LensingPass : ScriptableRenderPass
        {
            private static readonly int LensingCenterId = Shader.PropertyToID("_BlackholeLensingCenter");
            private static readonly int LensingSettingsId = Shader.PropertyToID("_BlackholeLensingSettings");
            private Material _material;
            private Vector4 _center;
            private Vector4 _settings;

            private sealed class PassData
            {
                internal TextureHandle source;
                internal TextureHandle destination;
                internal Material material;
                internal Vector4 center;
                internal Vector4 settings;
            }

            internal void Setup(Material material, Vector4 center, Vector4 settings)
            {
                _material = material;
                _center = center;
                _settings = settings;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (!resources.cameraColor.IsValid() || !resources.activeColorTexture.IsValid())
                    return;

                TextureDesc copyDescriptor = renderGraph.GetTextureDesc(resources.cameraColor);
                copyDescriptor.name = "Blackhole Lensing Source";
                copyDescriptor.clearBuffer = false;
                TextureHandle sourceCopy = renderGraph.CreateTexture(copyDescriptor);
                renderGraph.AddBlitPass(resources.cameraColor, sourceCopy, Vector2.one, Vector2.zero, passName: "Copy Color For Blackhole Lensing");

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Blackhole Gravitational Lensing", out var passData))
                {
                    passData.source = sourceCopy;
                    passData.destination = resources.activeColorTexture;
                    passData.material = _material;
                    passData.center = _center;
                    passData.settings = _settings;
                    builder.UseTexture(passData.source, AccessFlags.Read);
                    builder.SetRenderAttachment(passData.destination, 0, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    {
                        context.cmd.SetGlobalVector(LensingCenterId, data.center);
                        context.cmd.SetGlobalVector(LensingSettingsId, data.settings);
                        Blitter.BlitTexture(context.cmd, data.source, Vector2.one, data.material, 0);
                    });
                }
            }
        }
    }
}
