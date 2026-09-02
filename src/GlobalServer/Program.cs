using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using System;
using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;

namespace Sungaila.NewDark.GlobalServer
{
    public static partial class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.Title = "Thief 2 Multiplayer Global Server";

            try
            {
                var enUS = CultureInfo.GetCultureInfo("en-US");

                CultureInfo.DefaultThreadCurrentCulture = enUS;
                CultureInfo.DefaultThreadCurrentUICulture = enUS;
                CultureInfo.CurrentCulture = enUS;
                CultureInfo.CurrentUICulture = enUS;
            }
            catch (CultureNotFoundException)
            {
                // InvariantGlobalization is used for AoT compilation
                // if setting en-US fails, then just continue with InvariantCulture
            }

            var portOpt = new Option<int>("--port", "-p")
            {
                DefaultValueFactory = _ => 5199,
                Description = "Port for this global server"
            };
            portOpt.Validators.Add(r =>
            {
                if (r.GetValueOrDefault<int>() is < 1 or > 65535)
                {
                    r.AddError("Port must be between 1 and 65535.");
                }
            });

            var timeoutServerOpt = new Option<int>("--timeoutserver", "-s", "--timeout-server")
            {
                DefaultValueFactory = _ => (int)TimeSpan.FromMinutes(3).TotalSeconds,
                Description = "Game server timeout in seconds"
            };
            timeoutServerOpt.Validators.Add(r =>
            {
                if (r.GetValueOrDefault<int>() < 1)
                {
                    r.AddError("Game server timeout must be at least 1 second.");
                }
            });

            var timeoutClientOpt = new Option<int>("--timeoutclient", "-c", "--timeout-client")
            {
                DefaultValueFactory = _ => (int)TimeSpan.FromHours(1).TotalSeconds,
                Description = "Game client timeout in seconds"
            };
            timeoutClientOpt.Validators.Add(r =>
            {
                if (r.GetValueOrDefault<int>() < 1)
                {
                    r.AddError("Game client timeout must be at least 1 second.");
                }
            });

            var timeoutDirectPlayQueryOpt = new Option<int>("--timeoutdirectplayquery", "-d", "--timeout-directplay-query")
            {
                DefaultValueFactory = _ => (int)TimeSpan.FromSeconds(2).TotalSeconds,
                Description = "DirectPlay 8 query timeout in seconds"
            };
            timeoutDirectPlayQueryOpt.Validators.Add(r =>
            {
                if (r.GetValueOrDefault<int>() < 1)
                {
                    r.AddError("DirectPlay 8 query timeout must be at least 1 second.");
                }
            });

            var timeoutUnidentifiedOpt = new Option<int>("--timeoutunidentified", "-u", "--timeout-unidentified")
            {
                DefaultValueFactory = _ => (int)TimeSpan.FromSeconds(10).TotalSeconds,
                Description = "Timeout for connections to identify as client or server in seconds"
            };
            timeoutUnidentifiedOpt.Validators.Add(r =>
            {
                if (r.GetValueOrDefault<int>() < 1)
                {
                    r.AddError("Timeout for connections to identify must be at least 1 second");
                }
            });

            var showHeartbeatMinimalOpt = new Option<bool>("--showheartbeatminimal", "-b", "--show-heartbeat-minimal")
            {
                DefaultValueFactory = _ => false,
                Description = "Show HeartbeatMinimal messages in the log"
            };

            var hideFailedConnectionsOpt = new Option<bool>("--hidefailedconn", "-f", "--hide-failed-conn")
            {
                DefaultValueFactory = _ => false,
                Description = "Hide failed or unidentified connection attempts from the log"
            };

            var printTimestampsOpt = new Option<bool>("--printtimestamps", "-t", "--print-timestamps")
            {
                DefaultValueFactory = _ => false,
                Description = "Add timestamps to interactive console log output"
            };

            var websocketOpt = new Option<bool>("--websocket", "-w")
            {
                DefaultValueFactory = _ => false,
                Description = "Activate the optional WebSocket for non-game clients"
            };

            var websocketHostnameOpt = new Option<string>("--websockethostname", "-n", "--websocket-hostname")
            {
                DefaultValueFactory = _ => "localhost",
                Description = "Set the hostname for the WebSocket"
            };
            websocketHostnameOpt.Validators.Add(r =>
            {
                if (string.IsNullOrWhiteSpace(r.GetValueOrDefault<string?>()))
                {
                    r.AddError("WebSocket hostname cannot be empty.");
                }
            });

            var websocketPortOpt = new Option<int>("--websocketport", "-m", "--websocket-port")
            {
                DefaultValueFactory = _ => 5200,
                Description = "Set the port for the WebSocket"
            };
            websocketPortOpt.Validators.Add(r =>
            {
                if (r.GetValueOrDefault<int>() is < 1 or > 65535)
                {
                    r.AddError("WebSocket port must be between 1 and 65535.");
                }
            });

            var websocketSslOpt = new Option<bool>("--websocketssl", "-e", "--websocket-ssl")
            {
                DefaultValueFactory = _ => false,
                Description = "Activate SSL for the WebSocket"
            };

            var verboseOpt = new Option<bool>("--verbose", "-v")
            {
                DefaultValueFactory = _ => false,
                Description = "Show more verbose messages in the log"
            };

            var root = new RootCommand("Starts a server providing a game server list for Thief 2 Multiplayer.");
            root.Options.Add(portOpt);
            root.Options.Add(timeoutServerOpt);
            root.Options.Add(timeoutClientOpt);
            root.Options.Add(timeoutDirectPlayQueryOpt);
            root.Options.Add(timeoutUnidentifiedOpt);
            root.Options.Add(showHeartbeatMinimalOpt);
            root.Options.Add(hideFailedConnectionsOpt);
            root.Options.Add(printTimestampsOpt);
            root.Options.Add(websocketOpt);
            root.Options.Add(websocketHostnameOpt);
            root.Options.Add(websocketPortOpt);
            root.Options.Add(websocketSslOpt);
            root.Options.Add(verboseOpt);

            root.Validators.Add(r =>
            {
                var wsOptRes = r.GetResult(websocketOpt);
                var hostRes = r.GetResult(websocketHostnameOpt);
                var portRes = r.GetResult(websocketPortOpt);
                var sslRes = r.GetResult(websocketSslOpt);

                bool websocket = wsOptRes?.GetValueOrDefault<bool>() ?? false;

                bool hostSpecified = hostRes is not null && hostRes.Implicit == false;
                bool portSpecified = portRes is not null && portRes.Implicit == false;
                bool sslSpecified = sslRes is not null && sslRes.Implicit == false;

                if (!websocket)
                {
                    if (hostSpecified)
                        r.AddError("WebSocket hostname option requires --websocket.");

                    if (portSpecified)
                        r.AddError("WebSocket port option requires --websocket.");

                    if (sslSpecified)
                        r.AddError("WebSocket SSL option requires --websocket.");
                }
            });

            root.SetAction(async p =>
            {
                var printTimestamps = p.GetValue(printTimestampsOpt);

                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    Args = [],
                    ApplicationName = typeof(Program).Assembly.GetName().Name,
                    ContentRootPath = AppContext.BaseDirectory
                });

                // Both integrations are context-aware. In a normal console or Docker container,
                // ConsoleLifetime remains active and handles SIGINT/SIGTERM gracefully.
                builder.Services.AddSystemd();
                builder.Services.AddWindowsService(options => options.ServiceName = "NewDarkGlobalServer");

                // Use only the provider that matches the current host. This keeps logging small
                // and avoids duplicate output from the default Host logging providers.
                builder.Logging.ClearProviders();
                builder.Logging.SetMinimumLevel(LogLevel.Information);
                builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
                builder.Logging.AddFilter("System", LogLevel.Warning);

                if (WindowsServiceHelpers.IsWindowsService())
                {
                    builder.Logging.AddEventLog(settings =>
                    {
#pragma warning disable CA1416
                        settings.SourceName = "NewDarkGlobalServer";
                        settings.Filter = (category, level) =>
                            level >= (category?.StartsWith("Microsoft", StringComparison.Ordinal) == true ||
                                      category?.StartsWith("System", StringComparison.Ordinal) == true
                                ? LogLevel.Warning
                                : LogLevel.Information);
#pragma warning restore CA1416
                    });
                }
                else if (SystemdHelpers.IsSystemdService())
                {
                    // journald already owns timestamps and presentation. The systemd formatter
                    // emits syslog priorities and intentionally does not add ANSI colors.
                    builder.Logging.AddSystemdConsole(options =>
                    {
                        options.IncludeScopes = false;
                        options.TimestampFormat = null;
                    });
                }
                else
                {
                    // Interactive console and Docker stdout use the small built-in console logger.
                    // Colors are handled by SimpleConsole and automatically disabled when needed.
                    builder.Logging.AddSimpleConsole(options =>
                    {
                        options.IncludeScopes = false;
                        options.SingleLine = true;
                        options.TimestampFormat = printTimestamps ? "[yyyy-MM-dd'T'HH:mm:ss.fffzzz] " : null;
                    });
                }

                builder.Services.Configure<HostOptions>(options =>
                {
                    options.ServicesStartConcurrently = false;
                });

                builder.Services.AddSingleton(sp => new TcpGlobalServer(
                    sp.GetRequiredService<ILogger<TcpGlobalServer>>(),
                    sp.GetRequiredService<IHostApplicationLifetime>(),
                    p.GetValue(portOpt),
                    TimeSpan.FromSeconds(p.GetValue(timeoutUnidentifiedOpt)),
                    TimeSpan.FromSeconds(p.GetValue(timeoutServerOpt)),
                    TimeSpan.FromSeconds(p.GetValue(timeoutClientOpt)),
                    TimeSpan.FromSeconds(p.GetValue(timeoutDirectPlayQueryOpt)),
                    p.GetValue(showHeartbeatMinimalOpt),
                    p.GetValue(hideFailedConnectionsOpt),
                    p.GetValue(verboseOpt)));
                builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TcpGlobalServer>());

                if (p.GetValue(websocketOpt))
                {
                    builder.Services.AddSingleton(sp => new WebSocketGlobalServer(
                        sp.GetRequiredService<TcpGlobalServer>(),
                        sp.GetRequiredService<ILogger<WebSocketGlobalServer>>(),
                        sp.GetRequiredService<IHostApplicationLifetime>(),
                        p.GetValue(websocketHostnameOpt)!,
                        p.GetValue(websocketPortOpt),
                        p.GetValue(websocketSslOpt)));
                    builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketGlobalServer>());
                }

                using var host = builder.Build();

                var infoVersion = Assembly.GetExecutingAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                var version = infoVersion ?? typeof(Program).Assembly.GetName().Version?.ToString();
                host.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("NewDarkGlobalServer")
                    .LogInformation("Starting {ApplicationName} {Version}", typeof(Program).Assembly.GetName().Name, version);

                await host.RunAsync();
            });

            var result = await root.Parse(args).InvokeAsync();
            return result != 0 ? result : Environment.ExitCode;
        }
    }
}