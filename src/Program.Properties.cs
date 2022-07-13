using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using static NewDarkGlobalServer.Messages;
using static NewDarkGlobalServer.States;

namespace NewDarkGlobalServer
{
    public static partial class Program
    {
        /// <summary>
        /// The expected maximum message size.
        /// </summary>
        const int NetworkBufferSize = 256;

        /// <summary>
        /// The supported protocol version.
        /// </summary>
        const ushort SupportedProtocolVersion = 1100;

        /// <summary>
        /// The port the global server uses.
        /// </summary>
        static int Port = 5199;

        /// <summary>
        /// The timeout for connections have not sent requests yet.
        /// </summary>
        static TimeSpan UnidentifiedConnectionTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The timeout for game servers.
        /// </summary>
        static TimeSpan ServerConnectionTimeout = TimeSpan.FromMinutes(3);

        /// <summary>
        /// The timeout for game clients.
        /// </summary>
        static TimeSpan ClientConnectionTimeout = TimeSpan.FromHours(1);

        /// <summary>
        /// If <see cref="HeartbeatMinimalMessage"/> should be logged.
        /// </summary>
        static bool ShowHeartbeatMinimal = false;

        /// <summary>
        /// The interval for <see cref="HandleCleanupAsync(CancellationToken)"/> to run.
        /// </summary>
        static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// A thread-safe collection of all connections.
        /// </summary>
        static readonly ConcurrentDictionary<string, Connection> _connections = new();

        /// <summary>
        /// Decides if the given <paramref name="socket"/> is still alive. This isn't 100% reliable though.
        /// </summary>
        /// <param name="socket">The socket given for the alive check.</param>
        static bool IsSocketAlive(Socket socket) => socket != null && socket.Connected && socket.Poll(1000, SelectMode.SelectRead) && socket.Available != 0;
    }
}