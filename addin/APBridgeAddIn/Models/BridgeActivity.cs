using System;
using System.Text.Json.Serialization;

namespace APBridgeAddIn.Models
{
    public class BridgeActivity
    {
        [JsonPropertyName("op")]
        public string Op { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }

        [JsonIgnore]
        public string TimestampDisplay =>
            Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

        [JsonIgnore]
        public string DurationDisplay =>
            DurationMs >= 1000
                ? $"{DurationMs / 1000.0:F1}s"
                : $"{DurationMs}ms";

        [JsonIgnore]
        public string StatusIcon => Ok ? "\u2705" : "\u274C";
    }
}
