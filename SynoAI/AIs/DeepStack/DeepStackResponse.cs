using System.Collections.Generic;

namespace SynoAI.AIs.DeepStack
{
    /// <summary>
    /// An object representing a response from DeepStack.
    /// </summary>
    public class DeepStackResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
        public IEnumerable<DeepStackPrediction> Predictions { get; set; }
        public int? Count { get; set; }
        public int? InferenceMs { get; set; }
        public int? ProcessMs { get; set; }
        public string ModuleId { get; set; }
        public string ModuleName { get; set; }
        public string Command { get; set; }
        public string ExecutionProvider { get; set; }
        public bool? CanUseGPU { get; set; }
        public int? AnalysisRoundTripMs { get; set; }
    }
}
