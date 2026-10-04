using Sungaila.NewDark.Core;
using Sungaila.NewDark.Tests.Infrastructure;
using System.Net.Sockets;
using System.Text;
using static Sungaila.NewDark.Core.Messages;
using static Sungaila.NewDark.GlobalServer.States;

namespace Sungaila.NewDark.Tests;

public sealed partial class GlobalServerSmokeTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task Tcp_ListRequestMayBeFragmented(int splitAt)
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var gameServer = await RegisterGameServerAsync(host);
        using var client = await LoopbackPeer.ConnectAsync(host.TcpPort);
        var bytes = new ListRequestMessage(ProtocolVersion).ToByteArray();

        await client.SendAsync(bytes.AsMemory(0, splitAt));
        await client.AssertNoDataAsync(TimeSpan.FromMilliseconds(100));
        await client.SendAsync(bytes.AsMemory(splitAt));

        AssertServerInfo(await client.ReceiveMessageAsync<ServerInfoMessage>(), ServerName, MapName, 5198, GameStateFlags.Open);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(26)]
    [DataRow(57)]
    [DataRow(58)]
    [DataRow(89)]
    public async Task Tcp_FragmentedHeartbeatAndCoalescedCommandsKeepTheirBoundaries(int splitAt)
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var client = await ConnectAndRequestServerListAsync(host);
        using var gameServer = await LoopbackPeer.ConnectAsync(host.TcpPort);
        var serverName = new string('S', 31);
        var mapName = new string('M', 31);
        var bytes = CreateHeartbeat(serverName, mapName, 5198, GameStateFlags.Open).ToByteArray();

        await gameServer.SendAsync(bytes.AsMemory(0, splitAt));
        await gameServer.AssertNoDataAsync(TimeSpan.FromMilliseconds(100));
        await gameServer.SendAsync(bytes[splitAt..]
            .Concat(new HeartbeatMinimalMessage().ToByteArray())
            .Concat(new ServerClosedMessage().ToByteArray()).ToArray());

        AssertServerInfo(await client.ReceiveMessageAsync<ServerInfoMessage>(), serverName, mapName, 5198, GameStateFlags.Open);
        Assert.AreEqual((ushort)5198, (await client.ReceiveMessageAsync<RemoveServerMessage>()).Port);
        await gameServer.AssertEofAsync();
    }

    [TestMethod]
    public async Task Tcp_CoalescedRequestsLargerThanReceiveBufferAllGetResponses()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var gameServer = await RegisterGameServerAsync(host);
        using var client = await LoopbackPeer.ConnectAsync(host.TcpPort);
        var bytes = Enumerable.Range(0, 100).SelectMany(_ => new ListRequestMessage(ProtocolVersion).ToByteArray()).ToArray();
        await client.SendAsync(bytes);

        for (var i = 0; i < 100; i++)
            AssertServerInfo(await client.ReceiveMessageAsync<ServerInfoMessage>(), ServerName, MapName, 5198, GameStateFlags.Open);

        await client.AssertNoDataAsync(TimeSpan.FromMilliseconds(100));
    }

    [TestMethod]
    public async Task Tcp_CoalescedHeartbeatsLargerThanReceiveBufferAreProcessedInOrder()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var client = await ConnectAndRequestServerListAsync(host);
        using var gameServer = await LoopbackPeer.ConnectAsync(host.TcpPort);
        var name = new string('S', 31);
        var bytes = Enumerable.Range(0, 3)
            .SelectMany(i => CreateHeartbeat(name, new string((char)('A' + i), 31), 5198, GameStateFlags.Open).ToByteArray())
            .Concat(new ServerClosedMessage().ToByteArray()).ToArray();
        await gameServer.SendAsync(bytes);

        for (var i = 0; i < 3; i++)
            AssertServerInfo(await client.ReceiveMessageAsync<ServerInfoMessage>(), name, new string((char)('A' + i), 31), 5198, GameStateFlags.Open);

        _ = await client.ReceiveMessageAsync<RemoveServerMessage>();
        await gameServer.AssertEofAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Tcp_OverlongHeartbeatStringIsRejectedWithoutWaitingForMoreData(bool mapName)
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var gameServer = await LoopbackPeer.ConnectAsync(host.TcpPort);
        var prefix = CreateHeartbeat("S", "M", 5198, GameStateFlags.Open).ToByteArray()[..26];
        var bytes = prefix.Concat(mapName ? new byte[] { (byte)'S', 0 } : [])
            .Concat(Enumerable.Repeat((byte)'X', 32)).ToArray();
        await gameServer.SendAsync(bytes);
        await gameServer.AssertEofAsync();
        Assert.IsFalse(host.TcpServer.ServerConnections.Any());
    }

    [TestMethod]
    public async Task Tcp_EofDuringMessageClosesConnectionWithoutRegisteringServer()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var gameServer = await LoopbackPeer.ConnectAsync(host.TcpPort);
        await gameServer.SendAsync(CreateHeartbeat(ServerName, MapName, 5198, GameStateFlags.Open).ToByteArray()[..20]);
        gameServer.Socket.Shutdown(SocketShutdown.Send);
        await gameServer.AssertEofAsync();
        Assert.IsFalse(host.TcpServer.ServerConnections.Any());
    }

    [TestMethod]
    public async Task Tcp_ForwardsEightBitNamesAndTheirChanges()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var client = await ConnectAndRequestServerListAsync(host);
        using var gameServer = await RegisterGameServerAsync(host, "H\u00e4\u00df", "m\u0080.mis");
        AssertServerInfo(await client.ReceiveMessageAsync<ServerInfoMessage>(), "H\u00e4\u00df", "m\u0080.mis", 5198, GameStateFlags.Open);

        await gameServer.SendAsync(CreateHeartbeat("H\u00f6\u00df", "m\u00ff.mis", 5198, GameStateFlags.Open));
        AssertServerInfo(await client.ReceiveMessageAsync<ServerInfoMessage>(), "H\u00f6\u00df", "m\u00ff.mis", 5198, GameStateFlags.Open);
    }

    [TestMethod]
    [DataRow(850, "Gr\u00fc\u00dfe\u00b3", "entr\u00e9e.mis")]
    [DataRow(1250, "\u0141\u00f3d\u017a \u015al\u0105sk", "\u017c\u00f3\u0142\u0107.mis")]
    [DataRow(1251, "\u0420\u0443\u0441\u0441\u043a\u0438\u0439", "\u0434\u043e\u043c.mis")]
    [DataRow(65001, "\u0420\u0443\u0441\u0441\u043a\u0438\u0439 \U0001f5dd", "\u0434\u043e\u043c.mis")]
    public async Task TcpAndWebSocket_PreserveNamesAcrossEncodings(int codePage, string serverName, string mapName)
    {
        var clientEncoding = codePage == 65001
            ? Encoding.UTF8
            : CodePagesEncodingProvider.Instance.GetEncoding(codePage)!;
        var nameBytes = clientEncoding.GetBytes(serverName);
        var mapBytes = clientEncoding.GetBytes(mapName);
        var heartbeat = CreateRawHeartbeat(nameBytes, mapBytes);

        await using var host = await GlobalServerTestHost.StartAsync(enableWebSocket: true);
        using var client = await ConnectAndRequestServerListAsync(host);
        using var gameServer = await LoopbackPeer.ConnectAsync(host.TcpPort);
        // The first send splits a UTF-8 character for UTF-8 clients. Framing must wait for NUL,
        // independently of the encoding and the number of characters represented by the bytes.
        await gameServer.SendAsync(heartbeat.AsMemory(0, 27));
        await client.AssertNoDataAsync(TimeSpan.FromMilliseconds(100));
        await gameServer.SendAsync(heartbeat.AsMemory(27));
        AssertForwardedHeartbeat(await client.ReceiveMessageBytesAsync(), heartbeat);

        var webServer = (await GetWebSocketServerListAsync(host)).Single();
        Assert.AreSequenceEqual(nameBytes, Encoding.Latin1.GetBytes(webServer.ServerName));
        Assert.AreSequenceEqual(mapBytes, Encoding.Latin1.GetBytes(webServer.MapName));
        Assert.AreEqual(serverName, GameText.Decode(webServer.ServerName, (GameTextEncoding)codePage));
        Assert.AreEqual(mapName, GameText.Decode(webServer.MapName, (GameTextEncoding)codePage));
        if (codePage == 65001)
            Assert.AreEqual(serverName, GameText.Decode(webServer.ServerName));

        var updatedHeartbeat = CreateRawHeartbeat(clientEncoding.GetBytes(serverName + "!"), mapBytes);
        await gameServer.SendAsync(new HeartbeatMinimalMessage().ToByteArray().Concat(updatedHeartbeat).ToArray());
        AssertForwardedHeartbeat(await client.ReceiveMessageBytesAsync(), updatedHeartbeat);

        // Decoding the web view must not alter the bytes sent to clients requesting the initial list.
        using var lateClient = await ConnectAndRequestServerListAsync(host);
        AssertForwardedHeartbeat(await lateClient.ReceiveMessageBytesAsync(), updatedHeartbeat);

        var updatedWebServer = (await GetWebSocketServerListAsync(host)).Single();
        Assert.AreEqual(serverName + "!", GameText.Decode(updatedWebServer.ServerName, (GameTextEncoding)codePage));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Tcp_Preserves31ByteUtf8NamesIncludingIncompleteSequences(bool incomplete)
    {
        var nameBytes = Encoding.UTF8.GetBytes(new string('\u0416', 15))
            .Append(incomplete ? (byte)0xD0 : (byte)'!').ToArray();
        var mapBytes = incomplete
            ? Encoding.ASCII.GetBytes(new string('M', 28)).Concat(new byte[] { 0xF0, 0x9F, 0x97 }).ToArray()
            : Encoding.UTF8.GetBytes(new string('\u754c', 10) + "!");
        Assert.HasCount(31, nameBytes);
        Assert.HasCount(31, mapBytes);
        var heartbeat = CreateRawHeartbeat(nameBytes, mapBytes);

        await using var host = await GlobalServerTestHost.StartAsync();
        using var client = await ConnectAndRequestServerListAsync(host);
        using var gameServer = await LoopbackPeer.ConnectAsync(host.TcpPort);
        await gameServer.SendAsync(heartbeat.AsMemory(0, 27));
        await client.AssertNoDataAsync(TimeSpan.FromMilliseconds(100));
        await gameServer.SendAsync(heartbeat.AsMemory(27, 32));
        await client.AssertNoDataAsync(TimeSpan.FromMilliseconds(100));
        await gameServer.SendAsync(heartbeat[59..].Concat(new HeartbeatMinimalMessage().ToByteArray()).ToArray());
        AssertForwardedHeartbeat(await client.ReceiveMessageBytesAsync(), heartbeat);

        using var lateClient = await ConnectAndRequestServerListAsync(host);
        AssertForwardedHeartbeat(await lateClient.ReceiveMessageBytesAsync(), heartbeat);
    }

    private static byte[] CreateRawHeartbeat(byte[] nameBytes, byte[] mapBytes) =>
        CreateHeartbeat(string.Empty, string.Empty, 5198, GameStateFlags.Open).ToByteArray()[..26]
            .Concat(nameBytes).Append((byte)0).Concat(mapBytes).Append((byte)0).ToArray();

    private static void AssertForwardedHeartbeat(byte[] forwarded, byte[] heartbeat)
    {
        var expected = new byte[] { 0x00, 0xC9 }.Concat(heartbeat[4..])
            .Concat(Encoding.ASCII.GetBytes("127.0.0.1\0")).ToArray();
        Assert.AreSequenceEqual(expected, forwarded);
        Assert.AreEqual("127.0.0.1", new ServerInfoMessage(forwarded).ServerIP);
    }

    [TestMethod]
    public async Task Tcp_ReservedBytesDoNotTriggerGameUpdates()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var gameServer = await RegisterGameServerAsync(host);
        using var client = await ConnectAndRequestServerListAsync(host);
        _ = await client.ReceiveMessageAsync<ServerInfoMessage>();
        var heartbeat = CreateHeartbeat(ServerName, MapName, 5198, GameStateFlags.Open);
        await gameServer.SendAsync(heartbeat with { ServerInfo = heartbeat.ServerInfo with { Reserved1 = 1, Reserved2 = 2, Reserved3 = 3 } });
        await client.AssertNoDataAsync();
    }

    [TestMethod]
    public async Task DirectPlay_UsesAdvertisedPortAndExpiresCachedResponses()
    {
        var clock = new ManualTimeProvider();
        await using var host = await GlobalServerTestHost.StartAsync(
            enableWebSocket: true, clock: clock, directPlayQueryTimeout: TimeSpan.FromSeconds(1));
        using var udpHost = new LoopbackDirectPlayHost();
        using var gameServer = await RegisterGameServerAsync(host, port: udpHost.Port);
        await udpHost.ReplyAsync(await udpHost.ReceiveQueryAsync(), LoopbackDirectPlayHost.CreateResponse());
        await WaitUntilAsync(() => host.TcpServer.ServerConnections.Single().LastEnumResponse?.CurrentPlayers == 3);

        // A dropped query may reuse a recent reply, but cannot extend its lifetime.
        await gameServer.SendAsync(new HeartbeatMinimalMessage());
        _ = await udpHost.ReceiveQueryAsync();
        await Task.Delay(100);
        clock.Advance(TimeSpan.FromSeconds(29));
        var recent = (await GetWebSocketServerListAsync(host)).Single();
        Assert.IsFalse(recent.Status.HasFlag(WebSocketServerStatus.Denied));
        Assert.AreEqual((uint?)3, recent.CurrentPlayers);

        clock.Advance(TimeSpan.FromSeconds(1));
        var expired = (await GetWebSocketServerListAsync(host)).Single();
        Assert.IsTrue(expired.Status.HasFlag(WebSocketServerStatus.Denied));
        Assert.IsNull(expired.CurrentPlayers);
        Assert.IsNull(expired.MaxPlayers);

        await gameServer.SendAsync(new HeartbeatMinimalMessage());
        await udpHost.ReplyAsync(await udpHost.ReceiveQueryAsync(), LoopbackDirectPlayHost.CreateResponse(4));
        await WaitUntilAsync(() => host.TcpServer.ServerConnections.Single().LastEnumResponse?.CurrentPlayers == 4);
        Assert.AreEqual((uint?)4, (await GetWebSocketServerListAsync(host)).Single().CurrentPlayers);
    }

    [TestMethod]
    public async Task Tcp_PortChangeRemovesOldEntryAndClearsDirectPlayCache()
    {
        await using var host = await GlobalServerTestHost.StartAsync(
            enableWebSocket: true, directPlayQueryTimeout: TimeSpan.FromSeconds(1));
        using var firstUdpHost = new LoopbackDirectPlayHost();
        using var secondUdpHost = new LoopbackDirectPlayHost();
        using var gameServer = await RegisterGameServerAsync(host, port: firstUdpHost.Port);
        await firstUdpHost.ReplyAsync(await firstUdpHost.ReceiveQueryAsync(), LoopbackDirectPlayHost.CreateResponse());
        await WaitUntilAsync(() => host.TcpServer.ServerConnections.Single().LastEnumResponse != null);
        using var client = await ConnectAndRequestServerListAsync(host);
        Assert.AreEqual(firstUdpHost.Port, (await client.ReceiveMessageAsync<ServerInfoMessage>()).ServerInfo.Port);

        await gameServer.SendAsync(CreateHeartbeat(ServerName, MapName, secondUdpHost.Port, GameStateFlags.Open));
        Assert.AreEqual(firstUdpHost.Port, (await client.ReceiveMessageAsync<RemoveServerMessage>()).Port);
        Assert.AreEqual(secondUdpHost.Port, (await client.ReceiveMessageAsync<ServerInfoMessage>()).ServerInfo.Port);
        _ = await secondUdpHost.ReceiveQueryAsync();
        Assert.IsTrue((await GetWebSocketServerListAsync(host)).Single().Status.HasFlag(WebSocketServerStatus.Denied));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public async Task DirectPlay_InvalidRepliesDoNotMarkServerAsReachable(int corruption)
    {
        await using var host = await GlobalServerTestHost.StartAsync(directPlayQueryTimeout: TimeSpan.FromSeconds(1));
        using var udpHost = new LoopbackDirectPlayHost();
        using var gameServer = await RegisterGameServerAsync(host, port: udpHost.Port);
        var response = LoopbackDirectPlayHost.CreateResponse();

        switch (corruption)
        {
            case 0: response = response[..91]; break;
            case 1: response[0] = 1; break;
            case 2: response[1] = 4; break;
            case 3: response[2] = 0; break;
            case 4: response[12] = 0; break;
            case 5: response[76] = 0; break;
        }

        await udpHost.ReplyAsync(await udpHost.ReceiveQueryAsync(), response);
        await gameServer.SendAsync(new HeartbeatMinimalMessage());
        // The second query proves the first reply has already been processed.
        _ = await udpHost.ReceiveQueryAsync();
        Assert.IsNull(host.TcpServer.ServerConnections.Single().LastEnumResponse);
        Assert.IsTrue(host.TcpServer.GetWebSocketServerList().Single().Status.HasFlag(WebSocketServerStatus.Denied));
    }

    [TestMethod]
    public async Task Tcp_ConcurrentInitialListAndRemovalDoNotLeaveStaleEntry()
    {
        await using var host = await GlobalServerTestHost.StartAsync();

        for (var iteration = 0; iteration < 8; iteration++)
        {
            using var gameServer = await RegisterGameServerAsync(host);
            using var client = await ConnectAndRequestServerListAsync(host);
            var registeredConnection = host.TcpServer.ServerConnections.Single();
            // A synchronously processed heartbeat can publish the server before the accept loop
            // has assigned the task returned by HandleConnectionAsync.
            await WaitUntilAsync(() => registeredConnection.Task != null);
            gameServer.ShutdownAndClose();
            await registeredConnection.Task!.WaitAsync(TimeSpan.FromSeconds(5));

            // A later registration acts as a barrier after the initial list and removal.
            using var marker = await RegisterGameServerAsync(host, "Marker", "marker.mis", 5197);
            var entries = new HashSet<ushort>();

            while (true)
            {
                var message = await client.ReceiveMessageAsync();
                if (message is RemoveServerMessage removed)
                    entries.Remove(removed.Port);
                else if (message is ServerInfoMessage added)
                {
                    entries.Add(added.ServerInfo.Port);
                    if (added.ServerInfo.ServerName == "Marker")
                        break;
                }
            }

            Assert.IsFalse(entries.Contains(5198), "The client retained a server after its removal.");
            await client.AssertNoDataAsync(TimeSpan.FromMilliseconds(50));
            marker.ShutdownAndClose();
            await WaitUntilAsync(() => !host.TcpServer.ServerConnections.Any());
        }
    }

    [TestMethod]
    public async Task Connection_SnapshotSurvivesDisconnectAndLateQueryCannotRestoreState()
    {
        await using var host = await GlobalServerTestHost.StartAsync();
        using var peer = await LoopbackPeer.ConnectAsync(host.TcpPort);
        var connection = new Connection(peer.Socket);
        var info = CreateHeartbeat(ServerName, MapName, 5198, GameStateFlags.Open).ServerInfo;
        Assert.IsTrue(connection.TryUpdateServerInfo(info, out _));
        connection.SetEnumResponse(info, new SessionEnumerationResponse(LoopbackDirectPlayHost.CreateResponse()));
        Assert.IsTrue(connection.TryGetServerSnapshot(out var snapshot, out var response));

        Assert.IsTrue(connection.TryBeginDisconnect(out var removed));
        Assert.AreEqual((ServerInfo?)info, removed);
        Assert.IsFalse(connection.TryUpdateServerInfo(info, out _));
        connection.SetEnumResponse(info, response);
        Assert.IsFalse(connection.TryGetServerSnapshot(out _, out _));
        Assert.IsNull(connection.LastEnumResponse);
        Assert.AreEqual(info, snapshot);
        Assert.AreEqual((uint?)3, response?.CurrentPlayers);
    }

    [TestMethod]
    public async Task WebSocket_ConcurrentRequestsAndServerDisconnectReturnCompleteLists()
    {
        await using var host = await GlobalServerTestHost.StartAsync(enableWebSocket: true);
        using var gameServer = await RegisterGameServerAsync(host);
        var requests = Enumerable.Range(0, 12).Select(_ => GetWebSocketServerListAsync(host)).ToArray();
        gameServer.ShutdownAndClose();

        foreach (var list in await Task.WhenAll(requests))
        {
            Assert.IsTrue(list.Count <= 1);
            if (list.Count == 1)
                AssertWebSocketServer(list[0], ServerName, MapName, WebSocketServerStatus.Denied);
        }

        await WaitUntilAsync(() => !host.TcpServer.ServerConnections.Any());
        Assert.HasCount(0, await GetWebSocketServerListAsync(host));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, cts.Token);
    }
}