using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private enum PatternScanFlow
    {
        Accepted,
        ContinueCurrentPattern,
        NextPattern,
        RestartPatternList,
        StopLine,
    }

    private readonly record struct PatternScanResult(
        PatternScanFlow Flow,
        int NextOffset = -1)
    {
        public static PatternScanResult Accepted => new(PatternScanFlow.Accepted);
        public static PatternScanResult NextPattern => new(PatternScanFlow.NextPattern);
        public static PatternScanResult StopLine => new(PatternScanFlow.StopLine);

        public static PatternScanResult ContinueAt(int nextOffset) =>
            new(PatternScanFlow.ContinueCurrentPattern, nextOffset);

        public static PatternScanResult RestartAt(int nextOffset) =>
            new(PatternScanFlow.RestartPatternList, nextOffset);
    }

    private readonly record struct PatternLineScanContext(
        PatternExtractionContext Extraction,
        int LineIndex,
        PreparedPatternLine PreparedLine);

    private struct PatternStartScanState
    {
        public PatternStartScanState()
        {
            CSharpPropertyCandidateForLine = null;
            DeferCSharpBraceProperty = null;
            DeferCSharpFunction = null;
            DeferCSharpEvent = null;
            DeferCSharpDelegate = null;
            RecoverableCSharpPattern = null;
        }

        public CSharpPropertyMatchCandidate? CSharpPropertyCandidateForLine;
        public bool? DeferCSharpBraceProperty;
        public bool? DeferCSharpFunction;
        public bool? DeferCSharpEvent;
        public bool? DeferCSharpDelegate;
        public bool? RecoverableCSharpPattern;
    }

    private struct PatternCandidateScan
    {
        public PatternCandidateScan(
            PatternLineScanContext lineContext,
            SymbolPattern pattern,
            int patternStartOffset,
            CSharpPropertyMatchCandidate csharpPropertyCandidate,
            string patternMatchLine)
        {
            LineContext = lineContext;
            Pattern = pattern;
            PatternStartOffset = patternStartOffset;
            CSharpPropertyCandidate = csharpPropertyCandidate;
            PatternMatchLine = patternMatchLine;
            CSharpWrappedModifierPrefix = null;
            LineOffset = patternStartOffset;
        }

        public PatternLineScanContext LineContext;
        public SymbolPattern Pattern;
        public int PatternStartOffset;
        public CSharpPropertyMatchCandidate CSharpPropertyCandidate;
        public string PatternMatchLine;
        public string? CSharpWrappedModifierPrefix;
        public int LineOffset;
    }

    private readonly record struct CapturedPatternMatch(
        Match Match,
        int AbsoluteStartColumn);

    private readonly record struct MatchedPatternCandidateContext(
        PatternLineScanContext LineContext,
        SymbolPattern Pattern,
        int LineOffset,
        CSharpPropertyMatchCandidate CSharpPropertyCandidate,
        string PatternMatchLine,
        string? CSharpWrappedModifierPrefix,
        CapturedPatternMatch CapturedMatch);

    private readonly record struct PatternCandidateColumns(
        int CSharpGateRawStartColumn);

    private readonly record struct PatternSymbolDraft(
        string Name,
        string? RawReturnType,
        List<string>? RubyAttributeNames,
        int StartLine,
        int EndLine,
        int? BodyStartLine,
        int? BodyEndLine);

    private readonly record struct ShapedPatternSymbol(
        string Name,
        string Kind,
        string? RawReturnType,
        string? PythonSubKind,
        List<string>? RubyAttributeNames,
        int StartLine,
        int EndLine,
        int? BodyStartLine,
        int? BodyEndLine,
        PatternSignatureResult SignatureResult);
}
