using System.Text.Json.Serialization;

namespace ProxySwitch.Models;

public class ProxyRuntimeStatus
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown"; // online, offline, unknown

    [JsonPropertyName("lastCheckedAt")]
    public DateTime? LastCheckedAt { get; set; }

    [JsonPropertyName("lastChangedAt")]
    public DateTime? LastChangedAt { get; set; }

    [JsonPropertyName("latencyMs")]
    public int? LatencyMs { get; set; }
}
