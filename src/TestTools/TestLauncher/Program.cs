using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TestLauncher;

internal static class Program
{
    static int Main(string[] args)
    {
        var procName = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
        var mode = ResolveMode(procName, args);

        Console.WriteLine($"[{procName}] mode={mode}");

        try
        {
            return mode switch
            {
                "spawn" => RunSpawn(),
                "shellexec" => RunShellExec(),
                "long" => RunLong(),
                "child" => RunChild(),
                _ => RunUnknown(mode)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static string ResolveMode(string procName, string[] args)
    {
        if (procName.Contains("spawn")) return "spawn";
        if (procName.Contains("shellexec")) return "shellexec";
        if (procName.Contains("long")) return "long";
        if (procName.Contains("child") || procName.Contains("dummy")) return "child";
        if (args.Length > 0) return args[0].ToLowerInvariant();
        return "child";
    }

    static int RunSpawn()
    {
        var childPath = Path.Combine(AppContext.BaseDirectory, "child_dummy.exe");
        if (!File.Exists(childPath))
        {
            Console.Error.WriteLine($"child_dummy.exe not found at {childPath}");
            return 2;
        }
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = childPath,
            UseShellExecute = false
        });
        Console.WriteLine($"Spawned child PID={proc?.Id}, launcher will exit in 3s.");
        // Stay alive briefly so ProxySwitch's descendant tracker (500ms polling) sees us
        // and links child to launcher root before we die.
        Thread.Sleep(3000);
        return 0;
    }

    static int RunShellExec()
    {
        var childPath = Path.Combine(AppContext.BaseDirectory, "child_dummy.exe");
        if (!File.Exists(childPath))
        {
            Console.Error.WriteLine($"child_dummy.exe not found at {childPath}");
            return 2;
        }
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            lpVerb = "open",
            lpFile = childPath,
            nShow = 1,
            fMask = 0x00000040
        };
        if (!ShellExecuteEx(ref info))
        {
            Console.Error.WriteLine($"ShellExecuteEx failed: {Marshal.GetLastWin32Error()}");
            return 3;
        }
        Console.WriteLine($"ShellExecuted child, launcher exiting.");
        return 0;
    }

    static int RunLong()
    {
        Console.WriteLine("Long-running launcher (60s), no child.");
        Thread.Sleep(60_000);
        return 0;
    }

    static int RunChild()
    {
        Console.WriteLine("Child dummy (60s).");
        Thread.Sleep(60_000);
        return 0;
    }

    static int RunUnknown(string mode)
    {
        Console.Error.WriteLine($"Unknown mode '{mode}'. Rename exe to contain spawn/shellexec/long/child or pass as arg.");
        return 4;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
}
