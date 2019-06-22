using System;

using AzureAppService.LetsEncrypt.Internal;

namespace AzureAppService.LetsEncrypt
{
    public static class RetryStrategy
    {
        public static bool RetriableException(Exception exception)
        {
            return exception.InnerException is RetriableActivityException;
        }
    }
}
