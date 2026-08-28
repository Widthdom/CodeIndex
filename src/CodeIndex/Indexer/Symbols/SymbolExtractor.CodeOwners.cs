using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    internal const int CodeOwnersMaxRuleLineLength = 4096;
    internal const int CodeOwnersMaxOwnersPerRule = 128;
    internal const int CodeOwnersMaxOwnerLength = 256;

    private readonly record struct CodeOwnersOwnerToken(string Value, int Column);

    private static List<SymbolRecord> ExtractCodeOwnersSymbols(long fileId, string[] lines)
    {
        var symbols = CreateSymbolListForLines(lines.Length);
        var reportedDiagnostics = new HashSet<string>(StringComparer.Ordinal);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var lineNumber = lineIndex + 1;
            if (line.Length > CodeOwnersMaxRuleLineLength)
            {
                AddCodeOwnersDiagnostic(
                    symbols,
                    reportedDiagnostics,
                    fileId,
                    "codeowners_rule_line_too_long",
                    lineNumber,
                    lines,
                    "CODEOWNERS rule exceeded the bounded line length and was skipped.");
                continue;
            }

            if (!TryParseCodeOwnersRule(
                    line,
                    out var pattern,
                    out var patternColumn,
                    out var owners,
                    out var ownerBudgetExceeded,
                    out var diagnosticCategory,
                    out var diagnosticMessage))
            {
                if (diagnosticCategory != null)
                {
                    AddCodeOwnersDiagnostic(
                        symbols,
                        reportedDiagnostics,
                        fileId,
                        diagnosticCategory,
                        lineNumber,
                        lines,
                        diagnosticMessage!);
                }
                continue;
            }

            if (!TryAddCodeOwnersSymbol(
                    symbols,
                    reportedDiagnostics,
                    fileId,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "rule",
                        SubKind = "ownership_rule",
                        Name = pattern,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        StartColumn = patternColumn,
                        EndLine = lineNumber,
                        Signature = LimitStructuredDataLineSignature(line),
                    },
                    lineNumber,
                    lines))
            {
                break;
            }

            foreach (var owner in owners)
            {
                if (!TryAddCodeOwnersSymbol(
                        symbols,
                        reportedDiagnostics,
                        fileId,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "property",
                            SubKind = "owner",
                            Name = owner.Value,
                            Line = lineNumber,
                            StartLine = lineNumber,
                            StartColumn = owner.Column,
                            EndLine = lineNumber,
                            Signature = owner.Value,
                            ContainerKind = "rule",
                            ContainerName = pattern,
                            ContainerQualifiedName = pattern,
                        },
                        lineNumber,
                        lines))
                {
                    return symbols;
                }
            }

            if (ownerBudgetExceeded)
            {
                AddCodeOwnersDiagnostic(
                    symbols,
                    reportedDiagnostics,
                    fileId,
                    "codeowners_owner_budget_exceeded",
                    lineNumber,
                    lines,
                    "CODEOWNERS rule exceeded the per-rule owner budget; owner symbols were truncated.");
            }
        }

        return symbols;
    }

    private static bool TryParseCodeOwnersRule(
        string line,
        out string pattern,
        out int patternColumn,
        out List<CodeOwnersOwnerToken> owners,
        out bool ownerBudgetExceeded,
        out string? diagnosticCategory,
        out string? diagnosticMessage)
    {
        pattern = string.Empty;
        patternColumn = 0;
        owners = [];
        ownerBudgetExceeded = false;
        diagnosticCategory = null;
        diagnosticMessage = null;

        var span = line.AsSpan();
        var index = 0;
        SkipCodeOwnersWhitespace(span, ref index);
        if (index >= span.Length || span[index] == '#')
            return false;

        patternColumn = index;
        var patternStart = index;
        while (index < span.Length && !char.IsWhiteSpace(span[index]))
            index++;
        var rawPattern = span[patternStart..index];
        if (rawPattern.Length > StructuredDataMaxPathLength)
        {
            diagnosticCategory = "codeowners_pattern_too_long";
            diagnosticMessage = "CODEOWNERS pattern exceeded the structured path limit and was skipped.";
            return false;
        }

        if (rawPattern.IsEmpty
            || rawPattern.StartsWith(@"\#")
            || rawPattern[0] == '!'
            || rawPattern.IndexOf('[') >= 0
            || rawPattern.IndexOf(']') >= 0
            || ContainsCodeOwnersControlCharacter(rawPattern))
        {
            diagnosticCategory = "codeowners_unsupported_pattern";
            diagnosticMessage = "CODEOWNERS rule used unsupported or malformed pattern syntax and was skipped.";
            return false;
        }
        pattern = rawPattern.ToString();

        while (index < span.Length)
        {
            SkipCodeOwnersWhitespace(span, ref index);
            if (index >= span.Length)
                break;
            if (span[index] == '#')
                break;

            var ownerColumn = index;
            while (index < span.Length && !char.IsWhiteSpace(span[index]))
                index++;
            var owner = span[ownerColumn..index];
            if (owner.Length > CodeOwnersMaxOwnerLength || !IsCodeOwnersOwner(owner))
            {
                diagnosticCategory = "codeowners_invalid_owner";
                diagnosticMessage = "CODEOWNERS rule contained an invalid or oversized owner and was skipped.";
                owners.Clear();
                pattern = string.Empty;
                return false;
            }

            if (owners.Count >= CodeOwnersMaxOwnersPerRule)
            {
                ownerBudgetExceeded = true;
                continue;
            }
            owners.Add(new CodeOwnersOwnerToken(owner.ToString(), ownerColumn));
        }

        return true;
    }

    private static void SkipCodeOwnersWhitespace(ReadOnlySpan<char> value, ref int index)
    {
        while (index < value.Length && char.IsWhiteSpace(value[index]))
            index++;
    }

    private static bool IsCodeOwnersOwner(ReadOnlySpan<char> owner)
    {
        if (owner.Length < 2 || ContainsCodeOwnersControlCharacter(owner))
            return false;
        if (owner[0] == '@')
            return IsCodeOwnersMention(owner[1..]);

        var atIndex = owner.IndexOf('@');
        return atIndex > 0
            && atIndex == owner.LastIndexOf('@')
            && atIndex < owner.Length - 1;
    }

    private static bool IsCodeOwnersMention(ReadOnlySpan<char> mention)
    {
        var slashIndex = mention.IndexOf('/');
        if (slashIndex < 0)
            return IsCodeOwnersMentionSegment(mention);

        return slashIndex > 0
            && slashIndex < mention.Length - 1
            && mention[(slashIndex + 1)..].IndexOf('/') < 0
            && IsCodeOwnersMentionSegment(mention[..slashIndex])
            && IsCodeOwnersMentionSegment(mention[(slashIndex + 1)..]);
    }

    private static bool IsCodeOwnersMentionSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty
            || !IsCodeOwnersMentionAlphaNumeric(segment[0])
            || !IsCodeOwnersMentionAlphaNumeric(segment[^1]))
        {
            return false;
        }

        var previousWasHyphen = false;
        foreach (var character in segment)
        {
            if (IsCodeOwnersMentionAlphaNumeric(character))
            {
                previousWasHyphen = false;
                continue;
            }
            if (character != '-' || previousWasHyphen)
                return false;
            previousWasHyphen = true;
        }
        return true;
    }

    private static bool IsCodeOwnersMentionAlphaNumeric(char character)
        => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9';

    private static bool ContainsCodeOwnersControlCharacter(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
                return true;
        }
        return false;
    }

    private static bool TryAddCodeOwnersSymbol(
        List<SymbolRecord> symbols,
        HashSet<string> reportedDiagnostics,
        long fileId,
        SymbolRecord symbol,
        int line,
        string[] lines)
    {
        if (symbols.Count < StructuredDataMaxSymbols)
        {
            symbols.Add(symbol);
            return true;
        }

        AddCodeOwnersDiagnostic(
            symbols,
            reportedDiagnostics,
            fileId,
            "codeowners_symbol_budget_exceeded",
            line,
            lines,
            "CODEOWNERS symbol extraction exceeded the per-file budget; remaining rules were truncated.");
        return false;
    }

    private static void AddCodeOwnersDiagnostic(
        List<SymbolRecord> symbols,
        HashSet<string> reportedDiagnostics,
        long fileId,
        string category,
        int line,
        string[] lines,
        string message)
    {
        if (!reportedDiagnostics.Add(category))
            return;

        var signatureIndex = Math.Clamp(line - 1, 0, Math.Max(0, lines.Length - 1));
        var signature = lines.Length == 0 ? message : $"{message} {lines[signatureIndex].Trim()}";
        var diagnostic = new SymbolRecord
        {
            FileId = fileId,
            Kind = "annotation",
            SubKind = "extraction_diagnostic",
            Name = category,
            Line = Math.Max(1, line),
            StartLine = Math.Max(1, line),
            EndLine = Math.Max(1, line),
            Signature = LimitStructuredDataSignature(signature),
        };

        if (symbols.Count >= StructuredDataMaxSymbols)
            symbols[^1] = diagnostic;
        else
            symbols.Add(diagnostic);
    }
}
