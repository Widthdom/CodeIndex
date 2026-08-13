using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed class IndexedFindPipeline(DbReader owner)
    {
        private readonly DbReader _owner = owner;
        private readonly IndexedFindQuerySource _querySource = new(owner);

        internal FindResults Find(IndexedFindListRequest request)
        {
            var scan = request.Scan;
            scan.CancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(scan.Query) || request.Limit <= 0)
                return new FindResults([], new FindScanSummary(0, 0, 0));
            ValidateContinuation(scan.Resume);

            var normalizedRequest = request with
            {
                Before = Math.Max(0, request.Before),
                After = Math.Max(0, request.After),
                MaxLineWidth = LineWidthFormatter.ClampMaxLineWidth(request.MaxLineWidth),
                Offset = Math.Max(0, request.Offset),
            };
            var state = new FindScanState(scan.Resume);
            var collector = new FindResultCollector(normalizedRequest, state);
            var searchPlan = ScanFiles(scan, collector, state);
            ValidateCompletedScan(collector.Mode, state, collector);
            return collector.CreateResult(searchPlan);
        }

        internal FindCountResult Count(IndexedFindScanRequest request)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(request.Query))
                return new FindCountResult(0, 0, new FindScanSummary(0, 0, 0));
            ValidateContinuation(request.Resume);
            if (request.Resume.MatchOrdinal.HasValue || request.Resume.ByteOffset is not (null or 0))
            {
                throw new FindContinuationException(
                    "cursor_malformed",
                    "find count cursor must resume at a line boundary.");
            }

            var state = new FindScanState(request.Resume);
            var collector = new FindCountCollector(request, state);
            var searchPlan = ScanFiles(request, collector, state);
            ValidateCompletedScan(collector.Mode, state, collector);
            return collector.CreateResult(searchPlan);
        }

        private FindSearchPlan ScanFiles(
            IndexedFindScanRequest request,
            IFindScanCollector collector,
            FindScanState state)
        {
            var comparison = request.Exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var regexMatcher = request.Regex
                ? CreateFindRegexMatcher(request.Query, request.Exact)
                : null;
            var searchPlan = _querySource.CreateSearchPlan(request);
            using var fileCommand = _querySource.CreateFileCommand(searchPlan, request);
            state.CandidateFiles = _owner.CountFindCandidateFiles(
                request.Lang,
                request.PathPatterns,
                request.ExcludePathPatterns,
                request.ExcludeTests);

            using var fileReader = fileCommand.ExecuteTrackedReader();
            while (fileReader.TrackedRead())
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                if (collector.ShouldStopBeforeCandidate)
                    break;

                state.CandidateFileOrdinal++;
                var file = ReadCandidateFile(fileReader, state.CandidateFileOrdinal);
                if (!TryReachResumeFile(request.Resume, collector.Mode, file, state))
                    continue;
                if (ReachedCandidateFileLimit(request, file, state))
                    break;

                var firstEligibleLine = string.Equals(file.Path, request.Resume.Path, StringComparison.Ordinal)
                    ? Math.Max(1, request.Resume.Line ?? 1)
                    : 1;
                file = file with { FirstEligibleLine = firstEligibleLine };
                state.FilesScanned++;
                if (file.TotalLines <= 0)
                    continue;

                collector.BeginFile(file);
                var searchQuery = request.Exact && !request.Regex
                    ? ExactSourceSearchNormalizer.Normalize(request.Query, file.Lang)
                    : request.Query;
                var stopScanning = ScanFileLines(
                    request,
                    collector,
                    state,
                    file,
                    searchQuery,
                    comparison,
                    regexMatcher);
                collector.CompleteFile(file);
                if (stopScanning || collector.ShouldStopAfterFile)
                    break;
            }

            return searchPlan;
        }

        private bool ScanFileLines(
            IndexedFindScanRequest request,
            IFindScanCollector collector,
            FindScanState state,
            FindCandidateFile file,
            string searchQuery,
            StringComparison comparison,
            Regex? regexMatcher)
        {
            var firstContextLine = Math.Max(1, file.FirstEligibleLine - collector.ContextBefore);
            var stopScanning = false;
            foreach (var indexedLine in _querySource.EnumerateIndexedFileLines(file.Id))
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                if (indexedLine.Number > file.TotalLines)
                    break;
                if (indexedLine.Number < firstContextLine)
                    continue;

                var eligibleForMatch = indexedLine.Number >= file.FirstEligibleLine;
                if (ReachedLineScanLimit(request, file, indexedLine, state))
                {
                    stopScanning = true;
                    break;
                }
                RecordScannedLine(request, collector, state, eligibleForMatch);
                collector.ObserveLine(indexedLine);

                if (collector.ShouldEvaluateMatches(indexedLine, eligibleForMatch))
                {
                    var matchOrdinal = 0;
                    foreach (var lineMatch in IndexedFindQuerySource.EnumerateLineMatches(
                        indexedLine.Text,
                        file.Lang,
                        searchQuery,
                        comparison,
                        regexMatcher,
                        request.Exact && !request.Regex,
                        request.FocusColumn))
                    {
                        if (collector.AcceptMatch(file, indexedLine, lineMatch, matchOrdinal))
                        {
                            stopScanning = true;
                            break;
                        }
                        matchOrdinal++;
                    }
                }

                collector.CompleteLine(file, indexedLine);
                if (collector.CanStopAfterLine(stopScanning))
                    break;
            }
            return stopScanning;
        }

        private static void RecordScannedLine(
            IndexedFindScanRequest request,
            IFindScanCollector collector,
            FindScanState state,
            bool eligibleForMatch)
        {
            if (!eligibleForMatch)
                return;

            state.LinesScanned++;
            if (!collector.NotifyLineScanned)
                return;
            FindLineScannedForTesting?.Invoke();
            request.CancellationToken.ThrowIfCancellationRequested();
        }

        private static FindCandidateFile ReadCandidateFile(SqliteDataReader reader, int ordinal)
            => new(
                reader.GetInt64(0),
                reader.GetString(1),
                GetNullableString(reader, 2),
                reader.GetInt32(3),
                ordinal,
                FirstEligibleLine: 1);

        private static bool TryReachResumeFile(
            FindResumePosition resume,
            FindScanMode mode,
            FindCandidateFile file,
            FindScanState state)
        {
            if (!state.ResumePending)
                return true;

            if (resume.FileOrdinal.HasValue)
            {
                if (file.Ordinal < resume.FileOrdinal.Value)
                    return false;
                if (file.Ordinal != resume.FileOrdinal.Value
                    || !string.Equals(file.Path, resume.Path, StringComparison.Ordinal))
                {
                    throw new FindContinuationException(
                        "cursor_malformed",
                        mode == FindScanMode.Count
                            ? "find count cursor file position does not match the current candidate order."
                            : "find cursor file position does not match the current candidate order.");
                }
            }
            else if (!string.Equals(file.Path, resume.Path, StringComparison.Ordinal))
            {
                return false;
            }

            if (resume.Line > Math.Max(1, file.TotalLines))
            {
                throw new FindContinuationException(
                    "cursor_malformed",
                    mode == FindScanMode.Count
                        ? "find count cursor line position exceeds the selected file."
                        : "find cursor line position exceeds the selected file.");
            }
            state.ResumePending = false;
            return true;
        }

        private static bool ReachedCandidateFileLimit(
            IndexedFindScanRequest request,
            FindCandidateFile file,
            FindScanState state)
        {
            if (!request.MaxCandidateFiles.HasValue
                || state.FilesScanned < request.MaxCandidateFiles.Value)
            {
                return false;
            }

            state.Truncate(
                "candidate_file_limit",
                file.Path,
                nextLine: 1,
                file.Ordinal);
            return true;
        }

        private static bool ReachedLineScanLimit(
            IndexedFindScanRequest request,
            FindCandidateFile file,
            IndexedLine indexedLine,
            FindScanState state)
        {
            if (!request.MaxLinesScanned.HasValue
                || state.LinesScanned < request.MaxLinesScanned.Value)
            {
                return false;
            }

            state.TruncateIfUnset(
                "line_scan_limit",
                file.Path,
                indexedLine.Number,
                file.Ordinal);
            return true;
        }

        private static void ValidateContinuation(FindResumePosition resume)
        {
            if (resume.Path is null)
            {
                if (resume.Line.HasValue
                    || resume.FileOrdinal.HasValue
                    || resume.MatchOrdinal.HasValue
                    || resume.ByteOffset.HasValue)
                {
                    throw new FindContinuationException(
                        "cursor_malformed",
                        "find cursor continuation fields require a resume path.");
                }
                return;
            }

            if (!resume.Line.HasValue
                || resume.Line.Value <= 0
                || resume.FileOrdinal is < 0
                || resume.MatchOrdinal is < 0
                || resume.ByteOffset is < 0
                || resume.MatchOrdinal.HasValue && !resume.ByteOffset.HasValue
                || !resume.MatchOrdinal.HasValue && resume.ByteOffset is not (null or 0))
            {
                throw new FindContinuationException(
                    "cursor_malformed",
                    "find cursor continuation position is invalid.");
            }
        }

        private static void ValidateCompletedScan(
            FindScanMode mode,
            FindScanState state,
            IFindScanCollector collector)
        {
            if (state.ResumePending)
            {
                throw new FindContinuationException(
                    "cursor_malformed",
                    mode == FindScanMode.Count
                        ? "find count cursor position does not exist in the current candidate sequence."
                        : "find cursor position does not exist in the current result sequence.");
            }
            collector.ValidateCompletedScan();
        }
    }
}
