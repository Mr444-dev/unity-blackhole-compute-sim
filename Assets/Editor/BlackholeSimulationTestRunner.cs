using UnityEditor;
using UnityEngine;

namespace OmniTwin.BlackholeSimulation.Editor
{
    /// <summary>🧪 Runs the repository's edit-mode verification suite from the Unity menu.</summary>
    public static class BlackholeSimulationTestRunner
    {
        [MenuItem("Tools/Blackhole Simulation/Run Edit Mode Verification")]
        public static void RunAll()
        {
            BlackholePortfolioSetup.RunEditModeChecks(passed =>
            {
                if (!passed)
                    Debug.LogError("Blackhole Simulation edit-mode verification failed. See the Test Runner results.");
            });
        }
    }
}
