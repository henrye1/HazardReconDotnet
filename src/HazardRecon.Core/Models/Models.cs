namespace HazardRecon.Core.Models;

/// <summary>
/// What kind of book a run covers, for the engine's own use. Core deliberately
/// declares this itself rather than referencing the web layer's RunTypeLookup:
/// the CLI has no run types at all, and the engine only cares about the two
/// behaviours, not about how a run row stores them.
/// </summary>
public enum EngineRunType
{
    Lending,
    TradeReceivables
}

public static class EngineRunTypeGrain
{
    /// <summary>
    /// What one row of the defaults, age analysis or scored population actually
    /// counts, for log lines and report labels. One source, so the wording cannot
    /// drift between the four places that say it.
    /// </summary>
    public static string Noun(this EngineRunType runType) =>
        runType == EngineRunType.TradeReceivables ? "account/transaction pairs" : "accounts";
}

public class InventorySet
{
    public string Folder { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string LgdDefaults { get; set; } = string.Empty;
    public string? PdScored { get; set; }
    public string? Ifrs9 { get; set; }
    public string? Scenario { get; set; }
    public string? DebugJson { get; set; }
    public string? WriteOff { get; set; }
}

/// <summary>
/// The key and label a caller has already decided for a set folder, for when
/// the folder name cannot carry them: the web upload writes each set into a
/// numbered directory, so discovery re-deriving a key from disk would invent a
/// different one from the key the caller filed that set's column mapping under.
/// </summary>
public record SetIdentity(string Key, string Label);

public class Inventory
{
    public string Root { get; set; } = string.Empty;
    public string? WriteOff { get; set; }
    public Dictionary<string, InventorySet> Sets { get; set; } = new();
}

public class DefaultAccountRecord
{
    public string AccountNumber { get; set; } = string.Empty;

    /// <summary>
    /// The transaction (client) number this default belongs to, for display and
    /// export. Empty for a lending run, which has no second key part.
    /// </summary>
    public string TransactionNumber { get; set; } = string.Empty;

    /// <summary>
    /// The join key. For trade receivables this is the composite built by
    /// <see cref="Helpers.AccountUtils.CompositeKey"/>, not a bare account
    /// number - use AccountPartOf before comparing it with anything read from
    /// the write-off file, which has no transaction number.
    /// </summary>
    public string AccountNormalized { get; set; } = string.Empty;
    public string CohortDate { get; set; } = string.Empty;
    public string Rating { get; set; } = string.Empty;
    public double DefaultAmount { get; set; }
    public double MinLgdBalance { get; set; }
    public double? LastObsBucket { get; set; }
    public double? LastOutstanding { get; set; }
    public double RecoveredAmount { get; set; }
    public string RecoveryStatus { get; set; } = "NO POST-DEFAULT DATA";
    
    // Trace fields attached during Check 1
    public bool InWriteOff { get; set; }
    public bool InIFRS9 { get; set; }
    public bool Traced => InWriteOff || InIFRS9;
    public string TraceSource { get; set; } = "UNTRACED";
    public double? WriteOffAmount { get; set; }

    /// <summary>
    /// The account's whole write-off, for a key that identifies a transaction
    /// rather than an account. Kept apart from <see cref="WriteOffAmount"/>
    /// precisely so it is never read as this row's share: the exporters write it
    /// once per account, so the column still totals correctly.
    /// </summary>
    public double? AccountWriteOffTotal { get; set; }

    public double? Ifrs9AmountOutstanding { get; set; }
    public double? TraceAmount { get; set; }
    public double? LossVsTraceDiff { get; set; }
}

public class WriteOffAggRecord
{
    public string AccountNormalized { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public double WriteOffAmount { get; set; }
    public DateTime? FirstWriteOffDate { get; set; }
    public DateTime? LastWriteOffDate { get; set; }
    public int WriteOffRows { get; set; }
}

public class WriteOffNotDefaultRecord
{
    public string AccountNumber { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public double WriteOffAmount { get; set; }
    public DateTime? FirstWriteOffDate { get; set; }
    public DateTime? LastWriteOffDate { get; set; }
    public string WriteOffVsScoringWindow { get; set; } = string.Empty;
    public string? LastScoredDate { get; set; }
    public string? LastBucketRating { get; set; }
    public string ScoringWindow { get; set; } = string.Empty;
}

public class SourceAccountsResult
{
    public HashSet<string> AccountNumbers { get; set; } = new();
    public int TotalRows { get; set; }
    public Dictionary<string, double> AmountsPerAccount { get; set; } = new();
}

public class MigrationValidationResult
{
    public string Status { get; set; } = "N/A";
    public int? MaxAbsDiff { get; set; }
    public List<(int FromBucket, int ToBucket, int Ours, int Engine)> Mismatches { get; set; } = new();
}

public class MigrationMatrixResult
{
    // Key: (Year, Month, FromBucket, ToBucket) -> Count
    public Dictionary<(int Year, int Month, int FromBucket, int ToBucket), int> RawCounts { get; set; } = new();
    public int RowsTotal { get; set; }
    public int RowsInRange { get; set; }
    public int ScoredDistinct { get; set; }

    /// <summary>The scored population at join grain - composite for trade receivables.</summary>
    public HashSet<string> ScoredAccts { get; set; } = new();

    /// <summary>
    /// The same population projected to account numbers. Check 2 compares the
    /// scored population against the write-off file, which has no transaction
    /// number, so it needs this rather than <see cref="ScoredAccts"/>.
    /// </summary>
    public HashSet<string> ScoredAccounts { get; set; } = new();

    /// <summary>
    /// Keyed by **account**, not by the join key: its only consumer is check 2,
    /// which works at account grain. Keying it composite would leave every
    /// lookup missing and silently report no last-seen bucket for anything.
    /// </summary>
    public Dictionary<string, (string Date, string Bucket)> LastRating { get; set; } = new();
}

public class EngineLgdTermPoint
{
    public int? TermDays { get; set; }
    public double? Value { get; set; }
}

public class EngineScenario
{
    public List<List<double>>? HazardRateMatrix { get; set; }
    public List<List<double>>? CohortMatrix { get; set; }
    public string? ScoringType { get; set; }
    public string? Category { get; set; }
    public Dictionary<string, List<EngineLgdTermPoint>> Lgd { get; set; } = new();
    public Dictionary<string, object> Params { get; set; } = new();
    public string? GeneratedAt { get; set; }
    public List<List<double>>? CohortNlambda { get; set; }
}

public class ReconciliationSummary
{
    public string Label { get; set; } = string.Empty;
    public int TotalDefaults { get; set; }
    public double TotalExposure { get; set; }
    public int TracedWriteOff { get; set; }
    public int TracedIfrs9 { get; set; }
    public int TracedTotal { get; set; }
    public int UntracedTotal { get; set; }
    public double TracedExposure { get; set; }
    public double UntracedExposure { get; set; }
    public double TraceRate { get; set; }
    public int Ifrs9KeyOverlap { get; set; }
    public int Ifrs9Rows { get; set; }
    public string Ifrs9File { get; set; } = string.Empty;

    public int WoNotDefaultTotal { get; set; }
    public double WoNotDefaultAmount { get; set; }
    public int WoInWindow { get; set; }
    public double WoInWindowAmount { get; set; }
    public int WoPreWindow { get; set; }
    public int WoPostWindow { get; set; }
    public int ScoredInWriteOff { get; set; }
    public int? ScoredInIfrs9 { get; set; }
    public int WoInWindowBucket4 { get; set; }
    public double WoInWindowBucket4Amount { get; set; }
    public double WoInWindowBucket4Pct { get; set; }

    public string MigValidation { get; set; } = "N/A";
    public int? MigValidationMaxDiff { get; set; }

    public int ScoredDistinct { get; set; }
    public int WriteOffDistinct { get; set; }
    public int Ifrs9Distinct { get; set; }
    public int DefaultsDistinct { get; set; }
    public double? DefaultPctOfScored { get; set; }
    public int PdRows { get; set; }
    public string Window { get; set; } = "n/a";
    public int UntracedFullyRecovered { get; set; }
    public double UntracedFullyRecoveredAmount { get; set; }
    public List<string> Files { get; set; } = new();
}

public class SingleSetResult
{
    public List<DefaultAccountRecord> Defaults { get; set; } = new();
    public List<DefaultAccountRecord> Full { get; set; } = new();
    public List<DefaultAccountRecord> Untraced { get; set; } = new();
    public List<WriteOffNotDefaultRecord> WoNd { get; set; } = new();
    public ReconciliationSummary Summary { get; set; } = new();
    public MigrationMatrixResult Mig { get; set; } = new();
    public EngineScenario Engine { get; set; } = new();
}

public class ReconciliationRunResult
{
    public Dictionary<string, SingleSetResult> Results { get; set; } = new();
    public string Workbook { get; set; } = string.Empty;
    public string Dashboard { get; set; } = string.Empty;
    public string Outdir { get; set; } = string.Empty;
    public string? Memo { get; set; }
    public string? Analysis { get; set; }
}
