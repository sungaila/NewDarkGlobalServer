using Sungaila.NewDark.Core;
using Sungaila.NewDark.Tests.Infrastructure;
using System.Net.WebSockets;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GlobalServerSmokeTests
{
    private const ushort ProtocolVersion = 1100;
    private const string ServerName = "Loopback Server";
    private const string MapName = "Running Interference";

    [TestMethod]
    public async Task Tcp_ListRequestReturnsRegisteredGameServer()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var gameServer = await RegisterGameServerAsync(host);
        using var gameClient = await ConnectAndRequestServerListAsync(host);

        var serverInfo = await gameClient.ReceiveMessageAsync<ServerInfoMessage>();
        AssertServerInfo(serverInfo, ServerName, MapName, 5198, GameStateFlags.Open);
    }

    [TestMethod]
    public async Task Tcp_ClientObservesCompleteServerLifecycle()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var gameClient = await ConnectAndRequestServerListAsync(host);

        // No server is registered yet, so the initial list request has no response.
        await gameClient.AssertNoDataAsync();

        using var gameServer = await RegisterGameServerAsync(host);

        // A newly registered server is pushed to clients that already requested the list.
        var added = await gameClient.ReceiveMessageAsync<ServerInfoMessage>();
        AssertServerInfo(added, ServerName, MapName, 5198, GameStateFlags.Open);

        // A game that has started remains registered, but changes to the Closed state.
        await gameServer.SendAsync(CreateHeartbeat(
            ServerName,
            MapName,
            port: 5198,
            stateFlags: GameStateFlags.Closed));

        var closed = await gameClient.ReceiveMessageAsync<ServerInfoMessage>();
        AssertServerInfo(closed, ServerName, MapName, 5198, GameStateFlags.Closed);

        // Closed means the game has started, not that the global-server entry disappeared.
        // A client connecting now must still receive the server in its initial snapshot.
        using var lateClient = await ConnectAndRequestServerListAsync(host);
        var closedSnapshot = await lateClient.ReceiveMessageAsync<ServerInfoMessage>();
        AssertServerInfo(closedSnapshot, ServerName, MapName, 5198, GameStateFlags.Closed);

        // An unchanged heartbeat must not generate a duplicate ServerInfo broadcast.
        await gameServer.SendAsync(CreateHeartbeat(
            ServerName,
            MapName,
            port: 5198,
            stateFlags: GameStateFlags.Closed));
        await gameClient.AssertNoDataAsync();

        // ServerClosed removes the server and closes the game-server TCP connection.
        await gameServer.SendAsync(new ServerClosedMessage());

        var removed = await gameClient.ReceiveMessageAsync<RemoveServerMessage>();
        var lateRemoved = await lateClient.ReceiveMessageAsync<RemoveServerMessage>();
        Assert.AreEqual((ushort)5198, removed.Port);
        Assert.AreEqual("127.0.0.1", removed.ServerIP);
        Assert.AreEqual(removed, lateRemoved);
        await gameServer.AssertEofAsync();

        // The same client may request the list again; the removed server must be gone.
        await gameClient.SendAsync(new ListRequestMessage(ProtocolVersion));
        await gameClient.AssertNoDataAsync();
    }

    [TestMethod]
    public async Task Tcp_SnapshotAndLiveUpdatesAreBroadcastToMultipleClients()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var serverA = await RegisterGameServerAsync(host, "Server A", "map-a.mis", 5198);
        using var serverB = await RegisterGameServerAsync(host, "Server B", "map-b.mis", 5197);
        using var clientA = await ConnectAndRequestServerListAsync(host);
        using var clientB = await ConnectAndRequestServerListAsync(host);

        var snapshotA = await ReceiveServerInfosAsync(clientA, 2);
        var snapshotB = await ReceiveServerInfosAsync(clientB, 2);

        AssertSnapshotContains(snapshotA, "Server A", "map-a.mis", 5198);
        AssertSnapshotContains(snapshotA, "Server B", "map-b.mis", 5197);
        AssertSnapshotContains(snapshotB, "Server A", "map-a.mis", 5198);
        AssertSnapshotContains(snapshotB, "Server B", "map-b.mis", 5197);

        await serverA.SendAsync(CreateHeartbeat("Server A", "map-a2.mis", 5198, GameStateFlags.Open | GameStateFlags.Password));

        AssertServerInfo(await clientA.ReceiveMessageAsync<ServerInfoMessage>(), "Server A", "map-a2.mis", 5198, GameStateFlags.Open | GameStateFlags.Password);
        AssertServerInfo(await clientB.ReceiveMessageAsync<ServerInfoMessage>(), "Server A", "map-a2.mis", 5198, GameStateFlags.Open | GameStateFlags.Password);

        using var serverC = await RegisterGameServerAsync(host, "Server C", "map-c.mis", 5196);

        AssertServerInfo(await clientA.ReceiveMessageAsync<ServerInfoMessage>(), "Server C", "map-c.mis", 5196, GameStateFlags.Open);
        AssertServerInfo(await clientB.ReceiveMessageAsync<ServerInfoMessage>(), "Server C", "map-c.mis", 5196, GameStateFlags.Open);

        serverB.ShutdownAndClose();

        var removeA = await clientA.ReceiveMessageAsync<RemoveServerMessage>();
        var removeB = await clientB.ReceiveMessageAsync<RemoveServerMessage>();
        Assert.AreEqual((ushort)5197, removeA.Port);
        Assert.AreEqual((ushort)5197, removeB.Port);
    }

    [TestMethod]
    public async Task Tcp_GameServerDisconnectBroadcastsRemoveServer()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var gameServer = await RegisterGameServerAsync(host);
        using var gameClient = await ConnectAndRequestServerListAsync(host);

        _ = await gameClient.ReceiveMessageAsync<ServerInfoMessage>(); // initial ServerInfo
        gameServer.ShutdownAndClose();

        var remove = await gameClient.ReceiveMessageAsync<RemoveServerMessage>();
        Assert.AreEqual((ushort)5198, remove.Port);
        Assert.AreEqual("127.0.0.1", remove.ServerIP);
    }

    [TestMethod]
    public async Task Tcp_ClientExitClosesOnlyClientConnection()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var gameServer = await RegisterGameServerAsync(host);
        using var firstClient = await ConnectAndRequestServerListAsync(host);

        _ = await firstClient.ReceiveMessageAsync<ServerInfoMessage>();
        await firstClient.SendAsync(new ClientExitMessage(ExitReason.Quit));
        await firstClient.AssertEofAsync();

        // The game server remains registered and is visible to a new client.
        using var secondClient = await ConnectAndRequestServerListAsync(host);
        var serverInfo = await secondClient.ReceiveMessageAsync<ServerInfoMessage>();
        AssertServerInfo(serverInfo, ServerName, MapName, 5198, GameStateFlags.Open);
    }

    [TestMethod]
    [SuppressMessage("Style", "IDE0063")]
    public async Task Tcp_ConnectionRoleCannotChange()
    {
        await using var host = await GlobalServerTestHost.StartAsync();

        using (var gameClient = await ConnectAndRequestServerListAsync(host))
        {
            await gameClient.AssertNoDataAsync();
            await gameClient.SendAsync(CreateHeartbeat(ServerName, MapName, 5198, GameStateFlags.Open));
            await gameClient.AssertEofAsync();
        }

        using (var gameServer = await RegisterGameServerAsync(host))
        {
            await gameServer.SendAsync(new ListRequestMessage(ProtocolVersion));
            await gameServer.AssertEofAsync();
        }
    }

    [TestMethod]
    [SuppressMessage("Style", "IDE0063")]
    public async Task Tcp_UnsupportedProtocolVersionClosesConnection()
    {
        await using var host = await GlobalServerTestHost.StartAsync();

        using (var gameClient = await LoopbackPeer.ConnectAsync(host.TcpPort))
        {
            await gameClient.SendAsync(new ListRequestMessage((ushort)(ProtocolVersion + 1)));
            await gameClient.AssertEofAsync();
        }

        using (var gameServer = await LoopbackPeer.ConnectAsync(host.TcpPort))
        {
            await gameServer.SendAsync(CreateHeartbeat(ServerName, MapName, 5198, GameStateFlags.Open, (ushort)(ProtocolVersion + 1)));
            await gameServer.AssertEofAsync();
        }
    }

    [TestMethod]
    public async Task Tcp_InvalidMessageClosesConnectionCleanly()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var peer = await LoopbackPeer.ConnectAsync(host.TcpPort);

        await peer.SendAsync(new byte[] { 0xFF, 0xFF });

        await peer.AssertEofAsync();
    }

    [TestMethod]
    public async Task HostShutdown_ClosesTcpConnectionsWithoutRemoveServer()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var gameServer = await RegisterGameServerAsync(host);
        using var gameClient = await ConnectAndRequestServerListAsync(host);

        _ = await gameClient.ReceiveMessageAsync<ServerInfoMessage>(); // consume the initial ServerInfo

        await host.StopAsync();

        // If a RemoveServer message were sent during shutdown, this read would return message bytes
        // instead of EOF. The game-server socket must also be closed by the service.
        await gameClient.AssertEofAsync();
        await gameServer.AssertEofAsync();
    }

    [TestMethod]
    public async Task WebSocket_ReflectsTcpServerUpdatesAndRemoval()
    {
        await using var host = await GlobalServerTestHost.StartAsync(enableWebSocket: true);
        using var gameServer = await RegisterGameServerAsync(host);

        var openServers = await GetWebSocketServerListAsync(host);
        Assert.HasCount(1, openServers);
        AssertWebSocketServer(openServers.Single(), ServerName, MapName, WebSocketServerStatus.Denied);

        await gameServer.SendAsync(CreateHeartbeat(ServerName, "miss2.mis", 5198, GameStateFlags.Closed));
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await host.WaitForServerStateAsync(ServerName, "miss2.mis", GameStateFlags.Closed, cts.Token);
        }

        var closedServers = await GetWebSocketServerListAsync(host);
        Assert.HasCount(1, closedServers);
        AssertWebSocketServer(closedServers.Single(), ServerName, "miss2.mis", WebSocketServerStatus.Closed | WebSocketServerStatus.Denied);

        await gameServer.SendAsync(new ServerClosedMessage());
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await host.WaitForServerRemovalAsync(ServerName, cts.Token);
        }

        var removedServers = await GetWebSocketServerListAsync(host);
        Assert.HasCount(0, removedServers);
    }

    private static HeartbeatMessage CreateHeartbeat(
        string serverName,
        string mapName,
        ushort port,
        GameStateFlags stateFlags,
        ushort protocolVersion = ProtocolVersion) =>
        new(
            protocolVersion,
            new ServerInfo(
                Port: port,
                StateFlags: stateFlags,
                Reserved1: 0,
                Reserved2: 0,
                Reserved3: 0,
                GameId: Thief2GameId,
                ServerName: serverName,
                MapName: mapName));

    private static async Task<LoopbackPeer> RegisterGameServerAsync(
        GlobalServerTestHost host,
        string serverName = ServerName,
        string mapName = MapName,
        ushort port = 5198,
        GameStateFlags stateFlags = GameStateFlags.Open)
    {
        var gameServer = await LoopbackPeer.ConnectAsync(host.TcpPort);

        try
        {
            await gameServer.SendAsync(CreateHeartbeat(serverName, mapName, port, stateFlags));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.WaitForServerRegistrationAsync(serverName, cts.Token);
            return gameServer;
        }
        catch
        {
            gameServer.Dispose();
            throw;
        }
    }

    private static async Task<LoopbackPeer> ConnectAndRequestServerListAsync(GlobalServerTestHost host)
    {
        var gameClient = await LoopbackPeer.ConnectAsync(host.TcpPort);

        try
        {
            await gameClient.SendAsync(new ListRequestMessage(ProtocolVersion));
            return gameClient;
        }
        catch
        {
            gameClient.Dispose();
            throw;
        }
    }


    private static async Task<Dictionary<string, ServerInfoMessage>> ReceiveServerInfosAsync(LoopbackPeer client, int count)
    {
        var result = new Dictionary<string, ServerInfoMessage>(StringComparer.Ordinal);

        for (var i = 0; i < count; i++)
        {
            var message = await client.ReceiveMessageAsync<ServerInfoMessage>();
            result.Add(message.ServerInfo.ServerName, message);
        }

        return result;
    }

    private static void AssertSnapshotContains(
        Dictionary<string, ServerInfoMessage> snapshot,
        string serverName,
        string mapName,
        ushort port)
    {
        Assert.IsTrue(snapshot.TryGetValue(serverName, out var message), $"Snapshot does not contain '{serverName}'.");
        AssertServerInfo(message, serverName, mapName, port, GameStateFlags.Open);
    }

    private static void AssertServerInfo(
        ServerInfoMessage message,
        string serverName,
        string mapName,
        ushort port,
        GameStateFlags stateFlags)
    {
        Assert.AreEqual(serverName, message.ServerInfo.ServerName);
        Assert.AreEqual(mapName, message.ServerInfo.MapName);
        Assert.AreEqual(port, message.ServerInfo.Port);
        Assert.AreEqual(stateFlags, message.ServerInfo.StateFlags);
        Assert.AreEqual("127.0.0.1", message.ServerIP);
    }

    private static void AssertWebSocketServer(
        WebSocketServerInfo server,
        string serverName,
        string mapName,
        WebSocketServerStatus expectedStatus)
    {
        Assert.AreEqual(serverName, server.ServerName);
        Assert.AreEqual(mapName, server.MapName);
        Assert.AreEqual("127.0.***.***", server.Address);
        Assert.AreEqual(expectedStatus, server.Status);
    }

    private static async Task<List<WebSocketServerInfo>> GetWebSocketServerListAsync(GlobalServerTestHost host)
    {
        using var webSocket = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await webSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{host.WebSocketPort}/"), cts.Token);
        var json = await ReceiveWebSocketTextAsync(webSocket, cts.Token);
        var servers = JsonSerializer.Deserialize(json, SourceGenerationContext.Default.ListWebSocketServerInfo);

        if (servers is null)
        {
            Assert.Fail("WebSocket server returned null JSON.");
            return [];
        }

        return servers;
    }

    private static async Task<string> ReceiveWebSocketTextAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                throw new EndOfStreamException("WebSocket closed before the server list was received.");

            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}