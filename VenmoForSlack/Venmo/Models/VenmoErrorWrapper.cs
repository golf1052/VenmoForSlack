using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoErrorWrapper<T>
    {
        [JsonProperty("error")]
        public T? Error { get; set; }
    }
}
