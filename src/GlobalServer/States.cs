using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.GlobalServer
{
    public class States
    {
        /// <summary>
        /// The state in which a connection was last seen in.
        /// </summary>
        public enum ConnectionStatus
        {
            /// <summary>
            /// The connection failed due to invalid or unknown messages.
            /// </summary>
            InvalidMessageType = -2,

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
            private static readonly TimeSpan EnumResponseLifetime = TimeSpan.FromSeconds(30);

            private readonly Lock _stateLock = new();

            private readonly TimeProvider _timeProvider;

            private ConnectionStatus _status;

            private DateTimeOffset _lastActivity;

            private ServerInfo? _serverInfo;

            private SessionEnumerationResponse? _lastEnumResponse;

            private long _lastEnumResponseTimestamp;

            private bool _disconnectStarted;

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
            public ConnectionStatus Status
            {
                get
                {
                    lock (_stateLock)
                        return _status;
                }
                set
                {
                    lock (_stateLock)
                    {
                        if (!_disconnectStarted)
                            _status = value;
                    }
                }
            }

            /// <summary>
            /// The time at which this connection was created.
            /// </summary>
            public DateTimeOffset Created { get; }

            /// <summary>
            /// The time the last complete protocol message was received.
            /// </summary>
            public DateTimeOffset LastActivity
            {
                get
                {
                    lock (_stateLock)
                        return _lastActivity;
                }
                set
                {
                    lock (_stateLock)
                        _lastActivity = value;
                }
            }

            /// <summary>
            /// The last game server sent by this connection.
            /// This identifies it as a game server and <see cref="Status"/> should be set to <see cref="ConnectionStatus.AwaitServerCommand"/>.
            /// </summary>
            public ServerInfo? ServerInfo
            {
                get
                {
                    lock (_stateLock)
                        return _serverInfo;
                }
            }

            /// <summary>
            /// The last successful DirectPlay response, valid for at most 30 seconds.
            /// </summary>
            public SessionEnumerationResponse? LastEnumResponse
            {
                get
                {
                    lock (_stateLock)
                        return GetEnumResponse();
                }
            }

            private SessionEnumerationResponse? GetEnumResponse()
            {
                if (_lastEnumResponse != null &&
                    _timeProvider.GetElapsedTime(_lastEnumResponseTimestamp) >= EnumResponseLifetime)
                {
                    _lastEnumResponse = null;
                }

                return _lastEnumResponse;
            }

            public bool TryGetServerSnapshot(out ServerInfo serverInfo, out SessionEnumerationResponse? enumResponse)
            {
                lock (_stateLock)
                {
                    serverInfo = default;
                    enumResponse = null;

                    if (_status != ConnectionStatus.AwaitServerCommand || _serverInfo is not { } info)
                        return false;

                    serverInfo = info;
                    enumResponse = GetEnumResponse();
                    return true;
                }
            }

            public bool TryUpdateServerInfo(ServerInfo serverInfo, out ServerInfo? previousServerInfo)
            {
                lock (_stateLock)
                {
                    previousServerInfo = _serverInfo;

                    if (_disconnectStarted)
                        return false;

                    if (_serverInfo is not { } previous || previous.Port != serverInfo.Port || previous.GameId != serverInfo.GameId)
                        _lastEnumResponse = null;

                    _serverInfo = serverInfo;
                    _status = ConnectionStatus.AwaitServerCommand;
                    return true;
                }
            }

            public void SetEnumResponse(ServerInfo queriedServer, SessionEnumerationResponse? response)
            {
                lock (_stateLock)
                {
                    // A query that finishes after disconnect must not restore obsolete state.
                    if (_disconnectStarted || _serverInfo is not { } current ||
                        current.Port != queriedServer.Port || current.GameId != queriedServer.GameId)
                        return;

                    _lastEnumResponse = response;
                    _lastEnumResponseTimestamp = _timeProvider.GetTimestamp();
                }
            }

            /// <summary>
            /// If this connection is closing or closed.
            /// </summary>
            public bool IsDisconnected => Status is ConnectionStatus.Closed or ConnectionStatus.InvalidMessageType;

            /// <param name="socket">The socket of the accepted connection.</param>
            /// <param name="timeProvider">The clock used to expire cached DirectPlay responses.</param>
            /// <exception cref="ArgumentNullException"/>
            /// <exception cref="ArgumentException">Thrown if <see cref="Socket.RemoteEndPoint"/> is <see langword="null"/> or not an <see cref="IPEndPoint"/>.</exception>
            public Connection(Socket socket, TimeProvider? timeProvider = null)
            {
                ArgumentNullException.ThrowIfNull(socket);

                if (socket.RemoteEndPoint is not IPEndPoint iPEndPoint)
                    throw new ArgumentException("The RemoteEndPoint is null or not an IPEndPoint.");

                Socket = socket;
                InitialEndPoint = iPEndPoint;
                _timeProvider = timeProvider ?? TimeProvider.System;
                Status = ConnectionStatus.NewAndUnidentified;
                Created = DateTimeOffset.Now;
                LastActivity = Created;
            }

            /// <summary>
            /// If the connection successfully identified as a game client or server at least once.
            /// </summary>
            public bool WasIdentified { get; private set; }

            /// <summary>
            /// Marks this connection as successfully identified.
            /// </summary>
            public void MarkIdentified() => WasIdentified = true;

            private int _acceptedLogged;

            /// <summary>
            /// Attempts to mark the connection-accepted log entry as emitted.
            /// </summary>
            public bool TryMarkAcceptedLogged() => Interlocked.Exchange(ref _acceptedLogged, 1) == 0;

            /// <summary>
            /// Attempts to mark this connection as disconnecting.
            /// </summary>
            public bool TryBeginDisconnect(out ServerInfo? serverInfo)
            {
                lock (_stateLock)
                {
                    serverInfo = null;

                    if (_disconnectStarted)
                        return false;

                    _disconnectStarted = true;
                    serverInfo = _serverInfo;
                    _status = ConnectionStatus.Closed;
                    _serverInfo = null;
                    _lastEnumResponse = null;
                    return true;
                }
            }

            /// <summary>
            /// A lock used to ensure that only one thread is sending data on this connection at a time.
            /// </summary>
            public SemaphoreSlim SendLock { get; } = new(1, 1);
        }
    }
}