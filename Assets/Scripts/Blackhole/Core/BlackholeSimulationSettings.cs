using System;
using UnityEngine;

namespace OmniTwin.BlackholeSimulation.Core
{
    /// <summary>🪐 Authorable simulation scale and disk distribution parameters.</summary>
    [Serializable]
    public sealed class BlackholeSimulationSettings
    {
        [HideInInspector, Min(1)] public int particleCount = (int)ParticleCountPreset.TenThousand;
        [Min(0)] public int seed = 1847;
        [Min(0.0001f)] public float gravitationalParameter = 1f;
        [Min(0.0001f)] public float schwarzschildRadius = 0.25f;
        [Min(0.0001f)] public float outerRadius = 12f;
        [Min(0f)] public float diskHalfThickness = 0.18f;
        [Range(0f, 0.8f)] public float eccentricity = 0.02f;
        [Min(0.0001f)] public float softening = 0.01f;
        [Min(0f)] public float timeScale = 1f;
        [Min(0.0001f)] public float maximumIntegrationStep = 0.05f;
    }
}
