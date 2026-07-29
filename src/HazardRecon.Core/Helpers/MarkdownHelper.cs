using System.Net;
using System.Text;

namespace HazardRecon.Core.Helpers;

/// <summary>
/// The small subset of Markdown the model is asked to produce: h2/h3 headings,
/// bullet items and paragraphs. Shared by the dashboard and the chat reply so both
/// render generated text the same way.
/// </summary>
public static class MarkdownHelper
{
    public static string ToHtml(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return string.Empty;

        StringBuilder sb = new();
        foreach (string line in md.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("## ")) sb.AppendLine($"<h3>{WebUtility.HtmlEncode(t[3..])}</h3>");
            else if (t.StartsWith("# ")) sb.AppendLine($"<h2>{WebUtility.HtmlEncode(t[2..])}</h2>");
            else if (t.StartsWith("- ") || t.StartsWith("* ")) sb.AppendLine($"<li>{WebUtility.HtmlEncode(t[2..])}</li>");
            else if (!string.IsNullOrEmpty(t)) sb.AppendLine($"<p>{WebUtility.HtmlEncode(t)}</p>");
        }
        return sb.ToString();
    }
}
