using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ScanPatternLines(PatternExtractionContext context)
    {
        var fileId = context.FileId;
        var lang = context.Lang;
        var filePath = context.FilePath;
        var projectRoot = context.ProjectRoot;
        var cancellationToken = context.CancellationToken;
        var lines = context.Lines;
        var scanInputs = context.ScanInputs;
        var symbols = context.Symbols;
        var extractionState = context.ExtractionState;
        ref var scanState = ref context.ScanState;
        var dockerfileStageNames = context.DockerfileStageNames;
        for (int i = 0; i < lines.Length; i++)
        {
            if (symbols.IsAtCapacity)
                break;

            if ((i & 0x3f) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            if (!TryPreparePatternLine(
                    fileId,
                    lang,
                    filePath,
                    projectRoot,
                    lines,
                    scanInputs,
                    ref scanState,
                    symbols,
                    extractionState,
                    dockerfileStageNames,
                    i,
                    out var preparedLine))
            {
                continue;
            }

            ScanPreparedPatternLine(context, i, preparedLine);
            EmitCssLineSupplementals(context, i, preparedLine);
        }
    }

    private static void ScanPreparedPatternLine(
        PatternExtractionContext context,
        int i,
        PreparedPatternLine preparedLine)
    {
        var lineContext = new PatternLineScanContext(context, i, preparedLine);
        var patternStartOffset = preparedLine.PatternStartOffset;
        while (patternStartOffset >= 0
            && patternStartOffset < preparedLine.MatchLine.Length)
        {
            var result = ScanPatternListAtOffset(lineContext, patternStartOffset);
            if (result.Flow != PatternScanFlow.RestartPatternList
                || result.NextOffset <= patternStartOffset)
            {
                break;
            }

            patternStartOffset = result.NextOffset;
        }
    }

    private static PatternScanResult ScanPatternListAtOffset(
        PatternLineScanContext lineContext,
        int patternStartOffset)
    {
        var extraction = lineContext.Extraction;
        var patternStartState = new PatternStartScanState();
        foreach (var pattern in extraction.ApplicablePatterns)
        {
            if (!IsPatternApplicableToLine(lineContext, pattern))
                continue;

            var patternScan = CreatePatternCandidateScan(
                lineContext,
                pattern,
                patternStartOffset,
                ref patternStartState);
            var result = ScanApplicablePattern(
                ref patternScan,
                ref patternStartState);
            if (result.Flow is PatternScanFlow.RestartPatternList
                or PatternScanFlow.StopLine)
            {
                return result;
            }
        }

        return PatternScanResult.NextPattern;
    }

    private static bool IsPatternApplicableToLine(
        PatternLineScanContext lineContext,
        SymbolPattern pattern)
    {
        var extraction = lineContext.Extraction;
        var i = lineContext.LineIndex;
        var scanInputs = extraction.ScanInputs;
        if (scanInputs.PrologClauseContinuationLines?[i] == true
            && lineContext.PreparedLine.PrologContinuationResumeOffset < 0
            && pattern.Kind == "function")
        {
            return false;
        }

        if (extraction.Lang == "csharp"
            && ReferenceEquals(pattern.Regex, CSharpEnumMemberRegex))
        {
            return false;
        }

        return extraction.Lang != "powershell"
            || pattern.Kind != "enum"
            || pattern.BodyStyle != BodyStyle.None
            || scanInputs.PowershellEnumBodyLines![i];
    }

    private static PatternCandidateScan CreatePatternCandidateScan(
        PatternLineScanContext lineContext,
        SymbolPattern pattern,
        int patternStartOffset,
        ref PatternStartScanState patternStartState)
    {
        var extraction = lineContext.Extraction;
        var lines = extraction.Lines;
        var i = lineContext.LineIndex;
        var csharpMatchLines = extraction.ScanInputs.CSharpMatchLines;

        // Merge multi-line field headers for C# regardless of kind. Kind "property" (plain
        // fields) and kind "function" (const / static readonly fields) both need the
        // merge. Non-field function patterns (methods, constructors, operators, indexers)
        // are unaffected because CSharpPropertyHeaderPrefixRegex requires the line to end
        // before `(` or `{`, so lines like `public int Foo()` never satisfy the header
        // prefix and the merger returns the original line. Closes #355.
        // C# の複数行フィールドヘッダ結合は kind に依らず適用する。kind "property"（通常
        // フィールド）と kind "function"（`const` / `static readonly` フィールド）の両方で
        // 結合が必要。method / constructor / operator / indexer のような非フィールド
        // function パターンは `CSharpPropertyHeaderPrefixRegex` が `(` や `{` を含む行を
        // 受け付けないため影響を受けず、merger は元の行をそのまま返す。Closes #355.
        var csharpPropertyCandidate = extraction.Lang == "csharp"
            && pattern.Kind is "property" or "function"
                ? patternStartState.CSharpPropertyCandidateForLine ??=
                    BuildCSharpPropertyMatchLine(lines, csharpMatchLines!, i)
                : new CSharpPropertyMatchCandidate(
                    lineContext.PreparedLine.MatchLine,
                    i,
                    i);
        var patternMatchLine = lineContext.PreparedLine.FortranContinuationCandidate?.MatchLine
            ?? csharpPropertyCandidate.MatchLine;
        return new PatternCandidateScan(
            lineContext,
            pattern,
            patternStartOffset,
            csharpPropertyCandidate,
            patternMatchLine);
    }

    private static PatternScanResult ScanApplicablePattern(
        ref PatternCandidateScan patternScan,
        ref PatternStartScanState patternStartState)
    {
        while (patternScan.LineOffset >= 0
            && patternScan.LineOffset < patternScan.PatternMatchLine.Length)
        {
            var captureResult = TryCapturePatternMatch(
                patternScan.LineContext,
                patternScan.Pattern,
                patternScan.PatternStartOffset,
                patternScan.LineOffset,
                ref patternScan.PatternMatchLine,
                ref patternScan.CSharpWrappedModifierPrefix,
                ref patternStartState,
                out var capturedMatch);
            if (captureResult.Flow == PatternScanFlow.ContinueCurrentPattern)
            {
                patternScan.LineOffset = captureResult.NextOffset;
                continue;
            }
            if (captureResult.Flow != PatternScanFlow.Accepted)
                return captureResult;

            var candidate = new MatchedPatternCandidateContext(
                patternScan.LineContext,
                patternScan.Pattern,
                patternScan.LineOffset,
                patternScan.CSharpPropertyCandidate,
                patternScan.PatternMatchLine,
                patternScan.CSharpWrappedModifierPrefix,
                capturedMatch);
            var result = EvaluateCandidateSyntaxGates(
                candidate,
                out var candidateColumns);
            if (result.Flow == PatternScanFlow.ContinueCurrentPattern)
            {
                patternScan.LineOffset = result.NextOffset;
                continue;
            }
            if (result.Flow != PatternScanFlow.Accepted)
                return result;

            result = EvaluateCandidateScopeAndTypeGates(
                candidate,
                candidateColumns,
                out var rawReturnType);
            if (result.Flow == PatternScanFlow.ContinueCurrentPattern)
            {
                patternScan.LineOffset = result.NextOffset;
                continue;
            }
            if (result.Flow != PatternScanFlow.Accepted)
                return result;

            var draft = BuildPatternSymbolDraft(
                candidate,
                candidateColumns,
                rawReturnType);
            result = TryShapePatternSymbol(
                candidate,
                candidateColumns,
                draft,
                out var shapedSymbol);
            if (result.Flow == PatternScanFlow.ContinueCurrentPattern)
            {
                patternScan.LineOffset = result.NextOffset;
                continue;
            }
            if (result.Flow != PatternScanFlow.Accepted)
                return result;

            result = EmitAcceptedPatternSymbol(
                candidate,
                shapedSymbol,
                out var emittedKind);
            if (result.Flow != PatternScanFlow.Accepted)
                return result;

            result = ResolveCSharpFieldProgression(
                candidate);
            if (result.Flow == PatternScanFlow.Accepted)
            {
                result = ResolveTerminalPatternProgression(
                    candidate,
                    shapedSymbol,
                    emittedKind);
            }
            if (result.Flow == PatternScanFlow.Accepted)
            {
                result = ResolveBraceBodyPatternProgression(
                    candidate,
                    shapedSymbol,
                    emittedKind);
            }

            if (result.Flow == PatternScanFlow.ContinueCurrentPattern)
            {
                patternScan.LineOffset = result.NextOffset;
                continue;
            }

            return result;
        }

        return PatternScanResult.NextPattern;
    }

    private static void EmitCssLineSupplementals(
        PatternExtractionContext context,
        int i,
        PreparedPatternLine preparedLine)
    {
        var lang = context.Lang;
        var fileId = context.FileId;
        var line = preparedLine.SourceLine;
        var cssScannerLine = preparedLine.CssScannerLine;
        var cssScannerLines = context.ScanInputs.CssScannerLines;
        var applicablePatterns = context.ApplicablePatterns;
        var symbols = context.Symbols;
        var extractionState = context.ExtractionState;
        var cssSeenSymbols = context.CssSeenSymbols;
        if (lang == "css" && cssScannerLine != null)
        {
            if (cssScannerLine.IndexOf("--", StringComparison.Ordinal) >= 0)
            {
                foreach (Match match in Regex.EnumerateMatches(CssInlineCustomPropertyRegex, cssScannerLine))
                {
                    var propertyName = match.Groups["name"].ValueSpan.Trim().ToString();
                    if (propertyName.Length == 0)
                        continue;

                    AddSymbolRecord(
                        symbols,
                        extractionState,
                        cssSeenSymbols,
                        i + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "property",
                            Name = propertyName,
                            Line = i + 1,
                            StartLine = i + 1,
                            EndLine = i + 1,
                            Signature = line.Trim(),
                        });
                }
            }

            ExtractCssInlineGroupingSelectors(
                fileId,
                line,
                cssScannerLine,
                cssScannerLines!,
                i,
                applicablePatterns,
                symbols,
                cssSeenSymbols);
        }
    }
}
