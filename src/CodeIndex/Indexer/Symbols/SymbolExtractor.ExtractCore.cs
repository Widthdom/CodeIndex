using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static List<SymbolRecord> ExtractCore(
        long fileId,
        string? lang,
        string content,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        int? conflictMarkerLine,
        string? filePath = null,
        string? projectRoot = null,
        bool patternConfigsAlreadyLoaded = false,
        CancellationToken cancellationToken = default,
        int? maxSymbols = null,
        bool applyRequiredLiteralFileGate = true,
        bool applyRequiredLiteralMatchInputGate = true,
        RequiredLiteralGateCounts? requiredLiteralGateCounts = null,
        bool applyCSharpRegexProbeOptimizations = true,
        CSharpRegexProbeCounts? csharpRegexProbeCounts = null)
    {
        var originalLang = lang;
        if (TryPrepareSymbolExtraction(
            fileId,
            originalLang,
            content,
            contentIsNormalized,
            hasOversizeLine,
            conflictMarkerLine,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded,
            cancellationToken,
            out lang,
            out content,
            out var preparedSymbols))
        {
            return preparedSymbols!;
        }

        if (TryExtractSpecializedSymbols(
            fileId,
            lang,
            content,
            filePath,
            projectRoot,
            cancellationToken,
            out var specializedSymbols))
        {
            return specializedSymbols;
        }

        var preparation = PreparePatternExtraction(
            fileId,
            originalLang,
            lang,
            content,
            filePath,
            projectRoot,
            cancellationToken,
            maxSymbols,
            applyRequiredLiteralFileGate,
            applyRequiredLiteralMatchInputGate,
            requiredLiteralGateCounts,
            applyCSharpRegexProbeOptimizations,
            csharpRegexProbeCounts);
        if (preparation.ImmediateSymbols != null)
            return preparation.ImmediateSymbols;

        var context = preparation.Context!;
        ScanPatternLines(context);
        CompletePatternExtraction(context);
        return context.Symbols;
    }

    private static IReadOnlyList<SymbolPattern> SelectApplicablePatterns(
        IReadOnlyList<SymbolPattern> patterns,
        string content,
        bool applyRequiredLiteralGate)
    {
        if (!applyRequiredLiteralGate)
            return patterns;

        // A content-wide Ordinal check can only remove a pattern when a match is impossible.
        // Preserve the original order, return the original list when nothing is skipped, and pass
        // this same applicable set to every supplemental recovery scan.
        // content 全体の Ordinal 判定で match 不可能な pattern だけを除外する。元の順序を保ち、
        // skip がなければ元 list を返し、補助 recovery scan にも同じ applicable set を渡す。
        List<SymbolPattern>? applicablePatterns = null;
        for (var patternIndex = 0; patternIndex < patterns.Count; patternIndex++)
        {
            var pattern = patterns[patternIndex];
            if ((pattern.RequiredLiteral is not null || pattern.RequiredAnyLiterals is not null)
                && !ContainsRequiredGateLiteral(pattern, content.AsSpan()))
            {
                if (applicablePatterns == null)
                {
                    applicablePatterns = new List<SymbolPattern>(patterns.Count - 1);
                    for (var prefixIndex = 0; prefixIndex < patternIndex; prefixIndex++)
                        applicablePatterns.Add(patterns[prefixIndex]);
                }

                continue;
            }

            applicablePatterns?.Add(pattern);
        }

        return applicablePatterns ?? patterns;
    }

    private static bool ShouldAttemptPatternRegex(
        SymbolPattern pattern,
        ReadOnlySpan<char> matchInput,
        bool applyRequiredLiteralMatchInputGate,
        RequiredLiteralGateCounts? requiredLiteralGateCounts,
        bool applyCSharpRegexProbeOptimizations = true,
        CSharpRegexProbeCounts? csharpRegexProbeCounts = null)
    {
        // This second-stage proof must inspect the exact transformed input for one regex call.
        // Callers treat false as a failed match and must still run language-specific recovery.
        // 第2段の proof は1回の regex call に渡す変換済み input そのものを調べる。
        // false は match failure と同様に扱い、言語固有 recovery は引き続き実行する。
        if (applyRequiredLiteralMatchInputGate
            && (pattern.RequiredLiteral is not null || pattern.RequiredAnyLiterals is not null)
            && !ContainsRequiredGateLiteral(pattern, matchInput))
        {
            if (requiredLiteralGateCounts != null)
                requiredLiteralGateCounts.MatchInputLiteralSkipCount++;
            return false;
        }

        if (ReferenceEquals(pattern.Regex, CSharpPlainFieldRegex))
        {
            // Every successful plain-field path consumes `=` or `;`. Inspect the exact
            // transformed input rather than its final character because a same-line class
            // segment can contain the field terminator before a later sibling or `}`.
            // plain-field の全成功経路は `=` または `;` を必ず消費する。same-line class
            // segment では field 終端の後ろに sibling / `}` が続き得るため、末尾文字ではなく
            // regex に渡す変換済み input 全体を検査する。
            if (applyCSharpRegexProbeOptimizations
                && matchInput.IndexOfAny('=', ';') < 0)
            {
                if (csharpRegexProbeCounts != null)
                    csharpRegexProbeCounts.PlainFieldTerminatorSkipCount++;
                return false;
            }

            if (csharpRegexProbeCounts != null)
                csharpRegexProbeCounts.PlainFieldRegexAttemptCount++;
        }

        if (requiredLiteralGateCounts != null)
            requiredLiteralGateCounts.RegexAttemptCount++;
        return true;
    }

    private static bool ContainsRequiredGateLiteral(
        SymbolPattern pattern,
        ReadOnlySpan<char> input)
    {
        if (pattern.RequiredLiteral is { } requiredLiteral)
        {
            return input.IndexOf(requiredLiteral.AsSpan(), StringComparison.Ordinal) >= 0;
        }

        if (pattern.RequiredAnyLiterals is not { } requiredAnyLiterals)
            return true;

        for (var literalIndex = 0; literalIndex < requiredAnyLiterals.Count; literalIndex++)
        {
            if (input.IndexOf(
                    requiredAnyLiterals[literalIndex].AsSpan(),
                    StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Regex PrologOpenClauseRegex = new(
        @"^\s*(?:(?:[a-z][A-Za-z0-9_]*\s*(?:\([^\r\n]*\))?\s*(?::-|-->))|:-)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologMultilineHeadStartRegex = new(
        @"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*(?<open>\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologBareMultilineHeadStartRegex = new(
        @"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int PrologMultilineHeadLookaheadLineLimit = 256;
    private readonly record struct PrologMultilineHead(string Name, int StartColumn);
    private readonly record struct PrologSourcePosition(int LineIndex, int Column);

    private static bool TryGetNextPrologClauseOffset(
        string line,
        int currentClauseOffset,
        out int nextClauseOffset)
    {
        for (var column = Math.Max(0, currentClauseOffset); column < line.Length; column++)
        {
            if (!DynamicDeclarativeReferenceExtractor.IsPrologClauseTerminator(line, column))
                continue;

            for (var candidate = column + 1; candidate < line.Length; candidate++)
            {
                if (char.IsWhiteSpace(line[candidate]))
                    continue;

                if (char.IsLower(line[candidate]))
                {
                    nextClauseOffset = candidate;
                    return true;
                }
                break;
            }
        }

        nextClauseOffset = -1;
        return false;
    }

    private static bool[] BuildPrologClauseContinuationLines(
        IReadOnlyList<string> structuralLines,
        Dictionary<int, PrologMultilineHead> multilineHeads)
    {
        var continuationLines = new bool[structuralLines.Count];
        var matchingParentheses = BuildPrologMatchingParentheses(structuralLines);
        var clauseOpen = false;
        for (var lineIndex = 0; lineIndex < structuralLines.Count; lineIndex++)
        {
            continuationLines[lineIndex] = clauseOpen;
            var line = structuralLines[lineIndex];
            var lastTerminatorColumn = FindLastTopLevelPrologClauseTerminator(line);

            if (clauseOpen && lastTerminatorColumn < 0)
                continue;

            clauseOpen = false;
            var clauseCandidateOffset = lastTerminatorColumn + 1;
            var clauseCandidate = line[clauseCandidateOffset..];
            if (PrologOpenClauseRegex.IsMatch(clauseCandidate))
            {
                clauseOpen = true;
                continue;
            }

            var multilineHead = PrologMultilineHeadStartRegex.Match(clauseCandidate);
            if (multilineHead.Success
                && IsValidatedMultilinePrologHead(
                     structuralLines,
                     lineIndex,
                     clauseCandidateOffset + multilineHead.Groups["open"].Index,
                     matchingParentheses))
            {
                multilineHeads[lineIndex] = new PrologMultilineHead(
                    multilineHead.Groups["name"].Value,
                    clauseCandidateOffset + multilineHead.Groups["name"].Index);
                clauseOpen = true;
                continue;
            }

            var bareMultilineHead = PrologBareMultilineHeadStartRegex.Match(clauseCandidate);
            if (bareMultilineHead.Success
                && IsValidatedBareMultilinePrologHead(structuralLines, lineIndex))
            {
                multilineHeads[lineIndex] = new PrologMultilineHead(
                    bareMultilineHead.Groups["name"].Value,
                    clauseCandidateOffset + bareMultilineHead.Groups["name"].Index);
                clauseOpen = true;
            }
        }

        return continuationLines;
    }

    private static IReadOnlyDictionary<PrologSourcePosition, PrologSourcePosition>
        BuildPrologMatchingParentheses(IReadOnlyList<string> structuralLines)
    {
        var openParentheses = new Stack<PrologSourcePosition>();
        var matchingParentheses = new Dictionary<PrologSourcePosition, PrologSourcePosition>();
        for (var lineIndex = 0; lineIndex < structuralLines.Count; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                var ch = line[column];
                if (ch is '\'' or '"')
                {
                    column = SkipPrologQuotedTerm(line, column, ch) - 1;
                    continue;
                }

                if (ch == '(')
                {
                    openParentheses.Push(new PrologSourcePosition(lineIndex, column));
                }
                else if (ch == ')' && openParentheses.TryPop(out var openingParenthesis))
                {
                    matchingParentheses[openingParenthesis] = new PrologSourcePosition(lineIndex, column);
                }
            }
        }

        return matchingParentheses;
    }

    private static int FindLastTopLevelPrologClauseTerminator(string line)
        => FindTopLevelPrologClauseTerminator(line, findLast: true);

    private static int FindFirstTopLevelPrologClauseTerminator(string line)
        => FindTopLevelPrologClauseTerminator(line, findLast: false);

    private static int FindTopLevelPrologClauseTerminator(string line, bool findLast)
    {
        var terminatorColumn = -1;
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var column = 0; column < line.Length; column++)
        {
            var ch = line[column];
            if (ch is '\'' or '"')
            {
                column = SkipPrologQuotedTerm(line, column, ch) - 1;
                continue;
            }

            switch (ch)
            {
                case '(':
                    parenthesisDepth++;
                    continue;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    continue;
            }

            if (ch != '.'
                || parenthesisDepth != 0
                || bracketDepth != 0
                || braceDepth != 0)
            {
                continue;
            }

            var previous = column > 0 ? line[column - 1] : '\0';
            var next = column + 1 < line.Length ? line[column + 1] : '\0';
            if (previous != '.'
                && next != '.'
                && !(char.IsDigit(previous) && char.IsDigit(next))
                && (next == '\0' || char.IsWhiteSpace(next)))
            {
                terminatorColumn = column;
                if (!findLast)
                    break;
            }
        }

        return terminatorColumn;
    }

    private static bool IsValidatedBareMultilinePrologHead(
        IReadOnlyList<string> structuralLines,
        int startLineIndex)
    {
        var endLineExclusive = Math.Min(
            structuralLines.Count,
            startLineIndex + PrologMultilineHeadLookaheadLineLimit);
        for (var lineIndex = startLineIndex + 1; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                if (char.IsWhiteSpace(line[column]))
                    continue;
                return line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal)
                    || line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal)
                    || DynamicDeclarativeReferenceExtractor.IsPrologClauseTerminator(line, column);
            }
        }

        return false;
    }

    private static bool IsValidatedMultilinePrologHead(
        IReadOnlyList<string> structuralLines,
        int startLineIndex,
        int openingParenthesisColumn,
        IReadOnlyDictionary<PrologSourcePosition, PrologSourcePosition> matchingParentheses)
    {
        var endLineExclusive = Math.Min(
            structuralLines.Count,
            startLineIndex + PrologMultilineHeadLookaheadLineLimit);
        if (!matchingParentheses.TryGetValue(
                new PrologSourcePosition(startLineIndex, openingParenthesisColumn),
                out var closingParenthesis)
            || closingParenthesis.LineIndex >= endLineExclusive)
        {
            return false;
        }

        for (var lineIndex = closingParenthesis.LineIndex; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var startColumn = lineIndex == closingParenthesis.LineIndex
                ? closingParenthesis.Column + 1
                : 0;
            for (var column = startColumn; column < line.Length; column++)
            {
                var ch = line[column];
                if (ch is '\'' or '"')
                {
                    column = SkipPrologQuotedTerm(line, column, ch) - 1;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                    continue;
                if (line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal)
                    || line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal)
                    || DynamicDeclarativeReferenceExtractor.IsPrologClauseTerminator(line, column))
                {
                    return true;
                }

                return false;
            }
        }

        return false;
    }

    private static void AddPrologMultilineHeadSymbols(
        long fileId,
        IReadOnlyList<string> lines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState,
        IReadOnlyDictionary<int, PrologMultilineHead> multilineHeads)
    {
        foreach (var (lineIndex, multilineHead) in multilineHeads)
        {
            var lineNumber = lineIndex + 1;
            var line = lines[lineIndex];
            AddSymbolRecord(
                symbols,
                extractionState,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = multilineHead.Name,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = multilineHead.StartColumn,
                    EndLine = lineNumber,
                    Signature = line[multilineHead.StartColumn..].Trim(),
                },
                line);
        }
    }

    private static int SkipPrologQuotedTerm(string line, int startColumn, char delimiter)
    {
        for (var column = startColumn + 1; column < line.Length; column++)
        {
            if (line[column] == '\\')
            {
                column++;
                continue;
            }

            if (line[column] != delimiter)
                continue;

            if (column + 1 < line.Length && line[column + 1] == delimiter)
            {
                column++;
                continue;
            }

            return column + 1;
        }

        return line.Length;
    }

    private const int CSharpFieldInitializerSignatureLimit = 1024;

    private static string BoundCSharpFieldInitializerSignature(string signature)
    {
        if (signature.Length <= CSharpFieldInitializerSignatureLimit
            || !signature.Contains('='))
        {
            return signature;
        }

        // Field signatures are metadata, not bodies. Large object/collection initializers can
        // otherwise consume an entire CLI, JSON, MCP, or LSP response budget after multiline
        // signatures are collapsed to one line. Replace each top-level initializer with a
        // deterministic marker so modifiers, type, and every declarator remain readable without
        // persisting an arbitrary initializer prefix. Keep the legacy hard limit as a final guard
        // for pathologically large declarator lists. #4445, #4865
        // field signature は body ではなくメタデータである。複数行を1行へ畳み込んだ巨大な
        // object/collection initializer が CLI / JSON / MCP / LSP の応答予算を使い切らないよう、
        // top-level initializer を決定的 marker に置換し、任意の initializer prefix を保存せずに
        // modifier / type / 全 declarator を読める形で維持する。異常に長い declarator list には
        // 従来の hard limit を最終ガードとして残す。#4445, #4865
        var sanitized = LexCSharpLine(signature, new CSharpLexState()).SanitizedLine;
        var summarized = new StringBuilder(Math.Min(signature.Length, CSharpFieldInitializerSignatureLimit));
        var copyStart = 0;
        var searchStart = 0;

        while (TryFindCSharpTopLevelInitializerAssignment(sanitized, searchStart, out var assignmentColumn))
        {
            summarized.Append(signature.AsSpan(copyStart, assignmentColumn - copyStart).TrimEnd());
            summarized.Append(" = …");

            var delimiterColumn = FindCSharpTopLevelInitializerDelimiter(sanitized, assignmentColumn + 1);
            if (delimiterColumn >= signature.Length)
            {
                summarized.Append(';');
                copyStart = signature.Length;
                break;
            }

            summarized.Append(signature[delimiterColumn]);
            copyStart = delimiterColumn + 1;
            searchStart = copyStart;
        }

        if (copyStart < signature.Length)
            summarized.Append(signature.AsSpan(copyStart));

        var result = summarized.ToString().Trim();
        return result.Length <= CSharpFieldInitializerSignatureLimit
            ? result
            : string.Concat(result.AsSpan(0, CSharpFieldInitializerSignatureLimit - 2), "…;");
    }

    private static bool TryFindCSharpTopLevelInitializerAssignment(
        string sanitized,
        int startColumn,
        out int assignmentColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var column = startColumn; column < sanitized.Length; column++)
        {
            var ch = sanitized[column];
            switch (ch)
            {
                case '(':
                    parenDepth++;
                    continue;
                case ')' when parenDepth > 0:
                    parenDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    continue;
                case '<' when TryMatchCSharpGenericBracket(sanitized, column, out var genericEnd):
                    column = genericEnd;
                    continue;
            }

            if (ch != '=' || parenDepth != 0 || bracketDepth != 0 || braceDepth != 0)
                continue;

            var previous = column > 0 ? sanitized[column - 1] : '\0';
            var next = column + 1 < sanitized.Length ? sanitized[column + 1] : '\0';
            if (next is '=' or '>'
                || previous is '=' or '!' or '<' or '>' or '+' or '-' or '*' or '/' or '%'
                    or '&' or '|' or '^' or '?')
            {
                continue;
            }

            assignmentColumn = column;
            return true;
        }

        assignmentColumn = -1;
        return false;
    }

    private static int FindCSharpTopLevelInitializerDelimiter(string sanitized, int startColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var column = startColumn; column < sanitized.Length; column++)
        {
            var ch = sanitized[column];
            switch (ch)
            {
                case '(':
                    parenDepth++;
                    continue;
                case ')' when parenDepth > 0:
                    parenDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    continue;
                case '<' when TryMatchCSharpGenericBracket(sanitized, column, out var genericEnd):
                    column = genericEnd;
                    continue;
            }

            if (parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0
                && ch is ',' or ';')
            {
                return column;
            }
        }

        return sanitized.Length;
    }

    private static void AddScriptScopeSymbol(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        if (lines.Length == 0)
            return;

        // Add this after AssignContainers so the synthetic file-wide scope can own top-level
        // references without making every declared function appear nested under `<script>`.
        // AssignContainers の後で追加し、top-level reference の帰属先だけを提供する。
        // 宣言済み関数の親を `<script>` に変えないことで既存の symbol contract を維持する。
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            SubKind = "script_scope",
            Name = "<script>",
            Line = 1,
            StartLine = 1,
            EndLine = lines.Length,
            BodyStartLine = 1,
            BodyEndLine = lines.Length,
            Signature = "<script>",
        });
    }


}
