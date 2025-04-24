using Sungaila.NewDark.Core;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WatsonWebsocket;
using static Sungaila.NewDark.Core.Messages;
using static Sungaila.NewDark.GlobalServer.Logging;

namespace Sungaila.NewDark.GlobalServer
{
    internal sealed class WebSocketGlobalServer
    {
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var localEndPoint = new IPEndPoint(IPAddress.Loopback, 5200);

            LogWriteLine($"Bind {localEndPoint} and await WebSocket connections");

            try
            {
                using var server = new WatsonWsServer(localEndPoint.Address.ToString(), localEndPoint.Port);

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

                            list.Add(new WebSocketServerInfo(
                                con.ServerInfo!.Value.ServerName,
                                con.ServerInfo!.Value.MapName,
                                con.InitialEndPoint.Address.ToString(),
                                status,
                                null,
                                null));
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
