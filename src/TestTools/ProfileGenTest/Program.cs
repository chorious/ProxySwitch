using System.Xml;
using ProxySwitch.Services;

namespace ProfileGenTest;

internal static class Program
{
    static int passed = 0;
    static int failed = 0;

    static int Main()
    {
        var basePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "profiles", "proxifier");
        basePath = Path.GetFullPath(basePath);
        var templatePath = Path.Combine(basePath, "generated-10708.ppx");

        if (!File.Exists(templatePath))
        {
            Console.Error.WriteLine($"Template not found: {templatePath}");
            return 99;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "ppx_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"Work dir: {workDir}");

        try
        {
            TestAddAndIdempotency(templatePath, workDir);
            TestAtomicAndBackup(templatePath, workDir);
            TestMissingFile(workDir);
            TestMissingRule(templatePath, workDir);
            TestUserProfileUntouched(templatePath, workDir);
            TestRuleContains(templatePath, workDir);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"=== {passed} passed, {failed} failed ===");
        return failed == 0 ? 0 : 1;
    }

    static void TestAddAndIdempotency(string template, string workDir)
    {
        var ppx = Path.Combine(workDir, "test1.ppx");
        File.Copy(template, ppx);
        var gen = new ProxifierProfileGenerator();

        var r1 = gen.AddApplicationToRule(ppx, "ProxySwitch_10708_AssistedApps", "launcher_spawn.exe", "child_dummy.exe");
        Assert("Add new exes returns true", r1 == true);
        AssertXmlContains(ppx, "ProxySwitch_10708_AssistedApps", "launcher_spawn.exe");
        AssertXmlContains(ppx, "ProxySwitch_10708_AssistedApps", "child_dummy.exe");

        var r2 = gen.AddApplicationToRule(ppx, "ProxySwitch_10708_AssistedApps", "launcher_spawn.exe", "child_dummy.exe");
        Assert("Re-add same exes returns false (idempotent)", r2 == false);

        var r3 = gen.AddApplicationToRule(ppx, "ProxySwitch_10708_AssistedApps", "extra.exe");
        Assert("Add new exe returns true", r3 == true);
        AssertXmlContains(ppx, "ProxySwitch_10708_AssistedApps", "extra.exe");
        AssertXmlContains(ppx, "ProxySwitch_10708_AssistedApps", "launcher_spawn.exe"); // preserved
    }

    static void TestAtomicAndBackup(string template, string workDir)
    {
        var ppx = Path.Combine(workDir, "test_atomic.ppx");
        File.Copy(template, ppx);
        var gen = new ProxifierProfileGenerator();

        gen.AddApplicationToRule(ppx, "ProxySwitch_10708_AssistedApps", "first.exe");

        var bak = ppx + ".bak";
        Assert(".bak created after first edit", File.Exists(bak));

        // .tmp should not linger
        var tmp = ppx + ".tmp";
        Assert(".tmp not left behind", !File.Exists(tmp));

        // .bak should be the original (no apps in rule)
        var bakDoc = new XmlDocument();
        bakDoc.Load(bak);
        var bakRule = FindRule(bakDoc, "ProxySwitch_10708_AssistedApps");
        var bakApps = bakRule?.SelectSingleNode("Applications")?.InnerText ?? "";
        Assert(".bak preserved original empty Applications", string.IsNullOrWhiteSpace(bakApps));

        // Second edit should not overwrite .bak
        var bakTimeBefore = File.GetLastWriteTime(bak);
        Thread.Sleep(50);
        gen.AddApplicationToRule(ppx, "ProxySwitch_10708_AssistedApps", "second.exe");
        var bakTimeAfter = File.GetLastWriteTime(bak);
        Assert(".bak NOT overwritten on second edit", bakTimeBefore == bakTimeAfter);
    }

    static void TestMissingFile(string workDir)
    {
        var nonexistent = Path.Combine(workDir, "nonexistent.ppx");
        var gen = new ProxifierProfileGenerator();
        try
        {
            gen.AddApplicationToRule(nonexistent, "ProxySwitch_10708_AssistedApps", "x.exe");
            Assert("Missing file throws FileNotFoundException", false);
        }
        catch (FileNotFoundException ex)
        {
            Assert("Missing file throws FileNotFoundException", true);
            Assert("Error message contains setup hint", ex.Message.Contains("Create a base profile"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  unexpected exception: {ex.GetType().Name}");
            Assert("Missing file throws FileNotFoundException", false);
        }
    }

    static void TestMissingRule(string template, string workDir)
    {
        var ppx = Path.Combine(workDir, "test_missing_rule.ppx");
        File.Copy(template, ppx);
        var gen = new ProxifierProfileGenerator();
        try
        {
            gen.AddApplicationToRule(ppx, "NonexistentRule_NeverMade", "x.exe");
            Assert("Missing rule throws InvalidOperationException", false);
        }
        catch (InvalidOperationException ex)
        {
            Assert("Missing rule throws InvalidOperationException", true);
            Assert("Error message names the rule", ex.Message.Contains("NonexistentRule_NeverMade"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  unexpected exception: {ex.GetType().Name}");
            Assert("Missing rule throws InvalidOperationException", false);
        }
    }

    static void TestUserProfileUntouched(string template, string workDir)
    {
        var genPpx = Path.Combine(workDir, "test_gen.ppx");
        var userPpx = Path.Combine(workDir, "test_user.ppx");
        File.Copy(template, genPpx);
        File.Copy(template, userPpx);
        var userHashBefore = ComputeHash(userPpx);

        var gen = new ProxifierProfileGenerator();
        gen.AddApplicationToRule(genPpx, "ProxySwitch_10708_AssistedApps", "abc.exe");

        var userHashAfter = ComputeHash(userPpx);
        Assert("User profile hash unchanged after Add Rule", userHashBefore == userHashAfter);
    }

    static void TestRuleContains(string template, string workDir)
    {
        var ppx = Path.Combine(workDir, "test_contains.ppx");
        File.Copy(template, ppx);
        var gen = new ProxifierProfileGenerator();
        gen.AddApplicationToRule(ppx, "ProxySwitch_10708_AssistedApps", "foo.exe");

        Assert("RuleContains finds added exe", gen.RuleContains(ppx, "ProxySwitch_10708_AssistedApps", "foo.exe"));
        Assert("RuleContains case-insensitive", gen.RuleContains(ppx, "ProxySwitch_10708_AssistedApps", "FOO.exe"));
        Assert("RuleContains misses unrelated exe", !gen.RuleContains(ppx, "ProxySwitch_10708_AssistedApps", "bar.exe"));

        var apps = gen.GetApplicationsInRule(ppx, "ProxySwitch_10708_AssistedApps");
        Assert("GetApplicationsInRule returns added exe", apps.Contains("foo.exe", StringComparer.OrdinalIgnoreCase));
    }

    static XmlNode? FindRule(XmlDocument doc, string name)
    {
        foreach (XmlNode rule in doc.GetElementsByTagName("Rule"))
        {
            var n = rule.SelectSingleNode("Name");
            if (n != null && n.InnerText.Trim() == name) return rule;
        }
        return null;
    }

    static void AssertXmlContains(string ppx, string rule, string exe)
    {
        var doc = new XmlDocument();
        doc.Load(ppx);
        var ruleNode = FindRule(doc, rule);
        var apps = ruleNode?.SelectSingleNode("Applications")?.InnerText ?? "";
        Assert($"XML contains {exe} in {rule}", apps.Contains(exe, StringComparison.OrdinalIgnoreCase));
    }

    static string ComputeHash(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash);
    }

    static void Assert(string label, bool ok)
    {
        if (ok)
        {
            Console.WriteLine($"  [PASS] {label}");
            passed++;
        }
        else
        {
            Console.WriteLine($"  [FAIL] {label}");
            failed++;
        }
    }
}
