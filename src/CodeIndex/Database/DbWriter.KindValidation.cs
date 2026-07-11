using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private static void ValidateSymbolKinds(SymbolRecord symbol)
    {
        if (!SymbolKindCatalog.IsValidSymbolKind(symbol.Kind))
            throw new ArgumentException($"Unknown symbol kind '{symbol.Kind}'. Register the kind in {nameof(SymbolKindCatalog)} before writing it.", nameof(symbol));

        if (symbol.ContainerKind != null && !SymbolKindCatalog.IsValidSymbolKind(symbol.ContainerKind))
            throw new ArgumentException($"Unknown symbol container kind '{symbol.ContainerKind}'. Register the kind in {nameof(SymbolKindCatalog)} before writing it.", nameof(symbol));
    }

    private static void ValidateReferenceKinds(ReferenceRecord reference)
    {
        if (!SymbolKindCatalog.IsValidReferenceKind(reference.ReferenceKind))
            throw new ArgumentException($"Unknown reference kind '{reference.ReferenceKind}'. Register the kind in {nameof(SymbolKindCatalog)} before writing it.", nameof(reference));

        if (reference.ContainerKind != null && !SymbolKindCatalog.IsValidSymbolKind(reference.ContainerKind))
            throw new ArgumentException($"Unknown reference container kind '{reference.ContainerKind}'. Register the kind in {nameof(SymbolKindCatalog)} before writing it.", nameof(reference));
    }
}
