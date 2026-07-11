using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static List<JavaScriptClassScanTarget> GetJavaScriptTypeScriptExistingClassScanTargets(string lang, string[] lines, List<SymbolRecord> symbols)
    {
        List<(SymbolRecord Symbol, int OriginalIndex)>? classSymbols = null;
        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            if (symbol.Kind is "class" or "interface" && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
                (classSymbols ??= []).Add((symbol, index));
        }

        if (classSymbols is not { Count: > 0 })
            return [];

        if (classSymbols.Count == 1)
        {
            var symbol = classSymbols[0].Symbol;
            return
            [
                CreateJavaScriptClassScanTarget(
                    lines,
                    lang,
                    symbol.StartLine - 1,
                    FindJavaScriptTypeScriptSymbolStartColumn(lines[symbol.StartLine - 1], symbol.Signature),
                    symbol.BodyStartLine,
                    symbol.BodyEndLine,
                    symbol.Kind,
                    symbol.Name),
            ];
        }

        classSymbols.Sort(CompareJavaScriptTypeScriptClassSymbolEntries);

        var targets = new List<JavaScriptClassScanTarget>(classSymbols.Count);
        foreach (var entry in classSymbols)
        {
            var symbol = entry.Symbol;
            targets.Add(CreateJavaScriptClassScanTarget(
                lines,
                lang,
                symbol.StartLine - 1,
                FindJavaScriptTypeScriptSymbolStartColumn(lines[symbol.StartLine - 1], symbol.Signature),
                symbol.BodyStartLine,
                symbol.BodyEndLine,
                symbol.Kind,
                symbol.Name));
        }

        return targets;
    }

    private static void SortJavaScriptTypeScriptClassScanTargets(List<JavaScriptClassScanTarget> targets)
    {
        if (targets.Count < 2)
            return;

        var entries = new List<(JavaScriptClassScanTarget Target, int OriginalIndex)>(targets.Count);
        for (var index = 0; index < targets.Count; index++)
            entries.Add((targets[index], index));

        entries.Sort(CompareJavaScriptTypeScriptClassScanTargetEntries);
        for (var index = 0; index < entries.Count; index++)
            targets[index] = entries[index].Target;
    }

    private static int CompareJavaScriptTypeScriptClassSymbolEntries(
        (SymbolRecord Symbol, int OriginalIndex) left,
        (SymbolRecord Symbol, int OriginalIndex) right)
    {
        var startLineComparison = left.Symbol.StartLine.CompareTo(right.Symbol.StartLine);
        if (startLineComparison != 0)
            return startLineComparison;

        var endLineComparison = right.Symbol.EndLine.CompareTo(left.Symbol.EndLine);
        return endLineComparison != 0
            ? endLineComparison
            : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static int CompareJavaScriptTypeScriptClassScanTargetEntries(
        (JavaScriptClassScanTarget Target, int OriginalIndex) left,
        (JavaScriptClassScanTarget Target, int OriginalIndex) right)
    {
        var startIndexComparison = left.Target.StartIndex.CompareTo(right.Target.StartIndex);
        if (startIndexComparison != 0)
            return startIndexComparison;

        var scanEndComparison = right.Target.ScanEndExclusive.CompareTo(left.Target.ScanEndExclusive);
        return scanEndComparison != 0
            ? scanEndComparison
            : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static List<JavaScriptClassScanTarget> CollectJavaScriptTypeScriptSyntheticClassScanTargets(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        Func<JavaScriptScopePrivacyFlags[][]> getPrivateScopeColumns)
    {
        if (!LinesContain(lines, "class", StringComparison.Ordinal))
            return [];

        var privateScopeColumns = getPrivateScopeColumns();
        List<JavaScriptClassScanTarget>? targets = null;
        HashSet<SymbolLineIdentity>? symbolLineIdentities = null;
        HashSet<(int StartIndex, int StartColumn, int ScanStartIndex, int ScanEndExclusive, int FirstLineScanOffset, string ContainerKind, string ContainerName)>? targetIdentities = null;
        var lexState = new JavaScriptLexState();
        for (int i = 0; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var lineOffset = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (lineOffset >= 0 && lineOffset < sanitizedLine.Length)
            {
                TryAddJavaScriptTypeScriptSyntheticClassTarget(fileId, lang, lines, symbols, ref targets, ref symbolLineIdentities, ref targetIdentities, i, lineOffset, sanitizedLine, privateScopeColumns);
                lineOffset = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, lineOffset + 1);
            }
        }

        if (targets is null)
            return [];

        SortJavaScriptTypeScriptClassScanTargets(targets);
        return targets;
    }
}
