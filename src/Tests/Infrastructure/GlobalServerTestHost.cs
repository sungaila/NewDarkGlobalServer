using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sungaila.NewDark.GlobalServer;
using System.Net;
using System.Net.Sockets;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.Tests.Infrastructure;

internal sealed class GlobalServerTestHost : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly IHost _host;
    private bool _stopped;

    public int TcpPort { get; }
    public int? WebSocketPort { get; }
    public TcpGlobalServer TcpServer { get; }

    private GlobalServerTestHost(IHost host, int tcpPort, int? webSocketPort)
    {
        _host = host;
        TcpPort = tcpPort;
        WebSocketPort = webSocketPort;
        TcpServer = host.Services.GetRequiredService<TcpGlobalServer>();
    }

    public static async Task<GlobalServerTestHost> StartAsync(
        bool enableWebSocket = false,
        TimeProvider? clock = null,
        TimeSpan? directPlayQueryTimeout = null)
    {
        var ports = ReserveFreeTcpPorts(enableWebSocket ? 2 : 1);
        var tcpPort = ports[0];
        var webSocketPort = enableWebSocket ? ports[1] : (int?)null;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ApplicationName = typeof(GlobalServerTestHost).Assembly.GetName().Name
        });

        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = false);

        builder.Services.AddSingleton(sp => new TcpGlobalServer(
            sp.GetRequiredService<ILogger<TcpGlobalServer>>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            tcpPort,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            directPlayQueryTimeout ?? TimeSpan.FromMilliseconds(25),
            ShowHeartbeatMinimal: false,
            HideFailedConnections: true,
            Verbose: false,
            Clock: clock));
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TcpGlobalServer>());

        if (enableWebSocket)
        {
            builder.Services.AddSingleton(sp => new WebSocketGlobalServer(
                sp.GetRequiredService<TcpGlobalServer>(),
                sp.GetRequiredService<ILogger<WebSocketGlobalServer>>(),
                sp.GetRequiredService<IHostApplicationLifetime>(),
                IPAddress.Loopback.ToString(),
                webSocketPort!.Value,
                ssl: false));
            builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketGlobalServer>());
        }

        var host = builder.Build();
        var fixture = new GlobalServerTestHost(host, tcpPort, webSocketPort);

        try
        {
            using var startupCts = new CancellationTokenSource(StartupTimeout);
            await host.StartAsync(startupCts.Token);
            return fixture;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    public async Task WaitForServerRegistrationAsync(string serverName, CancellationToken cancellationToken)
    {
        while (!TcpServer.ServerConnections.Any(c => c.ServerInfo?.ServerName == serverName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    public async Task WaitForServerStateAsync(
        string serverName,
        string mapName,
        GameStateFlags stateFlags,
        CancellationToken cancellationToken)
    {
        while (!TcpServer.ServerConnections.Any(c =>
            c.ServerInfo is { } info &&
            info.ServerName == serverName &&
            info.MapName == mapName &&
            info.StateFlags == stateFlags))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    public async Task WaitForServerRemovalAsync(string serverName, CancellationToken cancellationToken)
    {
        while (TcpServer.ServerConnections.Any(c => c.ServerInfo?.ServerName == serverName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    public async Task StopAsync()
    {
        if (_stopped)
            return;

        _stopped = true;
        using var stopCts = new CancellationTokenSource(StopTimeout);
        await _host.StopAsync(stopCts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            _host.Dispose();
        }
    }

    private static int[] ReserveFreeTcpPorts(int count)
    {
        var listeners = new TcpListener[count];

        try
        {
            for (var i = 0; i < listeners.Length; i++)
            {
                listeners[i] = new TcpListener(IPAddress.Loopback, 0);
                listeners[i].Start();
            }

            return [.. listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port)];
        }
        finally
        {
            foreach (var listener in listeners)
                listener?.Stop();
        }
    }
}