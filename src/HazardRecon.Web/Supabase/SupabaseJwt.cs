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
    /// Name of the cookie that carries the access token for file requests.
    /// Scoped to /runs so it is never sent to the JSON API, which uses headers.
    /// </summary>
    public const string DownloadCookie = "hr_dl";

    /// <summary>
    /// Picks the token for a request: the Authorization header wins, falling back
    /// to the download cookie.
    ///
    /// A browser will not attach an Authorization header to an iframe load, a
    /// link navigation or a download - the token only exists in page script. The
    /// dashboard is shown in an iframe and the artifacts are plain links, so
    /// without this they all arrive anonymous and 401.
    /// </summary>
    public static string? TokenForRequest(string? headerToken, string? cookieToken) =>
        !string.IsNullOrEmpty(headerToken) ? headerToken
        : string.IsNullOrEmpty(cookieToken) ? null
        : cookieToken;

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
