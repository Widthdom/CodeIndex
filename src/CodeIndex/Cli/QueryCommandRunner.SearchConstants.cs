namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const int MaxNamedSearchQueryNameLength = 128;
    private const int MaxSearchProjectionFieldsCsvLength = 256;
    private const int MaxSearchProjectionFieldsCsvEntries = 16;
    private const int DefaultSearchGroupedPerFileLimit = 3;
    private const int MaxSearchGroupedPerFileLimit = 20;
    private const int MaxSearchNextStepLimit = 10;
    private const int MaxSearchJsonByteLimit = 16 * 1024 * 1024;
    private const int MaxIssueDraftEvidenceItems = 5;
    private const int MaxIssueDraftEvidenceSnippetLength = 512;
    private const string BareTokenAuthAuditHint = "Bare `token` searches are intentionally broad. For credential/auth-token review, run `cdidx search --recipe auth-token-audit`; use `cdidx search --recipe broad-token-audit` only when parser, LSP, or cancellation token domains are intentional.";
    internal const int MaxIssueDraftLabelCount = 16;
    internal const int MaxIssueDraftTitleLength = GitHubIssueReporter.MaxGitHubIssueTitleLength;
    internal const int MaxSearchRecipeQuerySelectorCount = 64;
    internal const int MaxSearchRecipeQuerySelectorLength = 128;
    internal const int MaxQueryPathFilterCount = 128;
    internal const int MaxQueryPathFilterLength = 1024;
    internal const int ExactZeroHintProbeLimit = 1;
    internal const int ExactZeroHintSampleLimit = 5;
    private const int SearchOriginFilterMinCandidates = 200;
    private const int SearchOriginFilterOverFetchFactor = 50;
    internal const int MaxQueryResultLimit = 10_000;
    private const int SearchOriginFilterMaxCandidates = MaxQueryResultLimit;
    private const int SearchOriginFilterMaxPages = 50;
    private const int SearchEnvelopeMinCandidates = 200;
    private const int SearchEnvelopeOverFetchFactor = 50;
    private const int SearchEnvelopeMaxCandidates = MaxQueryResultLimit;
    private const string SearchFilterNoMatchSentinel = "\0__cdidx_no_match__";
}
