using HazardRecon.Core.Models;
using HazardRecon.Core.Services;

namespace HazardRecon.Cli;

class Program
{
    static int Main(string[] args)
    {
        var roots = new List<string>();
        string outdir = "output";
        bool noAnalysis = false;

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
            Console.WriteLine("Usage: hazard-recon --root <folder> [--root <folder2> ...] [--outdir <output>] [--no-analysis]");
            Console.WriteLine("       Tip: --root may be repeated up to 4 times for multi-period runs.");
            Console.WriteLine("Error: at least one --root argument is required.");
            return 1;
        }

        Directory.CreateDirectory(outdir);

        try
        {
            var engine = new ReconciliationEngine();
            ReconciliationRunResult result = engine.Run(roots, outdir, analyze: !noAnalysis);

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
