using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private readonly record struct PatternSignatureBounds(
        bool CSharpSingleLineCollapsedMatch,
        int CSharpSignatureRawStartColumn,
        int SameLineEndColumn,
        bool SameLineEndUsesRawColumns);

    private readonly record struct PatternSignatureResult(
        string Signature,
        string Kind,
        PatternSignatureBounds Bounds);

    private static PatternSignatureBounds ResolvePatternSignatureBounds(
        string lang,
        SymbolPattern pattern,
        string kind,
        string line,
        string patternMatchLine,
        int[]?[] csharpMatchColumnToRaw,
        string[]? csharpMatchLines,
        int lineIndex,
        int absoluteStartColumn,
        int csharpGateRawStartColumn,
        int startLine,
        int? bodyEndLine)
    {
        var csharpSingleLineCollapsedMatch = lang == "csharp"
            && csharpMatchLines != null
            && ReferenceEquals(patternMatchLine, csharpMatchLines[lineIndex]);
        var csharpSignatureRawStartColumn = csharpGateRawStartColumn;
        var csharpSameLineBraceStartColumn = csharpSingleLineCollapsedMatch
            ? absoluteStartColumn
            : csharpSignatureRawStartColumn;
        var sameLineEndColumn = pattern.BodyStyle == BodyStyle.Brace
            && bodyEndLine == startLine
                ? (lang == "csharp" && csharpSingleLineCollapsedMatch
                    ? FindCSharpSameLineBraceEndColumnFromSanitized(patternMatchLine, csharpSameLineBraceStartColumn)
                    : FindSameLineBraceEndColumn(line, csharpSameLineBraceStartColumn, lang, kind))
                : -1;
        var sameLineEndUsesRawColumns = pattern.BodyStyle == BodyStyle.Brace
            && bodyEndLine == startLine
            && !(lang == "csharp" && csharpSingleLineCollapsedMatch);

        if (lang == "csharp"
            && csharpSingleLineCollapsedMatch
            && CanUseCSharpSameLineSemicolonEndColumn(kind))
        {
            var semicolonEndColumn = FindCSharpSameLineSemicolonEndColumn(patternMatchLine, absoluteStartColumn);
            if (semicolonEndColumn >= absoluteStartColumn
                && (sameLineEndColumn < absoluteStartColumn || semicolonEndColumn < sameLineEndColumn))
            {
                sameLineEndColumn = semicolonEndColumn;
                sameLineEndUsesRawColumns = false;
            }
        }

        if (lang == "csharp"
            && kind == "event"
            && pattern.BodyStyle == BodyStyle.None
            && HasCSharpEventAccessorStart(patternMatchLine[absoluteStartColumn..]))
        {
            var braceEndColumn = csharpSingleLineCollapsedMatch
                ? FindCSharpSameLineBraceEndColumnFromSanitized(patternMatchLine, csharpSameLineBraceStartColumn)
                : FindSameLineBraceEndColumn(line, csharpSameLineBraceStartColumn, lang, kind);
            if (braceEndColumn >= absoluteStartColumn
                && (sameLineEndColumn < absoluteStartColumn || braceEndColumn < sameLineEndColumn))
            {
                sameLineEndColumn = braceEndColumn;
                sameLineEndUsesRawColumns = !csharpSingleLineCollapsedMatch;
            }
        }

        if (sameLineEndColumn < absoluteStartColumn
            && lang == "csharp"
            && kind == "enum"
            && pattern.BodyStyle == BodyStyle.None)
        {
            sameLineEndColumn = FindCSharpSameLineEnumMemberEndColumn(patternMatchLine, absoluteStartColumn);
            sameLineEndUsesRawColumns = false;
        }

        return new PatternSignatureBounds(
            csharpSingleLineCollapsedMatch,
            csharpSignatureRawStartColumn,
            sameLineEndColumn,
            sameLineEndUsesRawColumns);
    }

    private static PatternSignatureResult BuildPatternSignature(
        string lang,
        SymbolPattern pattern,
        string[] lines,
        int lineIndex,
        string line,
        string patternMatchLine,
        int absoluteStartColumn,
        Match match,
        CSharpPropertyMatchCandidate csharpPropertyCandidate,
        string? csharpWrappedModifierPrefix,
        int[]?[] csharpMatchColumnToRaw,
        string[]? csharpMatchLines,
        int csharpGateRawStartColumn,
        int startLine,
        int? bodyStartLine,
        int? bodyEndLine,
        string kind)
    {
        var bounds = ResolvePatternSignatureBounds(
            lang,
            pattern,
            kind,
            line,
            patternMatchLine,
            csharpMatchColumnToRaw,
            csharpMatchLines,
            lineIndex,
            absoluteStartColumn,
            csharpGateRawStartColumn,
            startLine,
            bodyEndLine);

        string signature;
        if (csharpWrappedModifierPrefix is not null)
        {
            signature = BuildWrappedCSharpPatternSignature(
                line,
                lineIndex,
                match,
                csharpMatchColumnToRaw,
                csharpWrappedModifierPrefix,
                absoluteStartColumn,
                bounds);
        }
        else if (TryBuildCSharpBraceFunctionHeaderSignature(
            lang,
            pattern,
            lines,
            lineIndex,
            line,
            bodyStartLine,
            bodyEndLine,
            bounds.CSharpSignatureRawStartColumn,
            startLine,
            out signature))
        {
        }
        else if (bounds.SameLineEndColumn >= absoluteStartColumn)
        {
            signature = BuildBoundedSameLinePatternSignature(
                lang,
                line,
                patternMatchLine,
                lineIndex,
                match,
                csharpMatchColumnToRaw,
                absoluteStartColumn,
                bounds);
        }
        else if (TryBuildCSharpMultilinePatternSignature(
            lang,
            pattern,
            lines,
            lineIndex,
            bounds.CSharpSignatureRawStartColumn,
            csharpGateRawStartColumn,
            csharpPropertyCandidate,
            out signature))
        {
        }
        else if (lang == "csharp"
            && pattern.Kind is "event" or "delegate"
            && pattern.BodyStyle == BodyStyle.None)
        {
            signature = BuildCSharpSameLineStatementSignature(
                line,
                patternMatchLine,
                match,
                absoluteStartColumn);
        }
        else if (lang == "java"
            && pattern.BodyStyle == BodyStyle.Brace
            && bodyStartLine is null)
        {
            signature = BuildJavaSameLineStatementSignature(line, match, absoluteStartColumn);
        }
        else if (lang == "csharp"
            && pattern.Kind == "property"
            && pattern.BodyStyle == BodyStyle.None)
        {
            signature = BuildCSharpFieldPatternSignature(
                line,
                patternMatchLine,
                lineIndex,
                match,
                csharpMatchColumnToRaw,
                csharpMatchLines,
                absoluteStartColumn);
        }
        else
        {
            signature = lang == "fortran"
                ? patternMatchLine[absoluteStartColumn..].Trim()
                : line[absoluteStartColumn..].Trim();
        }

        if (lang == "python" && pattern.Kind is "function" or "class")
            signature = BuildPythonLogicalHeaderSignature(lines, lineIndex, absoluteStartColumn);

        if (lang == "csharp"
            && pattern.Kind == "property"
            && pattern.BodyStyle == BodyStyle.None)
        {
            // The plain-field matcher historically reused the internal `property` tag so it
            // could share the property/field scanning pipeline. Persist the public taxonomy
            // value here, after the field-only terminator pattern has already distinguished
            // declarations from accessor and expression-bodied properties. #4865
            // plain-field matcher は property / field の走査経路を共有するため内部的に
            // `property` tag を再利用してきた。field 専用終端 pattern が accessor /
            // expression-bodied property を区別した後、公開 taxonomy の `field` に正規化する。
            kind = "field";
        }

        if (kind == "function"
            && lang == "csharp"
            && pattern.BodyStyle == BodyStyle.None
            && IsCSharpConstOrStaticReadonlyField(signature))
        {
            kind = "field";
        }

        if (lang == "csharp"
            && pattern.BodyStyle == BodyStyle.None
            && (pattern.Kind == "property" || kind == "field"))
        {
            signature = BoundCSharpFieldInitializerSignature(signature);
        }

        return new PatternSignatureResult(signature, kind, bounds);
    }

    private static string BuildWrappedCSharpPatternSignature(
        string line,
        int lineIndex,
        Match match,
        int[]?[] csharpMatchColumnToRaw,
        string modifierPrefix,
        int absoluteStartColumn,
        PatternSignatureBounds bounds)
    {
        var nameLineStartColumn = bounds.CSharpSignatureRawStartColumn;
        var nameLineEndExclusive = bounds.SameLineEndColumn >= absoluteStartColumn
            ? (bounds.SameLineEndUsesRawColumns
                ? Math.Min(bounds.SameLineEndColumn + 1, line.Length)
                : Math.Min(
                    TranslateCSharpCollapsedColumnToRaw(
                        csharpMatchColumnToRaw,
                        lineIndex,
                        bounds.SameLineEndColumn,
                        line.Length) + 1,
                    line.Length))
            : line.Length;
        var nameLineContent = bounds.SameLineEndColumn >= absoluteStartColumn
            ? line.AsSpan(nameLineStartColumn, nameLineEndExclusive - nameLineStartColumn)
            : line.AsSpan(nameLineStartColumn);
        var signatureBuilder = new StringBuilder(modifierPrefix.Length + 1 + nameLineContent.Length);
        signatureBuilder.Append(modifierPrefix);
        signatureBuilder.Append(' ');
        signatureBuilder.Append(nameLineContent.TrimStart());
        return signatureBuilder.ToString().Trim();
    }

    private static bool TryBuildCSharpBraceFunctionHeaderSignature(
        string lang,
        SymbolPattern pattern,
        string[] lines,
        int lineIndex,
        string line,
        int? bodyStartLine,
        int? bodyEndLine,
        int signatureRawStartColumn,
        int startLine,
        out string signature)
    {
        if (lang == "csharp"
            && pattern.Kind == "function"
            && pattern.BodyStyle == BodyStyle.Brace
            && bodyStartLine.HasValue
            && bodyEndLine != startLine
            && !IsCSharpMultilineExpressionBodiedMember(lines, lineIndex, signatureRawStartColumn)
            && TryFindCSharpBraceBodyHeaderExtent(
                lines,
                lineIndex,
                Math.Min(signatureRawStartColumn, line.Length),
                out var lastLineIndex,
                out var lastLineExclusiveEndColumn))
        {
            signature = BuildCSharpMultilineSignature(
                lines,
                lineIndex,
                Math.Min(signatureRawStartColumn, line.Length),
                lastLineIndex,
                lastLineExclusiveEndColumn);
            return true;
        }

        signature = string.Empty;
        return false;
    }

    private static string BuildBoundedSameLinePatternSignature(
        string lang,
        string line,
        string patternMatchLine,
        int lineIndex,
        Match match,
        int[]?[] csharpMatchColumnToRaw,
        int absoluteStartColumn,
        PatternSignatureBounds bounds)
    {
        if (lang == "csharp" && bounds.CSharpSingleLineCollapsedMatch)
        {
            var rawStart = bounds.CSharpSignatureRawStartColumn;
            var rawEndInclusive = bounds.SameLineEndUsesRawColumns
                ? bounds.SameLineEndColumn
                : TranslateCSharpCollapsedColumnToRaw(
                    csharpMatchColumnToRaw,
                    lineIndex,
                    bounds.SameLineEndColumn,
                    line.Length);
            var rawEndExclusive = Math.Min(rawEndInclusive + 1, line.Length);
            if (rawStart > line.Length)
                rawStart = line.Length;
            if (rawEndExclusive <= rawStart)
                rawEndExclusive = Math.Min(rawStart + Math.Max(1, match.Length), line.Length);
            return line[rawStart..rawEndExclusive].Trim();
        }

        var signatureStartColumn = bounds.CSharpSingleLineCollapsedMatch && bounds.SameLineEndUsesRawColumns
            ? bounds.CSharpSignatureRawStartColumn
            : absoluteStartColumn;
        var signatureEndExclusive = Math.Min(bounds.SameLineEndColumn + 1, line.Length);
        if (signatureEndExclusive <= signatureStartColumn)
            signatureEndExclusive = Math.Min(signatureStartColumn + Math.Max(1, match.Length), line.Length);
        return line[signatureStartColumn..signatureEndExclusive].Trim();
    }

    private static bool TryBuildCSharpMultilinePatternSignature(
        string lang,
        SymbolPattern pattern,
        string[] lines,
        int lineIndex,
        int signatureRawStartColumn,
        int gateRawStartColumn,
        CSharpPropertyMatchCandidate propertyCandidate,
        out string signature)
    {
        if (lang == "csharp"
            && pattern.BodyStyle == BodyStyle.None
            && TryFindCSharpSemicolonTerminatedSignatureExtent(
                lines,
                lineIndex,
                gateRawStartColumn,
                out var fieldLastLineIndex,
                out var fieldLastLineExclusiveEndColumn)
            && fieldLastLineIndex > lineIndex)
        {
            signature = BuildCSharpMultilineSignature(
                lines,
                lineIndex,
                gateRawStartColumn,
                fieldLastLineIndex,
                fieldLastLineExclusiveEndColumn);
            return true;
        }

        if (lang == "csharp"
            && pattern.BodyStyle == BodyStyle.Brace
            && IsCSharpMultilineExpressionBodiedMember(lines, lineIndex, signatureRawStartColumn)
            && TryFindCSharpSemicolonTerminatedSignatureExtent(
                lines,
                lineIndex,
                signatureRawStartColumn,
                out var semicolonLastLineIndex,
                out var semicolonLastLineExclusiveEndColumn)
            && semicolonLastLineIndex > lineIndex)
        {
            signature = BuildCSharpMultilineSignature(
                lines,
                lineIndex,
                signatureRawStartColumn,
                semicolonLastLineIndex,
                semicolonLastLineExclusiveEndColumn);
            return true;
        }

        if (lang == "csharp" && propertyCandidate.LastConsumedLineIndex > lineIndex)
        {
            signature = BuildCSharpMultilineSignature(
                lines,
                lineIndex,
                signatureRawStartColumn,
                propertyCandidate.SignatureLastLineIndex,
                propertyCandidate.SignatureLastLineExclusiveEndColumn);
            return true;
        }

        if (lang == "csharp"
            && pattern.Kind is "class" or "struct" or "interface" or "enum"
            && TryFindCSharpTypeHeaderExtent(
                lines,
                lineIndex,
                signatureRawStartColumn,
                out var typeHeaderLastLineIndex,
                out var typeHeaderLastLineExclusiveEndColumn)
            && typeHeaderLastLineIndex > lineIndex)
        {
            signature = BuildCSharpTypeHeaderSignature(
                lines,
                lineIndex,
                signatureRawStartColumn,
                typeHeaderLastLineIndex,
                typeHeaderLastLineExclusiveEndColumn);
            return true;
        }

        signature = string.Empty;
        return false;
    }

    private static string BuildCSharpSameLineStatementSignature(
        string line,
        string patternMatchLine,
        Match match,
        int absoluteStartColumn)
    {
        var statementEnd = FindCSharpSameLineStatementEnd(patternMatchLine, absoluteStartColumn);
        if (statementEnd > line.Length)
            statementEnd = line.Length;
        if (statementEnd <= absoluteStartColumn)
            statementEnd = Math.Min(absoluteStartColumn + Math.Max(1, match.Length), line.Length);
        return line[absoluteStartColumn..statementEnd].Trim();
    }

    private static string BuildJavaSameLineStatementSignature(
        string line,
        Match match,
        int absoluteStartColumn)
    {
        var statementEnd = FindJavaSameLineStatementEnd(line, absoluteStartColumn);
        if (statementEnd > line.Length)
            statementEnd = line.Length;
        if (statementEnd <= absoluteStartColumn)
            statementEnd = Math.Min(absoluteStartColumn + Math.Max(1, match.Length), line.Length);
        return line[absoluteStartColumn..statementEnd].Trim();
    }

    private static string BuildCSharpFieldPatternSignature(
        string line,
        string patternMatchLine,
        int lineIndex,
        Match match,
        int[]?[] csharpMatchColumnToRaw,
        string[]? csharpMatchLines,
        int absoluteStartColumn)
    {
        var statementEnd = FindCSharpSameLineStatementEnd(patternMatchLine, absoluteStartColumn);
        if (csharpMatchLines != null
            && ReferenceEquals(patternMatchLine, csharpMatchLines[lineIndex]))
        {
            var rawStart = TranslateCSharpCollapsedColumnToRaw(
                csharpMatchColumnToRaw,
                lineIndex,
                absoluteStartColumn,
                line.Length);
            var rawEnd = TranslateCSharpCollapsedColumnToRaw(
                csharpMatchColumnToRaw,
                lineIndex,
                statementEnd,
                line.Length);
            if (rawEnd > line.Length)
                rawEnd = line.Length;
            if (rawStart > line.Length)
                rawStart = line.Length;
            if (rawEnd <= rawStart)
                rawEnd = Math.Min(rawStart + Math.Max(1, match.Length), line.Length);
            return line[rawStart..rawEnd].Trim();
        }

        if (statementEnd > line.Length)
            statementEnd = line.Length;
        if (statementEnd <= absoluteStartColumn)
            statementEnd = Math.Min(absoluteStartColumn + Math.Max(1, match.Length), line.Length);
        return line[absoluteStartColumn..statementEnd].Trim();
    }
}
