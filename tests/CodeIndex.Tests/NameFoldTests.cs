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
        (string Input, string Expected)[] names = [
            ("解析_𐐀_İ_Σ_Ꭰ_Ａ_カタカナ_é", "解析_𐐨_i\u0307_σ_Ꭰ_a_カタカナ_é"),
            ("\uAB70\u13F8", "\u13A0\u13F0"),
            ("\u1F88\u1FB7", "\u1F00ι\u03B1\u0342ι"),
            ("\uFB03\u212A\u1E9E", "ffikss"),
            ("e\u0301😀", "é😀"),
            ("\U00010400\U00010427\U0001E900", "\U00010428\U0001044F\U0001E922"),
        ];
        foreach (var (input, expected) in names)
            Assert.Equal(expected, NameFold.Fold(input));

        Assert.Throws<ArgumentException>(() => NameFold.Fold("broken\ud800"));
    }

    [Fact]
    public void Fold_ReusesAlreadyFoldedAsciiNames()
    {
        var name = "already_folded.name_123";

        Assert.Same(name, NameFold.Fold(name));
        Assert.Same(string.Empty, NameFold.Fold(string.Empty));
        Assert.Null(NameFold.Fold(null));
    }

    [Fact]
    public void Fold_FoldsAsciiUppercaseWithoutUnicodeNormalization()
    {
        Assert.Equal("my.symbol_123", NameFold.Fold("My.Symbol_123"));
        var allAscii = new string(Enumerable.Range(0, 128).Select(value => (char)value).ToArray());
        Assert.Equal(allAscii.ToLowerInvariant(), NameFold.Fold(allAscii));
        foreach (var length in new[] { 0, 1, 15, 16, 31, 32, 63, 64, 128 })
        {
            var prefix = new string('x', length);
            Assert.Same(prefix, NameFold.Fold(prefix));
            Assert.Equal(prefix + "az", NameFold.Fold(prefix + "AZ"));
            // An early ASCII capital cannot hide Unicode beyond a vector boundary.
            Assert.Equal("a" + prefix + "ai\u0307", NameFold.Fold("A" + prefix + "Ａİ"));
        }
    }

    [Fact]
    public void Fold_UnicodeNamesDoNotAllocatePerScalarStrings()
    {
        var name = string.Concat(Enumerable.Repeat("解析𐐀", 64));
        var expected = string.Concat(Enumerable.Repeat("解析𐐨", 64));
        Assert.Equal(expected, NameFold.Fold(name));

        string? actual = null;
        const int iterations = 32;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
            actual = NameFold.Fold(name);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, actual);
        // Covers normalization, builder storage, and the final string with headroom;
        // the old per-rune ToString/lowercase path exceeds this by more than 2x.
        Assert.InRange(allocated, 0, iterations * name.Length * 12L + 4096);
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
