using CodeIndex.Database;

namespace CodeIndex.Tests;

public class NameFoldTests
{
    [Fact]
    public void Fold_UsesUnicodeCaseFoldSemantics()
    {
        Assert.Equal(NameFold.Fold("Straße"), NameFold.Fold("STRASSE"));
        Assert.Equal(NameFold.Fold("Σ"), NameFold.Fold("ς"));
        Assert.Equal(NameFold.Fold("Σ"), NameFold.Fold("σ"));
    }

    [Fact]
    public void Fold_ReusesAlreadyFoldedAsciiNames()
    {
        var name = "already_folded.name_123";

        Assert.Same(name, NameFold.Fold(name));
    }

    [Fact]
    public void Fold_FoldsAsciiUppercaseWithoutUnicodeNormalization()
    {
        Assert.Equal("my.symbol_123", NameFold.Fold("My.Symbol_123"));
    }

    [Fact]
    public void Fold_RemainsLocaleInvariantForTurkishDottedI()
    {
        Assert.Equal("i\u0307", NameFold.Fold("İ"));
        Assert.Equal("i", NameFold.Fold("i"));
        Assert.NotEqual(NameFold.Fold("İ"), NameFold.Fold("i"));
    }

    [Fact]
    public void Fingerprint_ReturnsLowercaseHex()
    {
        var fingerprint = NameFold.Fingerprint();

        Assert.Equal(fingerprint.ToLowerInvariant(), fingerprint);
        Assert.DoesNotContain(fingerprint, c => c is >= 'A' and <= 'F');
    }

    [Fact]
    public void PersistedKeyContract_VersionsNimStyleInsensitiveIdentity_Issue4738()
    {
        Assert.Equal(3, NameFold.Version);
        Assert.Equal("myproc", DbReader.FoldNameForLanguage("my_proc", "nim"));
    }
}
