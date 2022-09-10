namespace VenmoForSlack.Database.Models
{
    public class VenmoAlias
    {
        public string Username { get; set; }
        public string Id { get; set; }

        public VenmoAlias(string username, string id)
        {
            Username = username;
            Id = id;
        }
    }
}
