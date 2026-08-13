namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static PatternSymbolDraft BuildPatternSymbolDraft(
        MatchedPatternCandidateContext candidate,
        PatternCandidateColumns columns,
        string? rawReturnType)
    {
        var lineContext = candidate.LineContext;
        var extraction = lineContext.Extraction;
        var lang = extraction.Lang;
        var filePath = extraction.FilePath;
        var projectRoot = extraction.ProjectRoot;
        var pattern = candidate.Pattern;
        var patternMatchLine = candidate.PatternMatchLine;
        var matchLine = lineContext.PreparedLine.MatchLine;
        var match = candidate.CapturedMatch.Match;
        var absoluteStartColumn = candidate.CapturedMatch.AbsoluteStartColumn;
        var csharpPropertyCandidate = candidate.CSharpPropertyCandidate;
        var csharpGateRawStartColumn = columns.CSharpGateRawStartColumn;
        var lines = extraction.Lines;
        var line = lineContext.PreparedLine.SourceLine;
        var i = lineContext.LineIndex;
        var fortranContinuationCandidate = lineContext.PreparedLine.FortranContinuationCandidate;
        var scanInputs = extraction.ScanInputs;
        var structuralLines = scanInputs.StructuralLines;
        var scientificBodyScannerLines = scanInputs.ScientificBodyScannerLines;
        var matlabExplicitOuterClosureByLine = scanInputs.MatlabExplicitOuterClosureByLine;
        var cssScannerLines = scanInputs.CssScannerLines;
        var shellScannerLines = scanInputs.ShellScannerLines;
        var csharpMatchLines = scanInputs.CSharpMatchLines;
        var getCSharpLineStartStates = scanInputs.GetCSharpLineStartStates;
        var name = match.Groups["name"].Success
            ? match.Groups["name"].ValueSpan.Trim().ToString()
            : match.ValueSpan.Trim().ToString();
        name = NormalizeExtractedSymbolName(lang, name, match, matchLine);
        if (pattern.Kind == "import" && lang is "javascript" or "typescript")
            name = ResolveJavaScriptTypeScriptModuleSpecifier(lang, filePath, projectRoot, name);
        var rubyAttrNames = lang == "ruby"
            && pattern.Kind == "property"
            ? TryExpandRubyAttrDeclaratorList(patternMatchLine, absoluteStartColumn, match, name)
            : null;

        var rangeLines = lang == "css" && cssScannerLines != null
            ? cssScannerLines
            : lang == "shell" && shellScannerLines != null
                ? shellScannerLines
            : structuralLines;
        var scalaBracelessClassEndLine = lang == "scala" && pattern.Kind == "class"
            ? TryFindScalaBracelessClassEndLine(lines, i, absoluteStartColumn)
            : null;
        var (endLine, bodyStartLine, bodyEndLine) = lang is "kotlin" or "scala"
            && pattern.Kind == "function"
            && TryFindKotlinScalaExpressionBodyEndLine(line, absoluteStartColumn)
                ? (i + 1, null, null)
                : scalaBracelessClassEndLine.HasValue
                        ? (scalaBracelessClassEndLine.Value + 1, null, null)
                        : lang == "csharp" && pattern.BodyStyle == BodyStyle.Brace && csharpMatchLines != null
                            ? FindCSharpPatternBraceRange(
                                lines,
                                csharpMatchLines,
                                getCSharpLineStartStates,
                                i,
                                absoluteStartColumn,
                                csharpGateRawStartColumn)
                            : ResolveRange(
                                rangeLines,
                                i,
                                pattern.BodyStyle,
                                lang,
                                absoluteStartColumn,
                                scientificBodyScannerLines,
                                matlabExplicitOuterClosureByLine);
        if (fortranContinuationCandidate != null)
            endLine = Math.Max(endLine, fortranContinuationCandidate.Value.LastConsumedLineIndex + 1);
        var startLine = i + 1;
        if (lang == "csharp"
            && pattern.Kind == "property"
            && pattern.BodyStyle == BodyStyle.None
            && csharpPropertyCandidate.ExpressionBodyEndLineIndex.HasValue)
        {
            endLine = Math.Max(endLine, csharpPropertyCandidate.ExpressionBodyEndLineIndex.Value + 1);
        }

        return new PatternSymbolDraft(
            name,
            rawReturnType,
            rubyAttrNames,
            startLine,
            endLine,
            bodyStartLine,
            bodyEndLine);
    }

    private static PatternScanResult TryShapePatternSymbol(
        MatchedPatternCandidateContext candidate,
        PatternCandidateColumns columns,
        PatternSymbolDraft draft,
        out ShapedPatternSymbol shapedSymbol)
    {
        shapedSymbol = default;
        var lineContext = candidate.LineContext;
        var extraction = lineContext.Extraction;
        var lang = extraction.Lang;
        var pattern = candidate.Pattern;
        var patternMatchLine = candidate.PatternMatchLine;
        var matchLine = lineContext.PreparedLine.MatchLine;
        var lineOffset = candidate.LineOffset;
        var match = candidate.CapturedMatch.Match;
        var absoluteStartColumn = candidate.CapturedMatch.AbsoluteStartColumn;
        var csharpPropertyCandidate = candidate.CSharpPropertyCandidate;
        var csharpWrappedModifierPrefix = candidate.CSharpWrappedModifierPrefix;
        var csharpGateRawStartColumn = columns.CSharpGateRawStartColumn;
        var lines = extraction.Lines;
        var line = lineContext.PreparedLine.SourceLine;
        var i = lineContext.LineIndex;
        var scanInputs = extraction.ScanInputs;
        var csharpMatchColumnToRaw = scanInputs.CSharpMatchColumnToRaw;
        var csharpMatchLines = scanInputs.CSharpMatchLines;
        var name = draft.Name;
        var startLine = draft.StartLine;
        var endLine = draft.EndLine;
        var bodyStartLine = draft.BodyStartLine;
        var bodyEndLine = draft.BodyEndLine;
        // Python @property decorator: reclassify the def as property
        // Python @property デコレータ: def を property に再分類
        var kind = pattern.Kind;
        string? pythonSubKind = null;
        if (kind == "function" && lang == "python" && HasPythonPropertyDecorator(lines, i))
        {
            kind = "property";
            pythonSubKind = GetPythonPropertyAccessorSubKind(lines, i);
        }
        else if (kind == "function" && lang == "python" && IsPythonClassHook(name))
        {
            kind = "class_hook";
            pythonSubKind = "dunder";
            (endLine, bodyStartLine, bodyEndLine) = FindPythonIndentedBodyRange(lines, i);
        }
        else if (kind == "function" && lang is "javascript" or "typescript")
        {
            kind = ResolveJavaScriptTypeScriptFunctionKind(
                TryGetGroup(match, "async") != null,
                TryGetGroup(match, "generator") != null);
        }

        if (lang == "css")
            name = ResolveCssSymbolName(matchLine[absoluteStartColumn..], name, lines, i, endLine);

        if (lang == "css" && string.IsNullOrWhiteSpace(name))
        {
            var skippedEndColumn = pattern.BodyStyle == BodyStyle.Brace
                && bodyEndLine == startLine
                ? FindSameLineBraceEndColumn(line, absoluteStartColumn, lang, kind)
                : -1;
            if (skippedEndColumn >= absoluteStartColumn)
            {
                lineOffset = FindNextSameLineBraceStatementStart(matchLine, skippedEndColumn + 1, lang);
                return PatternScanResult.ContinueAt(lineOffset);
            }

            return PatternScanResult.StopLine;
        }

        var signatureResult = BuildPatternSignature(
            lang,
            pattern,
            lines,
            i,
            line,
            patternMatchLine,
            absoluteStartColumn,
            match,
            csharpPropertyCandidate,
            csharpWrappedModifierPrefix,
            csharpMatchColumnToRaw,
            csharpMatchLines,
            csharpGateRawStartColumn,
            startLine,
            bodyStartLine,
            bodyEndLine,
            kind);
        kind = signatureResult.Kind;

        shapedSymbol = new ShapedPatternSymbol(
            name,
            kind,
            draft.RawReturnType,
            pythonSubKind,
            draft.RubyAttributeNames,
            startLine,
            endLine,
            bodyStartLine,
            bodyEndLine,
            signatureResult);
        return PatternScanResult.Accepted;
    }
}
