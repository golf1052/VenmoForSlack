using System;

namespace VenmoForSlack.Venmo
{
    public enum VenmoAction
    {
        Charge,
        Pay
    }

    public static class VenmoActionHelperMethods
    {
        public static string ToString(this VenmoAction venmoAction)
        {
            if (venmoAction == VenmoAction.Charge)
            {
                return "charge";
            }
            else if (venmoAction == VenmoAction.Pay)
            {
                return "pay";
            }
            else
            {
                throw new Exception($"Unknown VenmoAction: {venmoAction.ToString()}");
            }
        }

        public static VenmoAction FromString(string venmoAction)
        {
            if (venmoAction == "charge")
            {
                return VenmoAction.Charge;
            }
            else if (venmoAction == "pay")
            {
                return VenmoAction.Pay;
            }
            else
            {
                throw new Exception($"Unknown Venmo action: {venmoAction}");
            }
        }
    }
}
