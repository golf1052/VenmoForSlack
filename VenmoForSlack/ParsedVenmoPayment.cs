using System.Collections.Generic;
using VenmoForSlack.Venmo;

namespace VenmoForSlack
{
    public class ParsedVenmoPayment
    {
        public double Amount { get; private set; }
        public string Note { get; private set; }
        public List<string> Recipients { get; private set; }
        public VenmoAction Action { get; private set; }
        public VenmoAudience Audience { get; private set; }

        public ParsedVenmoPayment(double amount,
            string note,
            List<string> recipients,
            VenmoAction action,
            VenmoAudience audience)
        {
            Amount = amount;
            Note = note;
            Recipients = recipients;
            Action = action;
            Audience = audience;
        }
    }
}
