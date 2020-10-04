using System;
using System.Collections.Generic;

namespace AppService.Acmebot.Internal
{
    public interface IAzureEnvironment
    {
        Uri ActiveDirectory { get; }
        Uri ResourceManager { get; }
        string AppService { get; }
        string TrafficManager { get; }
    }

    internal class AzureEnvironment : IAzureEnvironment
    {
        public Uri ActiveDirectory { get; private set; }
        public Uri ResourceManager { get; private set; }
        public string AppService { get; private set; }
        public string TrafficManager { get; private set; }

        public static AzureEnvironment Get(string name)
        {
            return _environments[name];
        }

        private static readonly Dictionary<string, AzureEnvironment> _environments = new Dictionary<string, AzureEnvironment>
        {
            {
                "AzureCloud", new AzureEnvironment
                {
                    ActiveDirectory = new Uri("https://login.microsoftonline.com"),
                    ResourceManager = new Uri("https://management.azure.com"),
                    AppService = ".azurewebsites.net",
                    TrafficManager = ".trafficmanager.net"
                }
            },
            {
                "AzureChinaCloud", new AzureEnvironment
                {
                    ActiveDirectory = new Uri("https://login.chinacloudapi.cn"),
                    ResourceManager = new Uri("https://management.chinacloudapi.cn"),
                    AppService = ".chinacloudsites.cn",
                    TrafficManager = ".trafficmanager.cn"
                }
            },
            {
                "AzureUSGovernment", new AzureEnvironment
                {
                    ActiveDirectory = new Uri("https://login.microsoftonline.us"),
                    ResourceManager = new Uri("https://management.usgovcloudapi.net"),
                    AppService = ".azurewebsites.us",
                    TrafficManager = ".usgovtrafficmanager.net"
                }
            }
        };
    }
}
