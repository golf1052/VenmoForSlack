using System;

namespace VenmoForSlack.Providers
{
    public interface ICacheItemLifetimeProvider
    {
        public TimeSpan CacheItemLifetime { get { return TimeSpan.FromDays(1); } }
    }

    public class CacheItemLifetimeProvider : ICacheItemLifetimeProvider
    {
    }
}
