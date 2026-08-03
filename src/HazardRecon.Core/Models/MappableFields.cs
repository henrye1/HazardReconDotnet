namespace HazardRecon.Core.Models;

/// <summary>One field the mapping step needs a column for, with the explanation shown next to it.</summary>
public record MappingFieldSpec(string Field, string Note);

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

    public static readonly IReadOnlyList<MappingFieldSpec> Exposure = new[]
    {
        new MappingFieldSpec("LoanAccountNumber", "Join key - Check 1 traces defaults into this population"),
        new MappingFieldSpec("AmountOutstanding", "Summed per account for the exposure figure")
    };
}
