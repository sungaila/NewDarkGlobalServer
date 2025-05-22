using Microsoft.AspNetCore.Components.QuickGrid;
using Sungaila.NewDark.Core;
using Sungaila.NewDark.WebClient.Models;
using System.Net.WebSockets;
using System.Text.Json;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.WebClient.Pages
{
    public partial class Home
    {
        private static readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(1);

        private DateTime _lastRefresh = DateTime.MinValue;

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
            if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) == 0)
            {
                Model.Servers.Clear();

                Model.StatusMessage = $"Connecting to Global Server …";

                var diff = DateTime.Now.Subtract(_lastRefresh);
                if (diff < _refreshInterval)
                {
                    await Task.Delay(_refreshInterval - diff);
                }

                _lastRefresh = DateTime.Now;

                using var client = new ClientWebSocket();

                try
                {
                    await client.ConnectAsync(new Uri(Model.GlobalServerAddress), CancellationToken.None);
                    var buffer = new ArraySegment<byte>(new byte[1024]);
                    var result = await client.ReceiveAsync(buffer, CancellationToken.None);
                    var message = System.Text.Encoding.UTF8.GetString(buffer.Array!, 0, result.Count);

                    var obj = JsonSerializer.Deserialize(message, SourceGenerationContext.Default.ListWebSocketServerInfo)!;

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
        }
    }
}