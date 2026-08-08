using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using AppService.Acmebot.Models;
using AppService.Acmebot.Options;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

namespace AppService.Acmebot.Internal;

public class WebhookInvoker
{
    public WebhookInvoker(IHttpClientFactory httpClientFactory, IOptions<AcmebotOptions> options, ILogger<WebhookInvoker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AcmebotOptions _options;
    private readonly ILogger<WebhookInvoker> _logger;

    public Task SendCompletedEventAsync(string appName, string slotName, DateTimeOffset? expirationDate, IEnumerable<string> dnsNames)
    {
        if (string.IsNullOrEmpty(_options.Webhook))
        {
            return Task.CompletedTask;
        }

        object model;

        if (_options.Webhook.Contains("hooks.slack.com"))
        {
            model = new
            {
                username = "Acmebot",
                attachments = new[]
                {
                        new
                        {
                            text = "A new certificate has been issued.",
                            color = "good",
                            fields = new object[]
                            {
                                new
                                {
                                    title = "App Name",
                                    value= appName,
                                    @short = true
                                },
                                new
                                {
                                    title = "Slot Name",
                                    value = slotName,
                                    @short = true
                                },
                                new
                                {
                                    title = "Expiration Date",
                                    value = expirationDate
                                },
                                new
                                {
                                    title = "DNS Names",
                                    value = string.Join("\n", dnsNames)
                                }
                            }
                        }
                    }
            };
        }
        else if (_options.Webhook.Contains(".office.com"))
        {
            model = new
            {
                title = "Acmebot",
                text = $"A new certificate has been issued.\n\n**App Name**: {appName}\n\n**Slot Name**: {slotName}\n\n**Expiration Date**: {expirationDate}\n\n**DNS Names**: {string.Join(", ", dnsNames)}",
                themeColor = "2EB886"
            };
        }
        else
        {
            model = new
            {
                appName,
                slotName,
                dnsNames
            };
        }

        return SendEventAsync(model);
    }

    public Task SendFailedEventAsync(string functionName, string reason)
    {
        if (string.IsNullOrEmpty(_options.Webhook))
        {
            return Task.CompletedTask;
        }

        object model;

        if (_options.Webhook.Contains("hooks.slack.com"))
        {
            model = new
            {
                username = "Acmebot",
                attachments = new[]
                {
                        new
                        {
                            title = functionName,
                            text = reason,
                            color = "danger"
                        }
                    }
            };
        }
        else if (_options.Webhook.Contains(".office.com"))
        {
            model = new
            {
                title = "Acmebot",
                text = $"**{functionName}**\n\n**Reason**\n\n{reason}",
                themeColor = "A30200"
            };
        }
        else
        {
            model = new
            {
                functionName,
                reason
            };
        }

        return SendEventAsync(model);
    }

    public Task SendRenewalSummaryAsync(RenewalRunSummary summary)
    {
        if (string.IsNullOrEmpty(_options.Webhook))
        {
            _logger.LogInformation("Renewal digest not sent - Acmebot:Webhook is not configured.");

            return Task.CompletedTask;
        }

        // Identifies the BOT instance (app + slot), unlike the per-certificate events
        // which carry the TARGET site; lets both instances share one channel.
        var instance = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME")?.Split('.')[0] ?? "localhost";
        var endpoint = _options.Endpoint?.Host ?? "unknown";

        var renewed = summary.Results.Where(x => x.Outcome == CertificateRenewalResult.Renewed).ToArray();
        var skipped = summary.Results.Where(x => x.Outcome == CertificateRenewalResult.Skipped).ToArray();
        var failed = summary.Results.Where(x => x.Outcome == CertificateRenewalResult.Failed).ToArray();

        var inventoryEmpty = summary.InventoryCount == 0;

        object model;

        if (_options.Webhook.Contains("hooks.slack.com"))
        {
            var color = failed.Length > 0 || inventoryEmpty ? "danger" : skipped.Length > 0 ? "warning" : "good";

            var fields = new List<object>
            {
                new { title = "Instance", value = instance, @short = true },
                new { title = "Endpoint", value = endpoint, @short = true },
                new { title = "Due", value = summary.ExpiringCount.ToString(), @short = true },
                new { title = "Duration", value = $"{summary.DurationMinutes:0} min", @short = true }
            };

            if (summary.InventoryCount is not null)
            {
                fields.Add(new
                {
                    title = "Certificate inventory",
                    value = inventoryEmpty ? "EMPTY - possible certificate-list regression, verify!" : $"{summary.InventoryCount} certificates, none due",
                    @short = false
                });
            }

            if (renewed.Length > 0)
            {
                fields.Add(new { title = $"Renewed ({renewed.Length})", value = FormatResults(renewed), @short = false });
            }

            if (failed.Length > 0)
            {
                fields.Add(new { title = $"Failed ({failed.Length})", value = FormatResults(failed), @short = false });
            }

            if (skipped.Length > 0)
            {
                fields.Add(new { title = $"Fast-skipped ({skipped.Length})", value = FormatResults(skipped), @short = false });
            }

            model = new
            {
                username = "Acmebot",
                attachments = new[]
                {
                    new
                    {
                        text = $"Nightly renewal digest - {instance}",
                        color,
                        fields = fields.ToArray()
                    }
                }
            };
        }
        else if (_options.Webhook.Contains(".office.com"))
        {
            var text = new StringBuilder($"**Instance**: {instance}\n\n**Endpoint**: {endpoint}\n\n**Due**: {summary.ExpiringCount}\n\n**Duration**: {summary.DurationMinutes:0} min");

            if (summary.InventoryCount is not null)
            {
                text.Append($"\n\n**Certificate inventory**: {(inventoryEmpty ? "EMPTY - possible certificate-list regression, verify!" : $"{summary.InventoryCount} certificates, none due")}");
            }

            if (renewed.Length > 0)
            {
                text.Append($"\n\n**Renewed ({renewed.Length})**\n\n{FormatResults(renewed)}");
            }

            if (failed.Length > 0)
            {
                text.Append($"\n\n**Failed ({failed.Length})**\n\n{FormatResults(failed)}");
            }

            if (skipped.Length > 0)
            {
                text.Append($"\n\n**Fast-skipped ({skipped.Length})**\n\n{FormatResults(skipped)}");
            }

            model = new
            {
                title = $"Acmebot renewal digest - {instance}",
                text = text.ToString(),
                themeColor = failed.Length > 0 || inventoryEmpty ? "A30200" : "2EB886"
            };
        }
        else
        {
            model = new
            {
                instance,
                endpoint,
                expiringCount = summary.ExpiringCount,
                inventoryCount = summary.InventoryCount,
                durationMinutes = summary.DurationMinutes,
                results = summary.Results
            };
        }

        return SendEventAsync(model);
    }

    private static string FormatResults(IReadOnlyList<CertificateRenewalResult> results)
    {
        // Slack attachment fields cap out; keep digests readable on bad nights.
        const int maxLines = 15;

        var lines = results.Take(maxLines)
                           .Select(x => string.IsNullOrEmpty(x.Detail) ? x.DnsNames : $"{x.DnsNames} - {x.Detail}");

        var text = string.Join("\n", lines);

        return results.Count > maxLines ? $"{text}\n(+{results.Count - maxLines} more)" : text;
    }

    private async Task SendEventAsync(object model)
    {
        var httpClient = _httpClientFactory.CreateClient();

        var content = new StringContent(JsonConvert.SerializeObject(model), Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(_options.Webhook, content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning($"Failed invoke webhook. Status Code = {response.StatusCode}, Reason = {await response.Content.ReadAsStringAsync()}");
        }
    }
}
