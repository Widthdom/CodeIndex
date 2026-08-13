using System.Text;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class FindResultCollector(
        IndexedFindListRequest request,
        FindScanState state) : IFindScanCollector
    {
        private readonly List<FileFindResult> _results = [];
        private readonly Queue<PendingFileFindMatch> _pendingMatches = [];
        private readonly Queue<IndexedLine> _snippetWindow = [];
        private readonly Dictionary<int, string> _snippetLinesByNumber = [];
        private int _acceptedMatches;
        private int _matchesSkipped;
        private bool _resumeMatchPending = request.Scan.Resume.MatchOrdinal.HasValue;

        public FindScanMode Mode => FindScanMode.Results;
        public int ContextBefore => request.Before;
        public bool NotifyLineScanned => true;
        public bool ShouldStopBeforeCandidate
            => !request.CaptureContinuation && _results.Count >= request.Limit;
        public bool ShouldStopAfterFile
            => state.Truncated
               || state.ResultLimitReached
               || !request.CaptureContinuation && _results.Count >= request.Limit;

        public void BeginFile(FindCandidateFile file)
        {
            _pendingMatches.Clear();
            _snippetWindow.Clear();
            _snippetLinesByNumber.Clear();
            _acceptedMatches = _results.Count;
        }

        public void ObserveLine(IndexedLine line)
        {
            _snippetWindow.Enqueue(line);
            _snippetLinesByNumber[line.Number] = line.Text;
        }

        public bool ShouldEvaluateMatches(IndexedLine line, bool eligibleForMatch)
            => !state.ResultLimitReached
               && (_matchesSkipped < request.Offset
                   || request.CaptureContinuation && _acceptedMatches <= request.Limit
                   || !request.CaptureContinuation && _acceptedMatches < request.Limit)
               && eligibleForMatch
               && (!request.Scan.FocusLine.HasValue
                   || line.Number == request.Scan.FocusLine.Value);

        public bool AcceptMatch(
            FindCandidateFile file,
            IndexedLine line,
            FindLineMatch match,
            int matchOrdinal)
        {
            if (_resumeMatchPending && line.Number == file.FirstEligibleLine)
            {
                if (matchOrdinal < request.Scan.Resume.MatchOrdinal!.Value)
                    return false;
                var boundaryByteOffset = Encoding.UTF8.GetByteCount(
                    line.Text.AsSpan(0, match.Column));
                if (matchOrdinal != request.Scan.Resume.MatchOrdinal.Value
                    || boundaryByteOffset != request.Scan.Resume.ByteOffset)
                {
                    throw new FindContinuationException(
                        "cursor_malformed",
                        "find cursor match position is not a record boundary for the current query.");
                }
                _resumeMatchPending = false;
            }

            if (_matchesSkipped < request.Offset)
            {
                _matchesSkipped++;
                return false;
            }
            if (_acceptedMatches >= request.Limit)
            {
                state.SetResultLimit(
                    file.Path,
                    line.Number,
                    file.Ordinal,
                    matchOrdinal,
                    Encoding.UTF8.GetByteCount(line.Text.AsSpan(0, match.Column)));
                return true;
            }

            _pendingMatches.Enqueue(new PendingFileFindMatch(
                line.Number,
                match.Column,
                match.Length,
                Math.Max(1, line.Number - request.Before),
                Math.Min(file.TotalLines, line.Number + request.After)));
            _acceptedMatches++;
            return false;
        }

        public void CompleteLine(FindCandidateFile file, IndexedLine line)
        {
            FindSnippetAssembler.FlushReadyMatches(
                file,
                _pendingMatches,
                _snippetLinesByNumber,
                _results,
                request.MaxLineWidth,
                line.Number);
            FindSnippetAssembler.PruneWindow(
                line.Number,
                request.Before,
                _pendingMatches,
                _snippetWindow,
                _snippetLinesByNumber);
        }

        public bool CanStopAfterLine(bool stopScanning)
            => (stopScanning
                || !request.CaptureContinuation && _results.Count >= request.Limit)
               && _pendingMatches.Count == 0;

        public void CompleteFile(FindCandidateFile file)
            => FindSnippetAssembler.FlushReadyMatches(
                file,
                _pendingMatches,
                _snippetLinesByNumber,
                _results,
                request.MaxLineWidth,
                int.MaxValue);

        public void ValidateCompletedScan()
        {
            if (_resumeMatchPending)
            {
                throw new FindContinuationException(
                    "cursor_malformed",
                    "find cursor position does not exist in the current result sequence.");
            }
        }

        internal FindResults CreateResult(FindSearchPlan searchPlan)
            => new(
                _results,
                state.CreateSummary(
                    searchPlan,
                    request.Scan.MaxCandidateFiles,
                    request.Scan.MaxLinesScanned));
    }

    private sealed class FindCountCollector(
        IndexedFindScanRequest request,
        FindScanState state) : IFindScanCollector
    {
        private int _fileMatches;
        private int _count;
        private int _fileCount;

        public FindScanMode Mode => FindScanMode.Count;
        public int ContextBefore => 0;
        public bool NotifyLineScanned => false;
        public bool ShouldStopBeforeCandidate => false;
        public bool ShouldStopAfterFile => false;

        public void BeginFile(FindCandidateFile file)
            => _fileMatches = 0;

        public void ObserveLine(IndexedLine line)
        {
        }

        public bool ShouldEvaluateMatches(IndexedLine line, bool eligibleForMatch)
            => eligibleForMatch
               && (!request.FocusLine.HasValue || line.Number == request.FocusLine.Value);

        public bool AcceptMatch(
            FindCandidateFile file,
            IndexedLine line,
            FindLineMatch match,
            int matchOrdinal)
        {
            _fileMatches++;
            return false;
        }

        public void CompleteLine(FindCandidateFile file, IndexedLine line)
        {
        }

        public bool CanStopAfterLine(bool stopScanning)
            => stopScanning;

        public void CompleteFile(FindCandidateFile file)
        {
            if (_fileMatches <= 0)
                return;
            _count += _fileMatches;
            _fileCount++;
        }

        public void ValidateCompletedScan()
        {
        }

        internal FindCountResult CreateResult(FindSearchPlan searchPlan)
            => new(
                _count,
                _fileCount,
                state.CreateSummary(
                    searchPlan,
                    request.MaxCandidateFiles,
                    request.MaxLinesScanned));
    }
}
