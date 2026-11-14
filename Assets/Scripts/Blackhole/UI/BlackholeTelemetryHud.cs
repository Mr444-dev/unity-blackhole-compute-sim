using UnityEngine;
using OmniTwin.BlackholeSimulation.Core;
using OmniTwin.BlackholeSimulation.Systems;
using OmniTwin.BlackholeSimulation.Rendering;

namespace OmniTwin.BlackholeSimulation.UI
{
    /// <summary>📟 Displays simulation backend, visibility, and CPU/GPU timing telemetry during play.</summary>
    [DisallowMultipleComponent]
    public sealed class BlackholeTelemetryHud : MonoBehaviour
    {
        [SerializeField] private BlackholeSimulationController simulation;
        [SerializeField] private BlackholeTidalDisruptionController tidalDisruption;

        private GUIStyle _panelStyle;
        private GUIStyle _textStyle;
        private GUIStyle _buttonStyle;
        private Texture2D _panelTexture;

        /// <summary>🔗 Allows the Editor scene builder to assign the simulation explicitly.</summary>
        public void SetSimulation(BlackholeSimulationController target) => simulation = target;
        public void SetTidalDisruptionController(BlackholeTidalDisruptionController target) => tidalDisruption = target;

        private void OnGUI()
        {
            if (simulation == null || !simulation.isActiveAndEnabled)
                return;

            EnsureStyles();
            string backend = simulation.Backend == SimulationBackend.GPUCompute
                ? "GPU Compute + indirect draw"
                : simulation.Disk != null && simulation.Disk.UsesCpuMeshFallback
                    ? "CPU Burst + dynamic mesh fallback"
                    : simulation.Disk != null && simulation.Disk.SupportsIndirectRendering
                        ? "CPU Burst + indirect draw"
                        : "CPU Burst + direct procedural draw";

            string visible = simulation.Backend == SimulationBackend.CPUBurstJobs
                ? VisibleCount(simulation).ToString("N0")
                : "async GPU readback";
            GUI.Box(new Rect(14f, 14f, 310f, 164f), GUIContent.none, _panelStyle);
            GUI.Label(new Rect(25f, 19f, 288f, 17f), "BLACKHOLE / RELATIVITY", _textStyle);
            GUI.Label(new Rect(25f, 36f, 288f, 16f), "Backend: " + backend, _textStyle);
            GUI.Label(new Rect(25f, 51f, 288f, 16f), $"Particles: {simulation.ParticleCount:N0}   Visible: {visible}", _textStyle);
            GUI.Label(new Rect(25f, 66f, 288f, 16f), $"CPU: {simulation.LastIntegrationMilliseconds:F3}  Step: {simulation.LastTotalCpuStepMilliseconds:F3}  GPU: {simulation.LastGpuCommandMilliseconds:F3} ms", _textStyle);
            GUI.Label(new Rect(25f, 81f, 288f, 16f), $"Main-thread GC: {simulation.LastManagedAllocatedBytes:N0} B", _textStyle);
            string tdeStatus = tidalDisruption != null ? tidalDisruption.GetHudStatus() : "[LMB / Space] Launch Star (TDE): READY";
            GUI.Label(new Rect(25f, 100f, 290f, 17f), tdeStatus, _textStyle);
            GUI.Label(new Rect(25f, 117f, 288f, 16f), "[RMB Drag / Scroll] Orbit Camera", _textStyle);
            bool previousEnabled = GUI.enabled;
            GUI.enabled = tidalDisruption != null && tidalDisruption.CanLaunch;
            if (GUI.Button(new Rect(25f, 139f, 286f, 24f), "LAUNCH STAR  ·  TIDAL DISRUPTION", _buttonStyle))
                tidalDisruption.LaunchFromHud();
            GUI.enabled = previousEnabled;
        }

        private void EnsureStyles()
        {
            if (_panelStyle != null)
                return;

            _panelTexture = new Texture2D(3, 3, TextureFormat.RGBA32, false)
            {
                name = "Blackhole HUD Panel",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            Color fill = new Color(8f / 255f, 12f / 255f, 20f / 255f, 0.88f);
            Color border = new Color(0.21f, 0.34f, 0.48f, 0.95f);
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                _panelTexture.SetPixel(x, y, x == 1 && y == 1 ? fill : border);
            _panelTexture.Apply(false, true);
            _panelStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(1, 1, 1, 1),
                border = new RectOffset(1, 1, 1, 1),
                normal = { background = _panelTexture, textColor = Color.white },
                hover = { background = _panelTexture, textColor = Color.white },
                active = { background = _panelTexture, textColor = Color.white },
                focused = { background = _panelTexture, textColor = Color.white }
            };
            _textStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 12,
                wordWrap = false,
                richText = false,
                normal = { textColor = Color.white }
            };
            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.69f, 0.88f, 1f) },
                hover = { textColor = Color.white },
                active = { textColor = Color.white }
            };
        }

        private void OnDestroy()
        {
            if (_panelTexture != null)
                Destroy(_panelTexture);
        }

        private static uint VisibleCount(BlackholeSimulationController target)
        {
            return target.Backend == SimulationBackend.CPUBurstJobs
                ? target.CpuVisibleCount
                : target.Disk != null ? target.Disk.CpuVisibleCount : 0;
        }
    }
}
