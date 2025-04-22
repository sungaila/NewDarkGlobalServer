using Mono.Options;
using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static NewDarkGlobalServer.Logging;
using static NewDarkGlobalServer.Messages;
using static NewDarkGlobalServer.States;

namespace NewDarkGlobalServer
{
    public static partial class Program
    {
        public static async Task Main(string[] args)
        {
            Console.Title = "Thief 2 Multiplayer Global Server";

            bool showHelp = false;

            var options = new OptionSet {
                { "p|port=", $"Sets the port for this global server. Default is {Port.ToString(CultureInfo.InvariantCulture)}.", (int p) => Port = p },
                { "s|timeoutserver=", $"Sets timeout for game servers in seconds. Default is {ServerConnectionTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds ({ServerConnectionTimeout:c}).", (int s) => ServerConnectionTimeout = TimeSpan.FromSeconds(s) },
                { "c|timeoutclient=", $"Sets timeout for game clients in seconds. Default is {ClientConnectionTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds ({ClientConnectionTimeout:c}).", (int c) => ClientConnectionTimeout = TimeSpan.FromSeconds(c) },
                { "u|timeoutunidentified=", $"Sets timeout for connections to indentify as client or server in seconds. Default is {UnidentifiedConnectionTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds ({UnidentifiedConnectionTimeout:c}).", (int u) => UnidentifiedConnectionTimeout = TimeSpan.FromSeconds(u) },
                { "b|showheartbeatminimal", "Shows HeartbeatMinimal messages in the log. Each connected game server sends one every 10 seconds so the log may become cluttered.", b => ShowHeartbeatMinimal = b != null },
                { "f|hidefailedconn", "Hides failed connections attempts (due to invalid or unknown messages) from the log.", f => HideInvalidMessageTypes = f != null },
                { "t|printtimestamps", "Adds timestamps to the log output.", f => PrintTimeStamps = f != null },
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

            Console.WriteLine($"Starting {typeof(Program).Assembly.GetName().Name} {typeof(Program).Assembly.GetName().Version}");

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var localEndPoint = new IPEndPoint(IPAddress.Any, Port);

            LogWriteLine($"Bind {localEndPoint} and await connections");

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                socket.Bind(localEndPoint);
                socket.Listen();
            }
            catch (Exception ex)
            {
                ErrorWriteLine(default, "Failed to bind");
                ErrorWriteLine(default, ex.ToString());
                throw;
            }

            var cleanupTask = Task.Factory.StartNew(async () => await HandleCleanupAsync(cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current);

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var clientSocket = await socket.AcceptAsync(cts.Token);
                    clientSocket.ReceiveBufferSize = NetworkBufferSize;
                    clientSocket.SendBufferSize = NetworkBufferSize;

                    if (_connections.TryGetValue(clientSocket.RemoteEndPoint!.ToString()!, out var existingConnection))
                    {
                        await DisconnectAsync(existingConnection, cts.Token);
                    }

                    var newConnection = new Connection(clientSocket);
                    _connections.TryAdd(newConnection.InitialEndPoint.ToString(), newConnection);

                    LogWriteLineDelayed(newConnection.Id, "Connection accepted", $"for {clientSocket.RemoteEndPoint}");

                    newConnection.Task = Task.Factory.StartNew(async () => await HandleConnectionAsync(clientSocket, newConnection, cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current);
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

        static async Task HandleConnectionAsync(Socket socket, Connection connection, CancellationToken cancellationToken = default)
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

        static async Task NotifyServerAddOrUpdate(Connection addedOrUpdatedServer, CancellationToken cancellationToken = default)
        {
            if (addedOrUpdatedServer.Status != ConnectionStatus.AwaitServerCommand || addedOrUpdatedServer.ServerInfo == null)
                return;

            await BroadcastToClients(
                new ServerInfoMessage(
                    addedOrUpdatedServer.ServerInfo.Value,
                    addedOrUpdatedServer.InitialEndPoint.Address.ToString()),
                cancellationToken);
        }

        static async Task NotifyServerRemoval(Connection removedServer, CancellationToken cancellationToken = default)
        {
            if (removedServer.Status != ConnectionStatus.AwaitServerCommand || removedServer.ServerInfo == null || !removedServer.Socket.Connected)
                return;

            await BroadcastToClients(
                new RemoveServerMessage(
                    (ushort)removedServer.InitialEndPoint.Port,
                    removedServer.InitialEndPoint.Address.ToString()),
                cancellationToken);
        }

        static async Task BroadcastToClients(IMessage message, CancellationToken cancellationToken = default)
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

        static async Task HandleCleanupAsync(CancellationToken cancellationToken = default)
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

        static async Task DisconnectAsync(Connection connection, CancellationToken cancellationToken = default)
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