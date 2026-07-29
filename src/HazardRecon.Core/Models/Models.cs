namespace HazardRecon.Core.Models;

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

public class Inventory
{
    public string Root { get; set; } = string.Empty;
    public string? WriteOff { get; set; }
    public Dictionary<string, InventorySet> Sets { get; set; } = new();
}

public class DefaultAccountRecord
{
    public string AccountNumber { get; set; } = string.Empty;
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
    public HashSet<string> ScoredAccts { get; set; } = new();
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
