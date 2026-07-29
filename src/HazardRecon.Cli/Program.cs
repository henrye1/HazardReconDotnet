using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Microsoft.Extensions.Configuration;

namespace HazardRecon.Cli;

class Program
{
    static int Main(string[] args)
    {
        var roots = new List<string>();
        string outdir = "output";
        bool noAnalysis = false;
        string? modelFragment = null;

        for (int i = 0; i < args.Length; i++)
        {
            // --root can be supplied multiple times, OR as a single space-separated value
            if ((args[i] == "--root" || args[i] == "--roots") && i + 1 < args.Length)
            {
                roots.Add(args[i + 1]);
                i++;
            }
            else if (args[i] == "--outdir" && i + 1 < args.Length)
            {
                outdir = args[i + 1];
                i++;
            }
            else if (args[i] == "--model" && i + 1 < args.Length)
            {
                modelFragment = args[i + 1];
                i++;
            }
            else if (args[i] == "--no-analysis")
            {
                noAnalysis = true;
            }
            else if (!args[i].StartsWith('-'))
            {
                // bare positional argument — treat as a root folder
                roots.Add(args[i]);
            }
        }

        if (roots.Count == 0)
        {
            Console.WriteLine("Usage: hazard-recon --root <folder> [--root <folder2> ...] [--outdir <output>] [--model <name>] [--no-analysis]");
            Console.WriteLine("       Tip: --root may be repeated up to 4 times for multi-period runs.");
            Console.WriteLine("       --model takes a model id or part of its name; omit it to use the first available model.");
            Console.WriteLine("Error: at least one --root argument is required.");
            return 1;
        }

        Directory.CreateDirectory(outdir);

        AiAnalysisService? analyst = null;
        if (!noAnalysis)
        {
            IConfigurationRoot config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddUserSecrets<Program>(optional: true)
                .AddEnvironmentVariables()
                .Build();

            CyteLlmOptions llmOptions = new();
            config.GetSection("CyteLlm").Bind(llmOptions);

            if (!llmOptions.IsConfigured)
            {
                Console.WriteLine("! CyteLlm:ClientId / CyteLlm:ClientSecret not set - continuing without AI analysis.");
            }
            else
            {
                try
                {
                    CyteLlmClient client = new(llmOptions);
                    IReadOnlyList<LlmModel> models = client.ListModelsAsync().GetAwaiter().GetResult();
                    LlmModel? chosen = ModelResolver.Resolve(models, modelFragment);

                    if (chosen == null)
                    {
                        Console.WriteLine($"Error: no model matches '{modelFragment}'. Available models:");
                        foreach (LlmModel m in models)
                        {
                            Console.WriteLine($"  {m.Id}  {m.FriendlyName}  ({m.ModelName})");
                        }
                        return 1;
                    }

                    Console.WriteLine($"Using model: {chosen.FriendlyName} ({chosen.ModelName})");
                    analyst = new AiAnalysisService(client, chosen.Id);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"! Could not reach the LLM gateway ({ex.Message}) - continuing without AI analysis.");
                }
            }
        }

        try
        {
            var engine = new ReconciliationEngine();
            ReconciliationRunResult result = engine.Run(roots, outdir, analyze: analyst != null, analyst: analyst);

            Console.WriteLine($"\nWorkbook : {Path.GetFullPath(Path.Combine(outdir, result.Workbook))}");
            Console.WriteLine($"Dashboard: {Path.GetFullPath(Path.Combine(outdir, result.Dashboard))}");
            if (!string.IsNullOrEmpty(result.Memo))
                Console.WriteLine($"Memo     : {Path.GetFullPath(Path.Combine(outdir, result.Memo))}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
