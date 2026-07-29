namespace HazardRecon.Web;

internal class JobState
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "ready";
    public List<string> Roots { get; set; } = new();
    public string Outdir { get; set; } = string.Empty;
    public List<Dictionary<string, string>> Log { get; set; } = new();
    public object? Result { get; set; }
    public string? Error { get; set; }
    public string Started { get; set; } = string.Empty;
}
