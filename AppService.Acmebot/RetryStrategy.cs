using System;

using AppService.Acmebot.Internal;

namespace AppService.Acmebot
{
    public static class RetryStrategy
    {
        public static bool RetriableException(Exception exception)
        {
            return exception.InnerException is RetriableActivityException;
        }
    }
}
