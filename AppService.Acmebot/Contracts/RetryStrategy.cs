using System;

using AppService.Acmebot.Internal;

namespace AppService.Acmebot.Contracts
{
    public static class RetryStrategy
    {
        public static bool RetriableException(Exception exception)
        {
            return exception.InnerException is RetriableActivityException;
        }
    }
}
