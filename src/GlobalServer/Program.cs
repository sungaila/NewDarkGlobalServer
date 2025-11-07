using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static Sungaila.NewDark.GlobalServer.Logging;

namespace Sungaila.NewDark.GlobalServer
{
    public static partial class Program
    {
        internal static TcpGlobalServer? _tcp = null;
        internal static WebSocketGlobalServer? _webSocket = null;

        public static async Task<int> Main(string[] args)
        {
            Console.Title = "Thief 2 Multiplayer Global Server";

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

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

            var hideInvalidMessageTypesOpt = new Option<bool>("--hidefailedconn", "-f", "--hide-failed-conn")
            {
                DefaultValueFactory = _ => false,
                Description = "Hide failed connection attempts (due to invalid or unknown messages) from the log"
            };

            var printTimestampsOpt = new Option<bool>("--printtimestamps", "-t", "--print-timestamps")
            {
                DefaultValueFactory = _ => false,
                Description = "Add timestamps to the log output"
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
            root.Options.Add(timeoutUnidentifiedOpt);
            root.Options.Add(showHeartbeatMinimalOpt);
            root.Options.Add(hideInvalidMessageTypesOpt);
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
                bool sslSpecified = sslRes is not null && sslRes.Implicit  == false;

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
                var infoVersion = Assembly.GetExecutingAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                Console.WriteLine($"Starting {typeof(Program).Assembly.GetName().Name} {infoVersion ?? typeof(Program).Assembly.GetName().Version?.ToString()}");

                Verbose = p.GetValue(verboseOpt);
                PrintTimeStamps = p.GetValue(printTimestampsOpt);

                _tcp = new TcpGlobalServer(
                    p.GetValue(portOpt),
                    TimeSpan.FromSeconds(p.GetValue(timeoutUnidentifiedOpt)),
                    TimeSpan.FromSeconds(p.GetValue(timeoutServerOpt)),
                    TimeSpan.FromSeconds(p.GetValue(timeoutClientOpt)),
                    p.GetValue(showHeartbeatMinimalOpt),
                    p.GetValue(hideInvalidMessageTypesOpt));

                var tasks = new List<Task> { _tcp.RunAsync(cts.Token) };

                if (p.GetValue(websocketOpt))
                {
                    _webSocket = new WebSocketGlobalServer(p.GetValue(websocketHostnameOpt)!, p.GetValue(websocketPortOpt), p.GetValue(websocketSslOpt));
                    tasks.Add(_webSocket.RunAsync(cts.Token));
                }

                await Task.WhenAll(tasks);
            });

            return await root
                .Parse(args)
                .InvokeAsync(cancellationToken: cts.Token);
        }
    }
}