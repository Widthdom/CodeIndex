namespace CodeIndex.Database;

internal sealed class SearchGuardCandidateLimitException : Exception
{
    public SearchGuardCandidateLimitException(
        int candidateLimit,
        int requestedLimit,
        int requestedOffset,
        IReadOnlyList<string>? candidatePreviewPaths = null,
        IReadOnlyList<string>? candidatePreviewLanguages = null)
        : base($"guarded search inspected the maximum {candidateLimit} candidate chunks before satisfying the requested page (limit {requestedLimit}, offset {requestedOffset}).")
    {
        CandidateLimit = candidateLimit;
        RequestedLimit = requestedLimit;
        RequestedOffset = requestedOffset;
        CandidatePreviewPaths = candidatePreviewPaths ?? [];
        CandidatePreviewLanguages = candidatePreviewLanguages ?? [];
    }

    public int CandidateLimit { get; }
    public int RequestedLimit { get; }
    public int RequestedOffset { get; }
    public IReadOnlyList<string> CandidatePreviewPaths { get; }
    public IReadOnlyList<string> CandidatePreviewLanguages { get; }
}
