using Sungaila.NewDark.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static Sungaila.NewDark.Core.Messages;
using static Sungaila.NewDark.GlobalServer.Logging;
using static Sungaila.NewDark.GlobalServer.States;

namespace Sungaila.NewDark.GlobalServer
{
    /// <summary>
    /// Represents a TCP socket server for the global server.
    /// </summary>
    /// <param name="Port">The port the global server uses.</param>
    /// <param name="UnidentifiedConnectionTimeout">The timeout for connections have not sent requests yet.</param>
    /// <param name="ServerConnectionTimeout">The timeout for game servers.</param>
    /// <param name="ClientConnectionTimeout">The timeout for game clients.</param>
    /// <param name="DirectPlayQueryTimeout">The timeout for DirectPlay 8 queries.</param>
    /// <param name="ShowHeartbeatMinimal">If <see cref="HeartbeatMinimalMessage"/> should be logged.</param>
    /// <param name="HideInvalidMessageTypes">If failed connections due to invalid message types should be logged.</param>
    internal sealed class TcpGlobalServer(
        int Port,
        TimeSpan UnidentifiedConnectionTimeout,
        TimeSpan ServerConnectionTimeout,
        TimeSpan ClientConnectionTimeout,
        TimeSpan DirectPlayQueryTimeout,
        bool ShowHeartbeatMinimal,
        bool HideInvalidMessageTypes)
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
        /// The port used for DirectPlay 8.
        /// </summary>
        private const ushort DirectPlayPort = 5198;

        /// <summary>
        /// The interval for <see cref="HandleCleanupAsync(CancellationToken)"/> to run.
        /// </summary>
        private readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// A thread-safe collection of all connections.
        /// </summary>
        private readonly ConcurrentDictionary<string, Connection> _connections = new();

        public IEnumerable<Connection> ServerConnections => _connections.Values.Where(c => c.Status == ConnectionStatus.AwaitServerCommand && c.ServerInfo != null);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var localEndPoint = new IPEndPoint(IPAddress.Any, Port);

            LogWriteLine($"Bind {localEndPoint} and await TCP connections");

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                socket.Bind(localEndPoint);
                socket.Listen();
            }
            catch (Exception ex)
            {
                ErrorWriteLine(default, "Failed to bind TCP");
                ErrorWriteLine(default, ex.ToString());
                throw;
            }

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

                    var newConnection = new Connection(clientSocket);
                    _connections.TryAdd(newConnection.InitialEndPoint.ToString(), newConnection);

                    LogWriteLineDelayed(newConnection.Id, "Connection accepted (TCP)", $"for {clientSocket.RemoteEndPoint}");

                    newConnection.Task = HandleConnectionAsync(clientSocket, newConnection, cancellationToken);
                }
                catch (SocketException ex)
                {
                    ErrorWriteLine(default, "Failed to establish connection");
                    ErrorWriteLine(default, ex.ToString());
                }
                catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    LogWriteLine("Server terminated. Shutting down ...");
                }
            }

            socket.Close();

            await cleanupTask;
            await Task.WhenAll(_connections.Where(c => c.Value.Task != null).Select(c => c.Value.Task!).ToList());

            LogWriteLine("Server stopped.");
            return;
        }

        private async Task HandleConnectionAsync(Socket socket, Connection connection, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);

                while (socket.Connected)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var buffer = new byte[NetworkBufferSize];

                    var length = await socket.ReceiveAsync(buffer, default, cancellationToken);

                    if (length == 0)
                    {
                        LogWriteLine(connection.Id, "Connection closed", $"with {connection.InitialEndPoint}");

                        return;
                    }

                    if (length < 2)
                    {
                        ErrorWriteLine(connection.Id, "Received message is shorter than the message header", $"({connection.InitialEndPoint})");

                        connection.Status = ConnectionStatus.InvalidMessageType;
                        return;
                    }

                    connection.LastActivity = DateTimeOffset.Now;

                    switch ((MessageType)buffer[0..2].ShortToHostOrder())
                    {
                        case MessageType.ListRequest:
                            if (length != 4)
                            {
                                ErrorWriteLine(connection.Id, $"{typeof(ListRequestMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status == ConnectionStatus.AwaitServerCommand)
                            {
                                ErrorWriteLine(connection.Id, "Game server sent ListRequestMessage (message is client only)", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            connection.Status = ConnectionStatus.AwaitClientCommand;

                            var listRequest = new ListRequestMessage(buffer[..length]);
                            LogWriteLine(connection.Id, typeof(ListRequestMessage).Name, $"received from {socket.RemoteEndPoint}");

                            if (listRequest.ProtocolVersion > SupportedProtocolVersion)
                            {
                                ErrorWriteLine(connection.Id, $"Game client sent a higher ProtocolVersion ({listRequest.ProtocolVersion}) than supported ({SupportedProtocolVersion})", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            ConnectionsWriteLine(_connections.Values);

                            foreach (var otherConnection in ServerConnections.ToList())
                            {
                                if (otherConnection == connection)
                                    continue;

                                var serverInfo = otherConnection.ServerInfo;

                                if (serverInfo is not { } value)
                                    continue;

                                var serverInfoMessage =
                                    new ServerInfoMessage(
                                        value,
                                        otherConnection.InitialEndPoint.Address.ToString());

                                await SendAllAsync(connection, serverInfoMessage.ToByteArray(), cancellationToken);

                                LogWriteLine(connection.Id, serverInfoMessage.GetType().Name, $"sent to {socket.RemoteEndPoint}", $"(\"{serverInfoMessage.ServerInfo.ServerName}\", {serverInfoMessage.ServerIP}, \"{serverInfoMessage.ServerInfo.MapName}\", {serverInfoMessage.ServerInfo.StateFlags})");
                            }

                            break;

                        case MessageType.Heartbeat:
                            // ServerInfo contains two null-terminated strings:
                            // up to 31 characters for the server name and
                            // up to 31 characters for the map name.
                            if (length < 28 || length > 90)
                            {
                                ErrorWriteLine(connection.Id, $"{typeof(HeartbeatMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status == ConnectionStatus.AwaitClientCommand)
                            {
                                ErrorWriteLine(connection.Id, "Game client sent HeartbeatMessage (message is server only)", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            var heartbeat = new HeartbeatMessage(buffer[..length]);

                            LogWriteLine(connection.Id, typeof(HeartbeatMessage).Name, $"received from {socket.RemoteEndPoint}", $"(\"{heartbeat.ServerInfo.ServerName}\", \"{heartbeat.ServerInfo.MapName}\", {heartbeat.ServerInfo.StateFlags})");

                            if (heartbeat.ProtocolVersion > SupportedProtocolVersion)
                            {
                                ErrorWriteLine(connection.Id, $"Game server sent a higher ProtocolVersion ({heartbeat.ProtocolVersion}) than supported ({SupportedProtocolVersion})", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            var notifyClients =
                                connection.ServerInfo is not { } previousServerInfo ||
                                previousServerInfo != heartbeat.ServerInfo;

                            connection.ServerInfo = heartbeat.ServerInfo;
                            connection.Status = ConnectionStatus.AwaitServerCommand;

                            ConnectionsWriteLine(_connections.Values);

                            if (notifyClients)
                                await NotifyServerAddOrUpdate(connection, cancellationToken);

                            await DirectPlayEnumQueryAsync(connection, cancellationToken);
                            break;

                        case MessageType.HeartbeatMinimal:
                            if (length != 2)
                            {
                                ErrorWriteLine(connection.Id, $"{typeof(HeartbeatMinimalMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status != ConnectionStatus.AwaitServerCommand || connection.ServerInfo == null)
                            {
                                ErrorWriteLine(connection.Id, "Game client sent HeartbeatMinimalMessage (message is server only)", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (ShowHeartbeatMinimal)
                                LogWriteLine(connection.Id, typeof(HeartbeatMinimalMessage).Name, $"received from {socket.RemoteEndPoint}");

                            await DirectPlayEnumQueryAsync(connection, cancellationToken);
                            break;

                        // this message seems to be unused
                        case MessageType.ClientExit:
                            if (length != 3)
                            {
                                ErrorWriteLine(connection.Id, $"{typeof(ClientExitMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status != ConnectionStatus.AwaitClientCommand)
                            {
                                if (connection.Status == ConnectionStatus.AwaitServerCommand)
                                    ErrorWriteLine(connection.Id, "Game server sent ClientExitMessage (message is client only)", $"({socket.RemoteEndPoint})");
                                else if (connection.Status == ConnectionStatus.NewAndUnidentified)
                                    ErrorWriteLine(connection.Id, "Unidentified connetion sent ClientExitMessage (message is client only)", $"({socket.RemoteEndPoint})");

                                return;
                            }

                            var clientExit = new ClientExitMessage(buffer[..length]);
                            LogWriteLine(connection.Id, typeof(ClientExitMessage).Name, $"received from {socket.RemoteEndPoint}", $"({clientExit.ExitReason})");
                            return;

                        // this message seems to be unused
                        case MessageType.ServerClosed:
                            if (length != 2)
                            {
                                ErrorWriteLine(connection.Id, $"{typeof(ServerClosedMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status != ConnectionStatus.AwaitServerCommand)
                            {
                                if (connection.Status == ConnectionStatus.AwaitClientCommand)
                                    ErrorWriteLine(connection.Id, "Game client sent ServerClosedMessage (message is server only)", $"({socket.RemoteEndPoint})");
                                else if (connection.Status == ConnectionStatus.NewAndUnidentified)
                                    ErrorWriteLine(connection.Id, "Unidentified connetion sent ServerClosedMessage (message is server only)", $"({socket.RemoteEndPoint})");

                                return;
                            }

                            LogWriteLine(connection.Id, typeof(ServerClosedMessage).Name, $"received from {socket.RemoteEndPoint}");
                            return;

                        default:
                            if (!HideInvalidMessageTypes)
                            {
                                CleanDelayed(connection.Id);
                                ErrorWriteLine(connection.Id, "Unknown message type was received", $"({socket.RemoteEndPoint})");
                            }

                            connection.Status = ConnectionStatus.InvalidMessageType;
                            return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                }

            }
            catch (SocketException ex) when (ex.ErrorCode == (int)SocketError.OperationAborted || ex.ErrorCode == (int)SocketError.ConnectionAborted) { }
            catch (SocketException ex)
            {
                ErrorWriteLine(connection.Id, "Failed receiving message", $"from {connection.InitialEndPoint.Address}");
                ErrorWriteLine(connection.Id, ex.ToString());
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ErrorWriteLine(connection.Id, "Failed handling message", $"from {connection.InitialEndPoint.Address}");
                ErrorWriteLine(connection.Id, ex.ToString());
            }
            finally
            {
                if (connection.Status != ConnectionStatus.Closed && (connection.Status != ConnectionStatus.InvalidMessageType || !HideInvalidMessageTypes))
                    LogWriteLine(connection.Id, "Connection lost", $"for {connection.InitialEndPoint}");

                await DisconnectAsync(connection, cancellationToken);
            }
        }

        private async Task NotifyServerAddOrUpdate(Connection addedOrUpdatedServer, CancellationToken cancellationToken = default)
        {
            if (addedOrUpdatedServer.Status != ConnectionStatus.AwaitServerCommand || addedOrUpdatedServer.ServerInfo == null)
                return;

            await BroadcastToClients(
                new ServerInfoMessage(
                    addedOrUpdatedServer.ServerInfo.Value,
                    addedOrUpdatedServer.InitialEndPoint.Address.ToString()),
                cancellationToken);
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

                    LogWriteLine(connection.Id, message.GetType().Name, $"sent to {connection.Socket.RemoteEndPoint}");
                }
                catch (TaskCanceledException) { }
                catch (OperationCanceledException) { }
                catch (SocketException ex) when (ex.ErrorCode == (int)SocketError.OperationAborted || ex.ErrorCode == (int)SocketError.ConnectionAborted || ex.ErrorCode == (int)SocketError.ConnectionReset) { }
                catch (Exception ex)
                {
                    ErrorWriteLine(default, "Failed broadcast to client");
                    ErrorWriteLine(default, ex.ToString());
                }
            }
        }

        private static async Task SendAllAsync(Connection connection, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await connection.SendLock.WaitAsync(cancellationToken);

            try
            {
                var offset = 0;

                while (offset < data.Length)
                {
                    var sent = await connection.Socket.SendAsync(data[offset..], cancellationToken);

                    if (sent == 0)
                    {
                        throw new SocketException((int)SocketError.ConnectionReset);
                    }

                    offset += sent;
                }
            }
            finally
            {
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

                            LogWriteLine($"Connection timeout: {connection.InitialEndPoint}");

                            await DisconnectAsync(connection, cancellationToken);
                        }
                    }
                    catch (SocketException ex) when (ex.ErrorCode == (int)SocketError.OperationAborted || ex.ErrorCode == (int)SocketError.ConnectionAborted) { }
                }
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
        }

        private async Task DisconnectAsync(Connection connection, CancellationToken cancellationToken = default)
        {
            if (!connection.TryBeginDisconnect())
                return;

            var previousStatus = connection.Status;
            // Capture before clearing. Non-null means this connection successfully registered a game server.
            var serverInfo = connection.ServerInfo;

            connection.Status = ConnectionStatus.Closed;
            connection.ServerInfo = null;

            // Remove from the public server/client collection first.
            // A simultaneous ListRequest must no longer see this server.
            _connections.TryRemove(connection.InitialEndPoint.ToString(), out _);

            if (serverInfo is { } registeredServer)
            {
                await NotifyServerRemoval(connection, registeredServer, cancellationToken);
            }

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

            if (previousStatus != ConnectionStatus.InvalidMessageType || !HideInvalidMessageTypes)
            {
                ConnectionsWriteLine(_connections.Values);
            }

            CleanDelayed(connection.Id);
        }

        private async Task DirectPlayEnumQueryAsync(Connection connection, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DirectPlayQueryTimeout);

            try
            {
                using var client = new UdpClient(connection.InitialEndPoint.Address.ToString(), DirectPlayPort);

                var request = new SessionEnumerationQuery();
                await client.SendAsync(request.ToByteArray(), timeoutCts.Token);

                var response = await client.ReceiveAsync(timeoutCts.Token);

                if (response.Buffer.Length < 92)
                {
                    connection.LastEnumResponse = null;
                    return;
                }

                var parsed = new SessionEnumerationResponse(response.Buffer);

                if (parsed.LeadByte != 0x00 || parsed.CommandByte != 0x03 || parsed.EnumPayload != 0x67D1 || parsed.ApplicationDescSize != 0x50 || parsed.ApplicationGUID != Thief2GameId)
                {
                    connection.LastEnumResponse = null;
                    return;
                }

                connection.LastEnumResponse = parsed;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // keep the last successful enumeration response
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
            catch
            {
                connection.LastEnumResponse = null;
            }
        }
    }
}