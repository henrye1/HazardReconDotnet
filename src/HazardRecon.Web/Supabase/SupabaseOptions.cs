namespace HazardRecon.Web.Supabase;

/// <summary>
/// Connection settings for the Supabase project. Mirrors the CyteLlmOptions
/// shape so both hosts configure the same way.
/// </summary>
public class SupabaseOptions
{
    public string Url { get; set; } = string.Empty;
    public string AnonKey { get; set; } = string.Empty;
    public string ServiceRoleKey { get; set; } = string.Empty;

    /// <summary>Url without a trailing slash, so callers can concatenate paths.</summary>
    public string BaseUrl => Url.TrimEnd('/');

    public bool IsConfigured => MissingKeys().Count == 0;

    public IReadOnlyList<string> MissingKeys()
    {
        List<string> missing = new();
        if (string.IsNullOrWhiteSpace(Url)) missing.Add("Supabase:Url");
        if (string.IsNullOrWhiteSpace(AnonKey)) missing.Add("Supabase:AnonKey");
        if (string.IsNullOrWhiteSpace(ServiceRoleKey)) missing.Add("Supabase:ServiceRoleKey");
        return missing;
    }
}
