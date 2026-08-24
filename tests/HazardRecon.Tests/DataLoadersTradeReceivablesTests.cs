using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

/// <summary>
/// lgd_defaults.csv and the write-off file read as a trade receivables book: both
/// keyed on the customer number rather than the loan account number.
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

    /// <summary>One customer, two defaulted accounts - two genuinely separate debts.</summary>
    private const string TwoAccountsOneCustomer =
        "AccountNumber,ClientNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
        "A1,C1,Lifetime,2026-05-31,0,5,100.0\n" +
        "A2,C1,Lifetime,2026-05-31,0,5,250.0\n";

    private const string WithoutClient =
        "AccountNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
        "A1,Lifetime,2026-05-31,0,5,100.0\n";

    [Fact]
    public void TestTheCustomerNumberIsTheIdentifier()
    {
        string path = WriteFile("lgd_defaults.csv", TwoAccountsOneCustomer);

        List<DefaultAccountRecord> defaults =
            _loaders.LoadDefaults(path, null, EngineRunType.TradeReceivables);

        // one customer, so one default - and it is identified by the customer number
        Assert.Single(defaults);
        Assert.Equal("C1", defaults[0].AccountNumber);
        Assert.Equal("C1", defaults[0].AccountNormalized);
    }

    /// <summary>
    /// The rule that stops the exposure being understated: a customer's separate
    /// defaulted accounts are summed, where lending takes the largest because an
    /// account's several rows are restatements of one debt.
    /// </summary>
    [Fact]
    public void TestACustomersSeveralDefaultsAreSummed()
    {
        string path = WriteFile("lgd_defaults.csv", TwoAccountsOneCustomer);

        List<DefaultAccountRecord> defaults =
            _loaders.LoadDefaults(path, null, EngineRunType.TradeReceivables);

        Assert.Equal(350.0, defaults[0].DefaultAmount);
    }

    [Fact]
    public void TestALendingRunStillTakesTheLargestPerAccount()
    {
        string path = WriteFile("lgd_defaults.csv",
            "AccountNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
            "A1,Lifetime,2026-05-31,0,5,100.0\n" +
            "A1,Lifetime,2026-05-31,0,5,250.0\n");

        List<DefaultAccountRecord> defaults = _loaders.LoadDefaults(path);

        Assert.Single(defaults);
        Assert.Equal(250.0, defaults[0].DefaultAmount);
    }

    [Fact]
    public void TestTwoCustomersAreTwoDefaults()
    {
        string path = WriteFile("lgd_defaults.csv",
            "AccountNumber,ClientNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
            "A1,C1,Lifetime,2026-05-31,0,5,100.0\n" +
            "A2,C2,Lifetime,2026-05-31,0,5,250.0\n");

        List<DefaultAccountRecord> defaults =
            _loaders.LoadDefaults(path, null, EngineRunType.TradeReceivables);

        Assert.Equal(new[] { "C1", "C2" }, defaults.Select(d => d.AccountNormalized).Order());
    }

    /// <summary>
    /// The refusal that matters most: CsvHelper is configured to return null for a
    /// column the file does not have, so without the guard every key would be empty,
    /// nothing would match, and the run would report a plausible 0% trace rate
    /// instead of failing.
    /// </summary>
    [Fact]
    public void TestAMissingClientNumberRefusesTheRun()
    {
        string path = WriteFile("lgd_defaults.csv", WithoutClient);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _loaders.LoadDefaults(path, null, EngineRunType.TradeReceivables));

        Assert.Contains("lgd_defaults.csv", ex.Message);
        Assert.Contains("ClientNumber", ex.Message);
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
    public void TestTheCustomerNumberIsNormalised()
    {
        string path = WriteFile("lgd_defaults.csv",
            "AccountNumber,ClientNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
            "606323.0,77.0,Lifetime,2026-05-31,0,5,100.0\n");

        List<DefaultAccountRecord> defaults =
            _loaders.LoadDefaults(path, null, EngineRunType.TradeReceivables);

        Assert.Equal("77", defaults[0].AccountNormalized);
    }

    // ---- the write-off file ----

    private const string WriteOff =
        "LoanAccountNumber,CustomerId,Amount,ReportDate\n" +
        "A1,C1,100,2026-02-15\n" +
        "A2,C1,200,2026-02-20\n" +
        "A3,C2,300,2026-02-25\n";

    /// <summary>
    /// For a receivables run the write-off joins on the customer, so a customer's
    /// several written-off accounts are one write-off against that customer.
    /// </summary>
    [Fact]
    public void TestTheWriteOffIsKeyedOnTheCustomerForReceivables()
    {
        string path = WriteFile("writeoff.csv", WriteOff);

        var (agg, accts) = _loaders.LoadWriteoff(path, null, null, EngineRunType.TradeReceivables);

        Assert.Equal(new[] { "C1", "C2" }, accts.Order());
        Assert.Equal(300.0, agg.Single(a => a.AccountNormalized == "C1").WriteOffAmount);
    }

    [Fact]
    public void TestTheWriteOffStaysKeyedOnTheAccountForLending()
    {
        string path = WriteFile("writeoff.csv", WriteOff);

        var (_, accts) = _loaders.LoadWriteoff(path);

        Assert.Equal(new[] { "A1", "A2", "A3" }, accts.Order());
    }

    /// <summary>
    /// The customer id used to be carried, never matched, so it was read raw. As the
    /// join key it has to be normalised or the trailing ".0" these exports carry
    /// would stop it matching the defaults file.
    /// </summary>
    [Fact]
    public void TestTheWriteOffCustomerNumberIsNormalised()
    {
        string path = WriteFile("writeoff.csv",
            "LoanAccountNumber,CustomerId,Amount,ReportDate\nA1,77.0,100,2026-02-15\n");

        var (_, accts) = _loaders.LoadWriteoff(path, null, null, EngineRunType.TradeReceivables);

        Assert.Equal(new[] { "77" }, accts);
    }

    [Fact]
    public void TestAWriteOffWithNoCustomerColumnRefusesAReceivablesRun()
    {
        string path = WriteFile("writeoff.csv",
            "LoanAccountNumber,Amount,ReportDate\nA1,100,2026-02-15\n");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _loaders.LoadWriteoff(path, null, null, EngineRunType.TradeReceivables));

        Assert.Contains("customer number", ex.Message);
    }

    [Fact]
    public void TestAWriteOffWithNoCustomerColumnIsOnlyAWarningForLending()
    {
        string path = WriteFile("writeoff.csv",
            "LoanAccountNumber,Amount,ReportDate\nA1,100,2026-02-15\n");
        List<string> warnings = new();

        var (agg, _) = _loaders.LoadWriteoff(
            path, (m, k) => { if (k == LogKind.Warn) warnings.Add(m); });

        Assert.Single(agg);
        Assert.Contains(warnings, w => w.Contains("customer id"));
    }
}
