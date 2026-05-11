namespace ProxySwitch.Services;

// Stub Logger for ProfileGenTest. Writes to Console instead of file.
public static class Logger
{
    public static void Info(string m) => Console.WriteLine($"[INFO] {m}");
    public static void Error(string m) => Console.Error.WriteLine($"[ERROR] {m}");
}
