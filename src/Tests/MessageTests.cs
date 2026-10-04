using System.Buffers.Binary;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.Tests;

[TestClass]
public sealed class MessageTests
{
    private const ushort ProtocolVersion = 1100;

    private static ServerInfo CreateServerInfo() => new(
        Port: 5198,
        StateFlags: GameStateFlags.Open | GameStateFlags.Password,
        Reserved1: 0,
        Reserved2: 0,
        Reserved3: 0,
        GameId: Thief2GameId,
        ServerName: "Loopback Server",
        MapName: "miss1.mis");

    [TestMethod]
    public void ListRequestMessage_UsesNetworkByteOrderAndRoundTrips()
    {
        var message = new ListRequestMessage(ProtocolVersion);
        var bytes = message.ToByteArray();

        Assert.AreSequenceEqual(new byte[] { 0x00, 0x65, 0x04, 0x4C }, bytes);
        Assert.AreEqual(message, new ListRequestMessage(bytes));
    }

    [TestMethod]
    public void ClientExitMessage_UsesExpectedWireFormatAndRoundTrips()
    {
        var message = new ClientExitMessage(ExitReason.Quit);
        var bytes = message.ToByteArray();

        Assert.AreSequenceEqual(new byte[] { 0x00, 0x67, 0x02 }, bytes);
        Assert.AreEqual(message, new ClientExitMessage(bytes));
    }

    [TestMethod]
    public void ServerClosedAndHeartbeatMinimal_UseExpectedMessageIds()
    {
        var serverClosed = new ServerClosedMessage();
        var heartbeatMinimal = new HeartbeatMinimalMessage();

        Assert.AreSequenceEqual(new byte[] { 0x00, 0x96 }, serverClosed.ToByteArray());
        Assert.AreSequenceEqual(new byte[] { 0x00, 0xFB }, heartbeatMinimal.ToByteArray());
        Assert.AreEqual(serverClosed, new ServerClosedMessage(serverClosed.ToByteArray()));
        Assert.AreEqual(heartbeatMinimal, new HeartbeatMinimalMessage(heartbeatMinimal.ToByteArray()));
    }

    [TestMethod]
    public void ServerInfo_UsesExpectedNetworkByteOrderAndGuidLayout()
    {
        var info = new ServerInfo(
            Port: 0x1234,
            StateFlags: GameStateFlags.Open | GameStateFlags.Password,
            Reserved1: 0x01,
            Reserved2: 0x02,
            Reserved3: 0x03,
            GameId: new Guid("00112233-4455-6677-8899-AABBCCDDEEFF"),
            ServerName: "A",
            MapName: "B");

        var bytes = info.ToByteArray();

        Assert.AreSequenceEqual(
            new byte[]
            {
                0x12, 0x34,
                0x05, 0x01, 0x02, 0x03,
                0x00, 0x11, 0x22, 0x33,
                0x44, 0x55,
                0x66, 0x77,
                0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
                (byte)'A', 0x00,
                (byte)'B', 0x00
            },
            bytes);

        Assert.AreEqual(info, new ServerInfo(bytes));
    }

    [TestMethod]
    public void HeartbeatMessage_RoundTripsServerInfo()
    {
        var message = new HeartbeatMessage(ProtocolVersion, CreateServerInfo());
        var bytes = message.ToByteArray();

        Assert.AreSequenceEqual(new byte[] { 0x00, 0xFA, 0x04, 0x4C }, bytes[..4]);
        Assert.AreEqual(message, new HeartbeatMessage(bytes));
    }

    [TestMethod]
    public void ServerInfoMessage_RoundTripsServerInfoAndAddress()
    {
        var message = new ServerInfoMessage(CreateServerInfo(), "127.0.0.1");
        var bytes = message.ToByteArray();

        Assert.AreSequenceEqual(new byte[] { 0x00, 0xC9 }, bytes[..2]);
        Assert.AreEqual(message, new ServerInfoMessage(bytes));
    }

    [TestMethod]
    public void ServerInfoMessage_RoundTripsMaximumLengthStringsAndIPv4Address()
    {
        var info = CreateServerInfo() with
        {
            ServerName = new string('S', 31),
            MapName = new string('M', 31)
        };
        var message = new ServerInfoMessage(info, "255.255.255.255");

        var parsed = new ServerInfoMessage(message.ToByteArray());

        Assert.AreEqual(message, parsed);
        Assert.AreEqual(31, parsed.ServerInfo.ServerName.Length);
        Assert.AreEqual(31, parsed.ServerInfo.MapName.Length);
        Assert.AreEqual(15, parsed.ServerIP.Length);
    }

    [TestMethod]
    public void RemoveServerMessage_UsesNetworkByteOrderAndRoundTrips()
    {
        var message = new RemoveServerMessage(5198, "127.0.0.1");
        var bytes = message.ToByteArray();

        Assert.AreSequenceEqual(new byte[] { 0x00, 0x66, 0x14, 0x4E }, bytes[..4]);
        Assert.AreEqual(message, new RemoveServerMessage(bytes));
    }

    [TestMethod]
    public void ServerInfo_TruncatesStringsToProtocolMaximum()
    {
        var info = CreateServerInfo() with
        {
            ServerName = new string('S', 40),
            MapName = new string('M', 40)
        };

        var parsed = new ServerInfo(info.ToByteArray());

        Assert.AreEqual(31, parsed.ServerName.Length);
        Assert.AreEqual(31, parsed.MapName.Length);
        Assert.IsTrue(parsed.ServerName.All(c => c == 'S'));
        Assert.IsTrue(parsed.MapName.All(c => c == 'M'));
    }

    [TestMethod]
    public void ServerInfo_RejectsPortZeroAndMissingNullTerminator()
    {
        var zeroPort = CreateServerInfo().ToByteArray();
        zeroPort[0] = 0;
        zeroPort[1] = 0;

        Assert.ThrowsExactly<ArgumentException>(() => new ServerInfo(zeroPort));

        var missingTerminator = new byte[22 + 32];
        missingTerminator[0] = 0x14;
        missingTerminator[1] = 0x4E;
        Array.Fill(missingTerminator, (byte)'S', 22, 32);

        Assert.ThrowsExactly<ArgumentException>(() => new ServerInfo(missingTerminator));
    }

    [TestMethod]
    public void ServerInfo_PreservesEveryEightBitNameByte()
    {
        var info = CreateServerInfo() with { ServerName = "S", MapName = "M" };

        for (var value = 0x80; value <= 0xFF; value++)
        {
            var bytes = info.ToByteArray();
            bytes[22] = (byte)value;
            bytes[24] = (byte)value;

            var parsed = new ServerInfo(bytes);
            Assert.AreSequenceEqual(bytes, parsed.ToByteArray(), $"Name byte 0x{value:X2} changed.");

            var messageBytes = new ServerInfoMessage(parsed, "127.0.0.1").ToByteArray();
            Assert.AreSequenceEqual(messageBytes, new ServerInfoMessage(messageBytes).ToByteArray());
        }
    }

    [TestMethod]
    public void SessionEnumerationQuery_UsesExpectedDirectPlayHeaderAndGameId()
    {
        var bytes = new SessionEnumerationQuery().ToByteArray();

        Assert.HasCount(21, bytes);
        Assert.AreSequenceEqual(new byte[] { 0x00, 0x02, 0xD1, 0x67, 0x01 }, bytes[..5]);
        Assert.AreSequenceEqual(Thief2GameId.ToByteArray(), bytes[5..]);
    }

    [TestMethod]
    public void SessionEnumerationResponse_ParsesDirectPlayWireFormat()
    {
        var applicationInstanceGuid = new Guid("11223344-5566-7788-99AA-BBCCDDEEFF00");
        var bytes = new byte[92];

        bytes[0] = 0x00;
        bytes[1] = 0x03;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 0x67D1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 0x01020304);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 92);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 0x50);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), (uint)(ApplicationDescFlags.DPNSESSION_NODPNSVR | ApplicationDescFlags.DPNSESSION_REQUIREPASSWORD));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 88);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56, 4), 0);
        applicationInstanceGuid.ToByteArray().CopyTo(bytes, 60);
        Thief2GameId.ToByteArray().CopyTo(bytes, 76);

        var response = new SessionEnumerationResponse(bytes);

        Assert.AreEqual((byte)0x00, response.LeadByte);
        Assert.AreEqual((byte)0x03, response.CommandByte);
        Assert.AreEqual((ushort)0x67D1, response.EnumPayload);
        Assert.AreEqual((uint)0x01020304, response.ReplyOffset);
        Assert.AreEqual((uint)92, response.ResponseSize);
        Assert.AreEqual((uint)0x50, response.ApplicationDescSize);
        Assert.AreEqual(ApplicationDescFlags.DPNSESSION_NODPNSVR | ApplicationDescFlags.DPNSESSION_REQUIREPASSWORD, response.ApplicationDescFlags);
        Assert.AreEqual((uint)8, response.MaxPlayers);
        Assert.AreEqual((uint)3, response.CurrentPlayers);
        Assert.AreEqual((uint)88, response.SessionNameOffset);
        Assert.AreEqual((uint)2, response.SessionNameSize);
        Assert.AreEqual(applicationInstanceGuid, response.ApplicationInstanceGUID);
        Assert.AreEqual(Thief2GameId, response.ApplicationGUID);
    }

    [TestMethod]
    public void WebSocketServerInfo_FormatsStatusAndPlayers()
    {
        var open = new WebSocketServerInfo("Server", "Map", "127.0.***.***", WebSocketServerStatus.Open, 2, 8);
        var deniedClosed = open with { Status = WebSocketServerStatus.Closed | WebSocketServerStatus.Denied };

        Assert.AreEqual("2/8", open.Players);
        Assert.AreEqual("Open", open.StatusAsString);
        Assert.IsNull(deniedClosed.Players);
        Assert.AreEqual("Closed, Denied", deniedClosed.StatusAsString);
    }
}