using System.IO.Compression;
using System.Text.RegularExpressions;
using HazardRecon.Core.Models;

namespace HazardRecon.Core.Services;

public class InputDiscoverer
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        {"JAN", 1}, {"FEB", 2}, {"MAR", 3}, {"APR", 4}, {"MAY", 5}, {"JUN", 6},
        {"JUL", 7}, {"AUG", 8}, {"SEP", 9}, {"OCT", 10}, {"NOV", 11}, {"DEC", 12}
    };

    public static string SetKeyFromFolder(string name)
    {
        string up = name.ToUpperInvariant();
        Match yrMatch = Regex.Match(up, @"(20\d\d)");
        string? mon = Months.Keys.FirstOrDefault(m => up.Contains(m));
        Match pcMatch = Regex.Match(up, @"(\d+(?:[.,]\d+)?)\s*(?:PERCENT|PCT|%)");

        List<string> parts = new();
        if (mon != null && yrMatch.Success)
        {
            parts.Add($"{mon}{yrMatch.Groups[1].Value}");
        }
        else if (yrMatch.Success)
        {
            parts.Add(yrMatch.Groups[1].Value);
        }

        if (pcMatch.Success)
        {
            parts.Add(pcMatch.Groups[1].Value.Replace(',', '.') + "PCT");
        }

        string key = parts.Count > 0 ? string.Join(" ", parts) : Regex.Replace(name, @"^\d+\.\s*", "").Trim();
        if (key.Length > 16) key = key[..16];
        return string.IsNullOrWhiteSpace(key) ? "SET" : key;
    }

    public static bool IsSetFolder(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return false;

        if (Directory.GetFiles(dirPath, "*.zip").Length > 0) return true;
        if (File.Exists(Path.Combine(dirPath, "lgd_defaults.csv"))) return true;

        string extractedPath = Path.Combine(dirPath, "_extracted", "lgd_defaults.csv");
        return File.Exists(extractedPath);
    }

    public static string EffectiveRoot(string root)
    {
        root = Path.GetFullPath(root);
        for (int i = 0; i < 6; i++)
        {
            if (!Directory.Exists(root)) return root;

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(root);
            }
            catch (Exception)
            {
                return root;
            }

            List<string> visible = entries
                .Select(Path.GetFileName)
                .Where(e => !string.IsNullOrEmpty(e) && !e.StartsWith('.'))
                .ToList()!;

            if (visible.Count != 1) return root;

            string only = Path.Combine(root, visible[0]);
            if (!Directory.Exists(only) || IsSetFolder(only)) return root;

            root = only;
        }

        return root;
    }

    private static List<string> ExcludeStaleWriteOffCandidates(IEnumerable<string> paths)
    {
        List<string> result = new();
        foreach (string p in paths)
        {
            string baseName = Path.GetFileName(p).ToLowerInvariant();
            if (baseName.Contains("writeoff_not_default")) continue;

            string[] segments = p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Select(s => s.ToLowerInvariant())
                .ToArray();

            if (segments.Contains("output") || segments.Contains("outputs")) continue;

            result.Add(p);
        }
        return result;
    }

    public InventorySet? BuildSet(string dirPath, Action<string, string>? log = null, string? ifrs9Fallback = null)
    {
        string[] zips = Directory.Exists(dirPath)
            ? Directory.GetFiles(dirPath, "*.zip", SearchOption.TopDirectoryOnly).OrderBy(x => x).ToArray()
            : Array.Empty<string>();

        List<string> lgdFiles = Directory.Exists(dirPath)
            ? Directory.GetFiles(dirPath, "lgd_defaults.csv", SearchOption.AllDirectories).ToList()
            : new List<string>();

        if (zips.Length > 0 && lgdFiles.Count == 0)
        {
            string dest = Path.Combine(dirPath, "_extracted");
            Directory.CreateDirectory(dest);
            try
            {
                ZipFile.ExtractToDirectory(zips[0], dest, overwriteFiles: true);
                log?.Invoke($"extracted {Path.GetFileName(zips[0])}", "info");
            }
            catch (Exception ex)
            {
                log?.Invoke($"could not extract {Path.GetFileName(zips[0])}: {ex.Message}", "warn");
            }

            lgdFiles = Directory.GetFiles(dirPath, "lgd_defaults.csv", SearchOption.AllDirectories).ToList();
        }

        if (lgdFiles.Count == 0) return null;

        List<string> woCandidateFiles = Directory.GetFiles(dirPath, "*.csv", SearchOption.AllDirectories)
            .Where(f => {
                string name = Path.GetFileName(f).ToUpperInvariant();
                return name.Contains("WRITEOFF") || name.Contains("WRITE-OFF");
            })
            .ToList();

        woCandidateFiles = ExcludeStaleWriteOffCandidates(woCandidateFiles);

        string? selectedWo = null;
        if (woCandidateFiles.Count > 0)
        {
            selectedWo = woCandidateFiles
                .OrderBy(p => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                .ThenBy(p => p.Length)
                .First();
        }

        string? pdScored = Directory.GetFiles(dirPath, "pd_scored.csv", SearchOption.AllDirectories).FirstOrDefault();
        string? ifrs9 = Directory.GetFiles(dirPath, "*.csv", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).ToUpperInvariant().Contains("IFRS9"))
            .OrderBy(f => f)
            .FirstOrDefault() ?? ifrs9Fallback;

        string? scenario = Directory.GetFiles(dirPath, "scenario*.json", SearchOption.AllDirectories).FirstOrDefault();
        string? debugJson = Directory.GetFiles(dirPath, "debug.json", SearchOption.AllDirectories).FirstOrDefault();

        return new InventorySet
        {
            Folder = dirPath,
            Label = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            LgdDefaults = lgdFiles[0],
            PdScored = pdScored,
            Ifrs9 = ifrs9,
            Scenario = scenario,
            DebugJson = debugJson,
            WriteOff = selectedWo
        };
    }

    public Inventory DiscoverFromFolders(List<string> folders, Action<string, string>? log = null)
    {
        Inventory inv = new()
        {
            Root = string.Join("; ", folders.Select(Path.GetFullPath)),
            WriteOff = null
        };

        foreach (string f in folders)
        {
            string d = EffectiveRoot(f);
            InventorySet? s = BuildSet(d, log);
            if (s == null)
            {
                log?.Invoke($"no engine data found in {f}", "warn");
                continue;
            }

            string key = SetKeyFromFolder(s.Label);
            string baseKey = key;
            int n = 2;
            while (inv.Sets.ContainsKey(key))
            {
                key = $"{baseKey} ({n})";
                n++;
            }

            inv.Sets[key] = s;
            log?.Invoke($"{key}: write-off {(s.WriteOff != null ? "found" : "MISSING")}", "info");
        }

        log?.Invoke($"discovered {inv.Sets.Count} set(s): {(inv.Sets.Count > 0 ? string.Join(", ", inv.Sets.Keys) : "none")}", "ok");
        return inv;
    }

    public Inventory DiscoverInputs(string root, Action<string, string>? log = null)
    {
        root = Path.GetFullPath(root);
        root = EffectiveRoot(root);

        Inventory inv = new() { Root = root };

        List<string> woFiles = Directory.GetFiles(root, "*.csv", SearchOption.AllDirectories)
            .Where(f => {
                string name = Path.GetFileName(f).ToUpperInvariant();
                return name.Contains("WRITEOFF") || name.Contains("WRITE-OFF");
            })
            .ToList();

        if (woFiles.Count > 0)
        {
            List<string> pref = woFiles.Where(p => Path.GetFileName(p).ToUpperInvariant().Contains("WRITE-OFF")).ToList();
            List<string> choices = pref.Count > 0 ? pref : woFiles;
            inv.WriteOff = choices
                .OrderBy(p => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                .ThenBy(p => p.Length)
                .First();
        }

        string? ifrs9Fallback = Directory.GetFiles(root, "*.csv", SearchOption.AllDirectories)
            .Where(f => f.ToUpperInvariant().Contains("IFRS9"))
            .OrderBy(f => f)
            .FirstOrDefault();

        List<string> candidates = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(d => d)
            .ToList();

        if (!candidates.Any(IsSetFolder))
        {
            HashSet<string> found = new();
            foreach (string p in Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories))
            {
                if (Path.GetDirectoryName(p) is string d) found.Add(d);
            }
            foreach (string p in Directory.GetFiles(root, "lgd_defaults.csv", SearchOption.AllDirectories))
            {
                if (Path.GetDirectoryName(p) is string d)
                {
                    if (string.Equals(Path.GetFileName(d), "_extracted", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileName(d), "extracted", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Path.GetDirectoryName(d) is string parent) d = parent;
                    }
                    found.Add(d);
                }
            }

            if (found.Count > 0)
            {
                candidates = found.OrderBy(d => d).ToList();
                log?.Invoke($"debug sets found nested below the folder you chose ({candidates.Count})", "info");
            }
        }

        foreach (string d in candidates)
        {
            string name = Path.GetFileName(d);
            string upName = name.ToUpperInvariant();
            if (upName.Contains("OUTPUT") || upName.Contains("SQL SCRIPT") || upName.Contains("WRITE-OFF"))
                continue;

            InventorySet? s = BuildSet(d, log, ifrs9Fallback);
            if (s == null) continue;

            string key = SetKeyFromFolder(name);
            string baseKey = key;
            int n = 2;
            while (inv.Sets.ContainsKey(key))
            {
                key = $"{baseKey} ({n})";
                n++;
            }

            inv.Sets[key] = s;
        }

        log?.Invoke($"discovered {inv.Sets.Count} debug set(s): {(inv.Sets.Count > 0 ? string.Join(", ", inv.Sets.Keys) : "none")}; write-off={(inv.WriteOff != null ? "found" : "MISSING")}", "ok");
        return inv;
    }
}
