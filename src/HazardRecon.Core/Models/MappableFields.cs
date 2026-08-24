namespace HazardRecon.Core.Models;

/// <summary>
/// One field the mapping step needs a column for, with the explanation shown next
/// to it. <paramref name="Multiple"/> fields take several columns rather than one -
/// today that is only the age analysis' aging buckets, which are summed.
/// </summary>
public record MappingFieldSpec(string Field, string Note, bool Multiple = false);

/// <summary>
/// The fixed fields the engine reads from the write-off and exposure (IFRS9)
/// files - see DataLoaders.LoadWriteoff/LoadSourceAccounts for where each is
/// actually consumed.
/// </summary>
public static class MappableFields
{
    public static readonly IReadOnlyList<MappingFieldSpec> Writeoff = new[]
    {
        new MappingFieldSpec("LoanAccountNumber", "Normalised and used as the join key against defaults and exposure"),
        new MappingFieldSpec("CustomerId", "Carried through - not used for matching logic"),
        new MappingFieldSpec("Amount", "Summed per account into the write-off exposure"),
        new MappingFieldSpec("ReportDate", "Classifies each write-off as pre-, in- or post-window")
    };

    /// <summary>
    /// The same file for a receivables run, where the two identifiers swap roles:
    /// the customer number is what defaults and the age analysis are keyed on, so
    /// it is the join key, and the loan account number is carried instead.
    /// </summary>
    public static readonly IReadOnlyList<MappingFieldSpec> WriteoffByCustomer = new[]
    {
        new MappingFieldSpec("CustomerId", "Normalised and used as the join key against defaults and the age analysis"),
        new MappingFieldSpec("LoanAccountNumber", "Carried through - not used for matching logic"),
        new MappingFieldSpec("Amount", "Summed per customer into the write-off exposure"),
        new MappingFieldSpec("ReportDate", "Classifies each write-off as pre-, in- or post-window")
    };

    public static readonly IReadOnlyList<MappingFieldSpec> Exposure = new[]
    {
        new MappingFieldSpec("LoanAccountNumber", "Join key - Check 1 traces defaults into this population"),
        new MappingFieldSpec("AmountOutstanding", "Summed per account for the exposure figure")
    };

    /// <summary>
    /// What a trade receivables run reads instead of <see cref="Exposure"/>. An age
    /// analysis carries no loan account number at all - the customer number is the
    /// identifier - and no single balance column, so which aging buckets count as
    /// defaulted is the user's call rather than the file's.
    /// </summary>
    public static readonly IReadOnlyList<MappingFieldSpec> AgeAnalysis = new[]
    {
        new MappingFieldSpec("ClientNumber", "The join key - matched to ClientNumber in the defaults file"),
        new MappingFieldSpec(
            "AgingBuckets",
            "The aging columns that count as defaulted - summed per row to give the exposure",
            Multiple: true)
    };

    /// <summary>The field list a run of this type maps its exposure-slot file against.</summary>
    public static IReadOnlyList<MappingFieldSpec> ExposureFor(EngineRunType runType) =>
        runType == EngineRunType.TradeReceivables ? AgeAnalysis : Exposure;

    /// <summary>The write-off field list for this run type - the two identifiers swap roles.</summary>
    public static IReadOnlyList<MappingFieldSpec> WriteoffFor(EngineRunType runType) =>
        runType == EngineRunType.TradeReceivables ? WriteoffByCustomer : Writeoff;
}
