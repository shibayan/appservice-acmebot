using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http;

namespace AppService.Acmebot.Internal;

internal static class IdentityExtensions
{
    private const string AppRole = "Acmebot.IssueCertificate";

    private const string ClientPrincipalHeader = "x-ms-client-principal";

    private static bool IsAppRoleRequired => bool.TryParse(Environment.GetEnvironmentVariable("Acmebot:AppRoleRequired"), out var result) && result;

    public static bool IsEasyAuthEnabled => bool.TryParse(Environment.GetEnvironmentVariable("WEBSITE_AUTH_ENABLED"), out var result) && result;

    private static bool IsInAppRole(this ClaimsPrincipal claimsPrincipal, string role)
    {
        var roles = claimsPrincipal.Claims.Where(x => x.Type == "roles").Select(x => x.Value);

        return roles.Contains(role);
    }

    public static bool IsAppAuthorized(this ClaimsPrincipal claimsPrincipal)
    {
        if (!claimsPrincipal.Identity.IsAuthenticated)
        {
            return false;
        }

        if (IsAppRoleRequired && !claimsPrincipal.IsInAppRole(AppRole))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Authorization for the function-key protected API endpoints. The host has already
    /// validated the function key (AuthorizationLevel.Function); unlike the in-process
    /// model, the isolated worker does not synthesize an authenticated principal for
    /// key-based callers, so the principal check only applies when App Service
    /// Authentication (Easy Auth) is enabled.
    /// </summary>
    public static bool IsAppAuthorized(this HttpRequest req)
    {
        if (!IsEasyAuthEnabled)
        {
            return true;
        }

        return req.ParseClientPrincipal().IsAppAuthorized();
    }

    /// <summary>
    /// Reconstructs the Easy Auth principal from the x-ms-client-principal header, which
    /// the isolated worker does not materialize into HttpContext.User by itself.
    /// </summary>
    public static ClaimsPrincipal ParseClientPrincipal(this HttpRequest req)
    {
        if (!req.Headers.TryGetValue(ClientPrincipalHeader, out var header))
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        try
        {
            var decoded = Convert.FromBase64String(header[0]);
            var principal = JsonSerializer.Deserialize<ClientPrincipal>(decoded);

            var identity = new ClaimsIdentity(principal.IdentityProvider, principal.NameClaimType, principal.RoleClaimType);
            identity.AddClaims(principal.Claims.Select(x => new Claim(x.Type, x.Value)));

            return new ClaimsPrincipal(identity);
        }
        catch (Exception)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
    }

    private sealed class ClientPrincipal
    {
        [JsonPropertyName("auth_typ")]
        public string IdentityProvider { get; set; }

        [JsonPropertyName("name_typ")]
        public string NameClaimType { get; set; }

        [JsonPropertyName("role_typ")]
        public string RoleClaimType { get; set; }

        [JsonPropertyName("claims")]
        public IEnumerable<ClientPrincipalClaim> Claims { get; set; } = Array.Empty<ClientPrincipalClaim>();
    }

    private sealed class ClientPrincipalClaim
    {
        [JsonPropertyName("typ")]
        public string Type { get; set; }

        [JsonPropertyName("val")]
        public string Value { get; set; }
    }
}
