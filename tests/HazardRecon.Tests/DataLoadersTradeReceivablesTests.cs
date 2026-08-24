using HazardRecon.Core.Helpers;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

/// <summary>
/// lgd_defaults.csv read as a trade receivables book: keyed on (account, client)
/// rather than on the account alone.
/// </summary>
public class DataLoadersTradeReceivablesTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "hr-dataloaders-tr-tests", Guid.NewGuid().ToString("N")[..8]);

    private readonly DataLoaders _loaders = new();

    public DataLoadersTradeReceivablesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private const string WithClient =
        "AccountNumber,ClientNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
        "A1,T1,Lifetime,2026-05-31,0,5,100.0\n" +
        "A1,T2,Lifetime,2026-05-31,0,5,250.0\n";

    private const string WithoutClient =
        "AccountNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
        "A1,Lifetime,2026-05-31,0,5,100.0\n";

    [Fact]
    public void TestTwoTransactionsOnOneAccountAreTwoDefaults()
    {
        string path = WriteFile("lgd_defaults.csv", WithClient);

        List<DefaultAccountRecord> defaults =
            _loaders.LoadDefaults(path, null, EngineRunType.TradeReceivables);

        Assert.Equal(2, defaults.Count);
        Assert.Equal(new[] { "A1", "A1" }, defaults.Select(d => d.AccountNumber));
        Assert.Equal(new[] { "T1", "T2" }, defaults.Select(d => d.TransactionNumber).Order());
        Assert.Equal(350.0, defaults.Sum(d => d.DefaultAmount));
    }

    [Fact]
    public void TestTheSameFileReadAsLendingCollapsesToOneDefault()
    {
        string path = WriteFile("lgd_defaults.csv", WithClient);

        List<DefaultAccountRecord> defaults = _loaders.LoadDefaults(path);

        // one record per account, taking its largest Bucket-0 amount
        Assert.Single(defaults);
        Assert.Equal(250.0, defaults[0].DefaultAmount);
        Assert.Equal("", defaults[0].TransactionNumber);
    }

    [Fact]
    public void TestTheJoinKeyCarriesBothPartsAndSharesAnAccountPart()
    {
        string path = WriteFile("lgd_defaults.csv", WithClient);

        List<DefaultAccountRecord> defaults =
            _loaders.LoadDefaults(path, null, EngineRunType.TradeReceivables);

        Assert.All(defaults, d => Assert.Equal("A1", AccountUtils.AccountPartOf(d.AccountNormalized)));
        Assert.Equal(2, defaults.Select(d => d.AccountNormalized).Distinct().Count());
    }

    /// <summary>
    /// The refusal that matters most: CsvHelper is configured to return null for a
    /// column the file does not have, so without the guard every key would carry an
    /// empty second part, nothing would match, and the run would report a plausible
    /// 0% trace rate instead of failing.
    /// </summary>
    [Fact]
    public void TestAMissingClientNumberRefusesTheRun()
    {
        string path = WriteFile("lgd_defaults.csv", WithoutClient);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _loaders.LoadDefaults(path, null, EngineRunType.TradeReceivables));

        Assert.Contains("lgd_defaults.csv", ex.Message);
        Assert.Contains("ClientNumber", ex.Message);
        // names what the file does have, so the mismatch is visible
        Assert.Contains("AccountNumber", ex.Message);
    }

    [Fact]
    public void TestALendingRunIgnoresTheClientNumberColumnEntirely()
    {
        string path = WriteFile("lgd_defaults.csv", WithoutClient);

        List<DefaultAccountRecord> defaults = _loaders.LoadDefaults(path);

        Assert.Single(defaults);
        Assert.Equal("A1", defaults[0].AccountNormalized);
    }

    [Fact]
    public void TestBothKeyPartsAreNormalised()
    {
        string path = WriteFile("lgd_defaults.csv",
            "AccountNumber,ClientNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
            "606323.0,77.0,Lifetime,2026-05-31,0,5,100.0\n");

        List<DefaultAccountRecord> defaults =
            _loaders.LoadDefaults(path, null, EngineRunType.TradeReceivables);

        Assert.Equal(AccountUtils.CompositeKey("606323", "77"), defaults[0].AccountNormalized);
    }
}
