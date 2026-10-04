using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sungaila.NewDark.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static Sungaila.NewDark.Core.Messages;
using static Sungaila.NewDark.GlobalServer.States;

namespace Sungaila.NewDark.GlobalServer
{
    /// <summary>
    /// Represents a TCP socket server for the global server.
    /// </summary>
    /// <param name="Logger">The logger used by the TCP server.</param>
    /// <param name="ApplicationLifetime">The host lifetime used to distinguish normal disconnects from global server shutdown.</param>
    /// <param name="Port">The port the global server uses.</param>
    /// <param name="UnidentifiedConnectionTimeout">The timeout for connections have not sent requests yet.</param>
    /// <param name="ServerConnectionTimeout">The timeout for game servers.</param>
    /// <param name="ClientConnectionTimeout">The timeout for game clients.</param>
    /// <param name="DirectPlayQueryTimeout">The timeout for DirectPlay 8 queries.</param>
    /// <param name="ShowHeartbeatMinimal">If <see cref="HeartbeatMinimalMessage"/> should be logged.</param>
    /// <param name="HideFailedConnections">If failed or unidentified connection attempts should be hidden from the log.</param>
    /// <param name="Verbose">If verbose protocol details should be included in log messages.</param>
    /// <param name="Clock">The clock used to expire cached DirectPlay responses.</param>
    internal sealed class TcpGlobalServer(
        ILogger<TcpGlobalServer> Logger,
        IHostApplicationLifetime ApplicationLifetime,
        int Port,
        TimeSpan UnidentifiedConnectionTimeout,
        TimeSpan ServerConnectionTimeout,
        TimeSpan ClientConnectionTimeout,
        TimeSpan DirectPlayQueryTimeout,
        bool ShowHeartbeatMinimal,
        bool HideFailedConnections,
        bool Verbose,
        TimeProvider? Clock = null) : BackgroundService
    {
        /// <summary>
        /// The expected maximum message size.
        /// </summary>
        private const int NetworkBufferSize = 256;

        /// <summary>
        /// The supported protocol version.
        /// </summary>
        private const ushort SupportedProtocolVersion = 1100;

        /// <summary>
        /// The interval for <see cref="HandleCleanupAsync(CancellationToken)"/> to run.
        /// </summary>
        private readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// A thread-safe collection of all connections.
        /// </summary>
        private readonly ConcurrentDictionary<string, Connection> _connections = new();

        // Keep initial lists and subsequent add/update/remove messages in protocol order.
        private readonly SemaphoreSlim _serverListLock = new(1, 1);

        private Socket? _listenerSocket;

        public IEnumerable<Connection> ServerConnections => _connections.Values.Where(c => c.Status == ConnectionStatus.AwaitServerCommand && c.ServerInfo != null);

        private bool ShouldHideFailedConnection(Connection connection) =>
            HideFailedConnections && !connection.WasIdentified;

        private static bool IsExpectedConnectionTermination(SocketException ex) =>
            ex.SocketErrorCode is SocketError.OperationAborted or SocketError.ConnectionAborted or SocketError.ConnectionReset;

        private void LogConnectionAccepted(Connection connection)
        {
            if (connection.TryMarkAcceptedLogged())
                Logger.LogInformation("Connection accepted (TCP) for {RemoteEndPoint}", connection.InitialEndPoint);
        }

        private bool PrepareConnectionLog(Connection connection)
        {
            if (ShouldHideFailedConnection(connection))
                return false;

            if (connection.WasIdentified)
                LogConnectionAccepted(connection);

            return true;
        }

        private void LogConnections()
        {
            var currentConnections = _connections.Values.Where(c => !c.IsDisconnected).ToList();
            var connectionCount = currentConnections.Count;
            var serverOpenCount = currentConnections.Count(c => c.TryGetServerSnapshot(out var info, out _) && !info.StateFlags.HasFlag(GameStateFlags.Closed));
            var serverClosedCount = currentConnections.Count(c => c.TryGetServerSnapshot(out var info, out _) && info.StateFlags.HasFlag(GameStateFlags.Closed));
            var clientCount = currentConnections.Count(c => c.Status == ConnectionStatus.AwaitClientCommand);

            Logger.LogInformation(
                "Open connections: {ConnectionCount} ({OpenServerCount} open servers, {ClosedServerCount} closed servers, {ClientCount} clients)",
                connectionCount,
                serverOpenCount,
                serverClosedCount,
                clientCount);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            var localEndPoint = new IPEndPoint(IPAddress.Any, Port);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            Logger.LogInformation("Bind {LocalEndPoint} and await TCP connections", localEndPoint);

            try
            {
                socket.Bind(localEndPoint);
                socket.Listen();
                _listenerSocket = socket;

                // Complete the actual socket setup before the host startup completes.
                // With systemd Type=notify this ensures READY=1 is only sent after Bind/Listen succeeded.
                await base.StartAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _listenerSocket = null;
                socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                _listenerSocket = null;
                socket.Dispose();
                Logger.LogCritical(ex, "Failed to start TCP server on {LocalEndPoint}", localEndPoint);
                Environment.ExitCode = 1;
                throw;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            try
            {
                await RunServerAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal host shutdown.
            }
            catch
            {
                Environment.ExitCode = 1;
                throw;
            }
        }

        private async Task RunServerAsync(CancellationToken cancellationToken)
        {
            var socket = _listenerSocket ?? throw new InvalidOperationException("TCP listener was not initialized.");
            var cleanupTask = HandleCleanupAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var clientSocket = await socket.AcceptAsync(cancellationToken);
                    clientSocket.ReceiveBufferSize = NetworkBufferSize;
                    clientSocket.SendBufferSize = NetworkBufferSize;

                    if (_connections.TryGetValue(clientSocket.RemoteEndPoint!.ToString()!, out var existingConnection))
                    {
                        await DisconnectAsync(existingConnection, cancellationToken);
                    }

                    var newConnection = new Connection(clientSocket, Clock);
                    if (!_connections.TryAdd(newConnection.InitialEndPoint.ToString(), newConnection))
                    {
                        clientSocket.Dispose();
                        continue;
                    }

                    if (!HideFailedConnections)
                        LogConnectionAccepted(newConnection);

                    newConnection.Task = HandleConnectionAsync(clientSocket, newConnection, cancellationToken);
                }
                catch (SocketException ex)
                {
                    Logger.LogError(ex, "Failed to establish connection");
                }
                catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    Logger.LogInformation("Server terminated. Shutting down ...");
                }
            }

            socket.Close();
            _listenerSocket = null;

            await cleanupTask;
            await Task.WhenAll(_connections.Where(c => c.Value.Task != null).Select(c => c.Value.Task!).ToList());

            Logger.LogInformation("Server stopped.");
            return;
        }

        private async Task HandleConnectionAsync(Socket socket, Connection connection, CancellationToken cancellationToken = default)
        {
            try
            {
                var reader = new ClientMessageReader(socket);

                while (!connection.IsDisconnected)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var buffer = await reader.ReadAsync(cancellationToken);

                    if (buffer == null)
                    {
                        if (PrepareConnectionLog(connection))
                            Logger.LogInformation("Connection closed by {RemoteEndPoint}", connection.InitialEndPoint);

                        return;
                    }

                    var length = buffer.Length;
                    connection.LastActivity = DateTimeOffset.Now;
                    var messageType = (MessageType)buffer[0..2].ShortToHostOrder();

                    switch (messageType)
                    {
                        case MessageType.ListRequest:
                            if (length != 4)
                            {
                                if (PrepareConnectionLog(connection))
                                    Logger.LogWarning("{MessageType} received from {RemoteEndPoint} has invalid length {Length}; expected {ExpectedLength}", nameof(ListRequestMessage), connection.InitialEndPoint, length, 4);

                                return;
                            }

                            if (connection.Status == ConnectionStatus.AwaitServerCommand)
                            {
                                if (PrepareConnectionLog(connection))
                                    Logger.LogWarning("Game server {RemoteEndPoint} sent client-only {MessageType}", connection.InitialEndPoint, nameof(ListRequestMessage));

                                return;
                            }

                            var listRequest = new ListRequestMessage(buffer[..length]);

                            if (listRequest.ProtocolVersion > SupportedProtocolVersion)
                            {
                                if (PrepareConnectionLog(connection))
                                    Logger.LogWarning(
                                        "Game client {RemoteEndPoint} sent unsupported protocol version {ProtocolVersion}; maximum supported is {SupportedProtocolVersion}",
                                        connection.InitialEndPoint,
                                        listRequest.ProtocolVersion,
                                        SupportedProtocolVersion);

                                return;
                            }

                            await SendServerListAsync(connection, cancellationToken);

                            break;

                        case MessageType.Heartbeat:
                            // ServerInfo contains two null-terminated strings:
                            // up to 31 bytes for the server name and
                            // up to 31 bytes for the map name, regardless of text encoding.
                            if (length < 28 || length > 90)
                            {
                                if (PrepareConnectionLog(connection))
                                    Logger.LogWarning("{MessageType} received from {RemoteEndPoint} has invalid length {Length}; expected 28-90 bytes", nameof(HeartbeatMessage), connection.InitialEndPoint, length);

                                return;
                            }

                            if (connection.Status == ConnectionStatus.AwaitClientCommand)
                            {
                                if (PrepareConnectionLog(connection))
                                    Logger.LogWarning("Game client {RemoteEndPoint} sent server-only {MessageType}", connection.InitialEndPoint, nameof(HeartbeatMessage));

                                return;
                            }

                            var heartbeat = new HeartbeatMessage(buffer[..length]);

                            if (heartbeat.ProtocolVersion > SupportedProtocolVersion)
                            {
                                if (PrepareConnectionLog(connection))
                                    Logger.LogWarning(
                                        "Game server {RemoteEndPoint} sent unsupported protocol version {ProtocolVersion}; maximum supported is {SupportedProtocolVersion}",
                                        connection.InitialEndPoint,
                                        heartbeat.ProtocolVersion,
                                        SupportedProtocolVersion);

                                return;
                            }

                            if (!await UpdateServerAsync(connection, heartbeat.ServerInfo, cancellationToken))
                                return;

                            PrepareConnectionLog(connection);

                            if (Verbose)
                            {
                                Logger.LogInformation(
                                    "{MessageType} received from {RemoteEndPoint}: ServerName={ServerName}, MapName={MapName}, StateFlags={StateFlags}",
                                    nameof(HeartbeatMessage),
                                    connection.InitialEndPoint,
                                    heartbeat.ServerInfo.ServerName,
                                    heartbeat.ServerInfo.MapName,
                                    heartbeat.ServerInfo.StateFlags);
                            }
                            else
                            {
                                Logger.LogInformation("{MessageType} received from {RemoteEndPoint}", nameof(HeartbeatMessage), connection.InitialEndPoint);
                            }

                            LogConnections();

                            await DirectPlayEnumQueryAsync(connection, cancellationToken);
                            break;

                        case MessageType.HeartbeatMinimal:
                            if (length != 2)
                            {
                                if (PrepareConnectionLog(connection))
                                    Logger.LogWarning("{MessageType} received from {RemoteEndPoint} has invalid length {Length}; expected {ExpectedLength}", nameof(HeartbeatMinimalMessage), connection.InitialEndPoint, length, 2);

                                return;
                            }

                            if (connection.Status != ConnectionStatus.AwaitServerCommand || connection.ServerInfo == null)
                            {
                                if (PrepareConnectionLog(connection))
                                    Logger.LogWarning("Non-server connection {RemoteEndPoint} sent server-only {MessageType}", connection.InitialEndPoint, nameof(HeartbeatMinimalMessage));

                                return;
                            }

                            if (ShowHeartbeatMinimal)
                            {
                                PrepareConnectionLog(connection);
                                Logger.LogInformation("{MessageType} received from {RemoteEndPoint}", nameof(HeartbeatMinimalMessage), connection.InitialEndPoint);
                            }

                            await DirectPlayEnumQueryAsync(connection, cancellationToken);
                            break;

                        // this message seems to be unused
                        case MessageType.ClientExit:
                            if (length != 3)
                            {
                                if (PrepareConnectionLog(connection))
                                    Logger.LogWarning("{MessageType} received from {RemoteEndPoint} has invalid length {Length}; expected {ExpectedLength}", nameof(ClientExitMessage), connection.InitialEndPoint, length, 3);

                                return;
                            }

                            if (connection.Status != ConnectionStatus.AwaitClientCommand)
                            {
                                if (PrepareConnectionLog(connection))
                                {
                                    if (connection.Status == ConnectionStatus.AwaitServerCommand)
                                        Logger.LogWarning("Game server {RemoteEndPoint} sent client-only {MessageType}", connection.InitialEndPoint, nameof(ClientExitMessage));
                                    else if (connection.Status == ConnectionStatus.NewAndUnidentified)
                                        Logger.LogWarning("Unidentified connection {RemoteEndPoint} sent client-only {MessageType}", connection.InitialEndPoint, nameof(ClientExitMessage));
                                }

                                return;
                            }

                            var clientExit = new ClientExitMessage(buffer[..length]);
                            PrepareConnectionLog(connection);

                            if (Verbose)
                                Logger.LogInformation("{MessageType} received from {RemoteEndPoint}: ExitReason={ExitReason}", nameof(ClientExitMessage), connection.InitialEndPoint, clientExit.ExitReason);
                            else
                                Logger.LogInformation("{MessageType} received from {RemoteEndPoint}", nameof(ClientExitMessage), connection.InitialEndPoint);

                            return;

                        // this message seems to be unused
                        case MessageType.ServerClosed:
                            if (length != 2)
                            {
                                if (PrepareConnectionLog(connection))
                                    Logger.LogWarning("{MessageType} received from {RemoteEndPoint} has invalid length {Length}; expected {ExpectedLength}", nameof(ServerClosedMessage), connection.InitialEndPoint, length, 2);

                                return;
                            }

                            if (connection.Status != ConnectionStatus.AwaitServerCommand)
                            {
                                if (PrepareConnectionLog(connection))
                                {
                                    if (connection.Status == ConnectionStatus.AwaitClientCommand)
                                        Logger.LogWarning("Game client {RemoteEndPoint} sent server-only {MessageType}", connection.InitialEndPoint, nameof(ServerClosedMessage));
                                    else if (connection.Status == ConnectionStatus.NewAndUnidentified)
                                        Logger.LogWarning("Unidentified connection {RemoteEndPoint} sent server-only {MessageType}", connection.InitialEndPoint, nameof(ServerClosedMessage));
                                }

                                return;
                            }

                            PrepareConnectionLog(connection);
                            Logger.LogInformation("{MessageType} received from {RemoteEndPoint}", nameof(ServerClosedMessage), connection.InitialEndPoint);
                            return;

                        default:
                            if (PrepareConnectionLog(connection))
                                Logger.LogWarning("Unknown message type {MessageType} received from {RemoteEndPoint}", (ushort)messageType, connection.InitialEndPoint);

                            connection.Status = ConnectionStatus.InvalidMessageType;
                            return;
                    }

                }
            }
            catch (SocketException ex) when (IsExpectedConnectionTermination(ex)) { }
            catch (ObjectDisposedException) when (connection.IsDisconnected) { }
            catch (SocketException ex)
            {
                if (PrepareConnectionLog(connection))
                    Logger.LogWarning(ex, "Failed receiving message from {RemoteAddress}", connection.InitialEndPoint.Address);
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
            catch (IOException ex)
            {
                if (PrepareConnectionLog(connection))
                    Logger.LogWarning(ex, "Incomplete or invalid message from {RemoteEndPoint}", connection.InitialEndPoint);
            }
            catch (Exception ex)
            {
                if (PrepareConnectionLog(connection))
                    Logger.LogError(ex, "Failed handling message from {RemoteAddress}", connection.InitialEndPoint.Address);
            }
            finally
            {
                if (connection.Status != ConnectionStatus.Closed && PrepareConnectionLog(connection))
                    Logger.LogInformation("Connection lost for {RemoteEndPoint}", connection.InitialEndPoint);

                // Once the host is stopping, finish local socket cleanup without a canceled token.
                // DisconnectAsync suppresses RemoveServerMessage while ApplicationStopping is signaled.
                var cleanupToken = ApplicationLifetime.ApplicationStopping.IsCancellationRequested
                    ? CancellationToken.None
                    : cancellationToken;
                await DisconnectAsync(connection, cleanupToken);
            }
        }

        private async Task SendServerListAsync(Connection connection, CancellationToken cancellationToken)
        {
            await _serverListLock.WaitAsync(cancellationToken);

            try
            {
                if (connection.IsDisconnected)
                    return;

                connection.Status = ConnectionStatus.AwaitClientCommand;
                connection.MarkIdentified();
                PrepareConnectionLog(connection);
                Logger.LogInformation("{MessageType} received from {RemoteEndPoint}", nameof(ListRequestMessage), connection.InitialEndPoint);
                LogConnections();

                foreach (var otherConnection in _connections.Values)
                {
                    if (!otherConnection.TryGetServerSnapshot(out var serverInfo, out _))
                        continue;

                    var message = new ServerInfoMessage(serverInfo, otherConnection.InitialEndPoint.Address.ToString());
                    await SendAllAsync(connection, message.ToByteArray(), cancellationToken);

                    if (Verbose)
                    {
                        Logger.LogInformation(
                            "{MessageType} sent to {RemoteEndPoint}: ServerName={ServerName}, ServerIP={ServerIP}, MapName={MapName}, StateFlags={StateFlags}",
                            nameof(ServerInfoMessage), connection.InitialEndPoint, serverInfo.ServerName,
                            message.ServerIP, serverInfo.MapName, serverInfo.StateFlags);
                    }
                    else
                    {
                        Logger.LogInformation("{MessageType} sent to {RemoteEndPoint}", nameof(ServerInfoMessage), connection.InitialEndPoint);
                    }
                }
            }
            finally
            {
                _serverListLock.Release();
            }
        }

        private async Task<bool> UpdateServerAsync(Connection connection, ServerInfo serverInfo, CancellationToken cancellationToken)
        {
            await _serverListLock.WaitAsync(cancellationToken);

            try
            {
                // Reserved bytes are ignored by the reference implementation.
                serverInfo = serverInfo with { Reserved1 = 0, Reserved2 = 0, Reserved3 = 0 };

                if (!connection.TryUpdateServerInfo(serverInfo, out var previousServerInfo))
                    return false;

                connection.MarkIdentified();

                if (previousServerInfo is { } previous && previous.Port != serverInfo.Port)
                    await NotifyServerRemoval(connection, previous, cancellationToken);

                if (previousServerInfo != serverInfo)
                {
                    await BroadcastToClients(new ServerInfoMessage(
                        serverInfo, connection.InitialEndPoint.Address.ToString()), cancellationToken);
                }

                return true;
            }
            finally
            {
                _serverListLock.Release();
            }
        }

        public List<WebSocketServerInfo> GetWebSocketServerList()
        {
            var list = new List<WebSocketServerInfo>();

            foreach (var connection in _connections.Values)
            {
                if (!connection.TryGetServerSnapshot(out var serverInfo, out var enumResponse))
                    continue;

                var status = serverInfo.StateFlags.HasFlag(GameStateFlags.Closed)
                    ? WebSocketServerStatus.Closed
                    : default;

                if (enumResponse == null)
                    status |= WebSocketServerStatus.Denied;

                var split = connection.InitialEndPoint.Address.ToString().Split('.');
                var maskedIp = string.Join('.', split[..^2]) + ".***.***";

                list.Add(new WebSocketServerInfo(serverInfo.ServerName, serverInfo.MapName, maskedIp,
                    status, enumResponse?.CurrentPlayers, enumResponse?.MaxPlayers));
            }

            return list;
        }

        private Task NotifyServerRemoval(Connection removedServer, ServerInfo serverInfo, CancellationToken cancellationToken = default)
        {
            return BroadcastToClients(new RemoveServerMessage(
                    serverInfo.Port,
                    removedServer.InitialEndPoint.Address.ToString()),
                cancellationToken);
        }

        private async Task BroadcastToClients(IMessage message, CancellationToken cancellationToken = default)
        {
            var bytes = message.ToByteArray();

            foreach (var connection in _connections.Values.Where(c => c.Status == ConnectionStatus.AwaitClientCommand).ToList())
            {
                try
                {
                    await SendAllAsync(connection, bytes, cancellationToken);

                    if (PrepareConnectionLog(connection))
                        Logger.LogInformation("{MessageType} sent to {RemoteEndPoint}", message.GetType().Name, connection.InitialEndPoint);
                }
                catch (TaskCanceledException) { }
                catch (OperationCanceledException) { }
                catch (SocketException ex) when (IsExpectedConnectionTermination(ex)) { }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed broadcast to client");
                }
            }
        }

        private static async Task SendAllAsync(Connection connection, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendCts.CancelAfter(TimeSpan.FromSeconds(5));
            var lockTaken = false;

            try
            {
                await connection.SendLock.WaitAsync(sendCts.Token);
                lockTaken = true;
                var offset = 0;

                while (offset < data.Length)
                {
                    var sent = await connection.Socket.SendAsync(data[offset..], sendCts.Token);

                    if (sent == 0)
                    {
                        throw new SocketException((int)SocketError.ConnectionReset);
                    }

                    offset += sent;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A blocked client must not stall all list updates. A partial send cannot be retried
                // on the same stream because this protocol has no way to resynchronize messages.
                connection.Socket.Close();
                throw new TimeoutException("Client did not accept a protocol message within five seconds.");
            }
            finally
            {
                if (lockTaken)
                    connection.SendLock.Release();
            }
        }

        private async Task HandleCleanupAsync(CancellationToken cancellationToken = default)
        {
            using var timer = new PeriodicTimer(CleanupInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    try
                    {
                        foreach (var connection in _connections.Values.ToList())
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (connection.Status is ConnectionStatus.Closed or ConnectionStatus.InvalidMessageType)
                            {
                                await DisconnectAsync(connection, cancellationToken);
                                continue;
                            }

                            var timeout = connection.Status switch
                            {
                                ConnectionStatus.AwaitClientCommand => ClientConnectionTimeout,
                                ConnectionStatus.AwaitServerCommand => ServerConnectionTimeout,
                                _ => UnidentifiedConnectionTimeout
                            };

                            var timeSinceLastActivity = DateTimeOffset.Now - connection.LastActivity;

                            if (timeSinceLastActivity < timeout)
                                continue;

                            if (!ShouldHideFailedConnection(connection))
                                Logger.LogInformation("Connection timeout: {RemoteEndPoint}", connection.InitialEndPoint);

                            await DisconnectAsync(connection, cancellationToken);
                        }
                    }
                    catch (SocketException ex) when (IsExpectedConnectionTermination(ex)) { }
                }
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
        }

        private async Task DisconnectAsync(Connection connection, CancellationToken cancellationToken = default)
        {
            if (!connection.TryBeginDisconnect(out var serverInfo))
                return;

            // Only remove this instance: an old handler must not remove a replacement connection.
            _connections.TryRemove(new KeyValuePair<string, Connection>(connection.InitialEndPoint.ToString(), connection));

            // Local cleanup must complete even if broadcasting is canceled or fails.
            try
            {
                connection.Socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // The peer may already have closed/reset the socket.
            }
            catch (ObjectDisposedException)
            {
                // NOP
            }

            connection.Socket.Close();

            if (!ApplicationLifetime.ApplicationStopping.IsCancellationRequested && serverInfo is { } registeredServer)
            {
                await _serverListLock.WaitAsync(cancellationToken);

                try
                {
                    await NotifyServerRemoval(connection, registeredServer, cancellationToken);
                }
                finally
                {
                    _serverListLock.Release();
                }
            }

            if (!ShouldHideFailedConnection(connection))
            {
                LogConnections();
            }

        }

        private async Task DirectPlayEnumQueryAsync(Connection connection, CancellationToken cancellationToken)
        {
            if (!connection.TryGetServerSnapshot(out var serverInfo, out _))
                return;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DirectPlayQueryTimeout);

            try
            {
                using var client = new UdpClient(connection.InitialEndPoint.Address.ToString(), serverInfo.Port);

                var request = new SessionEnumerationQuery();
                await client.SendAsync(request.ToByteArray(), timeoutCts.Token);

                var response = await client.ReceiveAsync(timeoutCts.Token);

                if (response.Buffer.Length < 92)
                {
                    connection.SetEnumResponse(serverInfo, null);
                    return;
                }

                var parsed = new SessionEnumerationResponse(response.Buffer);

                if (parsed.LeadByte != 0x00 || parsed.CommandByte != 0x03 || parsed.EnumPayload != 0x67D1 || parsed.ApplicationDescSize != 0x50 || parsed.ApplicationGUID != Thief2GameId)
                {
                    connection.SetEnumResponse(serverInfo, null);
                    return;
                }

                connection.SetEnumResponse(serverInfo, parsed);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Brief packet loss is tolerated; Connection expires successful responses after 30 seconds.
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
            catch
            {
                connection.SetEnumResponse(serverInfo, null);
            }
        }
    }
}