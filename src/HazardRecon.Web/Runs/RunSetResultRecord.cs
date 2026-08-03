using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>
/// One row of public.run_set_results: one set's worth of ReconciliationSummary,
/// merged with the scalar fields DashboardSet and analysis_payload's sets[]
/// used to duplicate under different names. The list properties are only
/// populated when read via a PostgREST embedded select - they are the
/// per-set child tables, not columns on this row.
/// </summary>
public class RunSetResultRecord
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; set; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("set_key")]
    public string SetKey { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("window")]
    public string Window { get; set; } = "n/a";

    [JsonPropertyName("total_defaults")]
    public int TotalDefaults { get; set; }

    [JsonPropertyName("total_exposure")]
    public double TotalExposure { get; set; }

    [JsonPropertyName("traced_writeoff")]
    public int TracedWriteOff { get; set; }

    [JsonPropertyName("traced_ifrs9")]
    public int TracedIfrs9 { get; set; }

    [JsonPropertyName("traced_total")]
    public int TracedTotal { get; set; }

    [JsonPropertyName("untraced_total")]
    public int UntracedTotal { get; set; }

    [JsonPropertyName("traced_exposure")]
    public double TracedExposure { get; set; }

    [JsonPropertyName("untraced_exposure")]
    public double UntracedExposure { get; set; }

    /// <summary>Fraction 0..1. Percentage scaling happens where it is displayed.</summary>
    [JsonPropertyName("trace_rate")]
    public double TraceRate { get; set; }

    [JsonPropertyName("ifrs9_key_overlap")]
    public int Ifrs9KeyOverlap { get; set; }

    [JsonPropertyName("ifrs9_rows")]
    public int Ifrs9Rows { get; set; }

    [JsonPropertyName("ifrs9_file")]
    public string Ifrs9File { get; set; } = string.Empty;

    [JsonPropertyName("wo_not_default_total")]
    public int WoNotDefaultTotal { get; set; }

    [JsonPropertyName("wo_not_default_amount")]
    public double WoNotDefaultAmount { get; set; }

    [JsonPropertyName("wo_in_window")]
    public int WoInWindow { get; set; }

    [JsonPropertyName("wo_in_window_amount")]
    public double WoInWindowAmount { get; set; }

    [JsonPropertyName("wo_pre_window")]
    public int WoPreWindow { get; set; }

    [JsonPropertyName("wo_post_window")]
    public int WoPostWindow { get; set; }

    [JsonPropertyName("scored_in_writeoff")]
    public int ScoredInWriteOff { get; set; }

    [JsonPropertyName("scored_in_ifrs9")]
    public int? ScoredInIfrs9 { get; set; }

    [JsonPropertyName("wo_in_window_bucket4")]
    public int WoInWindowBucket4 { get; set; }

    [JsonPropertyName("wo_in_window_bucket4_amount")]
    public double WoInWindowBucket4Amount { get; set; }

    [JsonPropertyName("wo_in_window_bucket4_pct")]
    public double WoInWindowBucket4Pct { get; set; }

    [JsonPropertyName("mig_validation")]
    public string MigValidation { get; set; } = "N/A";

    [JsonPropertyName("mig_validation_max_diff")]
    public int? MigValidationMaxDiff { get; set; }

    [JsonPropertyName("scored_distinct")]
    public int ScoredDistinct { get; set; }

    [JsonPropertyName("writeoff_distinct")]
    public int WriteOffDistinct { get; set; }

    [JsonPropertyName("ifrs9_distinct")]
    public int Ifrs9Distinct { get; set; }

    [JsonPropertyName("defaults_distinct")]
    public int DefaultsDistinct { get; set; }

    [JsonPropertyName("default_pct_of_scored")]
    public double? DefaultPctOfScored { get; set; }

    [JsonPropertyName("pd_rows")]
    public int PdRows { get; set; }

    [JsonPropertyName("untraced_fully_recovered")]
    public int UntracedFullyRecovered { get; set; }

    [JsonPropertyName("untraced_fully_recovered_amount")]
    public double UntracedFullyRecoveredAmount { get; set; }

    [JsonPropertyName("run_set_migration_cells")]
    public List<MigrationCellRecord>? MigrationCells { get; set; }

    [JsonPropertyName("run_set_monthly_totals")]
    public List<MonthlyTotalRecord>? MonthlyTotals { get; set; }

    [JsonPropertyName("run_set_hazard_matrix")]
    public List<HazardMatrixCellRecord>? HazardMatrix { get; set; }

    [JsonPropertyName("run_set_cohort_matrix")]
    public List<CohortMatrixCellRecord>? CohortMatrix { get; set; }

    [JsonPropertyName("run_set_lgd_points")]
    public List<LgdPointRecord>? LgdPoints { get; set; }

    [JsonPropertyName("run_set_last_bucket_rows")]
    public List<LastBucketRowRecord>? LastBucketRows { get; set; }

    [JsonPropertyName("run_set_untraced_rows")]
    public List<UntracedRowRecord>? UntracedRows { get; set; }

    [JsonPropertyName("run_set_wo_exception_rows")]
    public List<WoExceptionRowRecord>? WoExceptionRows { get; set; }

    [JsonPropertyName("run_set_engine_params")]
    public List<EngineParamRecord>? EngineParams { get; set; }
}
