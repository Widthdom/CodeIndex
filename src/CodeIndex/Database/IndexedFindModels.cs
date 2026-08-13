namespace CodeIndex.Database;

public partial class DbReader
{
    private readonly record struct FindResumePosition(
        string? Path,
        int? Line,
        int? FileOrdinal,
        int? MatchOrdinal,
        int? ByteOffset);

    private sealed record IndexedFindScanRequest(
        string Query,
        string? Lang,
        IReadOnlyList<string>? PathPatterns,
        IReadOnlyList<string>? ExcludePathPatterns,
        bool ExcludeTests,
        bool Exact,
        int? FocusLine,
        int? FocusColumn,
        bool Regex,
        int? MaxCandidateFiles,
        int? MaxLinesScanned,
        bool UseIndexedLiteralCandidates,
        FindResumePosition Resume,
        CancellationToken CancellationToken);

    private sealed record IndexedFindListRequest(
        IndexedFindScanRequest Scan,
        int Limit,
        int Before,
        int After,
        int MaxLineWidth,
        int Offset,
        bool CaptureContinuation);

    private interface IFindScanCollector
    {
        FindScanMode Mode { get; }
        int ContextBefore { get; }
        bool NotifyLineScanned { get; }
        bool ShouldStopBeforeCandidate { get; }
        bool ShouldStopAfterFile { get; }
        void BeginFile(FindCandidateFile file);
        void ObserveLine(IndexedLine line);
        bool ShouldEvaluateMatches(IndexedLine line, bool eligibleForMatch);
        bool AcceptMatch(
            FindCandidateFile file,
            IndexedLine line,
            FindLineMatch match,
            int matchOrdinal);
        void CompleteLine(FindCandidateFile file, IndexedLine line);
        bool CanStopAfterLine(bool stopScanning);
        void CompleteFile(FindCandidateFile file);
        void ValidateCompletedScan();
    }

    private enum FindScanMode
    {
        Results,
        Count,
    }

    private readonly record struct FindSearchPlan(
        string Strategy,
        string? FallbackReason,
        string? TrigramMatchExpression);

    private readonly record struct FindCandidateFile(
        long Id,
        string Path,
        string? Lang,
        int TotalLines,
        int Ordinal,
        int FirstEligibleLine);

    private readonly record struct IndexedLine(int Number, string Text);

    private readonly record struct FindLineMatch(int Column, int Length);

    private readonly record struct PendingFileFindMatch(
        int LineNumber,
        int Column,
        int Length,
        int SnippetStart,
        int SnippetEnd);

    private sealed class FindScanState(FindResumePosition resume)
    {
        internal int CandidateFiles { get; set; }
        internal int FilesScanned { get; set; }
        internal int LinesScanned { get; set; }
        internal int CandidateFileOrdinal { get; set; } = -1;
        internal bool ResumePending { get; set; } = resume.Path is not null;
        internal bool Truncated { get; private set; }
        internal string? TruncationReason { get; private set; }
        internal string? NextPath { get; private set; }
        internal int? NextLine { get; private set; }
        internal int? NextFileOrdinal { get; private set; }
        internal int? NextMatchOrdinal { get; private set; }
        internal int? NextByteOffset { get; private set; }
        internal bool ResultLimitReached { get; private set; }

        internal void Truncate(
            string reason,
            string path,
            int nextLine,
            int fileOrdinal)
        {
            Truncated = true;
            TruncationReason ??= reason;
            NextPath = path;
            NextLine = nextLine;
            NextFileOrdinal = fileOrdinal;
            NextByteOffset = 0;
        }

        internal void TruncateIfUnset(
            string reason,
            string path,
            int nextLine,
            int fileOrdinal)
        {
            Truncated = true;
            TruncationReason ??= reason;
            if (NextPath != null)
                return;
            NextPath = path;
            NextLine = nextLine;
            NextFileOrdinal = fileOrdinal;
            NextByteOffset = 0;
        }

        internal void SetResultLimit(
            string path,
            int line,
            int fileOrdinal,
            int matchOrdinal,
            int byteOffset)
        {
            NextPath = path;
            NextLine = line;
            NextFileOrdinal = fileOrdinal;
            NextMatchOrdinal = matchOrdinal;
            NextByteOffset = byteOffset;
            ResultLimitReached = true;
        }

        internal FindScanSummary CreateSummary(
            FindSearchPlan searchPlan,
            int? candidateFileLimit,
            int? lineLimit)
            => new(
                CandidateFiles,
                FilesScanned,
                LinesScanned,
                Truncated,
                CapReached: Truncated,
                TimedOut: false,
                TruncationReason,
                candidateFileLimit,
                lineLimit,
                searchPlan.Strategy,
                searchPlan.FallbackReason,
                NextPath,
                NextLine,
                NextFileOrdinal,
                NextMatchOrdinal,
                NextByteOffset,
                ResultLimitReached);
    }
}
