using System;
using System.Collections.Generic;

using Azure.Identity;
using Azure.ResourceManager;

namespace AppService.Acmebot.Internal;

public class AzureEnvironment
{
    public Uri AuthorityHost { get; init; }
    public ArmEnvironment ResourceManager { get; init; }
    public string AppService { get; init; }
    public string TrafficManager { get; init; }

    public static AzureEnvironment Get(string name) => s_environments[name];

    private static readonly Dictionary<string, AzureEnvironment> s_environments = new()
    {
        {
            "AzureCloud",
            new AzureEnvironment
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
                ResourceManager = ArmEnvironment.AzurePublicCloud,
                AppService = ".azurewebsites.net",
                TrafficManager = ".trafficmanager.net"
            }
        },
        {
            "AzureChinaCloud",
            new AzureEnvironment
            {
                AuthorityHost = AzureAuthorityHosts.AzureChina,
                ResourceManager = ArmEnvironment.AzureChina,
                AppService = ".chinacloudsites.cn",
                TrafficManager = ".trafficmanager.cn"
            }
        },
        {
            "AzureUSGovernment",
            new AzureEnvironment
            {
                AuthorityHost = AzureAuthorityHosts.AzureGovernment,
                ResourceManager = ArmEnvironment.AzureGovernment,
                AppService = ".azurewebsites.us",
                TrafficManager = ".usgovtrafficmanager.net"
            }
        }
    };
}
