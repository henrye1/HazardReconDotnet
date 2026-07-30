using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using HazardRecon.Web.Supabase;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseJwtTests
{
    private static readonly RSA Key = RSA.Create(2048);
    private static readonly RSA OtherKey = RSA.Create(2048);

    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co",
        AnonKey = "anon-key",
        ServiceRoleKey = "service-key"
    };

    private static string Token(
        RSA key,
        string issuer = "https://ref.supabase.co/auth/v1",
        string audience = "authenticated",
        int minutesValid = 60,
        string subject = "11111111-1111-1111-1111-111111111111")
    {
        SigningCredentials creds = new(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256);
        JwtSecurityToken token = new(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim("sub", subject) },
            // must stay before expires, or the token cannot be constructed at all
            // - which is what makes the already-expired case buildable
            notBefore: DateTime.UtcNow.AddMinutes(Math.Min(-5, minutesValid - 1)),
            expires: DateTime.UtcNow.AddMinutes(minutesValid),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// The real parameters, with the signing key pinned locally instead of
    /// fetched from JWKS - everything else under test is the production config.
    /// </summary>
    private static TokenValidationParameters Parameters(RSA key)
    {
        TokenValidationParameters p = SupabaseJwt.BuildValidationParameters(Options());
        p.IssuerSigningKey = new RsaSecurityKey(key);
        return p;
    }

    private static ClaimsPrincipal Validate(string token, RSA key) =>
        new JwtSecurityTokenHandler().ValidateToken(token, Parameters(key), out _);

    [Fact]
    public void TestAValidTokenIsAccepted()
    {
        ClaimsPrincipal principal = Validate(Token(Key), Key);

        Assert.Equal(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SupabaseJwt.UserId(principal));
    }

    [Fact]
    public void TestATokenSignedByAnotherKeyIsRejected()
    {
        Assert.ThrowsAny<SecurityTokenException>(() => Validate(Token(OtherKey), Key));
    }

    [Fact]
    public void TestAnExpiredTokenIsRejected()
    {
        Assert.Throws<SecurityTokenExpiredException>(
            () => Validate(Token(Key, minutesValid: -10), Key));
    }

    [Fact]
    public void TestATokenFromAnotherIssuerIsRejected()
    {
        Assert.Throws<SecurityTokenInvalidIssuerException>(
            () => Validate(Token(Key, issuer: "https://evil.example/auth/v1"), Key));
    }

    [Fact]
    public void TestATokenForAnotherAudienceIsRejected()
    {
        Assert.Throws<SecurityTokenInvalidAudienceException>(
            () => Validate(Token(Key, audience: "anon"), Key));
    }

    [Fact]
    public void TestTheIssuerIsDerivedFromTheProjectUrl()
    {
        TokenValidationParameters p = SupabaseJwt.BuildValidationParameters(Options());

        Assert.Contains("https://ref.supabase.co/auth/v1", p.ValidIssuers!);
        Assert.True(p.ValidateLifetime);
        Assert.True(p.ValidateIssuerSigningKey);
    }

    [Fact]
    public void TestUserIdIsNullWhenTheSubClaimIsAbsent()
    {
        Assert.Null(SupabaseJwt.UserId(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
