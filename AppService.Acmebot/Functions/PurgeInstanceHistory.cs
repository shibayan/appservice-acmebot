using System;
using System.Threading.Tasks;

using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;

namespace AppService.Acmebot.Functions;

public class PurgeInstanceHistory
{
    [Function($"{nameof(PurgeInstanceHistory)}_{nameof(Timer)}")]
    public Task Timer([TimerTrigger("0 0 0 1 * *")] TimerInfo timer, [DurableClient] DurableTaskClient starter)
    {
        return starter.PurgeAllInstancesAsync(new PurgeInstancesFilter(
            CreatedFrom: DateTimeOffset.MinValue,
            CreatedTo: DateTimeOffset.UtcNow.AddMonths(-1),
            Statuses: new[] { OrchestrationRuntimeStatus.Completed, OrchestrationRuntimeStatus.Failed }));
    }
}
