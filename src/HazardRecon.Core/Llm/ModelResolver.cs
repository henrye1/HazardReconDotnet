namespace HazardRecon.Core.Llm;

public static class ModelResolver
{
    /// <summary>
    /// Resolves a user-supplied fragment to one model. An empty fragment means "the
    /// first model the gateway offered". A fragment matches an exact id, or appears
    /// anywhere in the friendly name or model name, case-insensitively. When several
    /// match, the first in gateway order wins — ambiguity is not an error. Returns
    /// null when nothing matches or there are no models.
    /// </summary>
    public static LlmModel? Resolve(IReadOnlyList<LlmModel> models, string? fragment)
    {
        if (models.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(fragment)) return models[0];

        string f = fragment.Trim();
        return models.FirstOrDefault(m =>
            m.Id.Equals(f, StringComparison.OrdinalIgnoreCase) ||
            m.FriendlyName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            m.ModelName.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
}
