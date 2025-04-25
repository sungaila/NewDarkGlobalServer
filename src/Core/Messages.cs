using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace Sungaila.NewDark.Core
{
    public class Messages
    {
        const ushort MessageSystemBase = 100;
        const ushort MessageGameBase = 200;

        public static readonly Guid Thief2GameId = new("00F682D7-AB11-4540-9FC2-C43E97358C73");

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
                input[6..10].Reverse().ToArray().CopyTo(guidArray, 0);
                input[10..12].Reverse().ToArray().CopyTo(guidArray, 4);
                input[12..14].Reverse().ToArray().CopyTo(guidArray, 6);
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
                byteList.AddRange(guidArray[0..4].Reverse());
                byteList.AddRange(guidArray[4..6].Reverse());
                byteList.AddRange(guidArray[6..8].Reverse());
                byteList.AddRange(guidArray[8..16]);

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

        public readonly record struct WebSocketServerInfo(string ServerName, string MapName, string Address, WebSocketServerStatus Status, uint? CurrentPlayers, uint? MaxPlayers)
        {
            [JsonIgnore]
            public string? Players
            {
                get
                {
                    if (Status.HasFlag(WebSocketServerStatus.Denied) || CurrentPlayers == null || MaxPlayers == null)
                        return null;

                    return $"{CurrentPlayers}/{MaxPlayers}";
                }
            }

            [JsonIgnore]
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

        public readonly record struct SessionEnumerationQuery() : ISerializableNetworkOrder
        {
            public byte[] ToByteArray()
            {
                var byteList = new List<byte>();

                byteList.Add(0x00); // LeadByte: 00 = Session Packet
                byteList.Add(0x02); // CommandByte: 02 = Enumeration Query
                byteList.AddRange(new byte[] { 0x67, 0xD1 }.ToNetworkOrder()); // EnumPayload: 26577 (0x67D1)
                byteList.Add(0x01); // QueryType: This query contains an ApplicationGUID field
                byteList.AddRange(Thief2GameId.ToByteArray()); // ApplicationGUID: {00F682D7-AB11-4540-9FC2-C43E97358C73}

                return byteList.ToArray();
            }
        }

        [Flags]
        public enum ApplicationDescFlags : uint
        {
            /// <summary>
            /// A client/server game session. If clear, a peer-to-peer game session.
            /// </summary>
            DPNSESSION_CLIENT_SERVER = 0x00000001,

            /// <summary>
            /// Host migration is allowed.
            /// </summary>
            DPNSESSION_MIGRATE_HOST = 0x00000004,

            /// <summary>
            /// Not using DirectPlay Name Server (DPNSVR) (game session is not enumerable via well-known port 6073).
            /// </summary>
            DPNSESSION_NODPNSVR = 0x00000040,

            /// <summary>
            /// Password required to join game session.
            /// </summary>
            DPNSESSION_REQUIREPASSWORD = 0x00000080,

            /// <summary>
            /// Enumerations are not allowed. This flag will never be set in an EnumResponse message.
            /// </summary>
            DPNSESSION_NOENUMS = 0x00000100,

            /// <summary>
            /// Fast message signing is in use. For details about fast message signing, see [MC-DPL8R].
            /// </summary>
            DPNSESSION_FAST_SIGNED = 0x00000200,

            /// <summary>
            /// Full message signing is in use. For details about full message signing, see [MC-DPL8R].
            /// </summary>
            DPNSESSION_FULL_SIGNED = 0x00000400
        }

        public readonly record struct SessionEnumerationResponse(
            byte LeadByte,
            byte CommandByte,
            ushort EnumPayload,
            uint ReplyOffset,
            uint ResponseSize,
            uint ApplicationDescSize,
            ApplicationDescFlags ApplicationDescFlags,
            uint MaxPlayers,
            uint CurrentPlayers,
            uint SessionNameOffset,
            uint SessionNameSize,
            uint PasswordOffset,
            uint PasswordSize,
            uint ReservedDataOffset,
            uint ReservedDataSize,
            uint ApplicationReservedDataOffset,
            uint ApplicationReservedDataSize,
            Guid ApplicationInstanceGUID,
            Guid ApplicationGUID
            ) : ISerializableNetworkOrder
        {
            public SessionEnumerationResponse(byte[] input) : this(default, default, default, default, default, default, default, default, default, default, default, default, default, default, default, default, default, default, default)
            {
                LeadByte = input[0]; // LeadByte: 00 = Session Packet
                CommandByte = input[1]; // CommandByte: 3 (0x3)
                EnumPayload = (ushort)input[2..4].DirectPlayShortToHostOrder(); // EnumPayload: 26577 (0x67D1)
                ReplyOffset = (uint)input[4..8].DirectPlayIntToHostOrder(); // ReplyOffset: 0 (0x0)
                ResponseSize = (uint)input[8..12].DirectPlayIntToHostOrder(); // ResponseSize: 0 bytes
                ApplicationDescSize = (uint)input[12..16].DirectPlayIntToHostOrder(); // ApplicationDescSize: 0x50, MUST be set to 0x00000050
                ApplicationDescFlags = (ApplicationDescFlags)input[16..20].DirectPlayIntToHostOrder(); // ApplicationDescFlags: 64 (0x40)
                MaxPlayers = (uint)input[20..24].DirectPlayIntToHostOrder(); // MaxPlayers: 8 (0x8)
                CurrentPlayers = (uint)input[24..28].DirectPlayIntToHostOrder(); // CurrentPlayers
                SessionNameOffset = (uint)input[28..32].DirectPlayIntToHostOrder(); // SessionNameOffset: 88 (0x58)
                SessionNameSize = (uint)input[32..36].DirectPlayIntToHostOrder(); // SessionNameSize: 2 bytes
                PasswordOffset = (uint)input[36..40].DirectPlayIntToHostOrder(); // PasswordOffset: 0 (0x0)
                PasswordSize = (uint)input[40..44].DirectPlayIntToHostOrder(); // PasswordSize: 0 bytes
                ReservedDataOffset = (uint)input[44..48].DirectPlayIntToHostOrder(); // ReservedDataOffset: 0 (0x0)
                ReservedDataSize = (uint)input[48..52].DirectPlayIntToHostOrder(); // ReservedDataSize: 0 bytes
                ApplicationReservedDataOffset = (uint)input[52..56].DirectPlayIntToHostOrder(); // ApplicationReservedDataOffset: 0 (0x0)
                ApplicationReservedDataSize = (uint)input[56..60].DirectPlayIntToHostOrder(); // ApplicationReservedDataSize: 0 bytes
                ApplicationInstanceGUID = input[60..76].DirectPlayGuidToHostOrder(); //ApplicationInstanceGUID
                ApplicationGUID = input[76..92].DirectPlayGuidToHostOrder();// ApplicationGUID: {00F682D7-AB11-4540-9FC2-C43E97358C73}
            }

            public byte[] ToByteArray()
            {
                throw new NotImplementedException();
            }
        }
    }
}
