using System.Collections.Generic;

namespace AppService.Acmebot.Internal;

public class AzureEnvironment
{
    public string ActiveDirectory { get; set; }
    public string ResourceManager { get; set; }
    public string AppService { get; set; }
    public string TrafficManager { get; set; }

    public static AzureEnvironment Get(string name)
    {
        return _environments[name];
    }

    private static readonly Dictionary<string, AzureEnvironment> _environments = new Dictionary<string, AzureEnvironment>
        {
            {
                "AzureCloud", new AzureEnvironment
                {
                    ActiveDirectory = "https://login.microsoftonline.com",
                    ResourceManager = "https://management.azure.com",
                    AppService = ".azurewebsites.net",
                    TrafficManager = ".trafficmanager.net"
                }
            },
            {
                "AzureChinaCloud", new AzureEnvironment
                {
                    ActiveDirectory = "https://login.chinacloudapi.cn",
                    ResourceManager = "https://management.chinacloudapi.cn",
                    AppService = ".chinacloudsites.cn",
                    TrafficManager = ".trafficmanager.cn"
                }
            },
            {
                "AzureUSGovernment", new AzureEnvironment
                {
                    ActiveDirectory = "https://login.microsoftonline.us",
                    ResourceManager = "https://management.usgovcloudapi.net",
                    AppService = ".azurewebsites.us",
                    TrafficManager = ".usgovtrafficmanager.net"
                }
            }
        };
}
