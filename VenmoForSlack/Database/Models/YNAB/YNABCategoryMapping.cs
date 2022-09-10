namespace VenmoForSlack.Database.Models.YNAB
{
    public class YNABCategoryMapping
    {
        public string CategoryName { get; set; }
        public string CategoryId { get; set; }
        public string VenmoNote { get; set; }

        public YNABCategoryMapping(string categoryName, string categoryId, string venmoNote)
        {
            CategoryName = categoryName;
            CategoryId = categoryId;
            VenmoNote = venmoNote;
        }
    }
}
