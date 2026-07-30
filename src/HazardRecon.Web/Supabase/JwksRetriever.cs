using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace HazardRecon.Web.Supabase;

/// <summary>
/// Reads a bare JWKS document into an OpenIdConnectConfiguration.
///
/// Supabase publishes signing keys at /auth/v1/.well-known/jwks.json but does not
/// yet serve an OpenID discovery document, so the stock retriever - which expects
/// discovery and follows its jwks_uri - has nothing to read. Going straight to the
/// key set works both before and after discovery ships, which is why this is the
/// only path rather than a fallback.
/// </summary>
public class JwksRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        string json = await retriever.GetDocumentAsync(address, cancel);

        OpenIdConnectConfiguration config = new();
        JsonWebKeySet keySet = new(json);

        foreach (SecurityKey key in keySet.GetSigningKeys())
        {
            config.SigningKeys.Add(key);
        }

        return config;
    }
}
