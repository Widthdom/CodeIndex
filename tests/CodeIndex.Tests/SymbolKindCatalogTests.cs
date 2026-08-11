using CodeIndex.Models;

namespace CodeIndex.Tests;

public class SymbolKindCatalogTests
{
    [Fact]
    public void Validation_AllDeclaredKindsUseExactSharedTaxonomy()
    {
        Assert.Equal(
            SymbolKindCatalog.SymbolKinds.Length,
            SymbolKindCatalog.SymbolKinds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            SymbolKindCatalog.ReferenceKinds.Length,
            SymbolKindCatalog.ReferenceKinds.Distinct(StringComparer.Ordinal).Count());

        Assert.All(
            SymbolKindCatalog.SymbolKinds,
            kind => Assert.True(SymbolKindCatalog.IsValidSymbolKind(kind), kind));
        Assert.All(
            SymbolKindCatalog.ReferenceKinds,
            kind => Assert.True(SymbolKindCatalog.IsValidReferenceKind(kind), kind));
    }

    [Fact]
    public void Validation_NullEmptyWhitespaceCaseAndUnknownRemainRejected()
    {
        string?[] invalidSymbolKinds =
        [
            null,
            string.Empty,
            " ",
            "\t\r\n",
            "Class",
            "class ",
            "unknown_symbol_kind",
        ];
        string?[] invalidReferenceKinds =
        [
            null,
            string.Empty,
            " ",
            "\t\r\n",
            "Call",
            "call ",
            "unknown_reference_kind",
        ];

        Assert.All(
            invalidSymbolKinds,
            kind => Assert.False(SymbolKindCatalog.IsValidSymbolKind(kind), kind));
        Assert.All(
            invalidReferenceKinds,
            kind => Assert.False(SymbolKindCatalog.IsValidReferenceKind(kind), kind));
    }
}
