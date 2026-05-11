using System.Xml;

namespace ProxySwitch.Services;

/// <summary>
/// Generates / updates Proxifier-compatible .ppx XML files.
/// Strategy: do NOT create .ppx from scratch (Proxifier format varies by version).
/// User must pre-export a base .ppx with an empty rule named e.g. "ProxySwitch_10708_AssistedApps".
/// This service only reads existing XML and updates the rule's Applications node.
/// </summary>
public class ProxifierProfileGenerator
{
    public const string RulePrefix = "ProxySwitch_";

    /// <summary>
    /// Add an application name to the named rule's Applications node.
    /// Returns true on success, throws on file/format issues.
    /// </summary>
    public bool AddApplicationToRule(string profilePath, string ruleName, params string[] exeNames)
    {
        if (!File.Exists(profilePath))
            throw new FileNotFoundException(
                $"Generated profile not found: {profilePath}\n\n" +
                $"Create a base profile in Proxifier first:\n" +
                $"1. Open Proxifier\n" +
                $"2. Create a new rule named '{ruleName}'\n" +
                $"3. Set Action to use the appropriate proxy\n" +
                $"4. File → Save Profile As... → {Path.GetFileName(profilePath)}");

        var doc = new XmlDocument();
        doc.Load(profilePath);

        // Find rule by name. Proxifier .ppx may use various paths, try common ones.
        var rule = FindRuleByName(doc, ruleName);
        if (rule == null)
            throw new InvalidOperationException(
                $"Rule '{ruleName}' not found in {Path.GetFileName(profilePath)}.\n\n" +
                $"Open the profile in Proxifier and create a rule with that exact name first.");

        var appsNode = FindOrCreateApplicationsNode(doc, rule);

        var existing = (appsNode.InnerText ?? "")
            .Split(new[] { ';', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool changed = false;
        foreach (var exe in exeNames)
        {
            if (string.IsNullOrWhiteSpace(exe)) continue;
            if (existing.Add(exe)) changed = true;
        }

        if (!changed) return false;

        appsNode.InnerText = string.Join("; ", existing.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

        // Atomic write: tmp + rename. Backup once.
        var backupPath = profilePath + ".bak";
        if (!File.Exists(backupPath))
        {
            try { File.Copy(profilePath, backupPath); } catch { /* best effort */ }
        }

        var tempPath = profilePath + ".tmp";
        doc.Save(tempPath);
        File.Move(tempPath, profilePath, overwrite: true);

        Logger.Info($"Updated {Path.GetFileName(profilePath)} rule '{ruleName}' with apps: {string.Join(", ", exeNames)}");
        return true;
    }

    public List<string> GetApplicationsInRule(string profilePath, string ruleName)
    {
        if (!File.Exists(profilePath)) return [];
        try
        {
            var doc = new XmlDocument();
            doc.Load(profilePath);
            var rule = FindRuleByName(doc, ruleName);
            var appsNode = rule == null ? null : FindOrCreateApplicationsNode(doc, rule, createIfMissing: false);
            if (appsNode == null) return [];
            return appsNode.InnerText
                .Split(new[] { ';', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to read apps from {profilePath}: {ex.Message}");
            return [];
        }
    }

    public bool RuleContains(string profilePath, string ruleName, string exeName)
    {
        var apps = GetApplicationsInRule(profilePath, ruleName);
        return apps.Any(a => string.Equals(a, exeName, StringComparison.OrdinalIgnoreCase));
    }

    private static XmlNode? FindRuleByName(XmlDocument doc, string ruleName)
    {
        // Try several common XPaths; Proxifier .ppx schemas vary across versions.
        // Search for any element with a child <Name> matching ruleName.
        var candidates = doc.GetElementsByTagName("Rule");
        foreach (XmlNode rule in candidates)
        {
            var nameEl = rule.SelectSingleNode("Name") ?? rule.SelectSingleNode("name");
            if (nameEl != null && string.Equals(nameEl.InnerText.Trim(), ruleName, StringComparison.OrdinalIgnoreCase))
                return rule;

            // Some schemas put name as attribute
            var nameAttr = rule.Attributes?["name"] ?? rule.Attributes?["Name"];
            if (nameAttr != null && string.Equals(nameAttr.Value.Trim(), ruleName, StringComparison.OrdinalIgnoreCase))
                return rule;
        }
        return null;
    }

    private static XmlNode FindOrCreateApplicationsNode(XmlDocument doc, XmlNode rule, bool createIfMissing = true)
    {
        var apps = rule.SelectSingleNode("Applications")
                ?? rule.SelectSingleNode("applications");
        if (apps != null) return apps;

        if (!createIfMissing)
            return null!;

        apps = doc.CreateElement("Applications");
        rule.AppendChild(apps);
        return apps;
    }
}
