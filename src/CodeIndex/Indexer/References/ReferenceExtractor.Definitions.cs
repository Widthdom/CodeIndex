using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static IReadOnlyDictionary<int, HashSet<string>> BuildDefinitionNamesByLine(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
    {
        if (symbols.Count == 0)
            return EmptyDefinitionNamesByLine;

        var limits = GetSafetyLimits();
        var definitionNamesComparer = GetDefinitionNamesComparer(language);
        var namesByLine = new Dictionary<int, HashSet<string>>();
        var lineBudgetReported = false;
        var lineNameBudgetReported = false;
        for (var index = 0; index < symbols.Count; index++)
        {
            if (index >= limits.MaxLookupSymbols)
            {
                ReportReferenceLookupBudgetHit(
                    reportDiagnostic,
                    "reference_definition_lookup_symbol_budget_exceeded",
                    $"Reference definition-name lookup used the first {limits.MaxLookupSymbols:N0} symbols and skipped additional symbols.");
                break;
            }

            var symbol = symbols[index];
            if (!namesByLine.TryGetValue(symbol.Line, out var names))
            {
                if (namesByLine.Count >= limits.MaxLookupLines)
                {
                    if (!lineBudgetReported)
                    {
                        ReportReferenceLookupBudgetHit(
                            reportDiagnostic,
                            "reference_definition_lookup_line_budget_exceeded",
                            $"Reference definition-name lookup used the first {limits.MaxLookupLines:N0} definition lines and skipped additional lines.");
                        lineBudgetReported = true;
                    }

                    continue;
                }

                names = new HashSet<string>(definitionNamesComparer);
                namesByLine[symbol.Line] = names;
            }

            if (names.Count >= limits.MaxNamesPerLine && !names.Contains(symbol.Name))
            {
                if (!lineNameBudgetReported)
                {
                    ReportReferenceLookupBudgetHit(
                        reportDiagnostic,
                        "reference_definition_lookup_line_name_budget_exceeded",
                        $"Reference definition-name lookup retained at most {limits.MaxNamesPerLine:N0} names per line and skipped additional names.");
                    lineNameBudgetReported = true;
                }

                continue;
            }

            names.Add(symbol.Name);
            if (language == "sql")
                SqlReferenceExtractor.AddDefinitionNameAliases(names, symbol);
        }

        return namesByLine;
    }

    private static IReadOnlyDictionary<int, Dictionary<string, HashSet<int>>>?
        BuildScientificDefinitionNameIndicesByLine(
            string language,
            IReadOnlyList<string> lines,
            IReadOnlyList<SymbolRecord> symbols,
            IReadOnlyDictionary<int, HashSet<string>> definitionNamesByLine)
    {
        if (!ScientificNativeReferenceExtractor.Supports(language) || symbols.Count == 0)
            return null;

        var limits = GetSafetyLimits();
        var comparer = GetDefinitionNamesComparer(language);
        var comparison = comparer == StringComparer.OrdinalIgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var indicesByLine = new Dictionary<int, Dictionary<string, HashSet<int>>>();
        for (var symbolIndex = 0;
             symbolIndex < symbols.Count && symbolIndex < limits.MaxLookupSymbols;
             symbolIndex++)
        {
            var symbol = symbols[symbolIndex];
            if (symbol.Line <= 0
                || symbol.Line > lines.Count
                || !definitionNamesByLine.TryGetValue(symbol.Line, out var retainedNames)
                || !retainedNames.Contains(symbol.Name))
            {
                continue;
            }

            var line = lines[symbol.Line - 1];
            var searchStart = Math.Clamp(symbol.StartColumn ?? 0, 0, line.Length);
            var definitionIndex = FindScientificDefinitionNameIndex(
                line,
                symbol.Name,
                searchStart,
                comparison);
            if (definitionIndex < 0)
                continue;

            if (!indicesByLine.TryGetValue(symbol.Line, out var indicesByName))
            {
                indicesByName = new Dictionary<string, HashSet<int>>(comparer);
                indicesByLine[symbol.Line] = indicesByName;
            }

            AddScientificDefinitionNameIndex(
                indicesByName,
                symbol.Name,
                definitionIndex);

            var leafSeparatorIndex = symbol.Name.LastIndexOf('.');
            if (leafSeparatorIndex >= 0 && leafSeparatorIndex + 1 < symbol.Name.Length)
            {
                AddScientificDefinitionNameIndex(
                    indicesByName,
                    symbol.Name[(leafSeparatorIndex + 1)..],
                    definitionIndex + leafSeparatorIndex + 1);
            }
        }

        return indicesByLine;
    }

    private static int FindScientificDefinitionNameIndex(
        string line,
        string name,
        int searchStart,
        StringComparison comparison)
    {
        while (searchStart <= line.Length - name.Length)
        {
            var index = line.IndexOf(name, searchStart, comparison);
            if (index < 0)
                return -1;

            var beforeIsBoundary = index == 0
                || !IsScientificDefinitionIdentifierChar(line[index - 1]);
            var end = index + name.Length;
            var afterIsBoundary = end == line.Length
                || !IsScientificDefinitionIdentifierChar(line[end]);
            if (beforeIsBoundary && afterIsBoundary)
                return index;

            searchStart = index + 1;
        }

        return -1;
    }

    private static bool IsScientificDefinitionIdentifierChar(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '!' or '?' or '$';

    private static void AddScientificDefinitionNameIndex(
        Dictionary<string, HashSet<int>> indicesByName,
        string name,
        int index)
    {
        if (!indicesByName.TryGetValue(name, out var indices))
        {
            indices = [];
            indicesByName[name] = indices;
        }

        indices.Add(index);
    }

    private static IReadOnlySet<string> BuildAllDefinitionNames(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
    {
        if (symbols.Count == 0)
            return EmptyDefinitionNameSet;

        var limits = GetSafetyLimits();
        var names = new HashSet<string>(GetDefinitionNamesComparer(language));
        for (var index = 0; index < symbols.Count; index++)
        {
            if (index >= limits.MaxLookupSymbols)
            {
                ReportReferenceLookupBudgetHit(
                    reportDiagnostic,
                    "reference_all_definition_lookup_symbol_budget_exceeded",
                    $"Reference all-definition lookup used the first {limits.MaxLookupSymbols:N0} symbols and skipped additional symbols.");
                break;
            }

            var symbol = symbols[index];
            names.Add(symbol.Name);
            if (language == "sql")
                SqlReferenceExtractor.AddDefinitionNameAliases(names, symbol);
        }

        return names;
    }

    private static IReadOnlySet<string> BuildFileDefinitionNames(IReadOnlyList<SymbolRecord> symbols)
    {
        if (symbols.Count == 0)
            return EmptyDefinitionNameSet;

        var names = new HashSet<string>(symbols.Count, StringComparer.Ordinal);
        foreach (var symbol in symbols)
            names.Add(symbol.Name);
        return names;
    }

    private static IReadOnlyList<SymbolRecord>? BuildCobolCallableSymbols(IReadOnlyList<SymbolRecord> symbols)
    {
        List<(SymbolRecord Symbol, int OriginalIndex)>? callableSymbols = null;
        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            if (symbol.Kind == "function")
                (callableSymbols ??= []).Add((symbol, index));
        }

        if (callableSymbols is not { Count: > 0 })
            return null;

        callableSymbols.Sort(CompareCobolCallableSymbolEntries);

        var sorted = new List<SymbolRecord>(callableSymbols.Count);
        foreach (var entry in callableSymbols)
            sorted.Add(entry.Symbol);
        return sorted;
    }

    private static int CompareCobolCallableSymbolEntries(
        (SymbolRecord Symbol, int OriginalIndex) left,
        (SymbolRecord Symbol, int OriginalIndex) right)
    {
        var lineComparison = left.Symbol.Line.CompareTo(right.Symbol.Line);
        if (lineComparison != 0)
            return lineComparison;

        var startLineComparison = left.Symbol.StartLine.CompareTo(right.Symbol.StartLine);
        if (startLineComparison != 0)
            return startLineComparison;

        var nameComparison = string.Compare(left.Symbol.Name, right.Symbol.Name, StringComparison.OrdinalIgnoreCase);
        return nameComparison != 0
            ? nameComparison
            : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static IReadOnlyList<SymbolRecord>? BuildRustEnumCandidates(IReadOnlyList<SymbolRecord> symbols)
    {
        List<(SymbolRecord Symbol, int OriginalIndex)>? candidates = null;
        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            if (symbol.Kind == "enum" && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
                (candidates ??= []).Add((symbol, index));
        }

        if (candidates is not { Count: > 0 })
            return null;

        candidates.Sort(CompareRustEnumCandidateEntries);

        var sorted = new List<SymbolRecord>(candidates.Count);
        foreach (var entry in candidates)
            sorted.Add(entry.Symbol);
        return sorted;
    }

    private static int CompareRustEnumCandidateEntries(
        (SymbolRecord Symbol, int OriginalIndex) left,
        (SymbolRecord Symbol, int OriginalIndex) right)
    {
        var spanComparison = GetRustEnumCandidateSpan(left.Symbol).CompareTo(GetRustEnumCandidateSpan(right.Symbol));
        return spanComparison != 0
            ? spanComparison
            : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static int GetRustEnumCandidateSpan(SymbolRecord symbol)
        => (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine);

    private static StringComparer GetDefinitionNamesComparer(string language)
        => language is "sql" or "ada"
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static IReadOnlyList<SymbolRecord> BuildReferenceContainerCandidates(
        IReadOnlyList<SymbolRecord> symbols,
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
        => BuildBoundedContainerCandidates(
            symbols,
            symbol => symbol.BodyStartLine != null && symbol.BodyEndLine != null &&
                      (IsFunctionLikeSymbolKind(symbol.Kind) || symbol.Kind == "hook" || symbol.Kind == "accessor" || symbol.Kind == "class"
                       || symbol.Kind == "struct" || symbol.Kind == "namespace"
                       || symbol.Kind == "object" || symbol.Kind == "property" || symbol.Kind == "heading" || symbol.Kind == "class_hook"),
            "reference_container_candidate_budget_exceeded",
            "Reference container lookup retained the highest-priority bounded candidate set and skipped additional candidates.",
            reportDiagnostic);

    private static IReadOnlyList<SymbolRecord>? BuildCSharpXmlDocAttachmentScopeCandidates(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
        => language == "csharp"
            ? BuildBoundedContainerCandidates(
                symbols,
                symbol => symbol.BodyStartLine != null && symbol.BodyEndLine != null
                          && symbol.Kind is "class" or "struct" or "interface" or "enum" or "namespace",
                "reference_csharp_xml_doc_scope_candidate_budget_exceeded",
                "C# XML documentation scope lookup retained the highest-priority bounded candidate set and skipped additional candidates.",
                reportDiagnostic)
            : null;

    private static IReadOnlyList<SymbolRecord> BuildEnclosingTypeCandidates(
        IReadOnlyList<SymbolRecord> symbols,
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
        => BuildBoundedContainerCandidates(
            symbols,
            symbol => symbol.BodyStartLine != null && symbol.BodyEndLine != null &&
                      (symbol.Kind == "class" || symbol.Kind == "struct" || symbol.Kind == "interface" || symbol.Kind == "enum"),
            "reference_enclosing_type_candidate_budget_exceeded",
            "Reference enclosing-type lookup retained the highest-priority bounded candidate set and skipped additional candidates.",
            reportDiagnostic);

    private static IReadOnlyDictionary<int, SymbolRecord[]>? BuildSwiftPropertyDefinitionsByLine(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
    {
        if (language != "swift")
            return null;

        var limits = GetSafetyLimits();
        Dictionary<int, List<SymbolRecord>>? byLine = null;
        var lineBudgetReported = false;
        var perLineBudgetReported = false;
        for (var index = 0; index < symbols.Count && index < limits.MaxLookupSymbols; index++)
        {
            var symbol = symbols[index];
            if (symbol.Kind != "property")
                continue;

            var lookup = byLine ??= new Dictionary<int, List<SymbolRecord>>();
            if (!lookup.TryGetValue(symbol.Line, out var lineSymbols))
            {
                if (lookup.Count >= limits.MaxLookupLines)
                {
                    if (!lineBudgetReported)
                    {
                        ReportReferenceLookupBudgetHit(
                            reportDiagnostic,
                            "reference_swift_property_line_budget_exceeded",
                            $"Swift property lookup retained at most {limits.MaxLookupLines:N0} definition lines and skipped additional lines.");
                        lineBudgetReported = true;
                    }

                    continue;
                }

                lineSymbols = [];
                lookup[symbol.Line] = lineSymbols;
            }

            if (lineSymbols.Count >= limits.MaxNamesPerLine)
            {
                if (!perLineBudgetReported)
                {
                    ReportReferenceLookupBudgetHit(
                        reportDiagnostic,
                        "reference_swift_property_line_name_budget_exceeded",
                        $"Swift property lookup retained at most {limits.MaxNamesPerLine:N0} properties per line and skipped additional properties.");
                    perLineBudgetReported = true;
                }

                continue;
            }

            lineSymbols.Add(symbol);
        }

        if (symbols.Count > limits.MaxLookupSymbols)
        {
            ReportReferenceLookupBudgetHit(
                reportDiagnostic,
                "reference_swift_property_symbol_budget_exceeded",
                $"Swift property lookup used the first {limits.MaxLookupSymbols:N0} symbols and skipped additional symbols.");
        }

        if (byLine is not { Count: > 0 })
            return null;

        var result = new Dictionary<int, SymbolRecord[]>(byLine.Count);
        foreach (var pair in byLine)
            result.Add(pair.Key, SortSwiftPropertyDefinitionCandidates(pair.Value));

        return result;
    }

    private static SymbolRecord[] SortSwiftPropertyDefinitionCandidates(IReadOnlyList<SymbolRecord> candidates)
    {
        if (candidates.Count == 1)
            return [candidates[0]];

        var entries = new List<(SymbolRecord Symbol, int OriginalIndex)>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
            entries.Add((candidates[index], index));

        entries.Sort(CompareSwiftPropertyDefinitionCandidateEntries);

        var sorted = new SymbolRecord[entries.Count];
        for (var index = 0; index < entries.Count; index++)
            sorted[index] = entries[index].Symbol;
        return sorted;
    }

    private static int CompareSwiftPropertyDefinitionCandidateEntries(
        (SymbolRecord Symbol, int OriginalIndex) left,
        (SymbolRecord Symbol, int OriginalIndex) right)
    {
        var startColumnComparison = (right.Symbol.StartColumn ?? 0).CompareTo(left.Symbol.StartColumn ?? 0);
        return startColumnComparison != 0
            ? startColumnComparison
            : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static IReadOnlyList<SymbolRecord> BuildBoundedContainerCandidates(
        IReadOnlyList<SymbolRecord> symbols,
        Func<SymbolRecord, bool> predicate,
        string diagnosticKind,
        string diagnosticMessage,
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
    {
        var limit = GetSafetyLimits().MaxContainerCandidates;
        List<ReferenceContainerCandidateSortEntry>? candidates = null;
        var truncated = false;
        for (var symbolIndex = 0; symbolIndex < symbols.Count; symbolIndex++)
        {
            var symbol = symbols[symbolIndex];
            if (!predicate(symbol))
                continue;

            if ((candidates?.Count ?? 0) >= limit)
            {
                truncated = true;
                continue;
            }

            (candidates ??= new List<ReferenceContainerCandidateSortEntry>(
                Math.Min(symbols.Count, limit))).Add(new ReferenceContainerCandidateSortEntry(
                symbol,
                GetReferenceContainerCandidateSpanLength(symbol),
                symbolIndex));
        }

        if (truncated)
            ReportReferenceLookupBudgetHit(reportDiagnostic, diagnosticKind, diagnosticMessage);

        if (candidates is not { Count: > 0 })
            return Array.Empty<SymbolRecord>();

        candidates.Sort(CompareReferenceContainerCandidateSortEntries);

        var sorted = new SymbolRecord[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
            sorted[index] = candidates[index].Symbol;

        return sorted;
    }

    private readonly record struct ReferenceContainerCandidateSortEntry(SymbolRecord Symbol, int SpanLength, int OriginalIndex);

    private static int CompareReferenceContainerCandidateSortEntries(
        ReferenceContainerCandidateSortEntry left,
        ReferenceContainerCandidateSortEntry right)
    {
        var compare = left.SpanLength.CompareTo(right.SpanLength);
        return compare != 0
            ? compare
            : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static int GetReferenceContainerCandidateSpanLength(SymbolRecord symbol)
        => (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine);

    private static void ReportReferenceLookupBudgetHit(
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic,
        string kind,
        string message)
        => reportDiagnostic?.Invoke(new ReferenceExtractionDiagnostic(kind, message));

}
