using Microsoft.AspNetCore.Mvc;

namespace VenmoForSlack.Controllers.Models
{
    public class SlackRequest
    {
        public string? Token { get; set; }
        
        [BindProperty(Name = "team_id")]
        public string? TeamId { get; set; }

        [BindProperty(Name = "team_domain")]
        public string? TeamDomain { get; set; }

        [BindProperty(Name = "channel_id")]
        public string? ChannelId { get; set; }

        [BindProperty(Name = "channel_name")]
        public string? ChannelName { get; set; }

        [BindProperty(Name = "user_id")]
        public string? UserId { get; set; }

        [BindProperty(Name = "user_name")]
        public string? UserName { get; set; }

        public string? Command { get; set; }

        public string? Text { get; set; }

        [BindProperty(Name = "response_url")]
        public string? ResponseUrl { get; set; }
    }
}
