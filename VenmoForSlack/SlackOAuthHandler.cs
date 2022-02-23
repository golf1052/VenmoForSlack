using System;
using System.Threading.Tasks;

namespace VenmoForSlack
{
    public class SlackOAuthHandler<T>
    {
        public string RequestAuthString { get; private set; }
        private Func<string, Task<T>> completeAuthMethod;

        public SlackOAuthHandler(string serviceName, string authUrl, string completeAuthString, Func<string, Task<T>> completeAuthMethod)
        {
            RequestAuthString = $"Authenticate to {serviceName} with the following URL: {authUrl} then send back the " +
                $"auth code in this format\n{completeAuthString}";
            this.completeAuthMethod = completeAuthMethod;
        }

        public async Task<T> CompleteAuth(string code)
        {
            return await completeAuthMethod(code);
        }
    }
}
