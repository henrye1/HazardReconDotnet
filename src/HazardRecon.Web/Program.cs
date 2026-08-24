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
            new HttpDocumentRetriever
            {
                // Fetching the (public) signing keys over plain HTTP is only ever true
                // for a local Supabase instance during development - production is
                // always https. This does not weaken token validation itself: the JWT's
                // signature, issuer and audience are still checked in full.
                RequireHttps = supabaseOptions.BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            });

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
builder.Services.AddSingleton<IColumnMappingStore>(sp => new SupabaseColumnMappingStore(sp.GetRequiredService<SupabaseRestClient>()));
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
    ?? SetFileReceiver.DefaultMaxBytesPerSet;
long maxRequestBytes = maxBytesPerSet * SetFileReceiver.MaxSets + (16L * 1024 * 1024);

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxRequestBytes);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxRequestBytes;
    o.MultipartHeadersLengthLimit = 65536;
    o.ValueCountLimit = 8 * SetFileReceiver.MaxSets + 32; // 4 kinds, up to 3 loose debug files
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

// header-match and saved-mapping resolution always run through this service;
// only the AI-guess step needs llm/MappingModelId, and no-ops without them
ColumnMappingService columnMapper = new(llm, llmOptions.MappingModelId);

string baseDir = AppContext.BaseDirectory;
string runsDir = Path.Combine(baseDir, "runs");
Directory.CreateDirectory(runsDir);

var jobs = new ConcurrentDictionary<string, JobState>();

// resolved after Build so a test host's replacements win
IRunStore runStore = app.Services.GetRequiredService<IRunStore>();
IRunFileStore runFileStore = app.Services.GetRequiredService<IRunFileStore>();
IFileStore fileStore = app.Services.GetRequiredService<IFileStore>();
IChatStore chatStore = app.Services.GetRequiredService<IChatStore>();
IColumnMappingStore columnMappingStore = app.Services.GetRequiredService<IColumnMappingStore>();
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

/// <summary>
/// Which file kind a run's exposure slot is mapped and saved under. A receivables
/// age analysis has its own field set, so it gets its own kind rather than sharing
/// the exposure profile.
/// </summary>
static string ExposureFileKind(string runType) =>
    runType == RunTypeLookup.TradeReceivables ? "age_analysis" : "exposure";

/// <summary>The engine's own notion of the run type, which Core declares itself.</summary>
static EngineRunType ToEngineRunType(string runType) =>
    runType == RunTypeLookup.TradeReceivables ? EngineRunType.TradeReceivables : EngineRunType.Lending;

/// <summary>
/// Long enough for "June 2026 retail book, revised write-off export" and short
/// enough that a run list stays readable. The name input carries the same
/// maxlength, so a longer one only ever reaches here from a non-browser client.
/// </summary>
const int MaxRunNameChars = 120;

// POST /api/discover - receives one exposure (IFRS9), one write-off, one
// debug (zip or its 1-3 loose extracted files), and one scenario file per
// set, each tagged by the client as set{N}.{kind}. Rehydrates each under the
// canonical name InputDiscoverer.BuildSet already looks for, so file *role*
// is never guessed - only the write-off/exposure CSVs' *columns* need a
// mapping, resolved here and returned for the Map-columns step to confirm.
app.MapPost("/api/discover", async (HttpContext ctx) =>
{
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest(new { error = "Please choose at least one set's files." });

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
            error = "Please choose at least one set's files.",
            detail = ex.GetType().Name
        });
    }

    List<SetFileItem> items = new();
    foreach (IFormFile file in form.Files)
    {
        string[] parts = file.Name.Split('.', 2);
        if (parts.Length != 2 || !parts[0].StartsWith("set", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[0].AsSpan(3), out int setIndex)
            || !Enum.TryParse(parts[1], ignoreCase: true, out SetFileKind kind))
        {
            return Results.BadRequest(new { error = $"Unexpected upload field '{file.Name}'." });
        }

        items.Add(new SetFileItem(setIndex, kind, file.FileName, file.OpenReadStream(), file.Length));
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

    if (items.Count == 0)
        return Results.BadRequest(new { error = "Please choose at least one set's files." });

    // The details step's two answers, as form values rather than files - the loop
    // above only walks form.Files, so these cannot be mistaken for a mis-named
    // set{N}.{kind} upload. Read last, immediately before the row is created: the
    // guards above own the response for an empty body, an unexpected field and
    // the daily quota, and checking a name first would take those over.
    //
    // FirstOrDefault rather than ToString: StringValues comma-joins a field sent
    // twice, which would read as one plausible name rather than a bad request.
    string runName = (form["name"].FirstOrDefault() ?? "").Trim();
    if (runName.Length == 0)
        return Results.BadRequest(new { error = "Please give this run a name." });

    // refused rather than truncated - a silently shortened name is a surprise the
    // user only finds later, in the history list
    if (runName.Length > MaxRunNameChars)
        return Results.BadRequest(new { error = $"The run name must be {MaxRunNameChars} characters or fewer." });

    // Saying nothing means the default, as the column itself says. Saying
    // something unrecognised is a client bug, and filing it as lending anyway
    // would quietly mislabel the run rather than surface that.
    string runType = (form["run_type"].FirstOrDefault() ?? "").Trim();
    if (runType.Length == 0) runType = RunTypeLookup.Default;
    if (!RunTypeLookup.IsKnown(runType))
        return Results.BadRequest(new { error = $"Unknown run type '{runType}'." });

    EngineRunType engineRunType = ToEngineRunType(runType);

    RunRecord created = await runStore.CreateAsync(
        userId.Value,
        runName,
        runType,
        items.Where(i => i.Kind == SetFileKind.Exposure)
            .OrderBy(i => i.SetIndex)
            .Select(i => Path.GetFileNameWithoutExtension(i.OriginalFileName))
            .ToList());

    string rid = created.Id.ToString();
    string runRoot = Path.Combine(runsDir, rid);
    string outdir = Path.Combine(runRoot, "output");
    string indir = Path.Combine(runRoot, "input");
    Directory.CreateDirectory(outdir);
    Directory.CreateDirectory(indir);

    SetReceiveOutcome received = await new SetFileReceiver(maxBytesPerSet).ReceiveAsync(indir, items, ctx.RequestAborted);
    if (!received.Ok)
    {
        Directory.Delete(runRoot, recursive: true);
        await runStore.UpdateStatusAsync(created.Id, "error", received.Error);
        return Results.BadRequest(new { error = received.Error });
    }

    List<string> setKeys = InputDiscoverer.SetKeysForLabels(received.Sets.Select(s => s.Label));

    JobState job = new()
    {
        Id = rid,
        UserId = userId.Value,
        Status = "ready",
        RunType = runType,
        Roots = received.Sets.Select(s => s.Root).ToList(),
        SetIdentities = received.Sets
            .Select((s, i) => (s.Root, Identity: new SetIdentity(setKeys[i], s.Label)))
            .ToDictionary(x => x.Root, x => x.Identity),
        Outdir = outdir,
        Indir = indir,
        Started = DateTime.Now.ToString("o")
    };
    jobs[rid] = job;

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

    var discoverer = new InputDiscoverer();
    var setViews = new List<object>();
    var mappingViews = new List<object>();
    var problems = new List<string>();

    foreach (ReceivedSet rs in received.Sets)
    {
        InventorySet? s = discoverer.BuildSet(rs.Root);
        if (s == null)
        {
            problems.Add($"{rs.Label}: no analysis data found - check the debug file.");
            continue;
        }

        s.Label = rs.Label;

        // the key this set will be known by for the rest of the run, decided
        // when the upload landed - deriving one here instead would file the
        // mapping below under a key the engine never looks under
        string key = job.SetIdentities[rs.Root].Key;

        setViews.Add(new
        {
            key,
            label = s.Label,
            lgd_defaults = s.LgdDefaults == null ? null : Path.GetFileName(s.LgdDefaults),
            pd_scored = s.PdScored == null ? null : Path.GetFileName(s.PdScored),
            ifrs9 = s.Ifrs9 == null ? null : Path.GetFileName(s.Ifrs9),
            scenario = s.Scenario == null ? null : Path.GetFileName(s.Scenario),
            debug_json = s.DebugJson == null ? null : Path.GetFileName(s.DebugJson),
            writeoff = s.WriteOff == null ? null : Path.GetFileName(s.WriteOff)
        });

        if (string.IsNullOrEmpty(s.WriteOff)) problems.Add($"{key}: no write-off CSV - check 2 cannot run for this set.");
        if (string.IsNullOrEmpty(s.PdScored)) problems.Add($"{key}: pd_scored.csv missing - no migrations.");
        if (string.IsNullOrEmpty(s.Scenario)) problems.Add($"{key}: scenario.json missing - no engine results.");
        if (string.IsNullOrEmpty(s.Ifrs9)) problems.Add($"{key}: no IFRS9 file - defaults can only trace to write-off.");

        // the write-off file is optional, so its canonical path may simply not be
        // there - sniffing it regardless would throw and turn a supported upload
        // into a 500
        string exposurePath = Path.Combine(rs.Root, "IFRS9.csv");
        string writeOffCandidate = Path.Combine(rs.Root, "writeoff.csv");
        string? writeOffPath = File.Exists(writeOffCandidate) ? writeOffCandidate : null;

        CsvSniff exposureSniff = CsvSniffer.Sniff(exposurePath);
        CsvSniff? writeoffSniff = writeOffPath == null ? null : CsvSniffer.Sniff(writeOffPath);

        job.MappableFiles[key] = new MappableSetFiles(
            writeOffPath, writeoffSniff?.HasHeaders ?? false, exposurePath, exposureSniff.HasHeaders);

        string exposureSignature = ColumnSignature.Compute(exposureSniff.Headers, exposureSniff.SampleRows);

        // A receivables book puts an age analysis in this slot, with a different
        // field set - so a saved profile is filed under its own kind rather than
        // being offered a column for a field that does not exist there.
        IReadOnlyList<MappingFieldSpec> exposureSpecs = MappableFields.ExposureFor(engineRunType);
        string exposureKind = ExposureFileKind(runType);

        IReadOnlyDictionary<string, IReadOnlyList<string>> savedExposure =
            await columnMappingStore.GetSavedMappingAsync(userId.Value, exposureKind, exposureSignature);

        IReadOnlyList<ResolvedField> exposureFields =
            columnMapper.Resolve(exposureSniff.Headers, exposureSniff.SampleRows, exposureSpecs, savedExposure);

        object FileView(CsvSniff sniff, IReadOnlyList<MappingFieldSpec> specs, IReadOnlyList<ResolvedField> resolved) => new
        {
            has_headers = sniff.HasHeaders,
            headers = sniff.Headers,
            samples = sniff.SampleRows,
            fields = specs.Select(spec =>
            {
                ResolvedField r = resolved.First(x => x.Field == spec.Field);
                return new
                {
                    field = spec.Field,
                    note = spec.Note,
                    // multiple/columns are the truth for a field that takes several;
                    // column is kept for the single case so the client's existing
                    // path is untouched
                    multiple = spec.Multiple,
                    column = r.Column,
                    columns = r.Columns,
                    confidence = r.Confidence,
                    source = r.Source
                };
            })
        };

        object? writeoffView = null;
        if (writeoffSniff != null)
        {
            string writeoffSignature = ColumnSignature.Compute(writeoffSniff.Headers, writeoffSniff.SampleRows);
            IReadOnlyDictionary<string, IReadOnlyList<string>> savedWriteoff =
                await columnMappingStore.GetSavedMappingAsync(userId.Value, "writeoff", writeoffSignature);
            IReadOnlyList<ResolvedField> writeoffFields =
                columnMapper.Resolve(writeoffSniff.Headers, writeoffSniff.SampleRows, MappableFields.Writeoff,
                    savedWriteoff);

            writeoffView = FileView(writeoffSniff, MappableFields.Writeoff, writeoffFields);
        }

        mappingViews.Add(new
        {
            key,
            // null rather than absent, so the client can tell "this set has no
            // write-off file" from "the response is malformed"
            writeoff = writeoffView,
            exposure = FileView(exposureSniff, exposureSpecs, exposureFields)
        });
    }

    if (setViews.Count == 0)
        problems.Insert(0, "No analysis sets found. Each set needs debug.zip (or an extracted lgd_defaults.csv).");

    return Results.Ok(new
    {
        run_id = rid,
        inventory = new { root = string.Join("; ", received.Sets.Select(s => s.Root)), sets = setViews },
        problems,
        mapping = mappingViews
    });
}).RequireAuthorization();

// POST /api/discover/mapping - persists the user-confirmed column mapping for
// each set's write-off/exposure files: an audit row per run+set+file, and an
// upserted reusable profile keyed by the file's column signature so the same
// export format does not need re-mapping next time. Stashes the confirmed
// maps into JobState so /api/run can hand them to the engine directly.
app.MapPost("/api/discover/mapping", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    string bodyStr = await reader.ReadToEndAsync();
    using var doc = JsonDocument.Parse(bodyStr);

    string? rid = doc.RootElement.TryGetProperty("run_id", out var rProp) ? rProp.GetString() : null;

    Guid? userId = SupabaseJwt.UserId(ctx.User);
    if (userId == null) return Results.Unauthorized();

    if (string.IsNullOrEmpty(rid) || !jobs.TryGetValue(rid, out var job) || job.UserId != userId.Value)
        return Results.NotFound(new { error = "Unknown run - please run discovery again." });

    if (!doc.RootElement.TryGetProperty("sets", out JsonElement setsElem) || setsElem.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = "Missing 'sets'." });

    Guid runGuid = Guid.Parse(rid);

    foreach (JsonElement setElem in setsElem.EnumerateArray())
    {
        string key = setElem.GetProperty("key").GetString() ?? "";
        if (!job.MappableFiles.TryGetValue(key, out MappableSetFiles? files)) continue;

        // whichever field set this run's exposure slot was offered - the client
        // sends back what /api/discover asked for, so both sides must agree on it
        string exposureKind = ExposureFileKind(job.RunType);

        Dictionary<string, IReadOnlyList<string>> exposureMapping =
            MappingRequest.ReadMapping(setElem, "exposure");

        await columnMappingStore.RecordRunMappingAsync(runGuid, key, exposureKind, exposureMapping);

        // the sniffer's verdict unless the user overruled it in the mapping step,
        // which decides both how the loaders address columns and which signature
        // the reusable profile is filed under
        bool exposureHasHeaders = MappingRequest.ReadHasHeaders(setElem, "exposure") ?? files.ExposureHasHeaders;

        CsvSniff exposureSniff = CsvSniffer.Reinterpret(CsvSniffer.Sniff(files.ExposurePath), exposureHasHeaders);
        string exposureSignature = ColumnSignature.Compute(exposureSniff.Headers, exposureSniff.SampleRows);

        await columnMappingStore.SaveMappingAsync(userId.Value, exposureKind, exposureSignature, exposureMapping);

        // a set uploaded without a write-off file has nothing to map for it, so
        // there is no signature to save a profile against and no map to hand the
        // engine - SetColumnMaps takes null for exactly this
        ColumnMap? writeOffMap = null;
        if (files.WriteOffPath != null)
        {
            Dictionary<string, IReadOnlyList<string>> writeoffMapping =
                MappingRequest.ReadMapping(setElem, "writeoff");

            await columnMappingStore.RecordRunMappingAsync(runGuid, key, "writeoff", writeoffMapping);

            bool writeoffHasHeaders = MappingRequest.ReadHasHeaders(setElem, "writeoff") ?? files.WriteOffHasHeaders;

            CsvSniff writeoffSniff = CsvSniffer.Reinterpret(CsvSniffer.Sniff(files.WriteOffPath), writeoffHasHeaders);
            string writeoffSignature = ColumnSignature.Compute(writeoffSniff.Headers, writeoffSniff.SampleRows);

            await columnMappingStore.SaveMappingAsync(userId.Value, "writeoff", writeoffSignature, writeoffMapping);

            writeOffMap = new ColumnMap(writeoffHasHeaders, writeoffMapping);
        }

        job.ColumnMaps[key] = new SetColumnMaps(
            writeOffMap,
            new ColumnMap(exposureHasHeaders, exposureMapping));
    }

    return Results.Ok(new { ok = true });
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
        job.Log.Enqueue(new JobLogEntry(DateTimeOffset.UtcNow, msg, kind));

    var capturedJob = job;
    StageReporter stages = new(list => capturedJob.Stages = list);
    _ = Task.Run(async () =>
    {
        // populated on success only; an error leaves the run with no set
        // results/output files, same as the old design leaving result null
        List<RunSetResultRecord> setResults = new();
        List<RunOutputFileRecord> outputFileRecords = new();
        List<RunCommentaryLineRecord> commentaryRecords = new();
        RunResultsRecord runResultsRow = new() { RunId = runGuid };

        try
        {
            var engine = new ReconciliationEngine();

            AiAnalysisService? analyst = (llm != null && capturedJob.ModelId != null)
                ? new AiAnalysisService(llm, capturedJob.ModelId)
                : null;

            ReconciliationRunResult outResult = engine.Run(
                capturedJob.Roots, capturedJob.Outdir, logger: Logger,
                analyze: analyst != null, analyst: analyst, stages: stages,
                columnMaps: capturedJob.ColumnMaps,
                setIdentities: capturedJob.SetIdentities,
                runType: ToEngineRunType(capturedJob.RunType));

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
                Logger($"Could not build chat payload: {payloadEx.GetType().Name}: {payloadEx.Message}", LogKind.Warn);
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
                commentary = WorkbookExporter.CommentaryLines(
                    outResult.Results, ToEngineRunType(capturedJob.RunType)),
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

            // the rows public.runs' completion RPC actually persists
            setResults = outResult.Results
                .Select(kv => RunSetResultMapper.Build(runGuid, capturedJob.UserId, kv.Key, kv.Value))
                .ToList();
            outputFileRecords = RunSetResultMapper.BuildOutputFiles(runGuid, capturedJob.UserId, capturedJob.Outdir, outResult);
            commentaryRecords = WorkbookExporter.CommentaryLines(
                    outResult.Results, ToEngineRunType(capturedJob.RunType))
                .Select((line, i) => new RunCommentaryLineRecord
                    { RunId = runGuid, UserId = capturedJob.UserId, Line = line, Position = i })
                .ToList();
            runResultsRow = new RunResultsRecord
            {
                RunId = runGuid,
                WorkbookFilename = outResult.Workbook,
                DashboardFilename = outResult.Dashboard,
                MemoFilename = outResult.Memo,
                AnalysisMarkdown = outResult.Analysis
            };

            // Same isolation as the chat payload above: the run is finished and
            // its artifacts are on disk. A storage or database failure here is
            // reported, never allowed to downgrade a completed run to an error.
            try
            {
                RunPersister.PersistOutcome stored = await persister.PersistDirectoryAsync(
                    capturedJob.UserId, runGuid, "output", capturedJob.Outdir);

                if (stored.Failed.Count > 0)
                    Logger($"Could not store {stored.Failed.Count} artifact(s): {string.Join(", ", stored.Failed)}", LogKind.Warn);
            }
            catch (Exception storeEx)
            {
                Logger($"Could not store artifacts: {storeEx.GetType().Name}: {storeEx.Message}", LogKind.Warn);
            }
        }
        catch (Exception ex)
        {
            capturedJob.Error = $"{ex.GetType().Name}: {ex.Message}";
            capturedJob.Status = "error";
            Logger(capturedJob.Error, LogKind.Warn);
            // otherwise the progress screen keeps a row spinning on a dead run
            stages.Settle(StageStatus.Error);
        }

        // already set when the engine returned; this covers the failure path
        capturedJob.FinishedAt ??= DateTimeOffset.UtcNow;

        List<LogEntryRecord> logRecords = capturedJob.Log.Select((l, i) => new LogEntryRecord
        {
            RunId = runGuid,
            UserId = capturedJob.UserId,
            Seq = i + 1,
            OccurredAt = l.OccurredAt,
            TypeId = LogTypeLookup.IdOf(l.Kind),
            Message = l.Message
        }).ToList();

        try
        {
            await runStore.SaveCompletionAsync(
                runGuid, capturedJob.UserId, capturedJob.Status, capturedJob.Error,
                runResultsRow, setResults, logRecords, outputFileRecords, commentaryRecords);
        }
        catch (Exception saveEx)
        {
            Logger($"Could not save the run: {saveEx.GetType().Name}: {saveEx.Message}", LogKind.Warn);
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
        log = job.Log.Select(l => new
        {
            t = l.OccurredAt.ToLocalTime().ToString("HH:mm:ss"),
            msg = l.Message,
            kind = l.Kind
        }),
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

    // a run reconciled without AI analysis has no model of its own, so the
    // conversation carries the one the user picked in the drawer
    string? askedModel = doc.RootElement.TryGetProperty("model_id", out var cmProp) ? cmProp.GetString() : null;

    Guid? chatUser = SupabaseJwt.UserId(ctx.User);
    if (chatUser == null) return Results.Unauthorized();

    if (string.IsNullOrEmpty(rid) || !Guid.TryParse(rid, out Guid chatRunGuid))
        return Results.NotFound(new { error = "Unknown run - please run reconciliation first." });

    // A run this process reconciled answers from what the engine just produced.
    // Anything else - reopened from history, or every run once the server has
    // restarted, since the job cache only ever holds this process's own runs -
    // is rebuilt from the stored tables rather than refused.
    string chatStatus;
    string? runModel;
    Dictionary<string, object> aggregates;

    if (jobs.TryGetValue(rid, out var job) && job.UserId == chatUser.Value)
    {
        chatStatus = job.Status;
        runModel = job.ModelId;
        aggregates = job.AnalysisPayload ?? new Dictionary<string, object>();
    }
    else
    {
        RunRecord? storedRun = await runStore.GetAsync(chatRunGuid, chatUser.Value);
        if (storedRun == null)
            return Results.NotFound(new { error = "Unknown run - please run reconciliation first." });

        chatStatus = storedRun.Status;
        runModel = storedRun.ModelId;
        aggregates = StoredAnalysisPayload.Build(storedRun);
    }

    if (chatStatus != RunStatus.Done)
        return Results.BadRequest(new { error = "This run has not finished yet." });

    if (string.IsNullOrEmpty(message))
        return Results.BadRequest(new { error = "Please enter a question." });

    ChatService chatService = new(llm, ChatModel.Choose(runModel, askedModel));
    var chatRes = chatService.ProcessQuestion(message, aggregates);
    if (chatRes.IsError)
        return Results.Json(new { error = chatRes.ErrorMessage }, statusCode: 503);

    // Isolated like the rest: the user has their answer on screen, so failing to
    // record it must not turn a good reply into an error.
    try
    {
        await chatStore.AddAsync(new[]
        {
            new ChatMessageRecord
            {
                RunId = chatRunGuid, UserId = chatUser.Value,
                Role = "user", Content = message
            },
            new ChatMessageRecord
            {
                RunId = chatRunGuid, UserId = chatUser.Value,
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
        RunSummary summary = RunSummary.From(r.RunSetResults);
        return new
        {
            id = r.Id,
            status = r.Status,
            // null for every run created before the wizard asked for a name
            name = r.Name,
            // the code, not the id and not a display string - the client holds the
            // labels, exactly as it does for status
            run_type = r.RunType,
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
        name = run.Name,
        run_type = run.RunType,
        model_id = run.ModelId,
        set_labels = run.SetLabels,
        created_at = run.CreatedAt,
        finished_at = run.FinishedAt,
        error = run.Error,
        log = RunDetailAssembler.BuildLog(run),
        result = RunDetailAssembler.BuildResult(run),
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

// DELETE /api/runs/{rid} - removes a run for good: its stored objects, its
// working folder and its row, which cascades to every child table. Offered from
// the run list and the run detail, both behind a confirmation.
app.MapDelete("/api/runs/{rid}", async (string rid, HttpContext ctx) =>
{
    Guid? deleteUser = SupabaseJwt.UserId(ctx.User);
    if (deleteUser == null) return Results.Unauthorized();

    if (!Guid.TryParse(rid, out Guid runId)) return Results.NotFound(new { error = "Unknown run" });

    // 404 rather than 403 for someone else's run, as everywhere else here
    RunRecord? run = await runStore.GetAsync(runId, deleteUser.Value);
    if (run == null) return Results.NotFound(new { error = "Unknown run" });

    // A live run's background task is still writing into the folder this would
    // delete underneath it, so it is refused rather than raced. The status in the
    // job cache is the live one; the stored row can lag a moment behind it.
    bool running = jobs.TryGetValue(rid, out JobState? job)
        ? job.Status == RunStatus.Running
        : run.Status == RunStatus.Running;

    if (running)
    {
        return Results.Conflict(new
        {
            error = "This run is still going. Wait for it to finish, then delete it."
        });
    }

    await new RunDeleter(runStore, runFileStore, fileStore, runsDir)
        .DeleteAsync(runId, deleteUser.Value, ctx.RequestAborted);

    // last, so a failure above leaves the run listed and deletable again rather
    // than stranding a cache entry pointing at a record that is gone
    jobs.TryRemove(rid, out _);

    return Results.Ok(new { ok = true });
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
    // 4 file kinds, with debug worth up to 3 (a zip, or its 3 loose extracted files)
    maxFilesPerSet = 6,
    maxSets = SetFileReceiver.MaxSets
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
