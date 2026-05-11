namespace ProxySwitch.Services;

public static class Logger
{
    private static readonly string LogDir = @"E:\proxyswitch\logs";
    private static readonly string LogFile = Path.Combine(LogDir, "proxyswitch.log");
    private static readonly object Lock = new();

    static Logger()
    {
        Directory.CreateDirectory(LogDir);
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message)
    {
        Write("ERROR", message);
    }

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        lock (Lock)
        {
            try
            {
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
