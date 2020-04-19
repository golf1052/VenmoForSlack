using System;

namespace VenmoForSlack.Venmo
{
    public class VenmoException : Exception
    {
        public VenmoException(string message) : base(message)
        {
        }
    }
}
