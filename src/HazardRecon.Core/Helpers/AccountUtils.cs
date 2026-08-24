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

    /// <summary>
    /// Separates the two parts of a composite join key. The unit separator is
    /// used precisely because no account or transaction reference contains it -
    /// '|', '-' and ':' all appear in real references, so any of those would
    /// split a key in the middle of an identifier.
    /// </summary>
    public const char KeySeparator = '\u001F';

    /// <summary>
    /// The key a record is joined on. A trade receivables account holds many
    /// transactions, so the account number alone no longer identifies a debt;
    /// for lending there is no second part and the key stays the bare account,
    /// which is what keeps <see cref="AccountPartOf"/> free of a run-type flag.
    /// </summary>
    public static string CompositeKey(string account, string? transaction) =>
        string.IsNullOrEmpty(transaction) ? account : account + KeySeparator + transaction;

    /// <summary>
    /// The account half of a key - and the identity function on a key that has no
    /// second half. That is what lets the write-off side, which has no
    /// transaction number, match a composite-keyed default without knowing
    /// whether it is looking at one.
    /// </summary>
    public static string AccountPartOf(string key)
    {
        int at = key.IndexOf(KeySeparator);
        return at < 0 ? key : key[..at];
    }

    /// <summary>The transaction half, or empty for a key that has none.</summary>
    public static string TransactionPartOf(string key)
    {
        int at = key.IndexOf(KeySeparator);
        return at < 0 ? string.Empty : key[(at + 1)..];
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
