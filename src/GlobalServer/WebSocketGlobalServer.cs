using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sungaila.NewDark.Core;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WatsonWebsocket;

namespace Sungaila.NewDark.GlobalServer
{
    /// <summary>
    /// Represents a WebSocket server for the global server (non-game clients).
    /// </summary>
    /// <param name="tcpGlobalServer">The TCP global server used as the source for the server list.</param>
    /// <param name="logger">The logger used by the WebSocket server.</param>
    /// <param name="applicationLifetime">The host lifetime used for WebSocket request cancellation during shutdown.</param>
    /// <param name="hostname">The hostnames or IPs the WebSocket listens to.</param>
    /// <param name="port">The port the WebSocket uses.</param>
    /// <param name="ssl">If SSL is used for the WebSocket.</param>
    internal sealed class WebSocketGlobalServer(TcpGlobalServer tcpGlobalServer, ILogger<WebSocketGlobalServer> logger, IHostApplicationLifetime applicationLifetime, string hostname, int port, bool ssl) : BackgroundService
    {
        private WatsonWsServer? _server;

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Bind {Hostname}:{Port} (SSL: {Ssl}) and await WebSocket connections", hostname, port, ssl);

            var server = new WatsonWsServer(hostname, port, ssl) { EnableStatistics = false };
            server.ClientConnected += async (s, e) =>
            {
                try
                {
                    logger.LogInformation("Connection accepted (WebSocket) for {Client}", e.Client);

                    var list = tcpGlobalServer.GetWebSocketServerList();

                    var message = JsonSerializer.Serialize(list, SourceGenerationContext.Default.ListWebSocketServerInfo);
                    await server.SendAsync(e.Client.Guid, message, token: applicationLifetime.ApplicationStopping);

                    await Task.Delay(TimeSpan.FromSeconds(10), applicationLifetime.ApplicationStopping);
                }
                catch (OperationCanceledException) when (applicationLifetime.ApplicationStopping.IsCancellationRequested)
                {
                    // Normal host shutdown.
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed handling WebSocket client {Client}", e.Client);
                }
                finally
                {
                    try
                    {
                        server.DisconnectClient(e.Client.Guid);
                    }
                    catch (Exception ex)
                    {
                        if (!applicationLifetime.ApplicationStopping.IsCancellationRequested)
                            logger.LogWarning(ex, "Failed closing WebSocket client {Client}", e.Client);
                    }
                }
            };

            try
            {
                // WatsonWsServer.StartAsync performs the actual listener startup. Await it before
                // the hosted service reports StartAsync completion so systemd READY=1 is not early.
                await server.StartAsync(cancellationToken);
                _server = server;
                await base.StartAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                server.Dispose();
                _server = null;
                throw;
            }
            catch (Exception ex)
            {
                server.Dispose();
                _server = null;
                logger.LogCritical(ex, "Failed to start WebSocket server on {Hostname}:{Port} (SSL: {Ssl})", hostname, port, ssl);
                Environment.ExitCode = 1;
                throw;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal host shutdown.
            }
            finally
            {
                _server?.Dispose();
                _server = null;
                logger.LogInformation("WebSocket server stopped");
            }
        }
    }
}