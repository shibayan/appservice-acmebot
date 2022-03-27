using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Rest;

namespace AppService.Acmebot.Internal;

internal class AppAuthenticationTokenProvider : ITokenProvider
{
    public AppAuthenticationTokenProvider(AzureEnvironment environment)
    {
        _environment = environment;
        _tokenProvider = new AzureServiceTokenProvider(azureAdInstance: _environment.ActiveDirectory);
    }

    private readonly AzureEnvironment _environment;
    private readonly AzureServiceTokenProvider _tokenProvider;

    public async Task<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
    {
        var accessToken = await _tokenProvider.GetAccessTokenAsync(_environment.ResourceManager, cancellationToken: cancellationToken);

        return new AuthenticationHeaderValue("Bearer", accessToken);
    }
}
