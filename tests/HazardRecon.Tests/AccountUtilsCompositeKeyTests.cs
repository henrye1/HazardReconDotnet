using HazardRecon.Core.Helpers;
using Xunit;

namespace HazardRecon.Tests;

public class AccountUtilsCompositeKeyTests
{
    [Fact]
    public void TestACompositeKeyRoundTripsBothParts()
    {
        string key = AccountUtils.CompositeKey("A1", "T7");

        Assert.Equal("A1", AccountUtils.AccountPartOf(key));
        Assert.Equal("T7", AccountUtils.TransactionPartOf(key));
    }

    /// <summary>
    /// The property the whole design rests on: a lending key has no second part,
    /// so AccountPartOf is the identity function on it. That is what lets the
    /// write-off side compare against a default without knowing the run type.
    /// </summary>
    [Fact]
    public void TestAccountPartOfIsTheIdentityOnALendingKey()
    {
        Assert.Equal("A1", AccountUtils.CompositeKey("A1", null));
        Assert.Equal("A1", AccountUtils.CompositeKey("A1", ""));
        Assert.Equal("A1", AccountUtils.AccountPartOf("A1"));
        Assert.Equal("", AccountUtils.TransactionPartOf("A1"));
    }

    [Fact]
    public void TestTheSeparatorIsOneNoIdentifierCanContain()
    {
        // '|', '-' and ':' all appear in real account and transaction references,
        // so any of those would split a key in the middle of an identifier
        Assert.DoesNotContain(AccountUtils.KeySeparator, "|-:/., ");
        Assert.True(char.IsControl(AccountUtils.KeySeparator));

        string key = AccountUtils.CompositeKey("A-1|X", "T:2/3");
        Assert.Equal("A-1|X", AccountUtils.AccountPartOf(key));
        Assert.Equal("T:2/3", AccountUtils.TransactionPartOf(key));
    }

    [Fact]
    public void TestTwoTransactionsOnOneAccountAreDifferentKeys()
    {
        Assert.NotEqual(AccountUtils.CompositeKey("A1", "T1"), AccountUtils.CompositeKey("A1", "T2"));

        // ...and share an account part, which is what the write-off match uses
        Assert.Equal(
            AccountUtils.AccountPartOf(AccountUtils.CompositeKey("A1", "T1")),
            AccountUtils.AccountPartOf(AccountUtils.CompositeKey("A1", "T2")));
    }

    /// <summary>
    /// A key built from unnormalised parts would not match one built from
    /// normalised parts, so both halves go through NormaliseAccount - client
    /// numbers arrive from the same float-mangling exports as account numbers.
    /// </summary>
    [Fact]
    public void TestBothPartsAreNormalisedBeforeTheKeyIsBuilt()
    {
        string key = AccountUtils.CompositeKey(
            AccountUtils.NormaliseAccount(" 606323.0 "),
            AccountUtils.NormaliseAccount("77.0"));

        Assert.Equal(AccountUtils.CompositeKey("606323", "77"), key);
    }
}
