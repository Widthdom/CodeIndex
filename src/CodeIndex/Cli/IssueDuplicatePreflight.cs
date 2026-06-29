using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

internal sealed class IssueDuplicatePreflight
{
    internal const int MaxOpenIssuesJsonBytes = 8 * 1024 * 1024;
    internal const int MaxOpenIssuesJsonDepth = 32;
    internal const int MaxOpenIssueCount = 1000;
    internal const int MaxLabelsPerOpenIssue = 32;
    internal const int MaxOpenIssueTitleLength = GitHubIssueReporter.MaxGitHubIssueTitleLength;
    internal const int MaxOpenIssueUrlLength = 2048;
    internal const int MaxOpenIssueLabelLength = 128;
    internal const int MaxRepositoryLabelCount = 1000;
    internal const int MaxOpenIssueNumberLength = 32;
    internal const int MaxTitleTokenizationInputLength = MaxOpenIssueTitleLength;
    internal const int MaxOpenIssueBodyLength = 24 * 1024;
    internal const int MaxBodyTokenizationInputLength = 4096;
    internal const int MaxGitHubRepositoryLength = 200;
    internal const double LowDuplicateThreshold = 0.35;
    internal const double DefaultDuplicateThreshold = 0.45;
    internal const double HighDuplicateThreshold = 0.7;
    internal const double TitleLabelSimilarityThreshold = DefaultDuplicateThreshold;
    internal const double EvidencePathSimilarityThreshold = 0.34;
    internal const double BodyLabelSimilarityThreshold = 0.35;
    internal const string LowDuplicateConfidence = "low";
    internal const string DefaultDuplicateConfidence = "medium";
    internal const string HighDuplicateConfidence = "high";
    internal const string CustomDuplicateConfidence = "custom";
    private const int GitHubOpenIssuesPerPage = 100;
    private const int MaxGitHubOpenIssuePages = (MaxOpenIssueCount / GitHubOpenIssuesPerPage) + 1;
    private const int GitHubLabelsPerPage = 100;
    private const int MaxGitHubLabelPages = (MaxRepositoryLabelCount / GitHubLabelsPerPage) + 1;
    private const string GitHubSourceName = "github";
    private const string GitHubSourcePrefix = "github:";
    private const string GitHubTokenEnvironmentVariable = "CDIDX_GITHUB_TOKEN";
    private const string GitHubApiBase = "https://api.github.com";
    private const string InvalidPreflightFileErrorCode = "invalid-preflight-file";
    private const int MaxPreflightErrorPathLength = 160;
    private const int MaxPreflightErrorDetailLength = 300;

    private static readonly HashSet<string> StopTitleTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ai",
        "suggestion",
        "suggestions",
        "cdidx",
        "the",
        "and",
        "or",
        "for",
        "with",
        "from",
        "into",
        "that",
        "this",
    };

    private readonly List<OpenIssue> _issues;
    private readonly List<string> _repositoryLabels;
    private static readonly HttpClient s_defaultHttpClient = CreateDefaultHttpClient();
    internal static HttpClient? s_httpClientOverride;
    private static HttpClient HttpClient => s_httpClientOverride ?? s_defaultHttpClient;

    private IssueDuplicatePreflight(
        bool isChecked,
        string? source,
        List<OpenIssue> issues,
        List<string>? repositoryLabels = null,
        bool repositoryLabelsChecked = false)
    {
        Checked = isChecked;
        Source = source;
        _issues = issues;
        _repositoryLabels = repositoryLabels ?? [];
        RepositoryLabelsChecked = repositoryLabelsChecked;
    }

    public bool Checked { get; }
    public string? Source { get; }
    public int OpenIssueCount => _issues.Count;
    public bool RepositoryLabelsChecked { get; }
    public IReadOnlyList<string> RepositoryLabels => _repositoryLabels;

    public static bool IsGitHubOpenIssuesSource(string? source)
        => !string.IsNullOrWhiteSpace(source)
            && (string.Equals(source.Trim(), GitHubSourceName, StringComparison.OrdinalIgnoreCase)
                || source.Trim().StartsWith(GitHubSourcePrefix, StringComparison.OrdinalIgnoreCase));

    public static bool TryLoad(string? path, out IssueDuplicatePreflight preflight, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            preflight = new IssueDuplicatePreflight(false, null, []);
            return true;
        }

        var pathForError = FormatPreflightErrorPath(path);
        try
        {
            var fullPath = Path.GetFullPath(path);
            var json = DataDirectorySecurity.ReadTextWithinLimit(fullPath, MaxOpenIssuesJsonBytes);
            if (json == null)
            {
                preflight = new IssueDuplicatePreflight(false, null, []);
                error = $"--open-issues file '{pathForError}' exceeds maximum supported size of {MaxOpenIssuesJsonBytes} bytes.";
                return false;
            }

            var root = BoundedJson.ParseNode(
                json,
                MaxOpenIssuesJsonBytes,
                MaxOpenIssuesJsonDepth);
            preflight = new IssueDuplicatePreflight(true, fullPath, ParseOpenIssues(root));
            return true;
        }
        catch (InvalidOpenIssuesFileException ex)
        {
            preflight = new IssueDuplicatePreflight(false, null, []);
            error = $"invalid --open-issues file '{pathForError}' ({InvalidPreflightFileErrorCode}): {SanitizePreflightErrorDetail(DiagnosticRedactor.FormatExceptionMessage(ex, MaxPreflightErrorDetailLength))}";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            preflight = new IssueDuplicatePreflight(false, null, []);
            error = $"could not read --open-issues file '{pathForError}': {CommandErrorWriter.FormatSanitizedException(ex)}";
            return false;
        }
    }

    public static bool TryLoad(string? source, string? repository, out IssueDuplicatePreflight preflight, out string? error)
    {
        var result = TryLoadAsync(source, repository, CancellationToken.None).GetAwaiter().GetResult();
        preflight = result.Preflight;
        error = result.Error;
        return result.Loaded;
    }

    internal static async Task<IssueDuplicatePreflightLoadResult> TryLoadAsync(
        string? source,
        string? repository,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsGitHubOpenIssuesSource(source))
        {
            if (!string.IsNullOrWhiteSpace(repository))
                return IssueDuplicatePreflightLoadResult.Failure("--repo can only be used with `--open-issues github`.");

            return TryLoad(source, out var filePreflight, out var fileError)
                ? IssueDuplicatePreflightLoadResult.Success(filePreflight)
                : IssueDuplicatePreflightLoadResult.Failure(fileError!);
        }

        var requestedRepository = ExtractGitHubRepository(source, repository);
        if (!TryNormalizeGitHubRepository(requestedRepository, out var normalizedRepository, out var error))
            return IssueDuplicatePreflightLoadResult.Failure(error!);

        return await TryLoadFromGitHubAsync(normalizedRepository, cancellationToken).ConfigureAwait(false);
    }

    public static bool TryNormalizeDuplicateConfidence(string value, out string confidence)
    {
        confidence = value.Trim().ToLowerInvariant();
        return confidence is LowDuplicateConfidence or DefaultDuplicateConfidence or HighDuplicateConfidence;
    }

    public static double ThresholdForDuplicateConfidence(string confidence) => confidence switch
    {
        LowDuplicateConfidence => LowDuplicateThreshold,
        HighDuplicateConfidence => HighDuplicateThreshold,
        _ => DefaultDuplicateThreshold,
    };

    public List<SuggestionIssueDraftDuplicateMatchJsonResult> FindMatches(string draftTitle, IReadOnlyList<string> draftLabels)
        => FindMatches(draftTitle, draftLabels, DefaultDuplicateThreshold, null, null);

    public List<SuggestionIssueDraftDuplicateMatchJsonResult> FindMatches(
        string draftTitle,
        IReadOnlyList<string> draftLabels,
        double minimumScore)
        => FindMatches(draftTitle, draftLabels, minimumScore, null, null);

    public List<SuggestionIssueDraftDuplicateMatchJsonResult> FindMatches(
        string draftTitle,
        IReadOnlyList<string> draftLabels,
        IReadOnlyList<string>? draftEvidencePaths,
        string? draftBody)
        => FindMatches(draftTitle, draftLabels, DefaultDuplicateThreshold, draftEvidencePaths, draftBody);

    public List<SuggestionIssueDraftDuplicateMatchJsonResult> FindMatches(
        string draftTitle,
        IReadOnlyList<string> draftLabels,
        double minimumScore,
        IReadOnlyList<string>? draftEvidencePaths,
        string? draftBody)
    {
        if (!Checked || _issues.Count == 0)
            return [];

        var draftLabelSet = draftLabels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedDraftTitle = NormalizeTitleText(draftTitle);
        var draftTokens = TokenizeTitle(draftTitle);
        var draftEvidencePathSet = NormalizeEvidencePaths(draftEvidencePaths);
        var draftBodyTokens = TokenizeBody(draftBody);
        var matches = new List<SuggestionIssueDraftDuplicateMatchJsonResult>();
        foreach (var issue in _issues)
        {
            var issueLabels = issue.Labels
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var overlappingLabels = issueLabels
                .Where(draftLabelSet.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var normalizedIssueTitle = NormalizeTitleText(issue.Title);
            var score = 0.0;
            string? reason = null;
            var signals = new List<string>();
            if (normalizedIssueTitle.Length > 0 && normalizedIssueTitle == normalizedDraftTitle)
            {
                score = 1.0;
                if (score >= minimumScore)
                    reason = "title_exact";
                signals.Add("title_exact");
            }
            else if (overlappingLabels.Count > 0)
            {
                score = ScoreTitleSimilarity(draftTokens, TokenizeTitle(issue.Title));
                if (score >= minimumScore)
                {
                    reason = "title_label_similarity";
                    signals.Add("title_similarity");
                    signals.Add("label_overlap");
                }
                else if (normalizedIssueTitle.Length > 16
                    && normalizedDraftTitle.Length > 16
                    && (normalizedIssueTitle.Contains(normalizedDraftTitle, StringComparison.Ordinal)
                        || normalizedDraftTitle.Contains(normalizedIssueTitle, StringComparison.Ordinal)))
                {
                    score = Math.Max(score, DefaultDuplicateThreshold);
                    if (score >= minimumScore)
                        reason = "title_label_contains";
                    signals.Add("title_contains");
                    signals.Add("label_overlap");
                }

                var evidenceScore = ScoreEvidencePathSimilarity(
                    draftEvidencePathSet,
                    ExtractEvidencePathsFromBody(issue.Body));
                if (evidenceScore >= EvidencePathSimilarityThreshold)
                {
                    var evidenceCandidateScore = 0.65 + Math.Min(0.2, evidenceScore * 0.2);
                    if (evidenceCandidateScore >= minimumScore)
                    {
                        reason ??= "evidence_path_overlap";
                        score = Math.Max(score, evidenceCandidateScore);
                        signals.Add("evidence_path_overlap");
                        signals.Add("label_overlap");
                    }
                }

                var bodyScore = ScoreTitleSimilarity(draftBodyTokens, TokenizeBody(issue.Body));
                if (bodyScore >= BodyLabelSimilarityThreshold)
                {
                    var bodyCandidateScore = 0.5 + Math.Min(0.25, bodyScore * 0.25);
                    if (bodyCandidateScore >= minimumScore)
                    {
                        reason ??= "body_label_similarity";
                        score = Math.Max(score, bodyCandidateScore);
                        signals.Add("body_similarity");
                        signals.Add("label_overlap");
                    }
                }
            }

            if (reason == null)
                continue;

            var roundedScore = Math.Round(score, 3);

            matches.Add(new SuggestionIssueDraftDuplicateMatchJsonResult(
                issue.Number,
                issue.Title,
                issue.Url,
                issueLabels,
                overlappingLabels,
                reason,
                roundedScore,
                ClassifyConfidence(roundedScore),
                signals.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Number ?? int.MaxValue)
            .Take(5)
            .ToList();
    }

    private static List<OpenIssue> ParseOpenIssues(JsonNode? root, bool skipPullRequests = false)
    {
        var array = root as JsonArray
            ?? root?["issues"] as JsonArray
            ?? root?["items"] as JsonArray;
        if (array == null)
            return [];

        var issues = new List<OpenIssue>();
        var entriesRead = 0;
        foreach (var item in array)
        {
            if (entriesRead >= MaxOpenIssueCount)
                break;

            entriesRead++;
            if (skipPullRequests && item?["pull_request"] != null)
                continue;
            var title = TryReadString(item?["title"], MaxOpenIssueTitleLength);
            if (string.IsNullOrWhiteSpace(title))
                continue;
            issues.Add(new OpenIssue(
                TryReadInt(item?["number"]),
                title,
                TryReadString(item?["html_url"], MaxOpenIssueUrlLength) ?? TryReadString(item?["url"], MaxOpenIssueUrlLength),
                ReadLabels(item?["labels"]),
                TryReadString(item?["body"], MaxOpenIssueBodyLength)));
        }

        return issues;
    }

    private static List<string> ReadLabels(JsonNode? labelsNode)
    {
        if (labelsNode is not JsonArray labels)
            return [];

        var result = new List<string>();
        var labelsRead = 0;
        foreach (var labelNode in labels)
        {
            if (labelsRead >= MaxLabelsPerOpenIssue)
                break;

            labelsRead++;
            var label = TryReadString(labelNode, MaxOpenIssueLabelLength)
                ?? TryReadString(labelNode?["name"], MaxOpenIssueLabelLength);
            if (!string.IsNullOrWhiteSpace(label))
                result.Add(label.Trim());
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string FormatPreflightErrorPath(string path)
    {
        var candidate = path.Trim();
        try
        {
            var trimmedPath = candidate.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar,
                '/',
                '\\');
            var separatorIndex = trimmedPath.LastIndexOfAny(['/', '\\']);
            var fileName = separatorIndex >= 0
                ? trimmedPath[(separatorIndex + 1)..]
                : Path.GetFileName(trimmedPath);
            if (!string.IsNullOrWhiteSpace(fileName))
                candidate = fileName;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            candidate = "path";
        }

        return SanitizePreflightErrorText(candidate, MaxPreflightErrorPathLength, fallback: "path");
    }

    private static string SanitizePreflightErrorDetail(string detail)
        => SanitizePreflightErrorText(detail, MaxPreflightErrorDetailLength, fallback: "InvalidOpenIssuesFileException");

    private static string SanitizePreflightErrorText(string value, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var trimmed = value.Trim();
        var builder = new StringBuilder(Math.Min(trimmed.Length, maxLength + 3));
        foreach (var c in trimmed)
        {
            if (builder.Length >= maxLength)
            {
                builder.Append("...");
                break;
            }

            builder.Append(char.IsControl(c) ? '?' : c);
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    private static async Task<IssueDuplicatePreflightLoadResult> TryLoadFromGitHubAsync(
        string repository,
        CancellationToken cancellationToken)
    {
        var issues = new List<OpenIssue>();
        try
        {
            for (var page = 1; page <= MaxGitHubOpenIssuePages && issues.Count < MaxOpenIssueCount; page++)
            {
                var pageResult = await FetchGitHubOpenIssuePageAsync(repository, page, cancellationToken)
                    .ConfigureAwait(false);
                issues.AddRange(pageResult.Issues);
                if (pageResult.RawEntryCount == 0 || pageResult.RawEntryCount < GitHubOpenIssuesPerPage)
                    break;
            }

            var labels = await FetchGitHubRepositoryLabelsAsync(repository, cancellationToken)
                .ConfigureAwait(false);

            return IssueDuplicatePreflightLoadResult.Success(
                new IssueDuplicatePreflight(
                    true,
                    $"{GitHubSourcePrefix}{repository}",
                    issues.Take(MaxOpenIssueCount).ToList(),
                    labels,
                    repositoryLabelsChecked: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableGitHubPreflightException(ex))
        {
            return IssueDuplicatePreflightLoadResult.Failure(
                $"could not fetch --open-issues github for repository '{repository}': {FormatPreflightFailureDetail(ex)}");
        }
    }

    private static async Task<List<string>> FetchGitHubRepositoryLabelsAsync(
        string repository,
        CancellationToken cancellationToken)
    {
        var labels = new List<string>();
        for (var page = 1; page <= MaxGitHubLabelPages && labels.Count < MaxRepositoryLabelCount; page++)
        {
            var pageResult = await FetchGitHubRepositoryLabelPageAsync(repository, page, cancellationToken)
                .ConfigureAwait(false);
            labels.AddRange(pageResult.Labels);
            if (pageResult.RawEntryCount == 0 || pageResult.RawEntryCount < GitHubLabelsPerPage)
                break;
        }

        return labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRepositoryLabelCount)
            .ToList();
    }

    private static async Task<GitHubOpenIssuePageResult> FetchGitHubOpenIssuePageAsync(
        string repository,
        int page,
        CancellationToken cancellationToken)
    {
        var slash = repository.IndexOf('/');
        var owner = repository[..slash];
        var name = repository[(slash + 1)..];
        var url = $"{GitHubApiBase}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues?state=open&per_page={GitHubOpenIssuesPerPage.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}";
        var timeout = GitHubIssueReporter.ResolveSubmitTimeout();
        using var requestCancellation = GitHubHttpClientFactory.CreateRequestCancellationScope(timeout, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        GitHubHttpClientFactory.ApplyDefaultHeaders(request.Headers);
        var token = CdidxEnvironment.GetProcessEnvironmentVariable(GitHubTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && requestCancellation.IsTimeoutCancellationRequested)
        {
            throw new TimeoutException(
                $"GitHub open-issues preflight timed out after {timeout.TotalSeconds:0} seconds.",
                ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new GitHubPreflightException(
                    await BuildGitHubApiErrorDetailAsync(response, requestCancellation.Token).ConfigureAwait(false));

            var json = await ReadContentWithinLimitAsync(response.Content, MaxOpenIssuesJsonBytes, requestCancellation.Token)
                .ConfigureAwait(false)
                ?? throw new IOException($"GitHub open-issues response exceeds maximum supported size of {MaxOpenIssuesJsonBytes} bytes.");
            var root = BoundedJson.ParseNode(json, MaxOpenIssuesJsonBytes, MaxOpenIssuesJsonDepth);
            var rawEntryCount = root is JsonArray array ? array.Count : 0;
            return new GitHubOpenIssuePageResult(ParseOpenIssues(root, skipPullRequests: true), rawEntryCount);
        }
    }

    private static async Task<GitHubRepositoryLabelPageResult> FetchGitHubRepositoryLabelPageAsync(
        string repository,
        int page,
        CancellationToken cancellationToken)
    {
        var slash = repository.IndexOf('/');
        var owner = repository[..slash];
        var name = repository[(slash + 1)..];
        var url = $"{GitHubApiBase}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/labels?per_page={GitHubLabelsPerPage.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}";
        var timeout = GitHubIssueReporter.ResolveSubmitTimeout();
        using var requestCancellation = GitHubHttpClientFactory.CreateRequestCancellationScope(timeout, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        GitHubHttpClientFactory.ApplyDefaultHeaders(request.Headers);
        var token = CdidxEnvironment.GetProcessEnvironmentVariable(GitHubTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && requestCancellation.IsTimeoutCancellationRequested)
        {
            throw new TimeoutException(
                $"GitHub labels preflight timed out after {timeout.TotalSeconds:0} seconds.",
                ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new GitHubPreflightException(
                    await BuildGitHubApiErrorDetailAsync(response, requestCancellation.Token).ConfigureAwait(false));

            var json = await ReadContentWithinLimitAsync(response.Content, MaxOpenIssuesJsonBytes, requestCancellation.Token)
                .ConfigureAwait(false)
                ?? throw new IOException($"GitHub labels response exceeds maximum supported size of {MaxOpenIssuesJsonBytes} bytes.");
            var root = BoundedJson.ParseNode(json, MaxOpenIssuesJsonBytes, MaxOpenIssuesJsonDepth);
            var rawEntryCount = root is JsonArray array ? array.Count : 0;
            return new GitHubRepositoryLabelPageResult(ParseRepositoryLabels(root), rawEntryCount);
        }
    }

    private static List<string> ParseRepositoryLabels(JsonNode? root)
    {
        if (root is not JsonArray labels)
            return [];

        var result = new List<string>();
        foreach (var item in labels)
        {
            if (result.Count >= MaxRepositoryLabelCount)
                break;

            var label = TryReadString(item, MaxOpenIssueLabelLength)
                ?? TryReadString(item?["name"], MaxOpenIssueLabelLength);
            if (!string.IsNullOrWhiteSpace(label))
                result.Add(label.Trim());
        }

        return result;
    }

    private static async Task<string> BuildGitHubApiErrorDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var errorBody = await GitHubIssueReporter.ReadBoundedApiErrorBodyAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        var retryAt = GitHubIssueReporter.GetRateLimitRetryAt(
            response,
            GitHubIssueReporter.TimeProvider.GetUtcNow().UtcDateTime);
        return retryAt is null
            ? GitHubIssueReporter.BuildApiErrorDetail((int)response.StatusCode, errorBody)
            : GitHubIssueReporter.BuildRateLimitErrorDetail((int)response.StatusCode, errorBody, retryAt.Value);
    }

    private static string? ExtractGitHubRepository(string? source, string? repository)
    {
        if (!string.IsNullOrWhiteSpace(repository))
            return repository;
        var trimmed = source?.Trim();
        return trimmed != null && trimmed.StartsWith(GitHubSourcePrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[GitHubSourcePrefix.Length..]
            : null;
    }

    private static bool TryNormalizeGitHubRepository(string? repository, out string normalizedRepository, out string? error)
    {
        normalizedRepository = string.Empty;
        if (string.IsNullOrWhiteSpace(repository))
        {
            error = "--open-issues github requires --repo <owner/name> or --open-issues github:<owner/name>.";
            return false;
        }

        var trimmed = repository.Trim();
        if (trimmed.Length > MaxGitHubRepositoryLength)
        {
            error = $"--repo value too long (max {MaxGitHubRepositoryLength} characters).";
            return false;
        }

        var slash = trimmed.IndexOf('/');
        if (slash <= 0 || slash != trimmed.LastIndexOf('/') || slash == trimmed.Length - 1)
        {
            error = "--repo must use owner/name form.";
            return false;
        }

        var owner = trimmed[..slash];
        var name = trimmed[(slash + 1)..];
        if (!IsValidGitHubRepositoryPart(owner) || !IsValidGitHubRepositoryPart(name))
        {
            error = "--repo must contain only letters, digits, '.', '_', or '-' in owner/name form.";
            return false;
        }

        normalizedRepository = $"{owner}/{name}";
        error = null;
        return true;
    }

    private static bool IsValidGitHubRepositoryPart(string value)
        => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    private static async Task<string?> ReadContentWithinLimitAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 8192));
        var chunk = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
            if (total > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool IsRecoverableGitHubPreflightException(Exception ex)
        => ex is HttpRequestException
            or OperationCanceledException
            or TimeoutException
            or JsonException
            or InvalidDataException
            or IOException
            or InvalidOperationException
            or InvalidOpenIssuesFileException
            or GitHubPreflightException;

    private static string FormatPreflightFailureDetail(Exception ex)
        => ex is GitHubPreflightException githubPreflight
            ? githubPreflight.Message
            : CommandErrorWriter.FormatSanitizedException(ex);

    private static HttpClient CreateDefaultHttpClient()
        => GitHubHttpClientFactory.CreateDefaultHttpClient(TimeSpan.FromSeconds(10));

    private static string? TryReadString(JsonNode? node, int maxLength)
        => TryReadString(node, maxLength, truncate: true, fieldName: null);

    private static string? TryReadString(JsonNode? node, int maxLength, bool truncate, string? fieldName)
    {
        if (node == null)
            return null;
        try
        {
            var value = node.GetValue<string>();
            if (value.Length <= maxLength)
                return value;
            if (truncate)
                return value[..maxLength];
            throw new InvalidOpenIssuesFileException(
                $"{fieldName ?? "string scalar"} exceeds maximum supported length of {maxLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? TryReadInt(JsonNode? node)
    {
        if (node == null)
            return null;
        try
        {
            return node.GetValue<int>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            var value = TryReadString(node, MaxOpenIssueNumberLength, truncate: false, fieldName: "issue number");
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }
    }

    private static string NormalizeTitleText(string title)
    {
        title = BoundTitleProcessingInput(title);
        var builder = new StringBuilder(title.Length);
        var previousWasSpace = true;
        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static HashSet<string> TokenizeTitle(string title)
    {
        return TokenizeWords(BoundTitleProcessingInput(title));
    }

    private static HashSet<string> TokenizeBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];

        var bounded = body.Length <= MaxBodyTokenizationInputLength
            ? body
            : body[..MaxBodyTokenizationInputLength];
        return TokenizeWords(bounded);
    }

    private static HashSet<string> TokenizeWords(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }

            AddToken(tokens, current);
        }

        AddToken(tokens, current);
        return tokens;
    }

    private static HashSet<string> NormalizeEvidencePaths(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
            return [];

        return paths
            .Select(NormalizeEvidencePath)
            .Where(path => path is not null)
            .Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ExtractEvidencePathsFromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];

        var bounded = body.Length <= MaxOpenIssueBodyLength
            ? body
            : body[..MaxOpenIssueBodyLength];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in bounded.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Trim();
            if (candidate.StartsWith("- ", StringComparison.Ordinal))
                candidate = candidate[2..].Trim();
            var normalized = NormalizeEvidencePath(candidate);
            if (normalized != null)
                paths.Add(normalized);
        }

        return paths;
    }

    private static string? NormalizeEvidencePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().Trim('`', '*');
        if (trimmed.Length > 512
            || trimmed.Any(char.IsControl)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || (!trimmed.Contains('/', StringComparison.Ordinal) && !trimmed.Contains('\\', StringComparison.Ordinal))
            || !trimmed.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed.Replace('\\', '/');
    }

    private static string BoundTitleProcessingInput(string title) =>
        title.Length <= MaxTitleTokenizationInputLength
            ? title
            : title[..MaxTitleTokenizationInputLength];

    private static void AddToken(HashSet<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
            return;
        var token = current.ToString();
        current.Clear();
        if (token.Length < 3 || StopTitleTokens.Contains(token))
            return;
        tokens.Add(token);
    }

    private static double ScoreTitleSimilarity(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0.0;

        var intersection = left.Count(right.Contains);
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0.0 : intersection / (double)union;
    }

    private static double ScoreEvidencePathSimilarity(HashSet<string> draftPaths, HashSet<string> issuePaths)
    {
        if (draftPaths.Count == 0 || issuePaths.Count == 0)
            return 0.0;

        var overlap = draftPaths.Count(issuePaths.Contains);
        return overlap / (double)Math.Max(draftPaths.Count, issuePaths.Count);
    }

    private static string ClassifyConfidence(double score)
        => score >= HighDuplicateThreshold
            ? HighDuplicateConfidence
            : score >= DefaultDuplicateThreshold
                ? DefaultDuplicateConfidence
                : LowDuplicateConfidence;

    internal sealed record IssueDuplicatePreflightLoadResult(
        bool Loaded,
        IssueDuplicatePreflight Preflight,
        string? Error)
    {
        internal static IssueDuplicatePreflightLoadResult Success(IssueDuplicatePreflight preflight) => new(true, preflight, null);

        internal static IssueDuplicatePreflightLoadResult Failure(string error) =>
            new(false, new IssueDuplicatePreflight(false, null, []), error);
    }

    private sealed record GitHubOpenIssuePageResult(List<OpenIssue> Issues, int RawEntryCount);

    private sealed record GitHubRepositoryLabelPageResult(List<string> Labels, int RawEntryCount);

    private sealed record OpenIssue(int? Number, string Title, string? Url, List<string> Labels, string? Body);

    private sealed class InvalidOpenIssuesFileException(string message) : Exception(message);

    private sealed class GitHubPreflightException(string message) : Exception(message);
}
