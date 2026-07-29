namespace HazardRecon.Web;

internal class ProbeLogger
{
    public List<Dictionary<string, string>> Lines { get; } = new();

    public void Log(string msg, string kind)
    {
        Lines.Add(new Dictionary<string, string> { ["msg"] = msg, ["kind"] = kind });
    }
}
