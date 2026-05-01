using Newtonsoft.Json;

namespace SynoAI.AIs.DeepStack
{
    public class DeepStackPrediction
    {
        public decimal Confidence { get; set; }
        public string Label { get; set; }

        // CodeProject.AI / DeepStack Face Recognition returns "userid" instead of "label"
        [JsonProperty("userid")]
        private string UserId
        {
            set { if (!string.IsNullOrWhiteSpace(value)) Label = value; }
        }

        [JsonProperty("x_min")]
        public int MinX { get; set; }
        [JsonProperty("y_min")]
        public int MinY { get; set; }
        [JsonProperty("x_max")]
        public int MaxX { get; set; }
        [JsonProperty("y_max")]
        public int MaxY { get; set; }
    }
}
