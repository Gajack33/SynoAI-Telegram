using Newtonsoft.Json;
using System.Collections.Generic;

namespace SynoAI.Models
{
    public class SynologyRecording
    {
        public int Id { get; set; }
        public int CameraId { get; set; }
        public string CameraName { get; set; }
        public string FilePath { get; set; }
        public long SizeByte { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class SynologyRecordings
    {
        [JsonProperty("recordings")]
        public IEnumerable<SynologyRecording> Recordings { get; set; }
    }
}
