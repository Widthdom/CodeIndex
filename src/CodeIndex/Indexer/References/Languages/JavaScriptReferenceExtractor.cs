using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class JavaScriptReferenceExtractor
{
    private const string JavaScriptIdentifierPattern = @"[_\p{L}\$][\w$]*";
    // JavaScript / TypeScript allow zero-argument constructor calls without parentheses:
    // `new Foo;`, `new Date;`, `new Demo.Provider;`, `new Box<number>;`.
    // Keep this language-gated at the call site so Java / C# / other `new` forms still require
    // either `(` or their own dedicated initializer path.
    // JavaScript / TypeScript では引数なしコンストラクタ呼び出しで括弧を省略できる。
    // 他言語の `new` 形と混線しないよう、呼び出し側で JS/TS に限定する。
    private static readonly Regex ParenlessConstructorRegex = new(
        $@"\bnew\s+(?:{ReferenceExtractor.CSharpIdentifierPattern}\s*\.\s*)*(?<name>{ReferenceExtractor.CSharpIdentifierPattern})(?:\s*<[^>\n]+>)?",
        RegexOptions.Compiled);
    private static readonly Regex DiscriminantStringGuardRegex = new(
        $@"(?<![\w$])(?<object>{JavaScriptIdentifierPattern})\s*(?:\.|\?\.)\s*(?<property>{JavaScriptIdentifierPattern})\s*(?:===|==|!==|!=)\s*(?<quote>[""'])(?<literal>(?:\\.|(?!\k<quote>).)*)\k<quote>",
        RegexOptions.Compiled);

    public static void EmitOptionalMemberChainReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var index = 0;
        while (index < preparedLine.Length)
        {
            if (!TryReadIdentifier(preparedLine, index, out var root, out var rootStart, out var rootEnd))
            {
                index++;
                continue;
            }

            index = rootEnd;
            var chain = root;
            var emittedAny = false;
            var probe = rootEnd;
            while (TryReadMemberContinuation(preparedLine, probe, out var member, out _, out var memberEnd))
            {
                chain = $"{chain}.{member}";
                probe = memberEnd;
                emittedAny = true;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    chain,
                    rootStart,
                    "reference",
                    context,
                    lineNumber,
                    resolveContainerForCall(rootStart));
            }

            if (emittedAny)
                index = Math.Max(index, probe);
        }
    }

    public static void EmitDiscriminantStringGuardReferences(
        string detectionLine,
        string sourceLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        foreach (Match match in DiscriminantStringGuardRegex.Matches(detectionLine))
        {
            var objectName = match.Groups["object"].Value;
            var propertyName = match.Groups["property"].Value;
            var propertyIndex = match.Groups["property"].Index;
            var literalGroup = match.Groups["literal"];
            var rawLiteral = literalGroup.Index >= 0 && literalGroup.Index + literalGroup.Length <= sourceLine.Length
                ? sourceLine.Substring(literalGroup.Index, literalGroup.Length)
                : literalGroup.Value;
            var literalValue = UnescapeSimpleStringLiteral(rawLiteral);
            var chain = $"{objectName}.{propertyName}";
            var container = resolveContainerForCall(match.Groups["object"].Index);

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                chain,
                match.Groups["object"].Index,
                "reference",
                context,
                lineNumber,
                container);
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                propertyName,
                propertyIndex,
                "reference",
                context,
                lineNumber,
                container);
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                $"{chain}={literalValue}",
                match.Groups["literal"].Index,
                "type_tag",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitParenlessConstructorReferences(
        string preparedLine,
        IReadOnlyList<string> preparedLines,
        int currentLineIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        foreach (Match match in ParenlessConstructorRegex.Matches(preparedLine))
        {
            var rawName = match.Groups["name"].Value;
            var nameIndex = match.Groups["name"].Index;
            var trailingProbe = match.Index + match.Length;
            while (trailingProbe < preparedLine.Length && char.IsWhiteSpace(preparedLine[trailingProbe]))
                trailingProbe++;

            if (trailingProbe >= preparedLine.Length)
            {
                if (NextNonEmptyPreparedLineStartsWithContinuation(preparedLines, currentLineIndex))
                    continue;
            }
            else
            {
                if (preparedLine[trailingProbe] is '(' or '.' or '[')
                    continue;

                if (preparedLine[trailingProbe] == '?'
                    && trailingProbe + 1 < preparedLine.Length
                    && preparedLine[trailingProbe + 1] == '.')
                {
                    continue;
                }
            }

            var initContainer = resolveContainerForCall(nameIndex);
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                rawName,
                nameIndex,
                "instantiate",
                context,
                lineNumber,
                initContainer);
        }
    }

    private static bool NextNonEmptyPreparedLineStartsWithContinuation(
        IReadOnlyList<string> preparedLines,
        int currentLineIndex)
    {
        for (var next = currentLineIndex + 1; next < preparedLines.Count; next++)
        {
            var trimmed = preparedLines[next].TrimStart();
            if (trimmed.Length == 0)
                continue;

            return trimmed.StartsWith(".", StringComparison.Ordinal)
                || trimmed.StartsWith("?.", StringComparison.Ordinal)
                || trimmed.StartsWith("[", StringComparison.Ordinal)
                || trimmed.StartsWith("(", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool TryReadMemberContinuation(
        string line,
        int start,
        out string member,
        out int memberStart,
        out int memberEnd)
    {
        member = string.Empty;
        memberStart = -1;
        memberEnd = start;

        var probe = SkipWhitespace(line, start);
        if (probe >= line.Length)
            return false;

        if (line[probe] == '.')
        {
            probe++;
        }
        else if (line[probe] == '?' && probe + 1 < line.Length && line[probe + 1] == '.')
        {
            probe += 2;
        }
        else
        {
            return false;
        }

        probe = SkipWhitespace(line, probe);
        if (probe >= line.Length || line[probe] == '(' || line[probe] == '[')
            return false;

        if (!TryReadIdentifier(line, probe, out member, out memberStart, out memberEnd))
            return false;

        return true;
    }

    private static bool TryReadIdentifier(string line, int start, out string identifier, out int identifierStart, out int identifierEnd)
    {
        identifier = string.Empty;
        identifierStart = start;
        identifierEnd = start;

        if (start > 0 && IsIdentifierPart(line[start - 1]))
            return false;
        if (start >= line.Length || !IsIdentifierStart(line[start]))
            return false;

        var end = start + 1;
        while (end < line.Length && IsIdentifierPart(line[end]))
            end++;

        identifier = line[start..end];
        identifierEnd = end;
        return true;
    }

    private static int SkipWhitespace(string line, int start)
    {
        while (start < line.Length && char.IsWhiteSpace(line[start]))
            start++;
        return start;
    }

    private static bool IsIdentifierStart(char c) =>
        c == '_' || c == '$' || char.IsLetter(c);

    private static bool IsIdentifierPart(char c) =>
        IsIdentifierStart(c) || char.IsDigit(c);

    private static string UnescapeSimpleStringLiteral(string value) =>
        value.Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\'", "'", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
}
