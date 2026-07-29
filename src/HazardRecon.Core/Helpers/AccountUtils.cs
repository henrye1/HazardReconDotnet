using System.Text.RegularExpressions;

namespace HazardRecon.Core.Helpers;

public static class AccountUtils
{
    private static readonly Regex TrailingZeroRegex = new(@"\.0$", RegexOptions.Compiled);

    /// <summary>
    /// Consistent account-number key across every file: strip whitespace and
    /// drop a trailing '.0' left behind by float parsing.
    /// </summary>
    public static string NormaliseAccount(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        string s = input.Trim();
        return TrailingZeroRegex.Replace(s, string.Empty);
    }

    public static string Money(double? value)
    {
        if (!value.HasValue) return "R 0.00";
        return $"R {value.Value:N2}";
    }

    public static string Pct(double? value, int dp = 2)
    {
        if (!value.HasValue) return "&mdash;";
        double percentage = value.Value * 100.0;
        return $"{percentage.ToString($"F{dp}")}%";
    }

    /// <summary>
    /// Excel caps sheet names at 31 characters.
    /// </summary>
    public static string SheetName(string key, string suffix)
    {
        string name = $"{key} {suffix}";
        return name.Length <= 31 ? name : name[..31];
    }
}
