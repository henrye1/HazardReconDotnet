using System.Text.Json;
using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>One cell of a run_set_results' migration matrix, per month (or "All months").</summary>
public class MigrationCellRecord
{
    [JsonPropertyName("month_label")]
    public string MonthLabel { get; set; } = string.Empty;

    [JsonPropertyName("from_bucket")]
    public short FromBucket { get; set; }

    [JsonPropertyName("to_bucket")]
    public short ToBucket { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>Total movements for one month, in the order the months are listed.</summary>
public class MonthlyTotalRecord
{
    [JsonPropertyName("month_label")]
    public string MonthLabel { get; set; } = string.Empty;

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("position")]
    public int Position { get; set; }
}

/// <summary>One cell of the model's fitted hazard-rate transition matrix.</summary>
public class HazardMatrixCellRecord
{
    [JsonPropertyName("row_idx")]
    public short RowIdx { get; set; }

    [JsonPropertyName("col_idx")]
    public short ColIdx { get; set; }

    [JsonPropertyName("value")]
    public double Value { get; set; }
}

/// <summary>One cell of the model's fitted cohort transition matrix.</summary>
public class CohortMatrixCellRecord
{
    [JsonPropertyName("row_idx")]
    public short RowIdx { get; set; }

    [JsonPropertyName("col_idx")]
    public short ColIdx { get; set; }

    [JsonPropertyName("value")]
    public double Value { get; set; }
}

/// <summary>
/// One (event, term) LGD fact. Stored sparse - the padded/aligned grid the UI
/// shows is a rendering concern, reconstructed from these at read time.
/// </summary>
public class LgdPointRecord
{
    [JsonPropertyName("event_name")]
    public string EventName { get; set; } = string.Empty;

    [JsonPropertyName("term_days")]
    public int TermDays { get; set; }

    [JsonPropertyName("value")]
    public double? Value { get; set; }
}

/// <summary>
/// One row of the last-bucket-seen census. Also feeds analysis_payload's old
/// in_window_last_bucket_hist - "share" is derived client-side.
/// </summary>
public class LastBucketRowRecord
{
    [JsonPropertyName("bucket")]
    public string Bucket { get; set; } = string.Empty;

    [JsonPropertyName("accounts")]
    public int Accounts { get; set; }

    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("position")]
    public int Position { get; set; }
}

/// <summary>A default that could not be traced, for the detail table.</summary>
public class UntracedRowRecord
{
    [JsonPropertyName("account")]
    public string Account { get; set; } = string.Empty;

    [JsonPropertyName("cohort_date")]
    public string CohortDate { get; set; } = string.Empty;

    [JsonPropertyName("rating")]
    public string Rating { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("position")]
    public int Position { get; set; }
}

/// <summary>A write-off with no default flag, for the exceptions table.</summary>
public class WoExceptionRowRecord
{
    [JsonPropertyName("account")]
    public string Account { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("wo_date")]
    public DateOnly? WoDate { get; set; }

    [JsonPropertyName("window")]
    public string Window { get; set; } = string.Empty;

    [JsonPropertyName("last_bucket")]
    public string LastBucket { get; set; } = string.Empty;

    [JsonPropertyName("position")]
    public int Position { get; set; }
}

/// <summary>
/// One engine scenario parameter. Genuinely open-ended per scenario type, so the
/// value stays jsonb rather than being typed further.
/// </summary>
public class EngineParamRecord
{
    [JsonPropertyName("param_key")]
    public string ParamKey { get; set; } = string.Empty;

    [JsonPropertyName("param_value")]
    public JsonElement ParamValue { get; set; }
}
