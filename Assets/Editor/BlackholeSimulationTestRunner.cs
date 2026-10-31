using UnityEditor;
using UnityEngine;
using OmniTwin.BlackholeSimulation.Tests;

namespace OmniTwin.BlackholeSimulation.Editor
{
    /// <summary>🧪 Runs the repository's edit-mode verification suite from the Unity menu.</summary>
    public static class BlackholeSimulationTestRunner
    {
        [MenuItem("Tools/Blackhole Simulation/Run Edit Mode Verification")]
        public static void RunAll()
        {
            BlackholeSimulationTests.RunAll();
            Debug.Log("Blackhole Simulation edit-mode verification passed.");
        }
    }
}
