using Newtonsoft.Json;
using NLog;
using NLog.Config;
using Socksifier;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Topshelf;
using LogLevel = Socksifier.LogLevel;

namespace ProxiFyre
{
    /// <summary>
    /// Main class for the SOCKS proxy application.
    /// Handles service lifecycle, configuration loading, and proxy association.
    /// </summary>
    public class ProxiFyreService
    {
        /// <summary>
        /// NLog logger instance for logging service events.
        /// </summary>
        private static readonly Logger LoggerInstance = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// The current log level for the service.
        /// </summary>
        private static LogLevel _logLevel;

        /// <summary>
        /// The Socksifier instance used to manage SOCKS5 proxies.
        /// </summary>
        private Socksifier.Socksifier _socksify;

        /// <summary>
        /// The session route IPC server for runtime PID management.
        /// </summary>
        private SessionRouteServer _sessionRouteServer;

        /// <summary>
        /// Starts the ProxiFyre service, loads configuration, and initializes proxies.
        /// </summary>
        public void Start()
        {
            // Get the current executable path
            var executablePath = Assembly.GetExecutingAssembly().Location;
            var directoryPath = Path.GetDirectoryName(executablePath);

            // Form the path to app-config.json
            var configFilePath = Path.Combine(directoryPath ?? string.Empty, "app-config.json");

            // Form the path to NLog.config
            var logConfigFilePath = Path.Combine(directoryPath ?? string.Empty, "NLog.config");

            // Load the configuration from JSON
            var serviceSettings = JsonConvert.DeserializeObject<ProxiFyreSettings>(File.ReadAllText(configFilePath));

            LogManager.Configuration = new XmlLoggingConfiguration(logConfigFilePath);

            // Handle the global log level from the configuration
            _logLevel = Enum.TryParse<LogLevel>(serviceSettings.LogLevel, true, out var globalLogLevel)
                ? globalLogLevel
                : LogLevel.Info;

            LoggerInstance.Info(
                $"ProxiFyre config loaded: logLevel={serviceSettings.LogLevel}, parsedLogLevel={_logLevel}, bypassLan={serviceSettings.BypassLan}, proxies={serviceSettings.Proxies.Count}, excludes={serviceSettings.ExcludedList.Count}");

            // Get an instance of the Socksifier
            _socksify = Socksifier.Socksifier.GetInstance(_logLevel);

            // Attach the LogPrinter method to the LogEvent event
            _socksify.LogEvent += LogPrinter;

            // Set the limit for logging and the interval between logs
            _socksify.LogLimit = 100;
            _socksify.LogEventInterval = 1000;

            // Configure LAN bypass if enabled
            if (serviceSettings.BypassLan)
            {
                _socksify.SetBypassLan();
                if (_logLevel >= LogLevel.Info)
                    LoggerInstance.Info("LAN bypass enabled - local network traffic will not be proxied.");
            }

            var proxyMap = new Dictionary<string, IntPtr>();
            foreach (var appSettings in serviceSettings.Proxies)
            {
                LoggerInstance.Info(
                    $"Configuring SOCKS5 endpoint {appSettings.Socks5ProxyEndpoint}: protocols={string.Join(",", appSettings.SupportedProtocols)}, appNames={appSettings.AppNames.Count}");

                // Add the defined SOCKS5 proxies
                var proxy = _socksify.AddSocks5Proxy(appSettings.Socks5ProxyEndpoint, appSettings.Username,
                    appSettings.Password, appSettings.SupportedProtocolsParse,
                    true); // Assuming the AddSocks5Proxy method supports a list of protocols

                if (proxy.ToInt64() != -1)
                {
                    proxyMap[appSettings.Socks5ProxyEndpoint] = proxy;
                    LoggerInstance.Info($"SOCKS5 endpoint registered: {appSettings.Socks5ProxyEndpoint} -> proxyHandle={proxy.ToInt64()}");
                }
                else
                {
                    LoggerInstance.Warn($"Failed to register SOCKS5 endpoint: {appSettings.Socks5ProxyEndpoint}");
                }

                foreach (var appName in appSettings.AppNames)
                    // Associate the defined application names to the proxies
                    if (proxy.ToInt64() != -1 && _socksify.AssociateProcessNameToProxy(appName, proxy) && _logLevel >= LogLevel.Info)
                        LoggerInstance.Info(
                            $"Successfully associated {appName} to {appSettings.Socks5ProxyEndpoint} SOCKS5 proxy with protocols {string.Join(", ", appSettings.SupportedProtocols)}!");
            }

            foreach (var excludedEntry in serviceSettings.ExcludedList)
            {
                // Add the relevant entries dynamically to the excluded list
                if (_socksify.ExcludeProcessName(excludedEntry)) {
                    LoggerInstance.Info($"Successfully excluded {excludedEntry} from being proxied.");
                } else {
                    LoggerInstance.Warn($"Failed to exclude {excludedEntry} from being proxied.");
                }
            }

            _socksify.Start();

            // Start the session route IPC server
            _sessionRouteServer = new SessionRouteServer(_socksify);
            _sessionRouteServer.SetProxyMap(proxyMap);
            _sessionRouteServer.Start();
            if (_logLevel >= LogLevel.Info)
                LoggerInstance.Info("SessionRouteServer started on \\\\.\\pipe\\ProxySwitch.ProxiFyre.SessionRoute");

            // Inform user that the application is running
            if (_logLevel >= LogLevel.Info)
                LoggerInstance.Info("ProxiFyre Service is running...");
        }

        /// <summary>
        /// Stops the ProxiFyre service and disposes of resources.
        /// </summary>
        public void Stop()
        {
            // Dispose the session route server first to clear PID routes
            _sessionRouteServer?.Dispose();
            _sessionRouteServer = null;

            // Dispose of the Socksifier before exiting
            _socksify?.Dispose();
            if (_logLevel >= LogLevel.Info)
                LoggerInstance.Info("ProxiFyre Service has stopped.");
            LogManager.Shutdown();
        }

        /// <summary>
        /// Handles logging events from the Socksifier and logs them using NLog.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The log event arguments.</param>
        private static void LogPrinter(object sender, LogEventArgs e)
        {
            // Loop through each log entry and log it using NLog
            foreach (var entry in e.Log.Where(entry => entry != null))
            {
                // Format log entry with ISO 8601 timestamp, event, description, and data.
                //var logMessage =
                //    $"{DateTimeOffset.FromUnixTimeMilliseconds(entry.TimeStamp):u} | Event: {entry.Event} | Description: {entry.Description ?? string.Empty} | Data: {entry.Data}";
                LoggerInstance.Info(entry.Description?.Replace("\n", "").Replace("\r", ""));
            }
        }

        //{
        //    "logLevel": "Warning",
        //    "proxies": [
        //        {
        //            "appNames": ["chrome", "chrome_canary"],
        //            "socks5ProxyEndpoint": "158.101.205.51:1080",
        //            "username": "username1",
        //            "password": "password1",
        //            "supportedProtocols": ["TCP", "UDP"]
        //        },
        //        {
        //            "appNames": ["firefox", "firefox_dev"],
        //            "socks5ProxyEndpoint": "159.101.205.52:1080",
        //            "username": "username2",
        //            "password": "password2",
        //            "supportedProtocols": ["TCP"]
        //        }
        //    ],
        //    "excludes": [
        //        "notepad.exe",
        //        "calc.exe",
        //        "C:\\Windows\\System32\\svchost.exe",
        //        "Windows\\System32\\",
        //        "antivirus"
        //    ]
        //}

        /// <summary>
        /// Represents the root configuration settings for ProxiFyre.
        /// </summary>
        private class ProxiFyreSettings
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="ProxiFyreSettings"/> class.
            /// </summary>
            /// <param name="logLevel">The log level as a string.</param>
            /// <param name="proxies">The list of proxy application settings.</param>
            /// <param name="excludedList">The list of process names or paths to exclude from proxying.</param>
            /// <param name="bypassLan">Whether to bypass LAN traffic.</param>
            public ProxiFyreSettings(string logLevel, List<AppSettings> proxies, List<string> excludedList = null, bool bypassLan = false)
            {
                LogLevel = logLevel;
                Proxies = proxies;
                ExcludedList = excludedList ?? new List<string>();
                BypassLan = bypassLan;
            }

            /// <summary>
            /// Gets the log level for the service.
            /// </summary>
            public string LogLevel { get; }

            /// <summary>
            /// Gets the list of proxy application settings.
            /// </summary>
            public List<AppSettings> Proxies { get; }

            /// <summary>
            /// Gets the list of app names to exclude.
            /// </summary>
            [JsonProperty("excludes", NullValueHandling = NullValueHandling.Ignore)]
            public List<string> ExcludedList { get; }

            /// <summary>
            /// Gets a value indicating whether LAN traffic should bypass the proxy.
            /// </summary>
            [JsonProperty("bypassLan", NullValueHandling = NullValueHandling.Ignore)]
            public bool BypassLan { get; }
        }

        /// <summary>
        /// Represents the settings for a single proxy and its associated applications.
        /// </summary>
        internal class AppSettings
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="AppSettings"/> class.
            /// </summary>
            /// <param name="appNames">List of application names to associate with the proxy.</param>
            /// <param name="socks5ProxyEndpoint">SOCKS5 proxy endpoint address.</param>
            /// <param name="username">Username for proxy authentication.</param>
            /// <param name="password">Password for proxy authentication.</param>
            /// <param name="supportedProtocols">List of supported protocols (e.g., TCP, UDP).</param>
            public AppSettings(List<string> appNames, string socks5ProxyEndpoint, string username, string password, List<string> supportedProtocols)
            {
                AppNames = appNames;
                Socks5ProxyEndpoint = socks5ProxyEndpoint;
                Username = username;
                Password = password;
                SupportedProtocols = supportedProtocols;
            }

            /// <summary>
            /// Gets or sets the list of application names to associate with the proxy.
            /// </summary>
            public List<string> AppNames { get; set; }

            /// <summary>
            /// Gets the SOCKS5 proxy endpoint address.
            /// </summary>
            public string Socks5ProxyEndpoint { get; }

            /// <summary>
            /// Gets the username for proxy authentication.
            /// </summary>
            public string Username { get; }

            /// <summary>
            /// Gets the password for proxy authentication.
            /// </summary>
            public string Password { get; }

            /// <summary>
            /// Gets the list of supported protocols (e.g., TCP, UDP).
            /// </summary>
            public List<string> SupportedProtocols { get; }

            /// <summary>
            /// Gets the supported protocols as an enum value.
            /// </summary>
            public SupportedProtocolsEnum SupportedProtocolsParse
            {
                get
                {
                    if (SupportedProtocols.Count == 0 ||
                        (SupportedProtocols.Contains("TCP") && SupportedProtocols.Contains("UDP")))
                        return SupportedProtocolsEnum.BOTH;
                    if (SupportedProtocols.Contains("TCP"))
                        return SupportedProtocolsEnum.TCP;
                    return SupportedProtocols.Contains("UDP")
                        ? SupportedProtocolsEnum.UDP
                        : SupportedProtocolsEnum.BOTH;
                }
            }
        }
    }

    /// <summary>
    /// Entry point for the ProxiFyre service application.
    /// </summary>
    internal class Program
    {
        /// <summary>
        /// Main method. Configures and runs the ProxiFyre service using Topshelf.
        /// </summary>
        /// <param name="args">Command-line arguments (e.g., install, uninstall, start, stop).</param>
        /// <returns>The Topshelf exit code as an integer.</returns>
        private static int Main(string[] args)
        {
            // Detect Topshelf lifecycle commands. For these commands the underlying
            // .NET installer (System.Configuration.Install.InstallContext, invoked via
            // ManagedInstallerClass.InstallHelper) writes multiple localized status
            // lines to Console.Out with noticeable delays between them, which can lead
            // users to close the console before the operation has actually completed.
            // We capture that output and replace it with a single, unambiguous message.
            var command = args.Length > 0 && !string.IsNullOrEmpty(args[0])
                ? args[0].ToLowerInvariant()
                : null;

            var isLifecycleCommand = command == "install"
                                     || command == "uninstall"
                                     || command == "start"
                                     || command == "stop";

            var originalOut = Console.Out;
            var originalError = Console.Error;

            using (var capturedOutput = isLifecycleCommand ? new StringWriter() : null)
            {
                if (isLifecycleCommand)
                {
                    Console.SetOut(capturedOutput);
                    Console.SetError(capturedOutput);
                }

                TopshelfExitCode exitCode;
                try
                {
                    exitCode = HostFactory.Run(x =>
                    {
                        x.Service<ProxiFyreService>(s =>
                        {
                            s.ConstructUsing(name => new ProxiFyreService());
                            s.WhenStarted(tc => tc.Start());
                            s.WhenStopped(tc => tc.Stop());
                        });

                        x.RunAsLocalSystem();

                        x.SetDescription("ProxiFyre - SOCKS5 ProxiFyre Service");
                        x.SetDisplayName("ProxiFyre Service");
                        x.SetServiceName("ProxiFyreService");
                    });
                }
                finally
                {
                    if (isLifecycleCommand)
                    {
                        Console.SetOut(originalOut);
                        Console.SetError(originalError);
                    }
                }

                if (isLifecycleCommand)
                {
                    if (exitCode == TopshelfExitCode.Ok)
                    {
                        string message;
                        switch (command)
                        {
                            case "install":
                                message = "ProxiFyre service installed successfully.";
                                break;
                            case "uninstall":
                                message = "ProxiFyre service uninstalled successfully.";
                                break;
                            case "start":
                                message = "ProxiFyre service started successfully.";
                                break;
                            case "stop":
                                message = "ProxiFyre service stopped successfully.";
                                break;
                            default:
                                message = "ProxiFyre command completed successfully.";
                                break;
                        }

                        originalOut.WriteLine(message);
                    }
                    else
                    {
                        // Surface the captured installer output so failures remain diagnosable.
                        var captured = capturedOutput.ToString();
                        if (!string.IsNullOrEmpty(captured))
                            originalError.Write(captured);
                        originalError.WriteLine($"ProxiFyre {command} command failed (exit code: {exitCode}).");
                    }
                }

                return (int)exitCode;
            }
        }
    }
}
