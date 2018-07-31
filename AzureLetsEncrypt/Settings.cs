using Microsoft.Extensions.Configuration;

namespace AzureLetsEncrypt
{
    public class Settings
    {
        public Settings()
        {
            var builder = new ConfigurationBuilder()
                          .AddJsonFile("local.settings.json", true)
                          .AddEnvironmentVariables();

            _configuration = builder.Build();
        }

        private readonly IConfiguration _configuration;

        public string Contacts => _configuration[nameof(Contacts)];

        public string SubscriptionId => _configuration[nameof(SubscriptionId)];

        public static Settings Default { get; } = new Settings();
    }
}
