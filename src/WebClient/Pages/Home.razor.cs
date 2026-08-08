using Microsoft.AspNetCore.Components.QuickGrid;
using Sungaila.NewDark.Core;
using Sungaila.NewDark.WebClient.Models;
using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.WebClient.Pages
{
    public partial class Home
    {
        private static readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(1);

        private long? _lastRefreshTimestamp;

        private int _isRefreshing = 0;

        public HomeModel Model { get; set; } = new();

        public GridSort<WebSocketServerInfo> PlayerSort = GridSort<WebSocketServerInfo>
            .ByAscending(x => x.CurrentPlayers)
            .ThenAscending(x => x.MaxPlayers);

        protected override async Task OnInitializedAsync()
        {
            await RefreshServerList();
        }

        public async Task OnRefresh()
        {
            await RefreshServerList();
        }

        private async Task RefreshServerList()
        {
            if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0)
            {
                return;
            }

            Model.Servers.Clear();
            Model.StatusMessage = "Connecting to Global Server …";

            await WaitForRefreshInterval();

            using var client = new ClientWebSocket();

            try
            {
                await client.ConnectAsync(new Uri(Model.GlobalServerAddress), CancellationToken.None);

                var obj = await ReceiveServerListAsync(client);

                Model.Servers.AddRange(obj);

                Model.StatusMessage = Model.Servers.Count == 0
                    ? "No game servers found."
                    : null;
            }
            catch (Exception ex)
            {
                Model.StatusMessage = $"Failed to connect to Global Server:\n{ex.Message}";
            }
            finally
            {
                _isRefreshing = 0;
            }
        }

        private async Task WaitForRefreshInterval()
        {
            if (_lastRefreshTimestamp is long lastRefresh)
            {
                var elapsed = Stopwatch.GetElapsedTime(lastRefresh);

                if (elapsed < _refreshInterval)
                {
                    await Task.Delay(_refreshInterval - elapsed);
                }
            }

            _lastRefreshTimestamp = Stopwatch.GetTimestamp();
        }

        private static async Task<List<WebSocketServerInfo>> ReceiveServerListAsync(ClientWebSocket client)
        {
            const int BufferSize = 4096;

            var buffer = new byte[BufferSize];
            var message = new ArrayBufferWriter<byte>(BufferSize);

            while (true)
            {
                var result = await client.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("Global Server closed the connection.");
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new WebSocketException($"Unexpected WebSocket message type: {result.MessageType}");
                }

                message.Write(buffer.AsSpan(0, result.Count));

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return JsonSerializer.Deserialize(message.WrittenSpan, SourceGenerationContext.Default.ListWebSocketServerInfo)
                ?? throw new JsonException("Global Server returned no server list.");
        }
    }
}