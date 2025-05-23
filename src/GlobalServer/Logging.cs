﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static Sungaila.NewDark.Core.Messages;
using static Sungaila.NewDark.GlobalServer.States;

namespace Sungaila.NewDark.GlobalServer
{
    internal static class Logging
    {
        /// <summary>
        /// If verbose messages should be logged.
        /// </summary>
        public static bool Verbose = false;

        /// <summary>
        /// If timestamps should be included in logged output.
        /// </summary>
        public static bool PrintTimeStamps = false;

        static readonly Lock _logWriteLineLock = new();

        private readonly record struct DelayedWriteLine(DateTimeOffset Timestamp, string PrimayMessage, string SecondaryMessage, string? Verbose);

        static readonly ConcurrentDictionary<Guid, List<DelayedWriteLine>> _delayedWriteLines = new();

        public static void LogWriteLine(string message)
        {
            lock (_logWriteLineLock)
            {
                if (PrintTimeStamps)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"[{DateTimeOffset.Now}] ");
                    Console.ResetColor();
                }
                Console.WriteLine(message);
            }
        }

        public static void LogWriteLineDelayed(Guid guid, string primayMessage, string secondaryMessage, string? verbose = null)
        {
            if (guid == default)
                return;

            var newEntry = new DelayedWriteLine(DateTimeOffset.Now, primayMessage, secondaryMessage, verbose);

            if (_delayedWriteLines.TryGetValue(guid, out var delayedWriteLines))
            {
                delayedWriteLines.Add(newEntry);
            }
            else
            {
                _delayedWriteLines.TryAdd(guid, [newEntry]);
            }
        }

        private static void FlushDelayed(Guid guid)
        {
            if (guid == default)
                return;

            if (!_delayedWriteLines.TryGetValue(guid, out var delayedWriteLines))
                return;

            foreach (var line in delayedWriteLines)
            {
                LogWriteLineInternal(line.Timestamp, line.PrimayMessage, line.SecondaryMessage, line.Verbose);
            }

            CleanDelayed(guid);
        }

        public static void CleanDelayed(Guid guid)
        {
            if (guid == default)
                return;

            _delayedWriteLines.TryRemove(guid, out _);
        }

        public static void LogWriteLine(Guid guid, string primayMessage, string secondaryMessage, string? verbose = null)
        {
            lock (_logWriteLineLock)
            {
                FlushDelayed(guid);
                LogWriteLineInternal(DateTime.Now, primayMessage, secondaryMessage, verbose);
            }
        }

        private static void LogWriteLineInternal(DateTimeOffset timestamp, string primayMessage, string secondaryMessage, string? verbose = null)
        {
            if (PrintTimeStamps)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{timestamp}] ");
                Console.ResetColor();
            }

            if (secondaryMessage == null)
            {
                Console.WriteLine(primayMessage);
            }
            else
            {
                Console.Write($"{primayMessage} ");
                Console.ForegroundColor = ConsoleColor.DarkGray;

                if (verbose == null || !Verbose)
                {
                    Console.WriteLine($"{secondaryMessage}");
                }
                else
                {
                    Console.Write($"{secondaryMessage} ");
                    Console.WriteLine(verbose);
                }

                Console.ResetColor();
            }
        }

        public static void ConnectionsWriteLine(IEnumerable<Connection> connections)
        {
            lock (_logWriteLineLock)
            {
                var currentConnections = connections.Where(c => !c.IsDisconnected).ToList();
                var conenctionCount = currentConnections.Count;
                var serverOpenCount = currentConnections.Count(c => c.Status == ConnectionStatus.AwaitServerCommand && c.ServerInfo?.StateFlags != GameStateFlags.Closed);
                var serverClosedCount = currentConnections.Count(c => c.Status == ConnectionStatus.AwaitServerCommand && c.ServerInfo?.StateFlags == GameStateFlags.Closed);
                var clientCount = currentConnections.Count(c => c.Status == ConnectionStatus.AwaitClientCommand);

                if (PrintTimeStamps)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"[{DateTimeOffset.Now}] ");
                }

                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"{conenctionCount} open connection{(conenctionCount != 1 ? "s" : string.Empty)} ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"({serverOpenCount} open server{(serverOpenCount != 1 ? "s" : string.Empty)}, {serverClosedCount} closed server{(serverClosedCount != 1 ? "s" : string.Empty)}, {clientCount} client{(clientCount != 1 ? "s" : string.Empty)})");
                Console.ResetColor();
            }
        }

        public static void ErrorWriteLine(Guid guid, string primayMessage, string? secondaryMessage = null)
        {
            lock (_logWriteLineLock)
            {
                FlushDelayed(guid);

                if (PrintTimeStamps)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"[{DateTimeOffset.Now}] ");
                    Console.ResetColor();
                }

                if (secondaryMessage == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(primayMessage);
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write($"{primayMessage} ");
                    Console.ResetColor();

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine(secondaryMessage);
                    Console.ResetColor();
                }
            }
        }
    }
}