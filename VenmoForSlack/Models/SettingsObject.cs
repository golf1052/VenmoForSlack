using Newtonsoft.Json;

namespace VenmoForSlack.Models
{
    public class SettingsObject
    {
        [JsonProperty]
        public WorkspacesObject Workspaces { get; set; }
    }
}
