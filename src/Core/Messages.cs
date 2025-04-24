using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Sungaila.NewDark.Core
{
    public class Messages
    {
        const ushort MessageSystemBase = 100;
        const ushort MessageGameBase = 200;

        public static readonly Guid Thief2GameId = new("00f682d7-ab11-4540-9fc2-c43e97358c73");

        public enum MessageType : ushort
        {
            ListRequest = MessageSystemBase + 1,
            RemoveServer = MessageSystemBase + 2,
            ClientExit = MessageSystemBase + 3,
            ServerClosed = MessageSystemBase + 50,

            ServerInfo = MessageGameBase + 1,
            Heartbeat = MessageGameBase + 50,
            HeartbeatMinimal = MessageGameBase + 51
        }

        [Flags]
        public enum GameStateFlags : byte
        {
            Open = 1 << 0,
            Closed = 1 << 1,
            Password = 1 << 2
        }

        public enum ExitReason : byte
        {
            JoinedGame = 0,
            AppCrash = 1,
            Quit = 2
        }

        public interface ISerializableNetworkOrder
        {
            public byte[] ToByteArray();
        }

        public readonly record struct ServerInfo(ushort Port, GameStateFlags StateFlags, byte Reserved1, byte Reserved2, byte Reserved3, Guid GameId, string ServerName, string MapName) : ISerializableNetworkOrder
        {
            public ServerInfo(byte[] input) : this(default, default, default, default, default, default, string.Empty, string.Empty)
            {
                Port = (ushort)input[0..2].ShortToHostOrder();
                StateFlags = (GameStateFlags)input[2];
                Reserved1 = input[3];
                Reserved2 = input[4];
                Reserved3 = input[5];

                var guidArray = new byte[16];
                BitConverter.GetBytes(input[6..10].IntToHostOrder()).CopyTo(guidArray, 0);
                BitConverter.GetBytes(input[10..12].ShortToHostOrder()).CopyTo(guidArray, 4);
                BitConverter.GetBytes(input[12..14].ShortToHostOrder()).CopyTo(guidArray, 6);
                input[14..22].CopyTo(guidArray, 8);
                GameId = new Guid(guidArray);

                var part = input[22..]!;
                var firstNullTerminator = Math.Min(Array.IndexOf(part, (byte)'\0'), 31);

                ServerName = Encoding.ASCII.GetString(part, 0, firstNullTerminator).Trim('\0');

                var mapNamePart = part[(firstNullTerminator+1)..];
                firstNullTerminator = Math.Min(Array.IndexOf(mapNamePart, (byte)'\0'), 31);

                MapName = Encoding.ASCII.GetString(mapNamePart, 0, firstNullTerminator).Trim('\0');
            }

            public byte[] ToByteArray()
            {
                var byteList = new List<byte>();

                byteList.AddRange(BitConverter.GetBytes(Port.ToNetworkOrder()));
                byteList.Add((byte)StateFlags);
                byteList.Add(Reserved1);
                byteList.Add(Reserved2);
                byteList.Add(Reserved3);

                var guidArray = GameId.ToByteArray();
                BitConverter.GetBytes(guidArray[0..4].IntToHostOrder()).CopyTo(guidArray, 0);
                BitConverter.GetBytes(guidArray[4..6].ShortToHostOrder()).CopyTo(guidArray, 4);
                BitConverter.GetBytes(guidArray[6..8].ShortToHostOrder()).CopyTo(guidArray, 6);
                byteList.AddRange(guidArray);

                byteList.AddRange(Encoding.ASCII.GetBytes(ServerName[..Math.Min(ServerName.Length, 31)] + '\0'));
                byteList.AddRange(Encoding.ASCII.GetBytes(MapName[..Math.Min(MapName.Length, 31)] + '\0'));

                return byteList.ToArray();
            }
        }

        public interface IMessage : ISerializableNetworkOrder
        {
            public MessageType Type { get; }
        }

        public readonly record struct ListRequestMessage(ushort ProtocolVersion) : IMessage
        {
            public MessageType Type => MessageType.ListRequest;

            public ListRequestMessage(byte[] input) : this((ushort)default)
            {
                var type = (MessageType)input[0..2].ShortToHostOrder();

                if (type != Type)
                    throw new ArgumentOutOfRangeException(nameof(input));

                ProtocolVersion = (ushort)input[2..4].ShortToHostOrder();
            }

            public byte[] ToByteArray()
            {
                var byteList = new List<byte>();

                byteList.AddRange(BitConverter.GetBytes(((short)Type).ToNetworkOrder()));
                byteList.AddRange(BitConverter.GetBytes(ProtocolVersion.ToNetworkOrder()));

                return byteList.ToArray();
            }
        }

        public readonly record struct ServerClosedMessage() : IMessage
        {
            public MessageType Type => MessageType.ServerClosed;

            public ServerClosedMessage(byte[] input) : this()
            {
                var type = (MessageType)input[0..2].ShortToHostOrder();

                if (type != Type)
                    throw new ArgumentOutOfRangeException(nameof(input));
            }

            public byte[] ToByteArray()
            {
                return BitConverter.GetBytes(((short)Type).ToNetworkOrder());
            }
        }

        public readonly record struct ClientExitMessage(ExitReason ExitReason) : IMessage
        {
            public MessageType Type => MessageType.ServerClosed;

            public ClientExitMessage(byte[] input) : this(default(ExitReason))
            {
                var type = (MessageType)input[0..2].ShortToHostOrder();

                if (type != Type)
                    throw new ArgumentOutOfRangeException(nameof(input));

                ExitReason = (ExitReason)input[2];
            }

            public byte[] ToByteArray()
            {
                var byteList = new List<byte>();

                byteList.AddRange(BitConverter.GetBytes(((short)Type).ToNetworkOrder()));
                byteList.Add((byte)ExitReason);

                return byteList.ToArray();
            }
        }

        public readonly record struct HeartbeatMessage(ushort ProtocolVersion, ServerInfo ServerInfo) : IMessage
        {
            public MessageType Type => MessageType.Heartbeat;

            public HeartbeatMessage(byte[] input) : this(default, default)
            {
                var type = (MessageType)input[0..2].ShortToHostOrder();

                if (type != Type)
                    throw new ArgumentOutOfRangeException(nameof(input));

                ProtocolVersion = (ushort)input[2..4].ShortToHostOrder();

                ServerInfo = new ServerInfo(input[4..]);
            }

            public byte[] ToByteArray()
            {
                var byteList = new List<byte>();

                byteList.AddRange(BitConverter.GetBytes(((short)Type).ToNetworkOrder()));
                byteList.AddRange(BitConverter.GetBytes(ProtocolVersion.ToNetworkOrder()));
                byteList.AddRange(ServerInfo.ToByteArray());

                return byteList.ToArray();
            }
        }

        public readonly record struct HeartbeatMinimalMessage() : IMessage
        {
            public MessageType Type => MessageType.HeartbeatMinimal;

            public HeartbeatMinimalMessage(byte[] input) : this()
            {
                var type = (MessageType)input[0..2].ShortToHostOrder();

                if (type != Type)
                    throw new ArgumentOutOfRangeException(nameof(input));
            }

            public byte[] ToByteArray()
            {
                return BitConverter.GetBytes(((short)Type).ToNetworkOrder());
            }
        }

        public readonly record struct RemoveServerMessage(ushort Port, string ServerIP) : IMessage
        {
            public MessageType Type => MessageType.RemoveServer;

            public RemoveServerMessage(byte[] input) : this(default, string.Empty)
            {
                var type = (MessageType)input[0..2].ShortToHostOrder();

                if (type != Type)
                    throw new ArgumentOutOfRangeException(nameof(input));

                Port = (ushort)input[2..4].ShortToHostOrder();

                var part = input[4..]!;
                var firstNullTerminator = Math.Min(Array.IndexOf(part, (byte)'\0'), 15);

                ServerIP = Encoding.ASCII.GetString(part, 0, firstNullTerminator).Trim('\0');
            }

            public byte[] ToByteArray()
            {
                var byteList = new List<byte>();

                byteList.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)Type)));
                byteList.AddRange(BitConverter.GetBytes(Port.ToNetworkOrder()));
                byteList.AddRange(Encoding.ASCII.GetBytes(ServerIP[..Math.Min(ServerIP.Length, 15)] + '\0'));

                return byteList.ToArray();
            }
        }

        public readonly record struct ServerInfoMessage(ServerInfo ServerInfo, string ServerIP) : IMessage
        {
            public MessageType Type => MessageType.ServerInfo;

            public ServerInfoMessage(byte[] input) : this(default, string.Empty)
            {
                var type = (MessageType)input[0..2].ShortToHostOrder();

                if (type != Type)
                    throw new ArgumentOutOfRangeException(nameof(input));

                ServerInfo = new ServerInfo(input[2..]);

                var part = input[(ServerInfo.ToByteArray().Length+2)..]!;
                var firstNullTerminator = Math.Min(Array.IndexOf(part, (byte)'\0'), 15);

                ServerIP = Encoding.ASCII.GetString(part, 0, firstNullTerminator).Trim('\0');
            }

            public byte[] ToByteArray()
            {
                var byteList = new List<byte>();

                byteList.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)Type)));
                byteList.AddRange(ServerInfo.ToByteArray());
                byteList.AddRange(Encoding.ASCII.GetBytes(ServerIP[..Math.Min(ServerIP.Length, 15)] + '\0'));

                return byteList.ToArray();
            }
        }

        [Flags]
        public enum WebSocketServerStatus : byte
        {
            Open = 1 << 0,
            Closed = 1 << 1,
            Denied = 1 << 2
        }

        public readonly record struct WebSocketServerInfo(string ServerName, string MapName, string Address, WebSocketServerStatus Status, ushort? PlayerCount, ushort? MaxPlayers)
        {
            public string? Players
            {
                get
                {
                    if (Status.HasFlag(WebSocketServerStatus.Denied) || PlayerCount == null || MaxPlayers == null)
                        return null;

                    return $"{PlayerCount}/{MaxPlayers}";
                }
            }

            public string StatusAsString
            {
                get
                {
                    var flags = new List<string>();

                    if (Status.HasFlag(WebSocketServerStatus.Closed))
                        flags.Add(nameof(WebSocketServerStatus.Closed));
                    else
                        flags.Add(nameof(WebSocketServerStatus.Open));

                    if (Status.HasFlag(WebSocketServerStatus.Denied))
                        flags.Add(nameof(WebSocketServerStatus.Denied));

                    return string.Join(", ", flags);
                }
            }
        }
    }
}
