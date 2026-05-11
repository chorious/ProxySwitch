using System.Text;
using ProxySwitch.Models;

namespace ProxySwitch.Services;

/// <summary>
/// Generates routing rule SNIPPETS for users to copy into their external router
/// (Clash Verge / v2ray). ProxySwitch never writes to these configs — this service
/// only produces text the user can paste.
/// </summary>
public class RoutingHintService
{
    private readonly ProxyConfig _config;

    public RoutingHintService(ProxyConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Build a Clash-style rule snippet for the given session.
    /// Includes PROCESS-NAME for the launcher and any tracked descendant/correlated processes.
    /// Domains are pulled from a matching preset (by launcher exe name) if any.
    /// </summary>
    public string BuildClashHint(LaunchSession session)
    {
        var policy = _config.RoutingHints.ClashPolicyName;
        var launcherName = Path.GetFileName(session.ExePath);

        var processNames = new List<string> { launcherName };
        lock (session.Processes)
        {
            foreach (var p in session.Processes
                .Where(p => p.Role != "root" && !string.IsNullOrEmpty(p.Name))
                .Select(p => p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!processNames.Contains(p, StringComparer.OrdinalIgnoreCase))
                    processNames.Add(p);
            }
        }

        var preset = FindPresetForApp(launcherName);

        var sb = new StringBuilder();
        sb.AppendLine($"# Routing hint for {launcherName} via {session.ProxyId} (policy: {policy})");
        sb.AppendLine($"# ProxySwitch does NOT apply this — copy into your Clash rules section.");
        sb.AppendLine();
        foreach (var pn in processNames)
        {
            sb.AppendLine($"- PROCESS-NAME,{pn},{policy}");
        }

        if (preset != null)
        {
            sb.AppendLine();
            sb.AppendLine($"# Preset: {preset.Name}");
            if (!string.IsNullOrEmpty(preset.Description))
                sb.AppendLine($"# {preset.Description}");
            foreach (var d in preset.Domains)
                sb.AppendLine($"- DOMAIN,{d},{policy}");
            foreach (var s in preset.DomainSuffixes)
                sb.AppendLine($"- DOMAIN-SUFFIX,{s},{policy}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build a v2ray-style routing rule snippet (JSON, to paste into routing.rules).
    /// </summary>
    public string BuildV2rayHint(LaunchSession session)
    {
        var outboundTag = _config.RoutingHints.V2rayOutboundTag;
        var launcherName = Path.GetFileName(session.ExePath);
        var preset = FindPresetForApp(launcherName);

        var sb = new StringBuilder();
        sb.AppendLine($"// Routing hint for {launcherName} via {session.ProxyId} (outboundTag: {outboundTag})");
        sb.AppendLine($"// ProxySwitch does NOT apply this — copy into your v2ray routing.rules array.");
        sb.AppendLine();

        var processList = new List<string> { launcherName };
        lock (session.Processes)
        {
            foreach (var p in session.Processes
                .Where(p => p.Role != "root" && !string.IsNullOrEmpty(p.Name))
                .Select(p => p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!processList.Contains(p, StringComparer.OrdinalIgnoreCase))
                    processList.Add(p);
            }
        }

        var processArr = string.Join(", ", processList.Select(p => $"\"{p}\""));
        sb.AppendLine("{");
        sb.AppendLine("  \"type\": \"field\",");
        sb.AppendLine($"  \"process\": [{processArr}],");
        sb.AppendLine($"  \"outboundTag\": \"{outboundTag}\"");
        sb.AppendLine("}");

        if (preset != null && (preset.Domains.Count > 0 || preset.DomainSuffixes.Count > 0))
        {
            sb.AppendLine(",");
            sb.AppendLine("{");
            sb.AppendLine("  \"type\": \"field\",");
            var domains = new List<string>();
            domains.AddRange(preset.Domains);
            domains.AddRange(preset.DomainSuffixes.Select(s => $"domain:{s}"));
            var domArr = string.Join(", ", domains.Select(d => $"\"{d}\""));
            sb.AppendLine($"  \"domain\": [{domArr}],");
            sb.AppendLine($"  \"outboundTag\": \"{outboundTag}\"");
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Pick the rule format for a session based on its proxy's routing backend.
    /// Defaults to clash if unknown.
    /// </summary>
    public string BuildHintForSession(LaunchSession session)
    {
        var backend = _config.RoutingBackends.FirstOrDefault(b => b.ProxyId == session.ProxyId);
        return backend?.RuleFormat?.ToLowerInvariant() switch
        {
            "v2ray" => BuildV2rayHint(session),
            _ => BuildClashHint(session)
        };
    }

    private RuleHintPreset? FindPresetForApp(string launcherName)
    {
        return _config.RuleHintPresets.FirstOrDefault(p =>
            p.Applications.Any(a => string.Equals(a, launcherName, StringComparison.OrdinalIgnoreCase)));
    }
}
