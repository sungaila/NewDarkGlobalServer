using System.Collections.Generic;
using System.Text.Json.Serialization;
using static Sungaila.NewDark.Core.Messages;

namespace Sungaila.NewDark.Core
{
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(WebSocketServerInfo))]
    [JsonSerializable(typeof(List<WebSocketServerInfo>))]
    public partial class SourceGenerationContext : JsonSerializerContext
    {
    }
}