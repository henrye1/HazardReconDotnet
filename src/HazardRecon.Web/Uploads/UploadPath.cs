namespace HazardRecon.Web.Uploads;

/// <summary>
/// Normalises a browser-supplied webkitRelativePath into a safe relative path,
/// or rejects it.
///
/// The value is fully attacker-controlled - a crafted "../../etc/passwd" is the
/// obvious attack - so this runs before the path touches the filesystem.
/// </summary>
public static class UploadPath
{
    // the Windows-invalid set, applied on every platform so a folder that uploads
    // on Linux does not fail on Windows or the reverse. '/' and '\' are absent on
    // purpose: they are the separators, already handled by splitting.
    private static readonly char[] InvalidInSegment = "<>:\"|?*".ToCharArray();

    public static bool TryNormalize(string? relativePath, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        // a NUL truncates the path at the OS layer, so anything after it - including
        // a suffix that made the path look safe - can silently disappear
        if (relativePath.Contains('\0')) return false;

        string candidate = relativePath.Replace('\\', '/');

        // rooted, UNC, or drive-qualified ("C:/x", and also "d:file.csv", which is
        // relative to the drive's current directory rather than to us)
        if (candidate.StartsWith('/')) return false;
        if (candidate.Length >= 2 && candidate[1] == ':') return false;

        List<string> segments = new();
        foreach (string segment in candidate.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") return false;   // never resolved, always refused

            // a real picker only ever sends names that are legal where they came
            // from, but a handcrafted request is not so limited, and these would
            // throw deep inside Directory.CreateDirectory instead of here
            if (segment.AsSpan().IndexOfAny(InvalidInSegment) >= 0) return false;
            if (segment.Any(char.IsControl)) return false;

            segments.Add(segment);
        }

        if (segments.Count == 0) return false;

        normalized = string.Join('/', segments);
        return true;
    }
}
