using Sungaila.NewDark.Core;
using System.Net;
using System.Net.Sockets;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.Tests.Infrastructure;

internal sealed class LoopbackPeer : IDisposable
{
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);

    public Socket Socket { get; }

    private LoopbackPeer(Socket socket)
    {
        Socket = socket;
    }

    public static async Task<LoopbackPeer> ConnectAsync(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            using var cts = new CancellationTokenSource(IoTimeout);
            await socket.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return new LoopbackPeer(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task SendAsync(IMessage message) => SendAsync(message.ToByteArray());

    public async Task SendAsync(ReadOnlyMemory<byte> bytes)
    {
        using var cts = new CancellationTokenSource(IoTimeout);
        var offset = 0;

        while (offset < bytes.Length)
        {
            var sent = await Socket.SendAsync(bytes[offset..], SocketFlags.None, cts.Token);

            if (sent == 0)
                throw new IOException("The loopback peer socket closed while sending.");

            offset += sent;
        }
    }

    public async Task<IMessage> ReceiveMessageAsync()
    {
        var bytes = await ReceiveMessageBytesAsync();
        var type = (MessageType)bytes[..2].ShortToHostOrder();

        return type switch
        {
            MessageType.ServerInfo => new ServerInfoMessage(bytes),
            MessageType.RemoveServer => new RemoveServerMessage(bytes),
            _ => throw new InvalidDataException($"Unexpected server-to-client message type {type}.")
        };
    }

    public async Task<byte[]> ReceiveMessageBytesAsync()
    {
        using var cts = new CancellationTokenSource(IoTimeout);
        var header = await ReceiveExactlyAsync(2, cts.Token);
        var type = (MessageType)header.ShortToHostOrder();

        return type switch
        {
            MessageType.ServerInfo => await ReceiveServerInfoMessageBytesAsync(header, cts.Token),
            MessageType.RemoveServer => await ReceiveRemoveServerMessageBytesAsync(header, cts.Token),
            _ => throw new InvalidDataException($"Unexpected server-to-client message type {type}.")
        };
    }

    public async Task<TMessage> ReceiveMessageAsync<TMessage>() where TMessage : struct, IMessage
    {
        var message = await ReceiveMessageAsync();
        Assert.IsInstanceOfType<TMessage>(message);
        return (TMessage)message;
    }

    public async Task AssertNoDataAsync(TimeSpan? duration = null)
    {
        using var cts = new CancellationTokenSource(duration ?? TimeSpan.FromMilliseconds(300));
        var buffer = new byte[1];

        try
        {
            var received = await Socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);

            if (received == 0)
            {
                Assert.Fail("Expected the TCP connection to remain open without receiving data, but the server closed it.");
                return;
            }

            Assert.Fail($"Expected no TCP data, but received {received} byte(s).");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Expected: the connection remained open and no data arrived during the observation window.
        }
    }

    public async Task AssertEofAsync()
    {
        using var cts = new CancellationTokenSource(IoTimeout);
        var buffer = new byte[1];
        var received = await Socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);

        Assert.AreEqual(0, received, "Expected the server to close the TCP stream cleanly (EOF).");
    }

    public void ShutdownAndClose()
    {
        try
        {
            Socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        Socket.Close();
    }

    public void Dispose() => Socket.Dispose();

    private async Task<byte[]> ReceiveServerInfoMessageBytesAsync(byte[] header, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(header);
        bytes.AddRange(await ReceiveExactlyAsync(22, cancellationToken));
        bytes.AddRange(await ReceiveNullTerminatedAsync(32, cancellationToken));
        bytes.AddRange(await ReceiveNullTerminatedAsync(32, cancellationToken));
        bytes.AddRange(await ReceiveNullTerminatedAsync(16, cancellationToken));
        return [.. bytes];
    }

    private async Task<byte[]> ReceiveRemoveServerMessageBytesAsync(byte[] header, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(header);
        bytes.AddRange(await ReceiveExactlyAsync(2, cancellationToken));
        bytes.AddRange(await ReceiveNullTerminatedAsync(16, cancellationToken));
        return [.. bytes];
    }

    private async Task<byte[]> ReceiveExactlyAsync(int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;

        while (offset < buffer.Length)
        {
            var received = await Socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, cancellationToken);

            if (received == 0)
                throw new EndOfStreamException($"TCP stream ended after {offset} of {length} expected bytes.");

            offset += received;
        }

        return buffer;
    }

    private async Task<byte[]> ReceiveNullTerminatedAsync(int maxLength, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(maxLength);
        var buffer = new byte[1];

        while (bytes.Count < maxLength)
        {
            var received = await Socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

            if (received == 0)
                throw new EndOfStreamException("TCP stream ended before a null-terminated string was complete.");

            bytes.Add(buffer[0]);

            if (buffer[0] == 0)
                return [.. bytes];
        }

        throw new InvalidDataException($"No null terminator found within {maxLength} bytes.");
    }
}