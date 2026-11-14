namespace OmniTwin.BlackholeSimulation.Systems
{
    /// <summary>⏱️ Shared input gate used while reproducible captures and benchmarks are running.</summary>
    public static class BlackholeInteractionState
    {
        public static bool DeterministicMode { get; set; }
    }
}
