using System.Collections.Concurrent;
using System.Text.Json;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using HazardRecon.Web;
using HazardRecon.Web.Supabase;

var builder = WebApplication.CreateBuilder(args);

string host = Environment.GetEnvironmentVariable("HOST") ?? "127.0.0.1";
int port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int p) ? p : 5000;
builder.WebHost.UseUrls($"http://{host}:{port}");

SupabaseOptions supabaseOptions = new();
builder.Configuration.GetSection("Supabase").Bind(supabaseOptions);

if (!supabaseOptions.IsConfigured)
{
    // Unlike the LLM, this is fatal. Without Supabase there is no login and no
    // storage, so every request would 500 - fail loudly at boot instead.
    Console.Error.WriteLine(
        " ! Supabase is not configured. Missing: " + string.Join(", ", supabaseOptions.MissingKeys()));
    return 1;
}

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

CyteLlmOptions llmOptions = new();
builder.Configuration.GetSection("CyteLlm").Bind(llmOptions);
CyteLlmClient? llm = llmOptions.IsConfigured ? new CyteLlmClient(llmOptions) : null;

if (llm == null)
{
    Console.WriteLine(" ! CyteLlm:ClientId / CyteLlm:ClientSecret not set - AI analysis and chat are unavailable.");
}

string baseDir = AppContext.BaseDirectory;
string runsDir = Path.Combine(baseDir, "runs");
Directory.CreateDirectory(runsDir);

var jobs = new ConcurrentDictionary<string, JobState>();

// POST /api/discover
app.MapPost("/api/discover", async (HttpContext ctx) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var paths = form["paths"]
        .Select(p => (p ?? "").Trim().Trim('"'))
        .Where(p => !string.IsNullOrEmpty(p))
        .ToList();

    if (paths.Count == 0)
        return Results.BadRequest(new { error = "Please add at least one folder path." });

    if (paths.Count > 4)
        return Results.BadRequest(new { error = "A maximum of 4 folders is supported." });

    for (int i = 0; i < paths.Count; i++)
    {
        if (!Directory.Exists(paths[i]))
            return Results.BadRequest(new { error = $"Folder {i + 1}: not a folder on this machine:\n{paths[i]}" });
    }

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (string path in paths)
    {
        string norm = Path.GetFullPath(path);
        if (!seen.Add(norm))
            return Results.BadRequest(new { error = $"Folder '{path}' is listed more than once." });
    }

    string rid = DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..6];
    string runRoot = Path.Combine(runsDir, rid);
    string outdir = Path.Combine(runRoot, "output");
    Directory.CreateDirectory(outdir);

    jobs[rid] = new JobState
    {
        Id = rid,
        Status = "ready",
        Roots = paths,
        Outdir = outdir,
        Started = DateTime.Now.ToString("o")
    };

    var probe = new ProbeLogger();
    var discoverer = new InputDiscoverer();
    Inventory inv = discoverer.DiscoverFromFolders(paths, probe.Log);

    var setViews = inv.Sets.Select(kv => new
    {
        key = kv.Key,
        label = kv.Value.Label,
        lgd_defaults = string.IsNullOrEmpty(kv.Value.LgdDefaults) ? null : Path.GetFileName(kv.Value.LgdDefaults),
        pd_scored = kv.Value.PdScored == null ? null : Path.GetFileName(kv.Value.PdScored),
        ifrs9 = kv.Value.Ifrs9 == null ? null : Path.GetFileName(kv.Value.Ifrs9),
        scenario = kv.Value.Scenario == null ? null : Path.GetFileName(kv.Value.Scenario),
        debug_json = kv.Value.DebugJson == null ? null : Path.GetFileName(kv.Value.DebugJson),
        writeoff = kv.Value.WriteOff == null ? null : Path.GetFileName(kv.Value.WriteOff)
    }).ToList();

    var problems = new List<string>();
    if (inv.Sets.Count == 0)
        problems.Add("No analysis sets found. Each folder needs debug.zip (or an extracted lgd_defaults.csv).");

    foreach (var s in setViews)
    {
        if (string.IsNullOrEmpty(s.writeoff)) problems.Add($"{s.key}: no write-off CSV - check 2 cannot run for this set.");
        if (string.IsNullOrEmpty(s.pd_scored)) problems.Add($"{s.key}: pd_scored.csv missing - no migrations.");
        if (string.IsNullOrEmpty(s.scenario)) problems.Add($"{s.key}: scenario.json missing - no engine results.");
        if (string.IsNullOrEmpty(s.ifrs9)) problems.Add($"{s.key}: no IFRS9 file - defaults can only trace to write-off.");
    }

    return Results.Ok(new
    {
        run_id = rid,
        inventory = new { root = inv.Root, sets = setViews },
        problems,
        log = probe.Lines
    });
});

// POST /api/run
app.MapPost("/api/run", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    string bodyStr = await reader.ReadToEndAsync();
    using var doc = JsonDocument.Parse(bodyStr);
    string? rid = doc.RootElement.TryGetProperty("run_id", out var rProp) ? rProp.GetString() : null;

    if (string.IsNullOrEmpty(rid) || !jobs.TryGetValue(rid, out var job))
        return Results.NotFound(new { error = "Unknown run - please run discovery again." });

    if (job.Status == "running")
        return Results.Ok(new { run_id = rid, status = "running" });

    string? modelId = doc.RootElement.TryGetProperty("model_id", out var modelProp) ? modelProp.GetString() : null;
    job.ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();

    job.Status = "running";
    job.Log.Clear();
    job.Error = null;
    job.Result = null;
    job.Started = DateTime.Now.ToString("HH:mm:ss");

    void Logger(string msg, string kind) =>
        job.Log.Add(new Dictionary<string, string>
        {
            ["t"] = DateTime.Now.ToString("HH:mm:ss"),
            ["msg"] = msg,
            ["kind"] = kind
        });

    var capturedJob = job;
    _ = Task.Run(() =>
    {
        try
        {
            var engine = new ReconciliationEngine();

            AiAnalysisService? analyst = (llm != null && capturedJob.ModelId != null)
                ? new AiAnalysisService(llm, capturedJob.ModelId)
                : null;

            ReconciliationRunResult outResult = engine.Run(
                capturedJob.Roots, capturedJob.Outdir, logger: Logger,
                analyze: analyst != null, analyst: analyst);

            // Isolated on purpose: this aggregation only feeds /api/chat. It must never
            // turn a completed run (workbook/CSVs/dashboard already on disk) into an
            // "error" job just because building the chat payload throws - especially on
            // a skip-analysis run, where this is the first time it runs outside the engine.
            try
            {
                capturedJob.AnalysisPayload = AiAnalysisService.BuildAnalysisPayload(outResult.Results);
            }
            catch (Exception payloadEx)
            {
                Logger($"Could not build chat payload: {payloadEx.GetType().Name}: {payloadEx.Message}", "warn");
            }

            var setSummaries = outResult.Results.Select(kv => new
            {
                key = kv.Key,
                label = kv.Value.Summary.Label,
                window = kv.Value.Summary.Window,
                defaults = kv.Value.Summary.TotalDefaults,
                exposure = kv.Value.Summary.TotalExposure,
                exposure_fmt = AccountUtils.Money(kv.Value.Summary.TotalExposure),
                traced = kv.Value.Summary.TracedTotal,
                traced_writeoff = kv.Value.Summary.TracedWriteOff,
                traced_ifrs9 = kv.Value.Summary.TracedIfrs9,
                untraced = kv.Value.Summary.UntracedTotal,
                untraced_fmt = AccountUtils.Money(kv.Value.Summary.UntracedExposure),
                trace_rate = Math.Round(kv.Value.Summary.TraceRate * 100.0, 1),
                wo_total = kv.Value.Summary.WoNotDefaultTotal,
                wo_in_window = kv.Value.Summary.WoInWindow,
                wo_in_window_fmt = AccountUtils.Money(kv.Value.Summary.WoInWindowAmount),
                wo_post_window = kv.Value.Summary.WoPostWindow,
                scored = kv.Value.Summary.ScoredDistinct,
                ifrs9_overlap = kv.Value.Summary.Ifrs9KeyOverlap,
                files = kv.Value.Summary.Files
            }).ToList();

            capturedJob.Result = new
            {
                sets = setSummaries,
                workbook = outResult.Workbook,
                dashboard = outResult.Dashboard,
                memo = outResult.Memo
            };
            capturedJob.Status = "done";
        }
        catch (Exception ex)
        {
            capturedJob.Error = $"{ex.GetType().Name}: {ex.Message}";
            capturedJob.Status = "error";
            Logger(capturedJob.Error, "warn");
        }
    });

    return Results.Ok(new { run_id = rid, status = "running" });
});

// GET /api/job/{rid}
app.MapGet("/api/job/{rid}", (string rid) =>
{
    if (!jobs.TryGetValue(rid, out var job))
        return Results.NotFound(new { error = "Unknown run" });

    return Results.Ok(new
    {
        id = rid,
        status = job.Status,
        log = job.Log,
        error = job.Error,
        result = job.Result
    });
});

// POST /api/chat
app.MapPost("/api/chat", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    string bodyStr = await reader.ReadToEndAsync();
    using var doc = JsonDocument.Parse(bodyStr);

    string? rid = doc.RootElement.TryGetProperty("run_id", out var rProp) ? rProp.GetString() : null;
    string message = doc.RootElement.TryGetProperty("message", out var mProp) ? (mProp.GetString() ?? "").Trim() : "";

    if (string.IsNullOrEmpty(rid) || !jobs.TryGetValue(rid, out var job))
        return Results.NotFound(new { error = "Unknown run - please run reconciliation first." });

    if (job.Status != "done")
        return Results.BadRequest(new { error = "This run has not finished yet." });

    if (string.IsNullOrEmpty(message))
        return Results.BadRequest(new { error = "Please enter a question." });

    ChatService chatService = new(llm, job.ModelId);
    var chatRes = chatService.ProcessQuestion(message, job.AnalysisPayload ?? new Dictionary<string, object>());
    if (chatRes.IsError)
        return Results.Json(new { error = chatRes.ErrorMessage }, statusCode: 503);

    return Results.Ok(new { reply = chatRes.Reply, reply_html = chatRes.ReplyHtml });
});

// GET /runs/{rid}/output/{filename}
app.MapGet("/runs/{rid}/output/{filename}", (string rid, string filename) =>
{
    string outdir = jobs.TryGetValue(rid, out var job) ? job.Outdir : Path.Combine(runsDir, rid, "output");
    string filePath = Path.GetFullPath(Path.Combine(outdir, filename));

    // keep the request inside the run's own output folder
    string outdirFull = Path.GetFullPath(outdir);
    if (!filePath.StartsWith(outdirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();

    if (!File.Exists(filePath)) return Results.NotFound();

    // The dashboard is shown in an iframe, so HTML has to be served inline with
    // its real content type - as an octet-stream attachment the frame renders
    // blank. Everything else stays a download.
    if (filename.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        return Results.File(filePath, contentType: "text/html; charset=utf-8");

    return Results.File(filePath, contentType: "application/octet-stream", fileDownloadName: filename);
});

// GET /api/models
app.MapGet("/api/models", async () =>
{
    if (llm == null)
    {
        return Results.Json(new { error = "The LLM gateway is not configured (CyteLlm:ClientId / ClientSecret missing)." }, statusCode: 503);
    }

    try
    {
        IReadOnlyList<LlmModel> models = await llm.ListModelsAsync();
        return Results.Ok(models.Select(m => new
        {
            id = m.Id,
            provider = m.Provider,
            friendlyName = m.FriendlyName,
            modelName = m.ModelName
        }));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Could not list models - {ex.Message}" }, statusCode: 503);
    }
});

// GET /health
app.MapGet("/health", () => Results.Ok(new { ok = true, runs = jobs.Count }));

Console.WriteLine("==================================================================");
Console.WriteLine(" Hazard-Rate Reconciliation (.NET) | Anchor Point Risk");
Console.WriteLine($" Open http://{host}:{port} in your browser (Ctrl+C here to stop)");
Console.WriteLine("==================================================================");

app.Run();
return 0;
