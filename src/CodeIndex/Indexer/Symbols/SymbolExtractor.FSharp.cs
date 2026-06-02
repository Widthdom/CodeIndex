using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{

    private enum FSharpTypeBodyKind
    {
        None,
        Pending,
        Record,
        Union,
    }

    private readonly record struct FSharpTypeBodyState(FSharpTypeBodyKind Kind, int DeclarationIndent)
    {
        public static readonly FSharpTypeBodyState None = new(FSharpTypeBodyKind.None, -1);
    }

    private static readonly Regex FSharpTypeDeclarationRegex = new(@"^\s*type\s+(?:(?:rec|private|internal|public)\s+)*(?<name>(?:``[^`]+``|[_\p{L}][\w']*))(?:\s*<[^>]+>)?\s*=\s*(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex FSharpRecordFieldRegex = new(@"^(?:\[\<[^>]+\>\]\s*)*(?:mutable\s+)?(?<name>(?:``[^`]+``|[_\p{L}][\w']*))\s*:\s*.+$", RegexOptions.Compiled);
    private static readonly Regex FSharpUnionCaseRegex = new(@"^\|?\s*(?:\[\<[^>]+\>\]\s*)*(?<name>(?:``[^`]+``|[_\p{L}][\w']*))(?:\s+of\b.*)?$", RegexOptions.Compiled);
    private static readonly Regex FSharpActivePatternDefinitionRegex = new(@"^\s*let\s+(?:(?:rec|mutable|inline|private|internal|public)\s+)*\(\|(?<cases>.+?)\|\)", RegexOptions.Compiled);
    private static readonly Regex FSharpActivePatternNameRegex = new(@"^(?:``[^`]+``|[_\p{L}][\w']*)$", RegexOptions.Compiled);
    private static readonly Regex FSharpOperatorDefinitionRegex = new(@"^\s*let\s+(?:(?:rec|mutable|inline|private|internal|public)\s+)*(?<name>\((?!\|)[^)\s]+\))(?:\s+(?:\w+|\())?", RegexOptions.Compiled);

    private static bool TryAddFSharpTypeMemberSymbols(List<SymbolRecord> symbols, long fileId, string line, int lineNumber, ref FSharpTypeBodyState state)
    {
        var trimmed = line.TrimStart();
        var lineIndent = line.Length - trimmed.Length;

        if (state.Kind == FSharpTypeBodyKind.Record)
        {
            if (string.IsNullOrWhiteSpace(trimmed))
                return false;

            if (lineIndent <= state.DeclarationIndent && !trimmed.StartsWith("}", StringComparison.Ordinal))
            {
                state = FSharpTypeBodyState.None;
                return false;
            }

            var emitted = TryAddFSharpRecordFields(symbols, fileId, line, lineNumber);
            if (trimmed.Contains('}'))
                state = FSharpTypeBodyState.None;
            return emitted;
        }

        if (state.Kind == FSharpTypeBodyKind.Union)
        {
            if (string.IsNullOrWhiteSpace(trimmed))
                return false;

            if (lineIndent <= state.DeclarationIndent && !trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                state = FSharpTypeBodyState.None;
                return false;
            }

            return TryAddFSharpUnionCases(symbols, fileId, line, lineNumber);
        }

        if (state.Kind == FSharpTypeBodyKind.Pending)
        {
            if (string.IsNullOrWhiteSpace(trimmed))
                return false;

            if (lineIndent <= state.DeclarationIndent)
            {
                state = FSharpTypeBodyState.None;
                return false;
            }

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                state = new FSharpTypeBodyState(FSharpTypeBodyKind.Record, state.DeclarationIndent);
                return TryAddFSharpRecordFields(symbols, fileId, trimmed, lineNumber);
            }

            if (trimmed.StartsWith("|", StringComparison.Ordinal) || FSharpUnionCaseRegex.IsMatch(trimmed))
            {
                state = new FSharpTypeBodyState(FSharpTypeBodyKind.Union, state.DeclarationIndent);
                return TryAddFSharpUnionCases(symbols, fileId, line, lineNumber);
            }

            state = FSharpTypeBodyState.None;
            return false;
        }

        if (!FSharpTypeDeclarationRegex.IsMatch(line))
            return false;

        var declarationMatch = FSharpTypeDeclarationRegex.Match(line);
        var rest = declarationMatch.Groups["rest"].Value;
        if (rest.Length == 0)
        {
            state = new FSharpTypeBodyState(FSharpTypeBodyKind.Pending, lineIndent);
            return false;
        }

        var restTrimmed = rest.TrimStart();
        if (restTrimmed.StartsWith("{", StringComparison.Ordinal))
        {
            state = new FSharpTypeBodyState(FSharpTypeBodyKind.Record, lineIndent);
            return TryAddFSharpRecordFields(symbols, fileId, rest, lineNumber);
        }

        if (TryAddFSharpUnionCasesFromTypeRemainder(symbols, fileId, rest, lineNumber))
        {
            state = new FSharpTypeBodyState(FSharpTypeBodyKind.Union, lineIndent);
            return true;
        }

        if (restTrimmed.StartsWith("|", StringComparison.Ordinal))
        {
            state = new FSharpTypeBodyState(FSharpTypeBodyKind.Union, lineIndent);
            return TryAddFSharpUnionCases(symbols, fileId, rest, lineNumber);
        }

        if (restTrimmed.StartsWith("class", StringComparison.Ordinal)
            || restTrimmed.StartsWith("interface", StringComparison.Ordinal)
            || restTrimmed.StartsWith("struct", StringComparison.Ordinal)
            || restTrimmed.StartsWith("enum", StringComparison.Ordinal)
            || restTrimmed.StartsWith("exception", StringComparison.Ordinal)
            || restTrimmed.StartsWith("(", StringComparison.Ordinal))
        {
            state = FSharpTypeBodyState.None;
            return false;
        }

        return false;
    }

    private static bool TryAddFSharpRecordFields(List<SymbolRecord> symbols, long fileId, string line, int lineNumber)
    {
        var emittedAny = false;
        foreach (var segment in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = segment.Trim().TrimStart('{').TrimEnd('}').TrimStart();
            if (candidate.Length == 0)
                continue;

            var match = FSharpRecordFieldRegex.Match(candidate);
            if (!match.Success)
                continue;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = FSharpSymbolNameNormalizer.Normalize(match.Groups["name"].Value),
                    Line = lineNumber,
                    StartLine = lineNumber,
                    EndLine = lineNumber,
                    Signature = candidate.Trim(),
                },
                rawLine: line);
            emittedAny = true;
        }

        return emittedAny;
    }

    private static bool TryAddFSharpRecordFieldsFromContext(List<SymbolRecord> symbols, long fileId, string[] lines, int lineIndex, string line, int lineNumber)
    {
        var candidate = line.TrimStart().TrimStart('{').TrimEnd('}').TrimStart();
        if (candidate.Length == 0 || !FSharpRecordFieldRegex.IsMatch(candidate))
            return false;

        for (var i = lineIndex - 1; i >= 0; i--)
        {
            var previous = lines[i].Trim();
            if (previous.Length == 0)
                continue;

            if (FSharpTypeDeclarationRegex.IsMatch(previous))
            {
                var declarationMatch = FSharpTypeDeclarationRegex.Match(previous);
                var rest = declarationMatch.Groups["rest"].Value.TrimStart();
                if (rest.Length > 0 && !rest.StartsWith("{", StringComparison.Ordinal) && !rest.StartsWith("|", StringComparison.Ordinal))
                    break;

                return TryAddFSharpRecordFields(symbols, fileId, line, lineNumber);
            }

            if (previous.StartsWith("type ", StringComparison.Ordinal)
                || previous.StartsWith("module ", StringComparison.Ordinal)
                || previous.StartsWith("let ", StringComparison.Ordinal)
                || previous.StartsWith("open ", StringComparison.Ordinal))
                break;
        }

        return false;
    }

    private static bool TryAddFSharpUnionCases(List<SymbolRecord> symbols, long fileId, string line, int lineNumber)
    {
        var emittedAny = false;
        var segments = line.Contains('|', StringComparison.Ordinal)
            ? line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [line];

        foreach (var segment in segments)
        {
            var candidate = segment.Trim().TrimStart('{').TrimEnd('}').TrimStart();
            if (candidate.Length == 0)
                continue;

            var match = FSharpUnionCaseRegex.Match(candidate);
            if (!match.Success)
                continue;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = FSharpSymbolNameNormalizer.Normalize(match.Groups["name"].Value),
                    Line = lineNumber,
                    StartLine = lineNumber,
                    EndLine = lineNumber,
                    Signature = candidate.Trim(),
                },
                rawLine: line);
            emittedAny = true;
        }

        return emittedAny;
    }

    private static bool TryAddFSharpUnionCasesFromTypeRemainder(List<SymbolRecord> symbols, long fileId, string remainder, int lineNumber)
    {
        var trimmed = remainder.TrimStart();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.Contains('|', StringComparison.Ordinal))
            return TryAddFSharpUnionCases(symbols, fileId, trimmed, lineNumber);

        if (!FSharpUnionCaseRegex.IsMatch(trimmed))
            return false;

        return TryAddFSharpUnionCases(symbols, fileId, trimmed, lineNumber);
    }

    private static bool TryAddFSharpActivePatternSymbols(List<SymbolRecord> symbols, long fileId, string line, int lineNumber)
    {
        var match = FSharpActivePatternDefinitionRegex.Match(line);
        if (!match.Success)
            return false;

        var activePatternNames = match.Groups["cases"].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var emittedAny = false;
        foreach (var rawName in activePatternNames)
        {
            if (rawName == "_" || !FSharpActivePatternNameRegex.IsMatch(rawName))
                continue;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = FSharpSymbolNameNormalizer.Normalize(rawName),
                    Line = lineNumber,
                    StartLine = lineNumber,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                },
                rawLine: line);
            emittedAny = true;
        }

        return emittedAny;
    }

    private static bool TryAddFSharpOperatorSymbols(List<SymbolRecord> symbols, long fileId, string line, int lineNumber)
    {
        var match = FSharpOperatorDefinitionRegex.Match(line);
        if (!match.Success)
            return false;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = FSharpSymbolNameNormalizer.Normalize(match.Groups["name"].Value),
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = lineNumber,
                Signature = line.Trim(),
            },
            rawLine: line);
        return true;
    }

}
