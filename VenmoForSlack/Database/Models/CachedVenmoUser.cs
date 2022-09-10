namespace VenmoForSlack.Database.Models
{
    public class CachedVenmoUser
    {
        public string Id { get; set; }

        public CachedVenmoUser(string id)
        {
            Id = id;
        }
    }
}
