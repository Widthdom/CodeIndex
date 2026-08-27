namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static PatternScanResult EmitAcceptedPatternSymbol(
        MatchedPatternCandidateContext candidate,
        ShapedPatternSymbol shapedSymbol,
        out string emittedKind)
    {
        var lineContext = candidate.LineContext;
        var extraction = lineContext.Extraction;
        var fileId = extraction.FileId;
        var lang = extraction.Lang;
        var pattern = candidate.Pattern;
        var lines = extraction.Lines;
        var i = lineContext.LineIndex;
        var lineOffset = candidate.LineOffset;
        var absoluteStartColumn = candidate.CapturedMatch.AbsoluteStartColumn;
        var line = lineContext.PreparedLine.SourceLine;
        var patternMatchLine = candidate.PatternMatchLine;
        var match = candidate.CapturedMatch.Match;
        var name = shapedSymbol.Name;
        var kind = shapedSymbol.Kind;
        var signature = shapedSymbol.SignatureResult.Signature;
        var rawReturnType = shapedSymbol.RawReturnType;
        var pythonSubKind = shapedSymbol.PythonSubKind;
        var pythonModulePrefix = extraction.ScanInputs.PythonModulePrefix;
        var rubyAttrNames = shapedSymbol.RubyAttributeNames;
        var endLine = shapedSymbol.EndLine;
        var bodyStartLine = shapedSymbol.BodyStartLine;
        var bodyEndLine = shapedSymbol.BodyEndLine;
        var signatureResult = shapedSymbol.SignatureResult;
        var csharpPropertyCandidate = candidate.CSharpPropertyCandidate;
        var scanInputs = extraction.ScanInputs;
        var cssScannerLines = scanInputs.CssScannerLines;
        var applicablePatterns = extraction.ApplicablePatterns;
        var applyRequiredLiteralMatchInputGate = extraction.ApplyRequiredLiteralMatchInputGate;
        var requiredLiteralGateCounts = extraction.RequiredLiteralGateCounts;
        var symbols = extraction.Symbols;
        var extractionState = extraction.ExtractionState;
        var cssSeenSymbols = extraction.CssSeenSymbols;
        var dockerfileStageNames = extraction.DockerfileStageNames;
        var isCSharpTestMethod = lang == "csharp"
            && scanInputs.CSharpTestMethodAttributedDeclarationLines is { } attributedDeclarationLines
            && IsCSharpTestMethod(
                attributedDeclarationLines,
                i,
                pattern.Kind == "function");
        ref var scanState = ref extraction.ScanState;
        ref var pendingRecordPrimaryComponents = ref extraction.PendingRecordPrimaryComponents;
        ref var recordPrimaryComponentParentIndex = ref extraction.RecordPrimaryComponentParentIndex;
        emittedKind = kind;
        var symbolCountBeforeEmission = symbols.Count;
        kind = EmitPatternSymbols(
            new PatternSymbolEmissionContext(
                fileId,
                lang,
                pattern,
                lines,
                i,
                lineOffset,
                absoluteStartColumn,
                line,
                patternMatchLine,
                match,
                name,
                kind,
                signature,
                rawReturnType,
                pythonSubKind,
                pythonModulePrefix,
                rubyAttrNames,
                new PatternSymbolRange(endLine, bodyStartLine, bodyEndLine),
                signatureResult.Bounds,
                symbols,
                extractionState,
                cssSeenSymbols,
                dockerfileStageNames,
                isCSharpTestMethod));
        if (lang == "csharp"
            && symbols.Count > symbolCountBeforeEmission
            && scanInputs.CSharpTestMethodAttributedDeclarationLines is { } emittedAttributeLines)
        {
            ConsumeCSharpTestAttributePrefix(
                emittedAttributeLines,
                i);
        }

        if (lang == "css"
            && pattern.Kind == "namespace"
            && pattern.BodyStyle == BodyStyle.Brace
            && cssScannerLines != null)
        {
            TryAddCssMediaFeatureSymbols(
                fileId,
                line,
                cssScannerLines[i],
                i,
                symbols,
                cssSeenSymbols);
        }

        if (lang == "css"
            && pattern.Kind == "class"
            && pattern.BodyStyle == BodyStyle.Brace
            && cssScannerLines != null)
        {
            var openingBraceIndex = cssScannerLines[i].IndexOf('{', absoluteStartColumn);
            if (openingBraceIndex > absoluteStartColumn)
            {
                TryAddCssSelectorListSegments(
                    fileId,
                    line[absoluteStartColumn..openingBraceIndex],
                    cssScannerLines[i][absoluteStartColumn..openingBraceIndex],
                    cssScannerLines,
                    i,
                    openingBraceIndex,
                    applicablePatterns,
                    symbols,
                    cssSeenSymbols,
                    applyRequiredLiteralMatchInputGate,
                    requiredLiteralGateCounts);
            }
        }

        if (lang == "csharp"
            && pattern.Kind == "property"
            && csharpPropertyCandidate.ExpressionBodyEndLineIndex.HasValue)
        {
            var expressionEndLineIndex = csharpPropertyCandidate.ExpressionBodyEndLineIndex.Value;
            if (expressionEndLineIndex > i
                && csharpPropertyCandidate.ExpressionBodyEndLineExclusiveEndColumn.HasValue)
            {
                // Suppress complete continuation lines, but resume after the
                // terminating semicolon so a valid same-line sibling remains visible.
                // 完全な continuation 行だけを抑止し、終端 semicolon の後から
                // 再開して有効な same-line sibling を維持する。
                scanState.CSharpSuppressedContinuationUntil = Math.Max(
                    scanState.CSharpSuppressedContinuationUntil,
                    expressionEndLineIndex - 1);
                scanState.CSharpSuppressedContinuationResumeLine = expressionEndLineIndex;
                scanState.CSharpSuppressedContinuationResumeRawColumn =
                    csharpPropertyCandidate.ExpressionBodyEndLineExclusiveEndColumn.Value;
            }
            else
            {
                scanState.CSharpSuppressedContinuationUntil = Math.Max(
                    scanState.CSharpSuppressedContinuationUntil,
                    expressionEndLineIndex);
            }
        }

        if (lang == "csharp"
            && pattern.Kind is "event" or "delegate"
            && pattern.BodyStyle == BodyStyle.None
            && (TryGetCSharpSameLineEventSiblingOffset(patternMatchLine, absoluteStartColumn, out var nextSemicolonSiblingOffset)
                || TryGetCSharpSameLineSemicolonSiblingOffset(patternMatchLine, absoluteStartColumn, out nextSemicolonSiblingOffset)))
        {
            return PatternScanResult.RestartAt(nextSemicolonSiblingOffset);
        }

        if (lang == "java"
            && pattern.BodyStyle == BodyStyle.Brace
            && bodyStartLine == null
            && TryGetJavaSameLineSemicolonSiblingOffset(patternMatchLine, absoluteStartColumn, out var nextJavaSiblingOffset))
        {
            // Body-less Java members inside `interface` / `@interface` / abstract-style
            // declarations can share one physical line (`String[] value(); int age();`).
            // Restart at the next sibling after the top-level `;` instead of stopping at
            // the first match, or later members on the same line disappear. Closes #788.
            // Java の body-less member（`interface` / `@interface` / abstract 形）は
            // `String[] value(); int age();` のように 1 行へ並ぶ。top-level `;`
            // の直後から sibling へ再開しないと、同一行の後続 member が最初の 1 個で
            // 途切れて消える。Closes #788.
            return PatternScanResult.RestartAt(nextJavaSiblingOffset);
        }
        if (lang is "prolog" or "ambiguous_pl"
            && pattern.Kind == "function"
            && TryGetNextPrologClauseOffset(
                patternMatchLine,
                absoluteStartColumn,
                out var nextPrologClauseOffset))
        {
            return PatternScanResult.RestartAt(nextPrologClauseOffset);
        }

        CollectRecordPrimaryComponentSymbols(
            fileId,
            lang,
            lines,
            i,
            absoluteStartColumn,
            kind,
            name,
            ref pendingRecordPrimaryComponents,
            ref recordPrimaryComponentParentIndex,
            symbols);

        emittedKind = kind;
        return PatternScanResult.Accepted;
    }
}
