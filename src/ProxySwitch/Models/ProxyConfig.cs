using System.Text.Json.Serialization;

namespace ProxySwitch.Models;

public class ProxyConfig
{
    [JsonPropertyName("proxies")]
    public List<ProxyInfo> Proxies { get; set; } = [];

    [JsonPropertyName("apps")]
    public List<AppConfig> Apps { get; set; } = [];

    /// <summary>
    /// External routing backends (Clash Verge, v2ray, etc.) bound to each proxy port.
    /// ProxySwitch does NOT control routing — it only opens the backend's app/config.
    /// </summary>
    [JsonPropertyName("routingBackends")]
    public List<RoutingBackend> RoutingBackends { get; set; } = [];

    /// <summary>
    /// Policy names / tags used when generating routing rule hints to copy.
    /// </summary>
    [JsonPropertyName("routingHints")]
    public RoutingHintSettings RoutingHints { get; set; } = new();

    /// <summary>
    /// Preset rule snippets for common apps (Obsidian Community Plugins, etc.).
    /// </summary>
    [JsonPropertyName("ruleHintPresets")]
    public List<RuleHintPreset> RuleHintPresets { get; set; } = [];
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

public class RoutingBackend
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Which proxy this backend listens on (matches ProxyInfo.Id).</summary>
    [JsonPropertyName("proxyId")]
    public string ProxyId { get; set; } = "";

    /// <summary>Path to the backend's exe — used by "Open Routing App".</summary>
    [JsonPropertyName("appPath")]
    public string AppPath { get; set; } = "";

    /// <summary>Config folder/file — used by "Open Routing Config".</summary>
    [JsonPropertyName("configPath")]
    public string ConfigPath { get; set; } = "";

    /// <summary>Rule snippet format: "clash" or "v2ray".</summary>
    [JsonPropertyName("ruleFormat")]
    public string RuleFormat { get; set; } = "clash";
}

public class RoutingHintSettings
{
    /// <summary>Default Clash policy name used in PROCESS-NAME / DOMAIN rules.</summary>
    [JsonPropertyName("clashPolicyName")]
    public string ClashPolicyName { get; set; } = "Proxy";

    /// <summary>Default v2ray outbound tag for generated routing snippets.</summary>
    [JsonPropertyName("v2rayOutboundTag")]
    public string V2rayOutboundTag { get; set; } = "proxy";
}

public class RuleHintPreset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("applications")]
    public List<string> Applications { get; set; } = [];

    [JsonPropertyName("domains")]
    public List<string> Domains { get; set; } = [];

    [JsonPropertyName("domainSuffixes")]
    public List<string> DomainSuffixes { get; set; } = [];
}
