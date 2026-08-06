using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.DurableTask.Client;

namespace AppService.Acmebot.Functions;

internal static class DurableClientExtensions
{
    /// <summary>
    /// Waits for an orchestration to complete and returns its JSON output as the HTTP
    /// response, replacing the in-process WaitForCompletionOrCreateCheckStatusResponseAsync.
    /// </summary>
    public static async Task<IActionResult> WaitForCompletionResultAsync(this DurableTaskClient client, string instanceId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        OrchestrationMetadata metadata;

        try
        {
            metadata = await client.WaitForInstanceCompletionAsync(instanceId, getInputsAndOutputs: true, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Still running when the timeout elapsed
            return new AcceptedResult();
        }

        if (metadata.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
        {
            return new ContentResult
            {
                Content = metadata.SerializedOutput,
                ContentType = "application/json",
                StatusCode = StatusCodes.Status200OK
            };
        }

        return new ObjectResult(metadata.FailureDetails?.ErrorMessage ?? $"Orchestration is {metadata.RuntimeStatus}.")
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };
    }
}
