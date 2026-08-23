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

    // Candidate-local proof cache shared only by the outer C# pattern loop and its
    // recoverable-pattern helper. The count encoding keeps default(struct) empty and prevents
    // an out-of-order miss from claiming an unproven gap in pattern priority.
    // outer loop と recovery helper が同じ物理 input を調べる場合だけ共有する candidate-local
    // proof cache。count 表現により default(struct) を空に保ち、pattern priority 上の未検証 gap を
    // 飛び越えた miss が prefix を広げないようにする。
    internal struct CSharpPhysicalInputNegativePrefixCache
    {
        private int _knownNegativePatternCount;

        internal readonly bool IsKnownNegative(int patternIndex) =>
            patternIndex >= 0 && patternIndex < _knownNegativePatternCount;

        internal void RecordFailedProbe(int patternIndex, bool timedOut)
        {
            if (!timedOut && patternIndex == _knownNegativePatternCount)
                _knownNegativePatternCount++;
        }
    }

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
            CSharpWrappedHeaderModifierLookupCompleted = false;
            CSharpWrappedHeaderModifierInfo = null;
            CSharpPhysicalInputNegativePrefix = new CSharpPhysicalInputNegativePrefixCache();
        }

        public CSharpPropertyMatchCandidate? CSharpPropertyCandidateForLine;
        public bool? DeferCSharpBraceProperty;
        public bool? DeferCSharpFunction;
        public bool? DeferCSharpEvent;
        public bool? DeferCSharpDelegate;
        public bool? RecoverableCSharpPattern;
        public bool CSharpWrappedHeaderModifierLookupCompleted;
        public CSharpWrappedHeaderModifierInfo? CSharpWrappedHeaderModifierInfo;
        public CSharpPhysicalInputNegativePrefixCache CSharpPhysicalInputNegativePrefix;
    }

    private struct PatternCandidateScan
    {
        public PatternCandidateScan(
            PatternLineScanContext lineContext,
            SymbolPattern pattern,
            int patternIndex,
            int patternStartOffset,
            CSharpPropertyMatchCandidate csharpPropertyCandidate,
            string patternMatchLine)
        {
            LineContext = lineContext;
            Pattern = pattern;
            PatternIndex = patternIndex;
            PatternStartOffset = patternStartOffset;
            CSharpPropertyCandidate = csharpPropertyCandidate;
            PatternMatchLine = patternMatchLine;
            CSharpWrappedModifierPrefix = null;
            LineOffset = patternStartOffset;
        }

        public PatternLineScanContext LineContext;
        public SymbolPattern Pattern;
        public int PatternIndex;
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
