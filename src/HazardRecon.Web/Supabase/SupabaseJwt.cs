using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace HazardRecon.Web.Supabase;

public static class SupabaseJwt
{
    /// <summary>The GoTrue issuer and audience for a Supabase project.</summary>
    public static string Issuer(SupabaseOptions options) => $"{options.BaseUrl}/auth/v1";

    public static TokenValidationParameters BuildValidationParameters(SupabaseOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuers = new[] { Issuer(options) },
        ValidateAudience = true,
        ValidAudiences = new[] { "authenticated" },
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub"
    };

    /// <summary>
    /// The authenticated user's id. Every data access scopes on this value and
    /// never on anything from a request body.
    /// </summary>
    public static Guid? UserId(ClaimsPrincipal principal)
    {
        string? sub = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(sub, out Guid id) ? id : null;
    }
}
