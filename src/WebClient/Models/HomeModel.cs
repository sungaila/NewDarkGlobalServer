using Sungaila.NewDark.Core;
using System.Reflection;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.WebClient.Models
{
    public class HomeModel
    {
        public string GlobalServerAddress { get; set; } = "wss://thief2.sungaila.de:5200";

        public bool ShowClosed { get; set; } = true;

        public bool ShowDenied { get; set; } = true;

        public GameTextEncoding NameEncoding { get; set; } = GameTextEncoding.Automatic;

        public string? StatusMessage { get; set; }

        public List<WebSocketServerInfo> Servers { get; } = [];

        public IQueryable<WebSocketServerInfo> ServersFiltered
        {
            get
            {
                return Servers
                    .Where(s => ShowClosed || !s.Status.HasFlag(WebSocketServerStatus.Closed))
                    .Where(s => ShowDenied || !s.Status.HasFlag(WebSocketServerStatus.Denied))
                    .Select(s => s with
                    {
                        ServerName = GameText.Decode(s.ServerName, NameEncoding),
                        MapName = GameText.Decode(s.MapName, NameEncoding)
                    })
                    .AsQueryable();
            }
        }

        public string InformationalVersion { get; } = Assembly.GetExecutingAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
    }
}