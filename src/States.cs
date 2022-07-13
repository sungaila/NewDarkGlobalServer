using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using static NewDarkGlobalServer.Messages;

namespace NewDarkGlobalServer
{
    public class States
    {
        /// <summary>
        /// The state in which a connection was last seen in.
        /// </summary>
        public enum ConnectionStatus
        {
            /// <summary>
            /// The connection is closing or closed.
            /// </summary>
            Closed = -1,

            /// <summary>
            /// The connection has not send data yet.
            /// </summary>
            NewAndUnidentified = 0,

            /// <summary>
            /// The connection is a game server.
            /// </summary>
            AwaitServerCommand,

            /// <summary>
            /// The connection is a game client.
            /// </summary>
            AwaitClientCommand
        }

        /// <summary>
        /// Represents a connection with a game server or client.
        /// </summary>
        public class Connection
        {
            /// <summary>
            /// The socket used for this connection.
            /// </summary>
            public Socket Socket { get; }

            /// <summary>
            /// The inital <see cref="Socket.RemoteEndPoint"/> (expected to be an <see cref="IPEndPoint"/>).
            /// </summary>
            public IPEndPoint InitialEndPoint { get; }

            /// <summary>
            /// The task used for handling this connection.
            /// </summary>
            public Task? Task { get; set; } = null;

            /// <summary>
            /// The state in which this connection was last seen in.
            /// </summary>
            public ConnectionStatus Status { get; set; }

            /// <summary>
            /// The time at which this connection was created.
            /// </summary>
            public DateTimeOffset Created { get; }

            /// <summary>
            /// The last time <see cref="Socket.SendAsync"/> or <see cref="Socket.ReceiveAsync"/> had been called for this connection.
            /// </summary>
            public DateTimeOffset LastActivity { get; set; }

            /// <summary>
            /// The last game server sent by this connection.
            /// This identifies it as a game server and <see cref="Status"/> should be set to <see cref="ConnectionStatus.AwaitServerCommand"/>.
            /// </summary>
            public ServerInfo? ServerInfo { get; set; } = null;

            /// <summary>
            /// If this connection is closing or closed.
            /// </summary>
            public bool IsDisconnected => Status == ConnectionStatus.Closed || Socket == null || !Socket.Connected;

            /// <param name="socket">The socket of the accepted connection.</param>
            /// <exception cref="ArgumentNullException"/>
            /// <exception cref="ArgumentException">Thrown if <see cref="Socket.RemoteEndPoint"/> is <see langword="null"/> or not an <see cref="IPEndPoint"/>.</exception>
            public Connection(Socket socket)
            {
                if (socket == null)
                    throw new ArgumentNullException(nameof(socket));

                if (socket.RemoteEndPoint is not IPEndPoint iPEndPoint)
                    throw new ArgumentException("The RemoteEndPoint is null or not an IPEndPoint.");

                Socket = socket;
                InitialEndPoint = iPEndPoint;
                Status = ConnectionStatus.NewAndUnidentified;
                Created = DateTimeOffset.Now;
                LastActivity = Created;
            }
        }
    }
}
