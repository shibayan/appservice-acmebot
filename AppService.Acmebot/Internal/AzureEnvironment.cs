using System;
using System.Collections.Generic;

using Azure.ResourceManager;

namespace AppService.Acmebot.Internal;

public class AzureEnvironment
{
    public Uri ActiveDirectory { get; init; }
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
                ActiveDirectory = new Uri("https://login.microsoftonline.com"),
                ResourceManager = ArmEnvironment.AzurePublicCloud,
                AppService = ".azurewebsites.net",
                TrafficManager = ".trafficmanager.net"
            }
        },
        {
            "AzureChinaCloud",
            new AzureEnvironment
            {
                ActiveDirectory = new Uri("https://login.chinacloudapi.cn"),
                ResourceManager = ArmEnvironment.AzureChina,
                AppService = ".chinacloudsites.cn",
                TrafficManager = ".trafficmanager.cn"
            }
        },
        {
            "AzureUSGovernment",
            new AzureEnvironment
            {
                ActiveDirectory = new Uri("https://login.microsoftonline.us"),
                ResourceManager = ArmEnvironment.AzureGovernment,
                AppService = ".azurewebsites.us",
                TrafficManager = ".usgovtrafficmanager.net"
            }
        }
    };
}
