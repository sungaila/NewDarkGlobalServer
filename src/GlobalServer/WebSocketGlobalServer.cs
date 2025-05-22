using Sungaila.NewDark.Core;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WatsonWebsocket;
using static Sungaila.NewDark.Core.Messages;
using static Sungaila.NewDark.GlobalServer.Logging;

namespace Sungaila.NewDark.GlobalServer
{
    /// <summary>
    /// Represents a WebSocket server for the global server (non-game clients).
    /// </summary>
    /// <param name="hostname">The hostnames or IPs the WebSocket listens to.</param>
    /// <param name="port">The port the WebSocket uses.</param>
    /// <param name="ssl">If SSL is used for the WebSocket.</param>
    internal sealed class WebSocketGlobalServer(string hostname, int port, bool ssl)
    {
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            LogWriteLine($"Bind {hostname}:{port} (SSL: {ssl}) and await WebSocket connections");

            try
            {
                using var server = new WatsonWsServer(hostname, port, ssl) { EnableStatistics = false };

                server.ClientConnected += async (s, e) =>
                {
                    LogWriteLine(e.Client.Guid, "Connection accepted (WebSocket)", $"for {e.Client}");

                    var list = new List<WebSocketServerInfo>();

                    if (Program._tcp != null)
                    {
                        foreach (var con in Program._tcp.ServerConnections)
                        {
                            WebSocketServerStatus status = new();

                            if (con.ServerInfo!.Value.StateFlags.HasFlag(GameStateFlags.Closed))
                            {
                                status |= WebSocketServerStatus.Closed;
                            }

                            if (con.LastEnumResponse == null)
                            {
                                status |= WebSocketServerStatus.Denied;
                            }

                            var maskedIp = con.InitialEndPoint.Address.MapToIPv4().ToString();
                            var split = maskedIp.Split('.');
                            maskedIp = string.Join('.', split[..^2]);
                            maskedIp += ".***.***";

                            list.Add(new WebSocketServerInfo(
                                con.ServerInfo!.Value.ServerName,
                                con.ServerInfo!.Value.MapName,
                                maskedIp,
                                status,
                                con.LastEnumResponse?.CurrentPlayers,
                                con.LastEnumResponse?.MaxPlayers));
                        }
                    }

                    var message = JsonSerializer.Serialize(list, SourceGenerationContext.Default.ListWebSocketServerInfo);
                    await server.SendAsync(e.Client.Guid, message, token: cancellationToken);

                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    server.DisconnectClient(e.Client.Guid);
                };

                await server.StartAsync(cancellationToken);

                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (Exception ex)
            {
                ErrorWriteLine(default, "Failed to bind WebSocket");
                ErrorWriteLine(default, ex.ToString());
                throw;
            }
        }
    }
}
