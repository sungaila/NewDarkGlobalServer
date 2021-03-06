using System;
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
        static bool ParseStartupArgs(string[] args, ref int port, ref TimeSpan timeoutServer, ref TimeSpan timeoutClient, ref TimeSpan timeoutUnidentified, ref bool hideVerbose, ref bool showheartbeatminimal)
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith("-port="))
                {
                    if (int.TryParse(arg["-port=".Length..], out var number))
                    {
                        port = number;
                    }
                }
                else if (arg.StartsWith("-timeoutserver="))
                {
                    if (int.TryParse(arg["-timeoutserver=".Length..], out var seconds))
                    {
                        timeoutServer = TimeSpan.FromSeconds(seconds);
                    }
                }
                else if (arg.StartsWith("-timeoutclient="))
                {
                    if (int.TryParse(arg["-timeoutclient=".Length..], out var seconds))
                    {
                        timeoutClient = TimeSpan.FromSeconds(seconds);
                    }
                }
                else if (arg.StartsWith("-timeoutunidentified="))
                {
                    if (int.TryParse(arg["-timeoutunidentified=".Length..], out var seconds))
                    {
                        timeoutUnidentified = TimeSpan.FromSeconds(seconds);
                    }
                }
                else if (arg.StartsWith("-hideverbose"))
                {
                    hideVerbose = true;
                }
                else if (arg.StartsWith("-showheartbeatminimal"))
                {
                    showheartbeatminimal = true;
                }
                else if (arg.StartsWith("-help"))
                {
                    Console.WriteLine();
                    Console.WriteLine("Syntax:");
                    Console.WriteLine();
                    Console.WriteLine("    NewDarkGlobalServer [-port=<n>] [-hideverbose] [-showheartbeatminimal] [-timeoutserver=<n>] [-timeoutclient=<n>] [-timeoutunidentified=<n>] [-help]");
                    Console.WriteLine();

                    Console.WriteLine("Arguments:");
                    Console.WriteLine();

                    Console.WriteLine("-port=<n>");
                    Console.WriteLine("    Sets the port for this global server. Default is 5199.");
                    Console.WriteLine();

                    Console.WriteLine("-timeoutserver=<n>");
                    Console.WriteLine("    Sets timeout for game servers in seconds. Default is 180 (3 minutes).");
                    Console.WriteLine();

                    Console.WriteLine("-timeoutclient=<n>");
                    Console.WriteLine("    Sets timeout for game clients in seconds. Default is 3600 (1 hour).");
                    Console.WriteLine();

                    Console.WriteLine("-timeoutunidentified=<n>");
                    Console.WriteLine("    Sets timeout for game clients in seconds. Default is 10 (seconds).");
                    Console.WriteLine();

                    Console.WriteLine("-hideverbose");
                    Console.WriteLine("    Hides some of the verbose messages in the log.");
                    Console.WriteLine();

                    Console.WriteLine("-showheartbeatminimal");
                    Console.WriteLine("    Shows HeartbeatMinimal messages in the log. Each connected game server sends one every 10 seconds.");
                    Console.WriteLine();

                    Console.WriteLine("-help");
                    Console.WriteLine("    Prints this helpful argument list.");
                    Console.WriteLine();
                    return false;
                }
                else
                {
                    Console.WriteLine($"Unknown argument {arg}. Use -help for a list of available arguments.");
                    return false;
                }
            }

            return true;
        }

        public static async Task Main(string[] args)
        {
            Console.Title = "Thief 2 Multiplayer Global Server";
            Console.WriteLine($"Starting {typeof(Program).Assembly.GetName().Name} {typeof(Program).Assembly.GetName().Version}");

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            if (!ParseStartupArgs(args, ref Port, ref ServerConnectionTimeout, ref ClientConnectionTimeout, ref UnidentifiedConnectionTimeout, ref HideVerbose, ref ShowHeartbeatMinimal))
            {
                return;
            }

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
                ErrorWriteLine("Failed to bind");
                ErrorWriteLine(ex.ToString());
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

                    LogWriteLine("Connection accepted", $"for {clientSocket.RemoteEndPoint}");

                    newConnection.Task = Task.Factory.StartNew(async () => await HandleConnectionAsync(clientSocket, newConnection, cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current);
                }
                catch (SocketException ex)
                {
                    ErrorWriteLine("Failed to establish connection");
                    ErrorWriteLine(ex.ToString());
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
                                ErrorWriteLine($"{typeof(ListRequestMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status != ConnectionStatus.NewAndUnidentified)
                            {
                                if (connection.Status == ConnectionStatus.AwaitClientCommand)
                                    ErrorWriteLine("Game client sent ListRequestMessage more than once", $"({socket.RemoteEndPoint})");
                                else if (connection.Status == ConnectionStatus.AwaitServerCommand)
                                    ErrorWriteLine("Game server sent ListRequestMessage (message is client only)", $"({socket.RemoteEndPoint})");

                                return;
                            }

                            connection.Status = ConnectionStatus.AwaitClientCommand;

                            var listRequest = new ListRequestMessage(buffer);
                            LogWriteLine(typeof(ListRequestMessage).Name, $"received from {socket.RemoteEndPoint}");

                            if (listRequest.ProtocolVersion < SupportedProtocolVersion)
                            {
                                ErrorWriteLine($"Game client sent a higher ProtocolVersion ({listRequest.ProtocolVersion}) than supported ({SupportedProtocolVersion})", $"({socket.RemoteEndPoint})");
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

                                LogWriteLine(serverInfoMessage.GetType().Name, $"sent to {socket.RemoteEndPoint}", $"(\"{serverInfoMessage.ServerInfo.ServerName}\", {serverInfoMessage.ServerIP}, \"{serverInfoMessage.ServerInfo.MapName}\", {serverInfoMessage.ServerInfo.StateFlags})");
                            }

                            break;

                        case MessageType.Heartbeat:
                            if (length > 88 || length < 48)
                            {
                                ErrorWriteLine($"{typeof(HeartbeatMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status == ConnectionStatus.AwaitClientCommand)
                            {
                                ErrorWriteLine("Game client sent HeartbeatMessage (message is server only)", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            connection.Status = ConnectionStatus.AwaitServerCommand;

                            var heartbeat = new HeartbeatMessage(buffer);
                            connection.ServerInfo = heartbeat.ServerInfo;
                            LogWriteLine(typeof(HeartbeatMessage).Name, $"received from {socket.RemoteEndPoint}", $"(\"{heartbeat.ServerInfo.ServerName}\", \"{heartbeat.ServerInfo.MapName}\", {heartbeat.ServerInfo.StateFlags})");

                            if (heartbeat.ProtocolVersion < SupportedProtocolVersion)
                            {
                                ErrorWriteLine($"Game server sent a higher ProtocolVersion ({heartbeat.ProtocolVersion}) than supported ({SupportedProtocolVersion})", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            ConnectionsWriteLine(_connections.Values);

                            await NotifyServerAddOrUpdate(connection, cancellationToken);
                            break;

                        case MessageType.HeartbeatMinimal:
                            if (length != 2)
                            {
                                ErrorWriteLine($"{typeof(HeartbeatMinimalMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status == ConnectionStatus.AwaitClientCommand)
                            {
                                ErrorWriteLine("Game client sent HeartbeatMinimalMessage (message is server only)", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (ShowHeartbeatMinimal)
                                LogWriteLine(typeof(HeartbeatMinimalMessage).Name, $"received from {socket.RemoteEndPoint}");
                            break;

                        // this message seems to be unused
                        case MessageType.ClientExit:
                            if (length != 3)
                            {
                                ErrorWriteLine($"{typeof(ClientExitMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status != ConnectionStatus.AwaitClientCommand)
                            {
                                if (connection.Status == ConnectionStatus.AwaitServerCommand)
                                    ErrorWriteLine("Game server sent ClientExitMessage (message is client only)", $"({socket.RemoteEndPoint})");
                                else if (connection.Status == ConnectionStatus.NewAndUnidentified)
                                    ErrorWriteLine("Unidentified connetion sent ClientExitMessage (message is client only)", $"({socket.RemoteEndPoint})");

                                return;
                            }

                            var clientExit = new ClientExitMessage(buffer);
                            LogWriteLine(typeof(ClientExitMessage).Name, $"received from {socket.RemoteEndPoint}", $"({clientExit.ExitReason})");
                            return;

                        // this message seems to be unused
                        case MessageType.ServerClosed:
                            if (length != 2)
                            {
                                ErrorWriteLine($"{typeof(ServerClosedMessage).Name} received has an invalid length", $"({socket.RemoteEndPoint})");
                                return;
                            }

                            if (connection.Status != ConnectionStatus.AwaitClientCommand)
                            {
                                if (connection.Status == ConnectionStatus.AwaitClientCommand)
                                    ErrorWriteLine("Game client sent ServerClosedMessage (message is server only)", $"({socket.RemoteEndPoint})");
                                else if (connection.Status == ConnectionStatus.NewAndUnidentified)
                                    ErrorWriteLine("Unidentified connetion sent ServerClosedMessage (message is server only)", $"({socket.RemoteEndPoint})");

                                return;
                            }

                            LogWriteLine(typeof(ServerClosedMessage).Name, $"received from {socket.RemoteEndPoint}");
                            return;

                        case 0:
                            if (!IsSocketAlive(socket))
                            {
                                LogWriteLine("Connection lost", $"with {socket.RemoteEndPoint}");
                                return;
                            }
                            goto default;

                        default:
                            ErrorWriteLine("Unknown message type was received", $"({socket.RemoteEndPoint})");
                            return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }

            }
            catch (SocketException ex) when (ex.ErrorCode == (int)SocketError.OperationAborted || ex.ErrorCode != (int)SocketError.ConnectionAborted) { }
            catch (SocketException ex)
            {
                ErrorWriteLine("Failed receiving message", $"from {connection.InitialEndPoint.Address}");
                ErrorWriteLine(ex.ToString());
            }
            catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException) { }
            catch (Exception ex)
            {
                ErrorWriteLine("Failed handling message", $"from {connection.InitialEndPoint.Address}");
                ErrorWriteLine(ex.ToString());
            }
            finally
            {
                if (socket.Connected)
                    LogWriteLine("Connection closed", $"for {connection.InitialEndPoint}");

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

                    LogWriteLine(message.GetType().Name, $"sent to {connection.Socket.RemoteEndPoint}");
                }
            }
            catch (SocketException ex) when (ex.ErrorCode == (int)SocketError.OperationAborted || ex.ErrorCode != (int)SocketError.ConnectionAborted) { }
            catch (SocketException ex)
            {
                ErrorWriteLine("Failed broadcasting to clients");
                ErrorWriteLine(ex.ToString());
            }
            catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException) { }
            catch (Exception ex)
            {
                ErrorWriteLine("Failed broadcasting to clients");
                ErrorWriteLine(ex.ToString());
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

            if (wasConnected)
                ConnectionsWriteLine(_connections.Values);
        }
    }
}