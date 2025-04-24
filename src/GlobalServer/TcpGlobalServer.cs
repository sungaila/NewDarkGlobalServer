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
    /// <param name="ShowHeartbeatMinimal">If <see cref="HeartbeatMinimalMessage"/> should be logged.</param>
    /// <param name="HideInvalidMessageTypes">If failed connections due to invalid message types should be logged.</param>
    internal sealed class TcpGlobalServer(
        int Port,
        TimeSpan UnidentifiedConnectionTimeout,
        TimeSpan ServerConnectionTimeout,
        TimeSpan ClientConnectionTimeout,
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
        /// The interval for <see cref="HandleCleanupAsync(CancellationToken)"/> to run.
        /// </summary>
        private readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// A thread-safe collection of all connections.
        /// </summary>
        private readonly ConcurrentDictionary<string, Connection> _connections = new();

        /// <summary>
        /// Decides if the given <paramref name="socket"/> is still alive. This isn't 100% reliable though.
        /// </summary>
        /// <param name="socket">The socket given for the alive check.</param>
        private static bool IsSocketAlive(Socket socket) => socket != null && socket.Connected && socket.Poll(1000, SelectMode.SelectRead) && socket.Available != 0;

        public IEnumerable<Connection> ServerConnections => _connections.Values.Where(c => c.ServerInfo != null);

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

            var cleanupTask = Task.Factory.StartNew(async () => await HandleCleanupAsync(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Current);

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

                    newConnection.Task = Task.Factory.StartNew(async () => await HandleConnectionAsync(clientSocket, newConnection, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Current);
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
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

                while (socket.Connected)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var buffer = new byte[NetworkBufferSize];

                    var length = await socket.ReceiveAsync(buffer, default, cancellationToken);

                    connection.LastActivity = DateTimeOffset.Now;

                    switch ((MessageType)buffer[0..2].ShortToHostOrder())
                    {
                        case MessageType.ListRequest:
                            if (length != 4)
                            {
                                ErrorWriteLine(connection.Id, $"{typeof(ListRequestMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status != ConnectionStatus.NewAndUnidentified)
                            {
                                if (connection.Status == ConnectionStatus.AwaitClientCommand)
                                    ErrorWriteLine(connection.Id, "Game client sent ListRequestMessage more than once", $"({socket.RemoteEndPoint})");
                                else if (connection.Status == ConnectionStatus.AwaitServerCommand)
                                    ErrorWriteLine(connection.Id, "Game server sent ListRequestMessage (message is client only)", $"({socket.RemoteEndPoint})");

                                return;
                            }

                            connection.Status = ConnectionStatus.AwaitClientCommand;

                            var listRequest = new ListRequestMessage(buffer);
                            LogWriteLine(connection.Id, typeof(ListRequestMessage).Name, $"received from {socket.RemoteEndPoint}");

                            if (listRequest.ProtocolVersion > SupportedProtocolVersion)
                            {
                                ErrorWriteLine(connection.Id, $"Game client sent a higher ProtocolVersion ({listRequest.ProtocolVersion}) than supported ({SupportedProtocolVersion})", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            ConnectionsWriteLine(_connections.Values);

                            foreach (var otherConnection in _connections.Values.ToList())
                            {
                                if (otherConnection == connection ||
                                    otherConnection.ServerInfo == null)
                                {
                                    continue;
                                }

                                var serverInfoMessage = new ServerInfoMessage(otherConnection.ServerInfo.Value, otherConnection.InitialEndPoint.Address.ToString());
                                await socket.SendAsync(serverInfoMessage.ToByteArray(), default, cancellationToken);

                                LogWriteLine(connection.Id, serverInfoMessage.GetType().Name, $"sent to {socket.RemoteEndPoint}", $"(\"{serverInfoMessage.ServerInfo.ServerName}\", {serverInfoMessage.ServerIP}, \"{serverInfoMessage.ServerInfo.MapName}\", {serverInfoMessage.ServerInfo.StateFlags})");
                            }

                            break;

                        case MessageType.Heartbeat:
                            // ServerInfo has either empty strings (min length) or full strings (30 chars server name, 30 chars map name; max length)
                            if (length < 28 || length > 88)
                            {
                                ErrorWriteLine(connection.Id, $"{typeof(HeartbeatMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status == ConnectionStatus.AwaitClientCommand)
                            {
                                ErrorWriteLine(connection.Id, "Game client sent HeartbeatMessage (message is server only)", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            connection.Status = ConnectionStatus.AwaitServerCommand;

                            var heartbeat = new HeartbeatMessage(buffer);
                            connection.ServerInfo = heartbeat.ServerInfo;
                            LogWriteLine(connection.Id, typeof(HeartbeatMessage).Name, $"received from {socket.RemoteEndPoint}", $"(\"{heartbeat.ServerInfo.ServerName}\", \"{heartbeat.ServerInfo.MapName}\", {heartbeat.ServerInfo.StateFlags})");

                            if (heartbeat.ProtocolVersion < SupportedProtocolVersion)
                            {
                                ErrorWriteLine(connection.Id, $"Game server sent a higher ProtocolVersion ({heartbeat.ProtocolVersion}) than supported ({SupportedProtocolVersion})", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            ConnectionsWriteLine(_connections.Values);

                            await NotifyServerAddOrUpdate(connection, cancellationToken);
                            break;

                        case MessageType.HeartbeatMinimal:
                            if (length != 2)
                            {
                                ErrorWriteLine(connection.Id, $"{typeof(HeartbeatMinimalMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status == ConnectionStatus.AwaitClientCommand)
                            {
                                ErrorWriteLine(connection.Id, "Game client sent HeartbeatMinimalMessage (message is server only)", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (ShowHeartbeatMinimal)
                                LogWriteLine(connection.Id, typeof(HeartbeatMinimalMessage).Name, $"received from {socket.RemoteEndPoint}");
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

                            var clientExit = new ClientExitMessage(buffer);
                            LogWriteLine(connection.Id, typeof(ClientExitMessage).Name, $"received from {socket.RemoteEndPoint}", $"({clientExit.ExitReason})");
                            return;

                        // this message seems to be unused
                        case MessageType.ServerClosed:
                            if (length != 2)
                            {
                                ErrorWriteLine(connection.Id, $"{typeof(ServerClosedMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status != ConnectionStatus.AwaitClientCommand)
                            {
                                if (connection.Status == ConnectionStatus.AwaitClientCommand)
                                    ErrorWriteLine(connection.Id, "Game client sent ServerClosedMessage (message is server only)", $"({socket.RemoteEndPoint})");
                                else if (connection.Status == ConnectionStatus.NewAndUnidentified)
                                    ErrorWriteLine(connection.Id, "Unidentified connetion sent ServerClosedMessage (message is server only)", $"({socket.RemoteEndPoint})");

                                return;
                            }

                            LogWriteLine(connection.Id, typeof(ServerClosedMessage).Name, $"received from {socket.RemoteEndPoint}");
                            return;

                        case 0:
                            if (!IsSocketAlive(socket))
                            {
                                LogWriteLine(connection.Id, "Connection closed", $"with {socket.RemoteEndPoint}");
                                connection.Status = ConnectionStatus.Closed;
                                return;
                            }
                            goto default;

                        default:
                            if (!HideInvalidMessageTypes)
                            {
                                CleanDelayed(connection.Id);
                                ErrorWriteLine(connection.Id, "Unknown message type was received", $"({socket.RemoteEndPoint})");
                            }

                            connection.Status = ConnectionStatus.InvalidMessageType;
                            return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }

            }
            catch (SocketException ex) when (ex.ErrorCode == (int)SocketError.OperationAborted || ex.ErrorCode != (int)SocketError.ConnectionAborted) { }
            catch (SocketException ex)
            {
                ErrorWriteLine(connection.Id, "Failed receiving message", $"from {connection.InitialEndPoint.Address}");
                ErrorWriteLine(connection.Id, ex.ToString());
            }
            catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException) { }
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

        private async Task NotifyServerRemoval(Connection removedServer, CancellationToken cancellationToken = default)
        {
            if (removedServer.Status != ConnectionStatus.AwaitServerCommand || removedServer.ServerInfo == null || !removedServer.Socket.Connected)
                return;

            await BroadcastToClients(
                new RemoveServerMessage(
                    (ushort)removedServer.InitialEndPoint.Port,
                    removedServer.InitialEndPoint.Address.ToString()),
                cancellationToken);
        }

        private async Task BroadcastToClients(IMessage message, CancellationToken cancellationToken = default)
        {
            try
            {
                foreach (var connection in _connections.Values.Where(c => c.Status == ConnectionStatus.AwaitClientCommand &&
                    c.Socket != null &&
                    c.Socket.Connected).ToList())
                {
                    await connection.Socket.SendAsync(message.ToByteArray(), default, cancellationToken);
                    connection.LastActivity = DateTimeOffset.Now;

                    LogWriteLine(connection.Id, message.GetType().Name, $"sent to {connection.Socket.RemoteEndPoint}");
                }
            }
            catch (SocketException ex) when (ex.ErrorCode == (int)SocketError.OperationAborted || ex.ErrorCode != (int)SocketError.ConnectionAborted) { }
            catch (SocketException ex)
            {
                ErrorWriteLine(default, "Failed broadcasting to clients");
                ErrorWriteLine(default, ex.ToString());
            }
            catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException) { }
            catch (Exception ex)
            {
                ErrorWriteLine(default, "Failed broadcasting to clients");
                ErrorWriteLine(default, ex.ToString());
            }
        }

        private async Task HandleCleanupAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    foreach (var connection in _connections.Values.ToList())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (connection.Status != ConnectionStatus.Closed &&
                            connection.Task != null &&
                            connection.Socket != null &&
                            connection.Socket.Connected)
                        {
                            var timeSinceLastActivity = DateTimeOffset.Now.Subtract(connection.LastActivity);

                            // game clients will not send messages except for the first request and when exiting
                            // so the timeout should be set rather high
                            if (connection.Status == ConnectionStatus.AwaitClientCommand)
                            {
                                if (timeSinceLastActivity < ClientConnectionTimeout)
                                    continue;
                            }
                            // game servers send heartbeats every 10 seconds (unless a cutscene is playing)
                            // this timeout should compensate a potential cutscene
                            else if (connection.Status == ConnectionStatus.AwaitServerCommand)
                            {
                                if (timeSinceLastActivity < ServerConnectionTimeout)
                                    continue;
                            }
                            // new connections should send their first message ASAP
                            // this timeout should be set short
                            else if (timeSinceLastActivity < UnidentifiedConnectionTimeout)
                            {
                                continue;
                            }
                        }

                        LogWriteLine($"Connection timeout: {connection.InitialEndPoint}");
                        await DisconnectAsync(connection, cancellationToken);
                    }

                    await Task.Delay(CleanupInterval, cancellationToken);
                }
            }
            catch (SocketException ex) when (ex.ErrorCode == (int)SocketError.OperationAborted || ex.ErrorCode != (int)SocketError.ConnectionAborted) { }
            catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException) { }
        }

        private async Task DisconnectAsync(Connection connection, CancellationToken cancellationToken = default)
        {
            await NotifyServerRemoval(connection, cancellationToken);
            var wasConnected = connection.Socket.Connected;

            connection.Status = ConnectionStatus.Closed;
            connection.Socket?.Close();
            _connections.TryRemove(connection.InitialEndPoint.ToString(), out _);

            if (wasConnected && (connection.Status != ConnectionStatus.InvalidMessageType || !HideInvalidMessageTypes))
                ConnectionsWriteLine(_connections.Values);

            CleanDelayed(connection.Id);
        }
    }
}
