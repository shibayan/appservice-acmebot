using Microsoft.Extensions.Configuration;

namespace AzureLetsEncrypt
{
    internal class Settings
    {
        public Settings()
        {
            var builder = new ConfigurationBuilder()
                          .AddJsonFile("local.settings.json", true)
                          .AddEnvironmentVariables();

            _configuration = builder.Build();
            _section = _configuration.GetSection("LetsEncrypt");
        }

        private readonly IConfiguration _configuration;
        private readonly IConfiguration _section;

        public string Contacts => _section[nameof(Contacts)];

        public string SubscriptionId => _section[nameof(SubscriptionId)];

        public string ResourceGroupName => _section[nameof(ResourceGroupName)];

        public static Settings Default { get; } = new Settings();
    }
}
