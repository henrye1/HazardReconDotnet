using System.Text;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using HazardRecon.Web.Uploads;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// The upload writes each set into a numbered folder, so the key its confirmed
/// column mapping is filed under cannot be re-derived from disk. When the two
/// sides disagreed the mapping was silently dropped and every loader fell back
/// to its canonical column names - a file whose account column is "Account"
/// then failed asking for "LoanAccountNumber". These pin the two sides together.
/// </summary>
public class SetKeyAgreementTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hr-setkey-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static SetFileItem Item(int set, SetFileKind kind, string originalName, string content) =>
        new(set, kind, originalName, new MemoryStream(Encoding.UTF8.GetBytes(content)), content.Length);

    /// <summary>A set whose two mappable files use none of the canonical column names.</summary>
    private static IReadOnlyList<SetFileItem> UploadedSet(int index, string exposureName) => new[]
    {
        Item(index, SetFileKind.Exposure, exposureName, "Account,Balance\nA1,100\n"),
        Item(index, SetFileKind.Writeoff, "writeoff.csv",
            "Report_Date,Customer,Account,Write_off_amount\n2026-04-30,C1,A1,50\n"),
        Item(index, SetFileKind.Debug, "lgd_defaults.csv",
            "AccountNumber,EventType,CohortDate,Bucket,Rating,Amount\nA1,Lifetime,2026-05-31,0,5,100.0\n"),
    };

    /// <summary>What the upload endpoint decides, and both later steps go by.</summary>
    private static Dictionary<string, SetIdentity> Identities(SetReceiveOutcome received)
    {
        List<string> keys = InputDiscoverer.SetKeysForLabels(received.Sets.Select(s => s.Label));
        return received.Sets
            .Select((s, i) => (s.Root, Identity: new SetIdentity(keys[i], s.Label)))
            .ToDictionary(x => x.Root, x => x.Identity);
    }

    [Fact]
    public async Task TestTheEngineFindsTheMapUnderTheKeyDiscoveryFiledItAgainst()
    {
        SetReceiveOutcome received = await new SetFileReceiver().ReceiveAsync(
            _root, UploadedSet(0, "IFRS9 FILE JUNE 2026.csv"));
        Assert.True(received.Ok, received.Error);

        Dictionary<string, SetIdentity> identities = Identities(received);
        string mappingKey = identities[received.Sets[0].Root].Key;

        Inventory inv = new InputDiscoverer().DiscoverFromFolders(
            received.Sets.Select(s => s.Root).ToList(), identities: identities);

        Assert.Equal(mappingKey, Assert.Single(inv.Sets.Keys));
    }

    [Fact]
    public async Task TestTheSetKeepsTheLabelTheUploadGaveItRatherThanItsFolderNumber()
    {
        SetReceiveOutcome received = await new SetFileReceiver().ReceiveAsync(
            _root, UploadedSet(0, "IFRS9 FILE JUNE 2026.csv"));

        Inventory inv = new InputDiscoverer().DiscoverFromFolders(
            received.Sets.Select(s => s.Root).ToList(), identities: Identities(received));

        Assert.Equal("IFRS9 FILE JUNE 2026", inv.Sets.Values.Single().Label);
    }

    [Fact]
    public async Task TestTwoSetsWhoseLabelsCollideEachKeepTheirOwnKey()
    {
        // both labels reduce to the same key, so the side that files the mapping
        // and the side that reads it must disambiguate identically
        SetReceiveOutcome received = await new SetFileReceiver().ReceiveAsync(_root,
            UploadedSet(0, "IFRS9 JUNE 2026.csv").Concat(UploadedSet(1, "IFRS9_JUNE_2026.csv")).ToList());
        Assert.True(received.Ok, received.Error);

        Dictionary<string, SetIdentity> identities = Identities(received);

        Inventory inv = new InputDiscoverer().DiscoverFromFolders(
            received.Sets.Select(s => s.Root).ToList(), identities: identities);

        Assert.Equal(2, inv.Sets.Count);
        Assert.Equal(
            identities.Values.Select(i => i.Key).OrderBy(k => k),
            inv.Sets.Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task TestACliFolderStillGetsTheKeyItsOwnNameGives()
    {
        // no identities supplied: the CLI path must be untouched
        string folder = Path.Combine(_root, "3. DEBUG FILE 30 JUNE 2026 0.5 PERCENT");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "lgd_defaults.csv"),
            "AccountNumber,EventType,CohortDate,Bucket,Rating,Amount\nA1,Lifetime,2026-05-31,0,5,100.0\n");

        Inventory inv = new InputDiscoverer().DiscoverFromFolders(new List<string> { folder });

        Assert.Equal("JUN2026 0.5PCT", Assert.Single(inv.Sets.Keys));
    }
}
