using System;
using System.Runtime.Serialization;

namespace AppService.Acmebot;

[Serializable]
public class RetriableOrchestratorException : Exception
{
    public RetriableOrchestratorException()
    {
    }

    public RetriableOrchestratorException(string message)
        : base(message)
    {
    }

    public RetriableOrchestratorException(string message, Exception inner)
        : base(message, inner)
    {
    }

    protected RetriableOrchestratorException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}
