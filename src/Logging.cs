using System;
using System.Collections.Generic;
using System.Linq;
using static NewDarkGlobalServer.Messages;
using static NewDarkGlobalServer.States;

namespace NewDarkGlobalServer
{
    internal class Logging
    {
        /// <summary>
        /// If verbose messages should not be logged.
        /// </summary>
        public static bool HideVerbose = false;

        static readonly object _logWriteLineLock = new();

        public static void LogWriteLine(string message)
        {
            lock (_logWriteLineLock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{DateTime.Now}] ");
                Console.ResetColor();
                Console.WriteLine(message);
            }
        }

        public static void LogWriteLine(string primayMessage, string secondaryMessage, string? verbose = null)
        {
            lock (_logWriteLineLock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{DateTime.Now}] ");
                Console.ResetColor();

                if (secondaryMessage == null)
                {
                    Console.WriteLine(primayMessage);
                }
                else
                {
                    Console.Write($"{primayMessage} ");
                    Console.ForegroundColor = ConsoleColor.DarkGray;

                    if (verbose == null || HideVerbose)
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

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{DateTime.Now}] ");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.Write($"{conenctionCount} connection{(conenctionCount != 1 ? "s" : string.Empty)} open ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"({serverOpenCount} open server{(serverOpenCount != 1 ? "s" : string.Empty)}, {serverClosedCount} closed server{(serverClosedCount != 1 ? "s" : string.Empty)}, {clientCount} client{(clientCount != 1 ? "s" : string.Empty)})");
                Console.ResetColor();
            }
        }

        public static void ErrorWriteLine(string primayMessage, string? secondaryMessage = null)
        {
            lock (_logWriteLineLock)
            {
                Console.ForegroundColor= ConsoleColor.DarkGray;
                Console.Write($"[{DateTime.Now}] ");
                Console.ResetColor();

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