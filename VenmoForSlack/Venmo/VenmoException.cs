using System;
using VenmoForSlack.Venmo.Models.Responses;

namespace VenmoForSlack.Venmo
{
    public class VenmoException : Exception
    {
        public VenmoErrorObject? Error { get; private set; }

        public string? VenmoOtpSecret { get; set; }

        public VenmoException(string message) : base(message)
        {
        }

        public VenmoException(VenmoErrorObject error) : base(error.Message)
        {
            Error = error;
        }
    }
}
