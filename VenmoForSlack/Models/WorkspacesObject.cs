using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VenmoForSlack.Models
{
    public class WorkspacesObject
    {
        [JsonExtensionData]
        public IDictionary<string, JToken> Workspaces { get; set; } = null!;
    }
}
