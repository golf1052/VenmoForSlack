using System;

namespace VenmoForSlack.Venmo
{
    public enum VenmoAudience
    {
        Private,
        Friends,
        Public
    }

    public static class VenmoAudienceHelperMethods
    {
        public static string ToString(this VenmoAudience venmoAudience)
        {
            if (venmoAudience == VenmoAudience.Private)
            {
                return "private";
            }
            else if (venmoAudience == VenmoAudience.Friends)
            {
                return "friends";
            }
            else if (venmoAudience == VenmoAudience.Public)
            {
                return "public";
            }
            else
            {
                throw new Exception($"Unknown VenmoAudience: {venmoAudience.ToString()}");
            }
        }

        public static VenmoAudience FromString(string venmoAudience)
        {
            if (venmoAudience == "private")
            {
                return VenmoAudience.Private;
            }
            else if (venmoAudience == "friends")
            {
                return VenmoAudience.Friends;
            }
            else if (venmoAudience == "public")
            {
                return VenmoAudience.Public;
            }
            else
            {
                throw new Exception($"Unknown string: {venmoAudience}");
            }
        }
    } 
}
