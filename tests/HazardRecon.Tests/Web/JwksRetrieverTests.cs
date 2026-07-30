using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using HazardRecon.Web.Supabase;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace HazardRecon.Tests.Web;

public class JwksRetrieverTests
{
    private static readonly RSA Signing = RSA.Create(2048);

    /// <summary>Serves a fixed document, standing in for the network fetch.</summary>
    private class StaticDocumentRetriever : IDocumentRetriever
    {
        private readonly string _document;

        public StaticDocumentRetriever(string document) => _document = document;

        public Task<string> GetDocumentAsync(string address, CancellationToken cancel) =>
            Task.FromResult(_document);
    }

    /// <summary>A key set in the shape Supabase serves at /auth/v1/.well-known/jwks.json.</summary>
    private static string JwksFor(RSA rsa, string kid = "test-key")
    {
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: false);
        string n = Base64UrlEncoder.Encode(p.Modulus);
        string e = Base64UrlEncoder.Encode(p.Exponent);

        return $$"""
        {"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"{{kid}}","n":"{{n}}","e":"{{e}}"}]}
        """;
    }

    private static Task<OpenIdConnectConfiguration> Retrieve(string document) =>
        new JwksRetriever().GetConfigurationAsync(
            "https://ref.supabase.co/auth/v1/.well-known/jwks.json",
            new StaticDocumentRetriever(document),
            CancellationToken.None);

    [Fact]
    public async Task TestABareKeySetYieldsItsSigningKey()
    {
        OpenIdConnectConfiguration config = await Retrieve(JwksFor(Signing));

        Assert.Single(config.SigningKeys);
        Assert.Equal("test-key", config.SigningKeys.First().KeyId);
    }

    [Fact]
    public async Task TestEveryKeyInTheSetIsReturned()
    {
        using RSA second = RSA.Create(2048);
        RSAParameters a = Signing.ExportParameters(false);
        RSAParameters b = second.ExportParameters(false);

        string twoKeys = $$"""
        {"keys":[
          {"kty":"RSA","use":"sig","alg":"RS256","kid":"one","n":"{{Base64UrlEncoder.Encode(a.Modulus)}}","e":"{{Base64UrlEncoder.Encode(a.Exponent)}}"},
          {"kty":"RSA","use":"sig","alg":"RS256","kid":"two","n":"{{Base64UrlEncoder.Encode(b.Modulus)}}","e":"{{Base64UrlEncoder.Encode(b.Exponent)}}"}
        ]}
        """;

        OpenIdConnectConfiguration config = await Retrieve(twoKeys);

        // key rotation publishes the old and new key together - dropping either
        // one silently invalidates half the live sessions
        Assert.Equal(2, config.SigningKeys.Count);
        Assert.Contains(config.SigningKeys, k => k.KeyId == "one");
        Assert.Contains(config.SigningKeys, k => k.KeyId == "two");
    }

    [Fact]
    public async Task TestTheRetrievedKeyActuallyValidatesARealToken()
    {
        OpenIdConnectConfiguration config = await Retrieve(JwksFor(Signing));

        SigningCredentials creds = new(new RsaSecurityKey(Signing), SecurityAlgorithms.RsaSha256);
        JwtSecurityToken jwt = new(
            issuer: "https://ref.supabase.co/auth/v1",
            audience: "authenticated",
            claims: new[] { new Claim("sub", "11111111-1111-1111-1111-111111111111") },
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(60),
            signingCredentials: creds);
        string token = new JwtSecurityTokenHandler().WriteToken(jwt);

        TokenValidationParameters parameters = SupabaseJwt.BuildValidationParameters(new SupabaseOptions
        {
            Url = "https://ref.supabase.co",
            AnonKey = "anon-key",
            ServiceRoleKey = "service-key"
        });
        parameters.IssuerSigningKeys = config.SigningKeys;

        ClaimsPrincipal principal = new JwtSecurityTokenHandler()
            .ValidateToken(token, parameters, out _);

        Assert.Equal(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SupabaseJwt.UserId(principal));
    }

    [Fact]
    public async Task TestAnEmptyKeySetYieldsNoKeys()
    {
        OpenIdConnectConfiguration config = await Retrieve("""{"keys":[]}""");

        Assert.Empty(config.SigningKeys);
    }
}
