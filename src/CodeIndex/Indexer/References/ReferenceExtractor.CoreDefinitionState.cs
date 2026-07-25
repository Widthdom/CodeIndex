namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private sealed class CoreLineDefinitionState(
        string language,
        string context,
        string preparedLine,
        HashSet<string>? definitionNames,
        StringComparer definitionNamesComparer,
        Dictionary<string, HashSet<int>>? scientificDefinitionNameIndices,
        List<SqlReferenceExtractor.DefinitionLeafSpan>? sqlDefinitionLeafSpans)
    {
        private Dictionary<string, int>? definitionNameIndices;

        internal bool ShouldSuppressDefinitionCall(string resolvedName, string rawName, int callIndex)
        {
            if (definitionNames == null)
                return false;

            if (language == "csharp")
            {
                if (context.Contains("when", StringComparison.Ordinal))
                    return false;

                // A verbatim definition such as `void @static()` normalizes to `static`.
                // Looking up only the normalized name can find an earlier modifier token on
                // the same line, so compare the raw declaration token before the shared path.
                // `void @static()` のような verbatim 定義は `static` に正規化される。
                // normalized name だけでは同じ行の先行 modifier を拾うため、共通処理より
                // 先に raw declaration token の位置を比較する。
                if (rawName.Length > 1
                    && rawName[0] == '@'
                    && definitionNames.Contains(resolvedName)
                    && preparedLine.IndexOf(rawName, StringComparison.Ordinal) == callIndex)
                {
                    return true;
                }
            }

            if (scientificDefinitionNameIndices != null
                && scientificDefinitionNameIndices.TryGetValue(
                    resolvedName,
                    out var scientificDefinitionIndices))
            {
                return scientificDefinitionIndices.Contains(callIndex);
            }

            if (language == "julia")
            {
                var targetQualifier =
                    ScientificNativeReferenceExtractor.GetParenthesizedCallTargetQualifier(
                        language,
                        preparedLine,
                        callIndex);
                if (targetQualifier != null)
                {
                    var qualifiedName = $"{targetQualifier}.{resolvedName}";
                    var qualifiedDefinitionIndex =
                        preparedLine.IndexOf(qualifiedName, StringComparison.Ordinal);
                    if (qualifiedDefinitionIndex >= 0
                        && callIndex == qualifiedDefinitionIndex + targetQualifier.Length + 1
                        && definitionNames.Contains(qualifiedName))
                    {
                        return true;
                    }
                }
            }

            if (language != "sql")
                return TryGetDefinitionNameIndex(resolvedName, out var definitionIndex)
                    && callIndex == definitionIndex;

            return SqlReferenceExtractor.ShouldSuppressDefinitionCall(
                sqlDefinitionLeafSpans,
                resolvedName,
                callIndex);
        }

        private bool TryGetDefinitionNameIndex(string resolvedName, out int definitionIndex)
        {
            definitionIndex = -1;
            if (definitionNames == null)
                return false;
            if (definitionNameIndices != null
                && definitionNameIndices.TryGetValue(resolvedName, out definitionIndex))
            {
                return true;
            }
            if (!definitionNames.Contains(resolvedName))
                return false;

            foreach (var definitionName in definitionNames)
            {
                if (!definitionNamesComparer.Equals(definitionName, resolvedName))
                    continue;

                definitionIndex = preparedLine.IndexOf(definitionName, StringComparison.Ordinal);
                if (definitionIndex < 0)
                    return false;

                (definitionNameIndices ??=
                    new Dictionary<string, int>(definitionNamesComparer))[definitionName] =
                    definitionIndex;
                return true;
            }

            return false;
        }
    }
}
