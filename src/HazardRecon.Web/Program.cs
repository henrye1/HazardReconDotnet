using System.Collections.Concurrent;
using System.Text.Json;
using HazardRecon.Core.Exporters;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using HazardRecon.Web;
using HazardRecon.Web.Files;
using HazardRecon.Web.Runs;
using HazardRecon.Web.Supabase;
using HazardRecon.Web.Uploads;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

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

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Read the key set directly rather than setting Authority: Supabase does
        // not yet serve an OpenID discovery document, so Authority has nothing to
        // resolve and every request would 401. Reading jwks.json works both now
        // and after discovery ships, so there is no path here to revisit.
        options.TokenValidationParameters = SupabaseJwt.BuildValidationParameters(supabaseOptions);
        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{SupabaseJwt.Issuer(supabaseOptions)}/.well-known/jwks.json",
            new JwksRetriever(),
            new HttpDocumentRetriever());

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                ctx.Request.Cookies.TryGetValue(SupabaseJwt.DownloadCookie, out string? cookie);
                ctx.Token = SupabaseJwt.TokenForRequest(ctx.Token, cookie);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Registered rather than newed up inline so a test host can substitute fakes -
// which is what IRunStore and IFileStore exist for.
builder.Services.AddSingleton(supabaseOptions);
builder.Services.AddSingleton(sp => new SupabaseRestClient(sp.GetRequiredService<SupabaseOptions>()));
builder.Services.AddSingleton<IRunStore>(sp => new SupabaseRunStore(sp.GetRequiredService<SupabaseRestClient>()));
builder.Services.AddSingleton<IRunFileStore>(sp => new SupabaseRunFileStore(sp.GetRequiredService<SupabaseRestClient>()));
builder.Services.AddSingleton<IFileStore>(sp => new SupabaseFileStore(
    sp.GetRequiredService<SupabaseRestClient>(), sp.GetRequiredService<SupabaseOptions>()));
builder.Services.AddSingleton<IChatStore>(sp => new SupabaseChatStore(sp.GetRequiredService<SupabaseRestClient>()));
builder.Services.AddSingleton(sp => new RunPersister(
    sp.GetRequiredService<IFileStore>(), sp.GetRequiredService<IRunFileStore>()));
builder.Services.AddSingleton(sp => new InputPurger(
    sp.GetRequiredService<IRunStore>(),
    sp.GetRequiredService<IRunFileStore>(),
    sp.GetRequiredService<IFileStore>()));
builder.Services.AddHostedService<InputPurgeService>();

// Kestrel refuses bodies over ~30 MB by default and the form reader caps
// multipart at 128 MB, so both have to be lifted to whatever the upload limit
// actually is - otherwise a folder inside the limit still dies as a 413.
long maxBytesPerSet = builder.Configuration.GetValue<long?>("Uploads:MaxBytesPerSet")
    ?? UploadReceiver.DefaultMaxBytesPerSet;
long maxRequestBytes = maxBytesPerSet * UploadReceiver.MaxSets + (16L * 1024 * 1024);

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxRequestBytes);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxRequestBytes;
    o.MultipartHeadersLengthLimit = 65536;
    o.ValueCountLimit = UploadReceiver.MaxFilesPerSet * UploadReceiver.MaxSets + 32;
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();

// The front end is three unversioned files, so a browser holding an old app.js
// against a new server is a real failure mode - it presents as an exception from
// a line number that no longer exists. "no-cache" still caches; it requires
// revalidation, so a reload gets a 304 when nothing changed and the new file the
// moment it did. At this size the round trip costs nothing next to the confusion.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
});

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

// resolved after Build so a test host's replacements win
IRunStore runStore = app.Services.GetRequiredService<IRunStore>();
IRunFileStore runFileStore = app.Services.GetRequiredService<IRunFileStore>();
IFileStore fileStore = app.Services.GetRequiredService<IFileStore>();
IChatStore chatStore = app.Services.GetRequiredService<IChatStore>();
RunPersister persister = app.Services.GetRequiredService<RunPersister>();

// A run only lives in this process, so anything still flagged running was killed
// by a restart and will never finish. Do it once, at boot.
try
{
    int interrupted = await runStore.MarkRunningAsInterruptedAsync();
    if (interrupted > 0)
        Console.WriteLine($" i marked {interrupted} interrupted run(s) from a previous process");
}
catch (Exception ex)
{
    Console.WriteLine($" ! could not reconcile interrupted runs: {ex.Message}");
}

/// <summary>20 runs per rolling 24 hours, per the spec's abuse guard.</summary>
const int RunsPerDay = 20;

// POST /api/discover - receives the picked folders as an upload. A browser
// cannot disclose a folder's path, so the files themselves are sent and
// rehydrated into a directory shaped exactly like the folder the user chose.
// Everything downstream - discovery, the engine, the exporters - is unchanged.
app.MapPost("/api/discover", async (HttpContext ctx) =>
{
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest(new { error = "Please choose at least one folder." });

    IFormCollection form;
    try
    {
        form = await ctx.Request.ReadFormAsync();
    }
    catch (Exception ex)
    {
        // a body that is empty or not really multipart throws in here; without
        // this the user gets a bare 500 for what is a bad request
        return Results.BadRequest(new
        {
            error = "Please choose at least one folder.",
            detail = ex.GetType().Name
        });
    }

    List<UploadItem> items = new();
    foreach (IFormFile file in form.Files)
    {
        // the browser sends one field per file, named set0..set3, carrying the
        // file's path relative to the folder that was picked
        if (!file.Name.StartsWith("set", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(file.Name.AsSpan(3), out int setIndex))
        {
            return Results.BadRequest(new { error = $"Unexpected upload field '{file.Name}'." });
        }

        items.Add(new UploadItem(setIndex, file.FileName, file.OpenReadStream(), file.Length));
    }

    Guid? userId = SupabaseJwt.UserId(ctx.User);
    if (userId == null) return Results.Unauthorized();

    int recent = await runStore.CountSinceAsync(userId.Value, DateTimeOffset.UtcNow.AddDays(-1));
    if (recent >= RunsPerDay)
    {
        return Results.Json(
            new { error = $"You have started {recent} runs in the last 24 hours; the limit is {RunsPerDay}." },
            statusCode: 429);
    }

    // the run id is the database's, so every artifact path and every later
    // lookup keys off the same value
    RunRecord created = await runStore.CreateAsync(
        userId.Value, items.Select(i => i.RelativePath.Split('/')[0]).Distinct().ToList());

    string rid = created.Id.ToString();
    string runRoot = Path.Combine(runsDir, rid);
    string outdir = Path.Combine(runRoot, "output");
    string indir = Path.Combine(runRoot, "input");
    Directory.CreateDirectory(outdir);
    Directory.CreateDirectory(indir);

    UploadOutcome upload = await new UploadReceiver(maxBytesPerSet).ReceiveAsync(indir, items, ctx.RequestAborted);
    if (!upload.Ok)
    {
        Directory.Delete(runRoot, recursive: true);
        await runStore.UpdateStatusAsync(created.Id, "error", upload.Error);
        return Results.BadRequest(new { error = upload.Error });
    }

    List<string> paths = upload.Sets.Select(s => s.Root).ToList();

    jobs[rid] = new JobState
    {
        Id = rid,
        UserId = userId.Value,
        Status = "ready",
        Roots = paths,
        Outdir = outdir,
        Indir = indir,
        Started = DateTime.Now.ToString("o")
    };

    // the uploaded inputs go up in the background: the user is waiting on
    // discovery, and a slow transfer must not hold up the inventory
    _ = Task.Run(async () =>
    {
        try
        {
            await persister.PersistDirectoryAsync(userId.Value, created.Id, "input", indir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($" ! could not store inputs for {rid}: {ex.Message}");
        }
    });

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
}).RequireAuthorization();

// POST /api/run
app.MapPost("/api/run", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    string bodyStr = await reader.ReadToEndAsync();
    using var doc = JsonDocument.Parse(bodyStr);
    string? rid = doc.RootElement.TryGetProperty("run_id", out var rProp) ? rProp.GetString() : null;

    Guid? runUser = SupabaseJwt.UserId(ctx.User);
    if (runUser == null) return Results.Unauthorized();

    // 404 rather than 403 for someone else's run: a 403 confirms it exists
    if (string.IsNullOrEmpty(rid) || !jobs.TryGetValue(rid, out var job) || job.UserId != runUser.Value)
        return Results.NotFound(new { error = "Unknown run - please run discovery again." });

    if (job.Status == "running")
        return Results.Ok(new { run_id = rid, status = "running" });

    string? modelId = doc.RootElement.TryGetProperty("model_id", out var modelProp) ? modelProp.GetString() : null;
    job.ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();

    Guid runGuid = Guid.Parse(rid);
    await runStore.SetModelAsync(runGuid, job.ModelId);
    await runStore.UpdateStatusAsync(runGuid, "running", null);

    job.Status = "running";
    job.Log.Clear();
    job.Error = null;
    job.Result = null;
    job.Stages = Array.Empty<RunStage>();
    job.Started = DateTime.Now.ToString("HH:mm:ss");
    job.StartedAt = DateTimeOffset.UtcNow;
    job.FinishedAt = null;

    void Logger(string msg, string kind) =>
        job.Log.Add(new Dictionary<string, string>
        {
            ["t"] = DateTime.Now.ToString("HH:mm:ss"),
            ["msg"] = msg,
            ["kind"] = kind
        });

    var capturedJob = job;
    StageReporter stages = new(list => capturedJob.Stages = list);
    _ = Task.Run(async () =>
    {
        try
        {
            var engine = new ReconciliationEngine();

            AiAnalysisService? analyst = (llm != null && capturedJob.ModelId != null)
                ? new AiAnalysisService(llm, capturedJob.ModelId)
                : null;

            ReconciliationRunResult outResult = engine.Run(
                capturedJob.Roots, capturedJob.Outdir, logger: Logger,
                analyze: analyst != null, analyst: analyst, stages: stages);

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
                wo_pre_window = kv.Value.Summary.WoPreWindow,
                wo_post_window = kv.Value.Summary.WoPostWindow,
                scored = kv.Value.Summary.ScoredDistinct,
                ifrs9_overlap = kv.Value.Summary.Ifrs9KeyOverlap,
                // the run detail reports the matrix comparison on its own card
                mig_validation = kv.Value.Summary.MigValidation,
                mig_max_diff = kv.Value.Summary.MigValidationMaxDiff,
                files = kv.Value.Summary.Files
            }).ToList();

            // the engine has finished; anything after this is bookkeeping
            capturedJob.FinishedAt = DateTimeOffset.UtcNow;

            capturedJob.Result = new
            {
                sets = setSummaries,
                workbook = outResult.Workbook,
                dashboard = outResult.Dashboard,
                memo = outResult.Memo,
                // the dashboard's own data, captured now: rebuilding the migration
                // matrix needs pd_scored.csv, and inputs are purged long before runs
                dashboard_sets = outResult.Results
                    .Select(kv => DashboardPayload.Build(kv.Key, kv.Value)).ToList(),
                // the same sentences the workbook opens with, so the verdict on screen
                // cannot disagree with the verdict in the signed-off spreadsheet
                commentary = WorkbookExporter.CommentaryLines(outResult.Results),
                analysis = outResult.Analysis,
                // named on the analysis card, so the reader knows what wrote it
                model_id = capturedJob.ModelId,
                // stored with the run so the timing and file sizes survive a restart
                // and a reopened run shows the same detail as a fresh one. Stages are
                // deliberately not kept: they describe a run in flight, and the
                // progress screen is the only thing that shows them.
                elapsed_seconds = capturedJob.StartedAt == null
                    ? (double?)null
                    : Math.Round((capturedJob.FinishedAt.Value - capturedJob.StartedAt.Value).TotalSeconds, 1),
                outputs = OutputFiles.Describe(capturedJob.Outdir, outResult)
            };
            capturedJob.Status = "done";

            // Same isolation as the chat payload above: the run is finished and
            // its artifacts are on disk. A storage or database failure here is
            // reported, never allowed to downgrade a completed run to an error.
            try
            {
                RunPersister.PersistOutcome stored = await persister.PersistDirectoryAsync(
                    capturedJob.UserId, runGuid, "output", capturedJob.Outdir);

                if (stored.Failed.Count > 0)
                    Logger($"Could not store {stored.Failed.Count} artifact(s): {string.Join(", ", stored.Failed)}", "warn");
            }
            catch (Exception storeEx)
            {
                Logger($"Could not store artifacts: {storeEx.GetType().Name}: {storeEx.Message}", "warn");
            }
        }
        catch (Exception ex)
        {
            capturedJob.Error = $"{ex.GetType().Name}: {ex.Message}";
            capturedJob.Status = "error";
            Logger(capturedJob.Error, "warn");
            // otherwise the progress screen keeps a row spinning on a dead run
            stages.Settle(StageStatus.Error);
        }

        // already set when the engine returned; this covers the failure path
        capturedJob.FinishedAt ??= DateTimeOffset.UtcNow;

        try
        {
            await runStore.SaveCompletionAsync(
                runGuid, capturedJob.Status, capturedJob.Error,
                capturedJob.Result, capturedJob.AnalysisPayload, capturedJob.Log);
        }
        catch (Exception saveEx)
        {
            Logger($"Could not save the run: {saveEx.GetType().Name}: {saveEx.Message}", "warn");
        }
    });

    return Results.Ok(new { run_id = rid, status = "running" });
}).RequireAuthorization();

// GET /api/job/{rid}
app.MapGet("/api/job/{rid}", (string rid, HttpContext ctx) =>
{
    Guid? jobUser = SupabaseJwt.UserId(ctx.User);
    if (jobUser == null) return Results.Unauthorized();

    // another user's run is reported missing, not forbidden
    if (!jobs.TryGetValue(rid, out var job) || job.UserId != jobUser.Value)
        return Results.NotFound(new { error = "Unknown run" });

    return Results.Ok(new
    {
        id = rid,
        status = job.Status,
        log = job.Log,
        error = job.Error,
        result = job.Result,
        // drives the stage list, progress bar and elapsed clock
        stages = job.Stages,
        elapsed_seconds = job.StartedAt == null
            ? (double?)null
            : Math.Round(((job.FinishedAt ?? DateTimeOffset.UtcNow) - job.StartedAt.Value).TotalSeconds, 1)
    });
}).RequireAuthorization();

// POST /api/chat
app.MapPost("/api/chat", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    string bodyStr = await reader.ReadToEndAsync();
    using var doc = JsonDocument.Parse(bodyStr);

    string? rid = doc.RootElement.TryGetProperty("run_id", out var rProp) ? rProp.GetString() : null;
    string message = doc.RootElement.TryGetProperty("message", out var mProp) ? (mProp.GetString() ?? "").Trim() : "";

    Guid? chatUser = SupabaseJwt.UserId(ctx.User);
    if (chatUser == null) return Results.Unauthorized();

    if (string.IsNullOrEmpty(rid) || !jobs.TryGetValue(rid, out var job) || job.UserId != chatUser.Value)
        return Results.NotFound(new { error = "Unknown run - please run reconciliation first." });

    if (job.Status != "done")
        return Results.BadRequest(new { error = "This run has not finished yet." });

    if (string.IsNullOrEmpty(message))
        return Results.BadRequest(new { error = "Please enter a question." });

    ChatService chatService = new(llm, job.ModelId);
    var chatRes = chatService.ProcessQuestion(message, job.AnalysisPayload ?? new Dictionary<string, object>());
    if (chatRes.IsError)
        return Results.Json(new { error = chatRes.ErrorMessage }, statusCode: 503);

    // Isolated like the rest: the user has their answer on screen, so failing to
    // record it must not turn a good reply into an error.
    try
    {
        Guid chatRunId = Guid.Parse(rid);
        await chatStore.AddAsync(new[]
        {
            new ChatMessageRecord
            {
                RunId = chatRunId, UserId = chatUser.Value,
                Role = "user", Content = message
            },
            new ChatMessageRecord
            {
                RunId = chatRunId, UserId = chatUser.Value,
                Role = "assistant", Content = chatRes.Reply ?? "", ContentHtml = chatRes.ReplyHtml
            }
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($" ! could not save chat for {rid}: {ex.Message}");
    }

    return Results.Ok(new { reply = chatRes.Reply, reply_html = chatRes.ReplyHtml });
}).RequireAuthorization();

// GET /runs/{rid}/output/{filename}
app.MapGet("/runs/{rid}/output/{filename}", async (string rid, string filename, HttpContext ctx) =>
{
    Guid? fileUser = SupabaseJwt.UserId(ctx.User);
    if (fileUser == null) return Results.Unauthorized();

    // an unowned run is indistinguishable from a missing one
    if (jobs.TryGetValue(rid, out var job) && job.UserId != fileUser.Value)
        return Results.NotFound();

    string outdir = job?.Outdir ?? Path.Combine(runsDir, rid, "output");
    string filePath = Path.GetFullPath(Path.Combine(outdir, filename));

    // keep the request inside the run's own output folder
    string outdirFull = Path.GetFullPath(outdir);
    if (!filePath.StartsWith(outdirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();

    if (File.Exists(filePath))
    {
        // The dashboard is shown in an iframe, so HTML has to be served inline with
        // its real content type - as an octet-stream attachment the frame renders
        // blank. Everything else stays a download.
        if (filename.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            return Results.File(filePath, contentType: "text/html; charset=utf-8");

        return Results.File(filePath, contentType: "application/octet-stream", fileDownloadName: filename);
    }

    // Not on disk: either a restart wiped it or this is a run from an earlier
    // process. Fall back to object storage, which is the whole point of keeping
    // the artifacts there.
    if (!Guid.TryParse(rid, out Guid storedRunId)) return Results.NotFound();

    RunFileRecord? record = await runFileStore.FindOutputAsync(storedRunId, fileUser.Value, filename);
    if (record == null) return Results.NotFound();

    string signed = await fileStore.CreateSignedUrlAsync(record.StoragePath, 60);
    return Results.Redirect(signed);
}).RequireAuthorization();

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
}).RequireAuthorization();

// GET /api/runs - the caller's history, newest first
app.MapGet("/api/runs", async (HttpContext ctx) =>
{
    Guid? historyUser = SupabaseJwt.UserId(ctx.User);
    if (historyUser == null) return Results.Unauthorized();

    IReadOnlyList<RunRecord> runs = await runStore.ListAsync(historyUser.Value);

    return Results.Ok(runs.Select(r =>
    {
        RunSummary summary = RunSummary.From(r.Result);
        return new
        {
            id = r.Id,
            status = r.Status,
            model_id = r.ModelId,
            set_labels = r.SetLabels,
            created_at = r.CreatedAt,
            finished_at = r.FinishedAt,
            error = r.Error,
            inputs_purged = r.InputsPurgedAt != null,
            // drives the stat tiles and the trend, read from the stored result so
            // the list can never disagree with the run it describes
            sets = summary.Sets,
            untraced = summary.Untraced,
            trace_rate = summary.TraceRate,
            exceptions = summary.Exceptions
        };
    }));
}).RequireAuthorization();

// GET /api/runs/{rid} - one past run in full, so it can be reopened
app.MapGet("/api/runs/{rid}", async (string rid, HttpContext ctx) =>
{
    Guid? historyUser = SupabaseJwt.UserId(ctx.User);
    if (historyUser == null) return Results.Unauthorized();

    if (!Guid.TryParse(rid, out Guid runId)) return Results.NotFound(new { error = "Unknown run" });

    RunRecord? run = await runStore.GetAsync(runId, historyUser.Value);
    if (run == null) return Results.NotFound(new { error = "Unknown run" });

    IReadOnlyList<ChatMessageRecord> chat;
    try
    {
        chat = await chatStore.ListAsync(runId, historyUser.Value);
    }
    catch (Exception)
    {
        // the run itself is the point; a missing conversation must not 500 it
        chat = Array.Empty<ChatMessageRecord>();
    }

    return Results.Ok(new
    {
        id = run.Id,
        status = run.Status,
        model_id = run.ModelId,
        set_labels = run.SetLabels,
        created_at = run.CreatedAt,
        finished_at = run.FinishedAt,
        error = run.Error,
        log = run.Log,
        result = run.Result,
        inputs_purged = run.InputsPurgedAt != null,
        chat = chat.Select(m => new
        {
            role = m.Role,
            content = m.Content,
            content_html = m.ContentHtml,
            created_at = m.CreatedAt
        })
    });
}).RequireAuthorization();

// POST /api/session - hands the verified token back as a cookie so the browser
// can load the dashboard iframe and the artifact links, which cannot carry an
// Authorization header. Scoped to /runs, so it is never sent to the JSON API.
app.MapPost("/api/session", (HttpContext ctx) =>
{
    string? token = ctx.Request.Headers.Authorization.ToString()
        .Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();

    if (string.IsNullOrEmpty(token)) return Results.Unauthorized();

    ctx.Response.Cookies.Append(SupabaseJwt.DownloadCookie, token, new CookieOptions
    {
        HttpOnly = true,
        Secure = ctx.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/runs",
        MaxAge = TimeSpan.FromHours(1)
    });

    return Results.Ok(new { ok = true });
}).RequireAuthorization();

// DELETE /api/session - drop the cookie on sign-out
app.MapDelete("/api/session", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete(SupabaseJwt.DownloadCookie, new CookieOptions { Path = "/runs" });
    return Results.Ok(new { ok = true });
});

// GET /api/config - the browser needs the project URL and the public anon key to
// start a session. The service-role key is never exposed here.
app.MapGet("/api/config", () => Results.Ok(new
{
    supabaseUrl = supabaseOptions.BaseUrl,
    supabaseAnonKey = supabaseOptions.AnonKey,
    // served rather than duplicated in app.js: a browser limit that disagrees
    // with the server's rejects folders the server would have accepted
    maxBytesPerSet,
    maxFilesPerSet = UploadReceiver.MaxFilesPerSet,
    maxSets = UploadReceiver.MaxSets
}));

// GET /health
app.MapGet("/health", () => Results.Ok(new { ok = true, runs = jobs.Count }));

Console.WriteLine("==================================================================");
Console.WriteLine(" Hazard-Rate Reconciliation (.NET) | Anchor Point Risk");
Console.WriteLine($" Open http://{host}:{port} in your browser (Ctrl+C here to stop)");
Console.WriteLine(" Single instance only - run history assumes one process owns the live job cache");
Console.WriteLine("==================================================================");

app.Run();
return 0;

// exposed so WebApplicationFactory can boot the real app in tests
public partial class Program { }
