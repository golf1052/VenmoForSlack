using Newtonsoft.Json;

namespace VenmoForSlack.Models
{
    public class WorkspaceInfo
    {
        [JsonProperty]
        public string Token { get; set; }

        [JsonProperty]
        public string BotToken { get; set; }
    }
}
