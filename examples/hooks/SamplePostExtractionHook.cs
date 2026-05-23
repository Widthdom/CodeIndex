using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Examples.Hooks;

public sealed class SamplePostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        if (!string.Equals(context.Language, "csharp", StringComparison.Ordinal))
            return;

        foreach (var symbol in symbols.Where(symbol => symbol.Kind == "class"))
            symbol.SubKind ??= "sample_hook_class";
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
    }
}
