using System.Text.Json.Serialization;

namespace ProxySwitch.Models;

public class ProxyConfig
{
    [JsonPropertyName("proxies")]
    public List<ProxyInfo> Proxies { get; set; } = [];

    [JsonPropertyName("apps")]
    public List<AppConfig> Apps { get; set; } = [];

    [JsonPropertyName("proxifier")]
    public ProxifierConfig Proxifier { get; set; } = new();

    [JsonPropertyName("lastProfile")]
    public string? LastProfile { get; set; }
}

public class ProxyInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "socks5";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

public class AppConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("exe")]
    public string Exe { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "browser-direct";

    [JsonPropertyName("proxyId")]
    public string? ProxyId { get; set; }

    [JsonPropertyName("userDataDir")]
    public string UserDataDir { get; set; } = "";
}

public class ProxifierConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("exe")]
    public string Exe { get; set; } = "";

    [JsonPropertyName("profiles")]
    public Dictionary<string, string> Profiles { get; set; } = [];
}
