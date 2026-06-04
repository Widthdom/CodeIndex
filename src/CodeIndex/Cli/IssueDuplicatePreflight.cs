using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeIndex.Cli;

internal sealed class IssueDuplicatePreflight
{
    internal const int MaxOpenIssuesJsonBytes = 8 * 1024 * 1024;
    internal const int MaxOpenIssuesJsonDepth = 32;

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

    private IssueDuplicatePreflight(bool isChecked, string? source, List<OpenIssue> issues)
    {
        Checked = isChecked;
        Source = source;
        _issues = issues;
    }

    public bool Checked { get; }
    public string? Source { get; }
    public int OpenIssueCount => _issues.Count;

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            preflight = new IssueDuplicatePreflight(false, null, []);
            error = $"could not read --open-issues file '{path}': {ex.Message}";
            return false;
        }
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

    private static List<OpenIssue> ParseOpenIssues(JsonNode? root)
    {
        var array = root as JsonArray
            ?? root?["issues"] as JsonArray
            ?? root?["items"] as JsonArray;
        if (array == null)
            return [];

        var issues = new List<OpenIssue>();
        foreach (var item in array)
        {
            var title = TryReadString(item?["title"]);
            if (string.IsNullOrWhiteSpace(title))
                continue;
            issues.Add(new OpenIssue(
                TryReadInt(item?["number"]),
                title,
                TryReadString(item?["url"]) ?? TryReadString(item?["html_url"]),
                ReadLabels(item?["labels"])));
        }

        return issues;
    }

    private static List<string> ReadLabels(JsonNode? labelsNode)
    {
        if (labelsNode is not JsonArray labels)
            return [];

        var result = new List<string>();
        foreach (var labelNode in labels)
        {
            var label = TryReadString(labelNode) ?? TryReadString(labelNode?["name"]);
            if (!string.IsNullOrWhiteSpace(label))
                result.Add(label.Trim());
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? TryReadString(JsonNode? node)
    {
        if (node == null)
            return null;
        try
        {
            return node.GetValue<string>();
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
        catch (InvalidOperationException)
        {
            var value = TryReadString(node);
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }
    }

    private static string NormalizeTitleText(string title)
    {
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
}
