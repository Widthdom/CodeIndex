using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    internal const int MaxOpenIssueNumberLength = 32;
    internal const int MaxTitleTokenizationInputLength = MaxOpenIssueTitleLength;
    internal const int MaxGitHubRepositoryLength = 200;
    private const int GitHubOpenIssuesPerPage = 100;
    private const int MaxGitHubOpenIssuePages = (MaxOpenIssueCount / GitHubOpenIssuesPerPage) + 1;
    private const string GitHubSourceName = "github";
    private const string GitHubSourcePrefix = "github:";
    private const string GitHubTokenEnvironmentVariable = "CDIDX_GITHUB_TOKEN";
    private const string GitHubApiBase = "https://api.github.com";
    private const string InvalidPreflightFileErrorCode = "invalid-preflight-file";

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
    private static readonly HttpClient s_defaultHttpClient = CreateDefaultHttpClient();
    internal static HttpClient? s_httpClientOverride;
    private static HttpClient HttpClient => s_httpClientOverride ?? s_defaultHttpClient;

    private IssueDuplicatePreflight(bool isChecked, string? source, List<OpenIssue> issues)
    {
        Checked = isChecked;
        Source = source;
        _issues = issues;
    }

    public bool Checked { get; }
    public string? Source { get; }
    public int OpenIssueCount => _issues.Count;

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

        try
        {
            var fullPath = Path.GetFullPath(path);
            var json = DataDirectorySecurity.ReadTextWithinLimit(fullPath, MaxOpenIssuesJsonBytes);
            if (json == null)
            {
                preflight = new IssueDuplicatePreflight(false, null, []);
                error = $"--open-issues file '{path}' exceeds maximum supported size of {MaxOpenIssuesJsonBytes} bytes.";
                return false;
            }

            var root = JsonNode.Parse(
                json,
                documentOptions: new JsonDocumentOptions { MaxDepth = MaxOpenIssuesJsonDepth });
            preflight = new IssueDuplicatePreflight(true, fullPath, ParseOpenIssues(root));
            return true;
        }
        catch (InvalidOpenIssuesFileException ex)
        {
            preflight = new IssueDuplicatePreflight(false, null, []);
            error = $"invalid --open-issues file '{path}' ({InvalidPreflightFileErrorCode}): {ex.Message}";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            preflight = new IssueDuplicatePreflight(false, null, []);
            error = $"could not read --open-issues file '{path}': {ex.Message}";
            return false;
        }
    }

    public static bool TryLoad(string? source, string? repository, out IssueDuplicatePreflight preflight, out string? error)
    {
        error = null;
        if (!IsGitHubOpenIssuesSource(source))
        {
            if (!string.IsNullOrWhiteSpace(repository))
            {
                preflight = new IssueDuplicatePreflight(false, null, []);
                error = "--repo can only be used with `--open-issues github`.";
                return false;
            }

            return TryLoad(source, out preflight, out error);
        }

        var requestedRepository = ExtractGitHubRepository(source, repository);
        if (!TryNormalizeGitHubRepository(requestedRepository, out var normalizedRepository, out error))
        {
            preflight = new IssueDuplicatePreflight(false, null, []);
            return false;
        }

        return TryLoadFromGitHub(normalizedRepository, out preflight, out error);
    }

    public List<SuggestionIssueDraftDuplicateMatchJsonResult> FindMatches(string draftTitle, IReadOnlyList<string> draftLabels)
    {
        if (!Checked || _issues.Count == 0)
            return [];

        var draftLabelSet = draftLabels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedDraftTitle = NormalizeTitleText(draftTitle);
        var draftTokens = TokenizeTitle(draftTitle);
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
            if (normalizedIssueTitle.Length > 0 && normalizedIssueTitle == normalizedDraftTitle)
            {
                reason = "title_exact";
                score = 1.0;
            }
            else if (overlappingLabels.Count > 0)
            {
                score = ScoreTitleSimilarity(draftTokens, TokenizeTitle(issue.Title));
                if (score >= 0.45)
                {
                    reason = "title_label_similarity";
                }
                else if (normalizedIssueTitle.Length > 16
                    && normalizedDraftTitle.Length > 16
                    && (normalizedIssueTitle.Contains(normalizedDraftTitle, StringComparison.Ordinal)
                        || normalizedDraftTitle.Contains(normalizedIssueTitle, StringComparison.Ordinal)))
                {
                    reason = "title_label_contains";
                    score = Math.Max(score, 0.45);
                }
            }

            if (reason == null)
                continue;

            matches.Add(new SuggestionIssueDraftDuplicateMatchJsonResult(
                issue.Number,
                issue.Title,
                issue.Url,
                issueLabels,
                overlappingLabels,
                reason,
                Math.Round(score, 3)));
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
                ReadLabels(item?["labels"])));
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

    private static bool TryLoadFromGitHub(string repository, out IssueDuplicatePreflight preflight, out string? error)
    {
        var issues = new List<OpenIssue>();
        try
        {
            for (var page = 1; page <= MaxGitHubOpenIssuePages && issues.Count < MaxOpenIssueCount; page++)
            {
                var pageIssues = FetchGitHubOpenIssuePage(repository, page, out var rawEntryCount);
                issues.AddRange(pageIssues);
                if (rawEntryCount == 0 || rawEntryCount < GitHubOpenIssuesPerPage)
                    break;
            }

            preflight = new IssueDuplicatePreflight(true, $"{GitHubSourcePrefix}{repository}", issues.Take(MaxOpenIssueCount).ToList());
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException or InvalidOpenIssuesFileException)
        {
            preflight = new IssueDuplicatePreflight(false, null, []);
            error = $"could not fetch --open-issues github for repository '{repository}': {ex.Message}";
            return false;
        }
    }

    private static List<OpenIssue> FetchGitHubOpenIssuePage(string repository, int page, out int rawEntryCount)
    {
        var slash = repository.IndexOf('/');
        var owner = repository[..slash];
        var name = repository[(slash + 1)..];
        var url = $"{GitHubApiBase}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues?state=open&per_page={GitHubOpenIssuesPerPage.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = Environment.GetEnvironmentVariable(GitHubTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = HttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub API responded {(int)response.StatusCode} {response.ReasonPhrase}");

        var json = ReadContentWithinLimit(response.Content, MaxOpenIssuesJsonBytes)
            ?? throw new IOException($"GitHub open-issues response exceeds maximum supported size of {MaxOpenIssuesJsonBytes} bytes.");
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = MaxOpenIssuesJsonDepth });
        rawEntryCount = root is JsonArray array ? array.Count : 0;
        return ParseOpenIssues(root, skipPullRequests: true);
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

    private static string? ReadContentWithinLimit(HttpContent content, int maxBytes)
    {
        using var stream = content.ReadAsStream();
        using var buffer = new MemoryStream(Math.Min(maxBytes, 8192));
        var chunk = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
                break;
            total += read;
            if (total > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

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
        title = BoundTitleProcessingInput(title);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new StringBuilder();
        foreach (var c in title)
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

    private sealed record OpenIssue(int? Number, string Title, string? Url, List<string> Labels);

    private sealed class InvalidOpenIssuesFileException(string message) : Exception(message);
}
