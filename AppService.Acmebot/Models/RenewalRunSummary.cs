using System.Collections.Generic;

namespace AppService.Acmebot.Models;

/// <summary>
/// Aggregate result of one nightly renewal run, delivered as a single webhook
/// digest per instance. Built inside the orchestration (replay-safe), so it
/// does not depend on Application Insights telemetry surviving the run.
/// </summary>
public class RenewalRunSummary
{
    public int ExpiringCount { get; set; }

    /// <summary>
    /// Total Acmebot certificate inventory; only computed on runs that found
    /// nothing due, to make a silently EMPTY certificate list (seen once with
    /// an api-version regression) distinguishable from a genuinely quiet night.
    /// </summary>
    public int? InventoryCount { get; set; }

    public double DurationMinutes { get; set; }

    public IReadOnlyList<CertificateRenewalResult> Results { get; set; }
}
