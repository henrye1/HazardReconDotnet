namespace HazardRecon.Web.Files;

/// <summary>
/// Object storage for run inputs and outputs. The bucket is private: nothing is
/// ever served from it directly, only through short-lived signed URLs.
/// </summary>
public interface IFileStore
{
    Task UploadAsync(string storagePath, Stream content, string contentType, CancellationToken ct = default);

    Task<string> CreateSignedUrlAsync(string storagePath, int expiresInSeconds, CancellationToken ct = default);

    Task DeletePrefixAsync(string prefix, CancellationToken ct = default);

    /// <summary>
    /// Deletes exactly these objects. Used where the caller already knows every
    /// path it wants gone - deleting a run reads them out of run_files - which a
    /// prefix listing cannot match, since it only sees one level and a nested
    /// folder is not itself an object.
    /// </summary>
    Task DeletePathsAsync(IReadOnlyList<string> storagePaths, CancellationToken ct = default);
}
