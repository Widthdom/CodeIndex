using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptBareMethods(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        Func<JavaScriptScopePrivacyFlags[][]> getPrivateScopeColumns,
        Func<string[]> getSanitizedLines)
    {
        ExtractJavaScriptTypeScriptBareMethods(fileId, "javascript", lines, symbols, getPrivateScopeColumns, getSanitizedLines);
    }
}
