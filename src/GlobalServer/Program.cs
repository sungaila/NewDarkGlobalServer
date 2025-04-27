using Mono.Options;
using System;
using System.Collections.Generic;
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

        public static async Task Main(string[] args)
        {
            Console.Title = "Thief 2 Multiplayer Global Server";

            bool showHelp = false;
            int port = 5199;
            bool websocket = false;
            string websocketHostname = "localhost";
            int websocketPort = 5200;
            bool websocketSsl = false;
            TimeSpan unidentifiedConnectionTimeout = TimeSpan.FromSeconds(10);
            TimeSpan serverConnectionTimeout = TimeSpan.FromMinutes(3);
            TimeSpan clientConnectionTimeout = TimeSpan.FromHours(1);
            bool showHeartbeatMinimal = false;
            bool hideInvalidMessageTypes = false;

            var options = new OptionSet {
                { "p|port=", $"Sets the port for this global server. Default is {port.ToString(CultureInfo.InvariantCulture)}.", (int p) => port = p },
                { "s|timeoutserver=", $"Sets timeout for game servers in seconds. Default is {serverConnectionTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds ({serverConnectionTimeout:c}).", (int s) => serverConnectionTimeout = TimeSpan.FromSeconds(s) },
                { "c|timeoutclient=", $"Sets timeout for game clients in seconds. Default is {clientConnectionTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds ({clientConnectionTimeout:c}).", (int c) => clientConnectionTimeout = TimeSpan.FromSeconds(c) },
                { "u|timeoutunidentified=", $"Sets timeout for connections to indentify as client or server in seconds. Default is {unidentifiedConnectionTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds ({unidentifiedConnectionTimeout:c}).", (int u) => unidentifiedConnectionTimeout = TimeSpan.FromSeconds(u) },
                { "b|showheartbeatminimal", "Shows HeartbeatMinimal messages in the log. Each connected game server sends one every 10 seconds so the log may become cluttered.", b => showHeartbeatMinimal = b != null },
                { "f|hidefailedconn", "Hides failed connections attempts (due to invalid or unknown messages) from the log.", f => hideInvalidMessageTypes = f != null },
                { "t|printtimestamps", "Adds timestamps to the log output.", f => PrintTimeStamps = f != null },
                { "w|websocket", $"Activates the optional WebSocket for non-game clients. Deactivated by default.", b => websocket = b != null },
                { "n|websockethostname=", $"Sets the hostname for the WebSocket. Default is {websocketHostname.ToString(CultureInfo.InvariantCulture)}.", (string h) => websocketHostname = h },
                { "m|websocketport=", $"Sets the port for the WebSocket. Default is {websocketPort.ToString(CultureInfo.InvariantCulture)}.", (int p) => websocketPort = p },
                { "e|websocketssl", $"Activates SSL for the WebSocket. Deactivated by default.", b => websocketSsl = b != null },
                { "v|verbose", "Shows more verbose messages in the log.", v => Verbose = v != null },
                { "h|help", "Prints this helpful option list and exits.", h => showHelp = h != null },
            };

            try
            {
                options.Parse(args);
            }
            catch (OptionException ex)
            {
                ErrorWriteLine(default, ex.Message, "Use --help for more information.");
                throw;
            }

            if (showHelp)
            {
                Console.WriteLine($"Usage: {typeof(Program).Assembly.GetName().Name} [options]");
                Console.WriteLine("Starts a server providing a game server list for Thief 2 Multiplayer.");
                Console.WriteLine();

                Console.WriteLine("Options:");
                options.WriteOptionDescriptions(Console.Out);
                return;
            }

            var infoVersion = Assembly.GetExecutingAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Console.WriteLine($"Starting {typeof(Program).Assembly.GetName().Name} {infoVersion ?? typeof(Program).Assembly.GetName().Version?.ToString()}");

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            _tcp = new TcpGlobalServer(
                port,
                unidentifiedConnectionTimeout,
                serverConnectionTimeout,
                clientConnectionTimeout,
                showHeartbeatMinimal,
                hideInvalidMessageTypes);

            var tasks = new List<Task>
            {
                _tcp.RunAsync(cts.Token)
            };

            if (websocket)
            {
                _webSocket = new WebSocketGlobalServer(websocketHostname, websocketPort, websocketSsl);
                tasks.Add(_webSocket.RunAsync(cts.Token));
            }

            await Task.WhenAll(tasks);
        }
    }
}