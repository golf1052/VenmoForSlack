using Newtonsoft.Json;

namespace VenmoForSlack.Models
{
    public class WorkspaceInfo
    {
        /// <summary>
        /// Verification token for Slack.
        /// </summary>
        [JsonProperty]
        public string Token { get; set; }

        /// <summary>
        /// Slack bot token.
        /// </summary>
        [JsonProperty]
        public string BotToken { get; set; }

        [JsonConstructor]
        public WorkspaceInfo(string token, string botToken)
        {
            Token = token;
            BotToken = botToken;
        }
    }
}
