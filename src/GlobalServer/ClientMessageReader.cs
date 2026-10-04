using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.GlobalServer
{
    /// <summary>
    /// Splits the TCP stream into protocol messages, retaining partial and subsequent messages.
    /// </summary>
    internal sealed class ClientMessageReader(Socket socket)
    {
        private readonly byte[] _buffer = new byte[256];
        private int _offset;
        private int _count;

        public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var length = GetMessageLength(_buffer.AsSpan(_offset, _count));

                if (length > 0 && _count >= length)
                {
                    var message = _buffer.AsSpan(_offset, length).ToArray();
                    _offset += length;
                    _count -= length;
                    return message;
                }

                if (_offset > 0)
                {
                    _buffer.AsSpan(_offset, _count).CopyTo(_buffer);
                    _offset = 0;
                }

                var received = await socket.ReceiveAsync(_buffer.AsMemory(_count), SocketFlags.None, cancellationToken);

                if (received == 0)
                {
                    if (_count != 0)
                        throw new EndOfStreamException("TCP stream ended before the message was complete.");

                    return null;
                }

                _count += received;
            }
        }

        private static int GetMessageLength(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < 2)
                return 0;

            var type = (MessageType)BinaryPrimitives.ReadUInt16BigEndian(buffer);

            if (type != MessageType.Heartbeat)
            {
                return type switch
                {
                    MessageType.ListRequest => 4,
                    MessageType.ClientExit => 3,
                    _ => 2 // Dispatch unknown IDs immediately so the connection is rejected.
                };
            }

            // Message ID, protocol version and the fixed ServerInfo fields occupy 26 bytes.
            if (buffer.Length < 26)
                return 0;

            var serverNameLength = GetStringLength(buffer[26..]);

            if (serverNameLength == 0)
                return 0;

            var mapNameLength = GetStringLength(buffer[(26 + serverNameLength)..]);
            return mapNameLength == 0 ? 0 : 26 + serverNameLength + mapNameLength;
        }

        private static int GetStringLength(ReadOnlySpan<byte> buffer)
        {
            var terminator = buffer[..Math.Min(buffer.Length, 32)].IndexOf((byte)0);

            if (terminator >= 0)
                return terminator + 1;

            if (buffer.Length >= 32)
                throw new InvalidDataException("Heartbeat string is not null-terminated within 32 bytes.");

            return 0;
        }
    }
}