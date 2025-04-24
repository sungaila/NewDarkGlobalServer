using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.WebClient.Models
{
    public class HomeModel
    {
        public string GlobalServerName { get; set; } = "localhost";

        public int GlobalServerPort { get; set; } = 5200;

        public bool ShowClosed { get; set; } = true;

        public bool ShowDenied { get; set; } = true;

        public string? StatusMessage { get; set; }

        public List<WebSocketServerInfo> Servers { get; } = [];

        public IEnumerable<WebSocketServerInfo> ServersFiltered
        {
            get
            {
                return Servers
                    .Where(s => ShowClosed || !s.Status.HasFlag(WebSocketServerStatus.Closed))
                    .Where(s => ShowDenied || !s.Status.HasFlag(WebSocketServerStatus.Denied));
            }
        }
    }
}
