using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.Tests.Infrastructure;

internal sealed class LoopbackDirectPlayHost : IDisposable
{
    private readonly UdpClient _client = new(new IPEndPoint(IPAddress.Loopback, 0));

    public ushort Port => (ushort)((IPEndPoint)_client.Client.LocalEndPoint!).Port;

    public async Task<UdpReceiveResult> ReceiveQueryAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var query = await _client.ReceiveAsync(cts.Token);
        Assert.AreSequenceEqual(new SessionEnumerationQuery().ToByteArray(), query.Buffer);
        return query;
    }

    public Task ReplyAsync(UdpReceiveResult query, byte[] response) =>
        _client.SendAsync(response, query.RemoteEndPoint).AsTask();

    public static byte[] CreateResponse(uint currentPlayers = 3, uint maxPlayers = 8)
    {
        var bytes = new byte[92];
        bytes[1] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 0x67D1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0x50);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), maxPlayers);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), currentPlayers);
        Thief2GameId.ToByteArray().CopyTo(bytes, 76);
        return bytes;
    }

    public void Dispose() => _client.Dispose();
}