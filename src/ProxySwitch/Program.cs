namespace ProxySwitch;

using ProxySwitch.Services;
using System.Reflection;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var infoVersion = typeof(Program).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        Logger.Info($"=== ProxySwitch starting (version: {infoVersion}) ===");

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Logger.Error($"UnhandledException: {e.ExceptionObject}");
            MessageBox.Show($"ProxySwitch crashed:\n{e.ExceptionObject}", "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        Application.ThreadException += (s, e) =>
        {
            Logger.Error($"ThreadException: {e.Exception}");
            MessageBox.Show($"ProxySwitch error:\n{e.Exception.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Logger.Error($"Application.Run failed: {ex}");
            MessageBox.Show($"ProxySwitch failed to start:\n{ex.Message}\n\nSee log: E:\\proxyswitch\\logs\\proxyswitch.log", "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Logger.Info("=== ProxySwitch exiting ===");
    }
}
