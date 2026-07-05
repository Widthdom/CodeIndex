using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractTypeScriptBareMethods(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        Func<JavaScriptScopePrivacyFlags[][]> getPrivateScopeColumns,
        Func<string[]> getSanitizedLines)
    {
        ExtractJavaScriptTypeScriptBareMethods(fileId, "typescript", lines, symbols, getPrivateScopeColumns, getSanitizedLines);
    }
}
