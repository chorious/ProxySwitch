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

    /// <summary>
    /// Optional transparent per-app routing backend (e.g. ProxiFyre).
    /// When configured, generic apps dropped into proxy lanes get a real
    /// routing rule written to the backend; without it, ProxySwitch falls
    /// back to v0.5 "external routing required" intent only.
    /// </summary>
    [JsonPropertyName("transparentBackend")]
    public TransparentBackendConfig TransparentBackend { get; set; } = new();

    /// <summary>
    /// Per-app routing rules ProxySwitch maintains, regenerated into the
    /// transparent backend's config on each change.
    /// </summary>
    [JsonPropertyName("appRoutes")]
    public List<AppRoute> AppRoutes { get; set; } = [];

    [JsonPropertyName("sessionSupervisor")]
    public SessionSupervisorConfig SessionSupervisor { get; set; } = new();
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

public class TransparentBackendConfig
{
    /// <summary>"none" or "proxifyre".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "none";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Path to ProxiFyre.exe.</summary>
    [JsonPropertyName("exe")]
    public string Exe { get; set; } = "";

    /// <summary>Path to app-config.json that ProxiFyre reads on startup.</summary>
    [JsonPropertyName("configPath")]
    public string ConfigPath { get; set; } = "";

    /// <summary>Windows service name. Topshelf default for ProxiFyre 2.2.1 is "ProxiFyreService".</summary>
    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = "ProxiFyreService";

    /// <summary>
    /// If true, ProxySwitch will call ProxiFyre.exe install/start/stop and use
    /// sc.exe / Service Controller APIs. Default false — user manages the
    /// service manually for safety.
    /// </summary>
    [JsonPropertyName("manageService")]
    public bool ManageService { get; set; }

    /// <summary>If true, restart backend after every config write.
    /// Restart uses ServiceController when ManageService is on, otherwise spawns
    /// an elevated helper (single UAC prompt).</summary>
    [JsonPropertyName("autoRestartOnConfigChange")]
    public bool AutoRestartOnConfigChange { get; set; } = true;
}

public class AppRoute
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Full path to the executable when known (preferred match).</summary>
    [JsonPropertyName("exePath")]
    public string ExePath { get; set; } = "";

    /// <summary>Fallback short name (e.g. "Obsidian.exe") when full path is not stable.</summary>
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "";

    [JsonPropertyName("proxyId")]
    public string ProxyId { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// true (set)  — saved into proxyswitch.json, restored on next startup,
    ///               and also tracked when the app is launched outside ProxySwitch.
    /// false (tmp) — in-memory only for this ProxySwitch session.
    /// </summary>
    [JsonPropertyName("isPersistent")]
    public bool IsPersistent { get; set; } = true;

    /// <summary>"drop-zone-tmp" / "drop-zone-set" / "pinned" / "child-detected" / "user".</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "user";

    /// <summary>
    /// For session-only (tmp) routes: the LaunchSession.Id that owns this route.
    /// When that session exits and the user finalizes it (or ProxySwitch closes),
    /// the route is removed from app-config.json. Null for persistent routes.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>path | process-name | msix-package</summary>
    [JsonPropertyName("matchKind")]
    public string MatchKind { get; set; } = "path";

    [JsonPropertyName("packageFamilyName")]
    public string PackageFamilyName { get; set; } = "";

    [JsonPropertyName("packageRelativeExePath")]
    public string PackageRelativeExePath { get; set; } = "";

    [JsonPropertyName("resolvedExePath")]
    public string ResolvedExePath { get; set; } = "";

    [JsonPropertyName("resolvedAt")]
    public DateTime? ResolvedAt { get; set; }
}

public class SessionSupervisorConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("restartGraceSeconds")]
    public int RestartGraceSeconds { get; set; } = 20;
}
