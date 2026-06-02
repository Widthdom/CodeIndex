using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Cli;

/// <summary>
/// Reports improvement suggestions as GitHub Issues using the GitHub REST API.
/// GitHub REST APIを使用して改善提案をGitHub Issueとして報告する。
///
/// This class is part of the AI feedback system. It only transmits structured
/// gap descriptions — never source code. The data sent is limited to:
///   - Category (one of 8 fixed enum values)
///   - Language name (e.g. "typescript")
///   - Description text (validated by SourceCodeDetector to not contain code)
///   - Context text (also validated)
///   - Attribution metadata captured from MCP initialize/client context
///   - cdidx version and suggestion hash
///
/// このクラスはAIフィードバックシステムの一部である。構造化されたギャップ記述のみを
/// 送信し、ソースコードは一切送信しない。送信データは以下に限定される:
///   - カテゴリ（8つの固定 enum 値のいずれか）
///   - 言語名（例: "typescript"）
///   - 説明テキスト（SourceCodeDetector によりコードが含まれないことを検証済み）
///   - コンテキストテキスト（同様に検証済み）
///   - MCP initialize / client context から取得した attribution metadata
///   - cdidx バージョンと提案ハッシュ
///
/// GitHub Issues are only created when the user has explicitly configured
/// a GitHub token via the CDIDX_GITHUB_TOKEN environment variable.
/// The generic GITHUB_TOKEN is NOT used — this prevents ambient tokens
/// (e.g. from CI environments) from silently publishing to an external repo.
/// If CDIDX_GITHUB_TOKEN is not set, this class does nothing.
/// The destination repository is hardcoded to widthdom/CodeIndex.
/// GitHub Issue は、ユーザーが CDIDX_GITHUB_TOKEN 環境変数で
/// GitHub トークンを明示的に設定した場合にのみ作成される。
/// 汎用の GITHUB_TOKEN は使用しない — CI 等の環境トークンが意図せず
/// 外部リポジトリに公開されることを防ぐ。
/// CDIDX_GITHUB_TOKEN が未設定の場合、このクラスは何もしない。
/// 送信先リポジトリは widthdom/CodeIndex に固定されている。
/// </summary>
internal static class GitHubIssueReporter
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRateLimitRetryDelay = TimeSpan.FromMinutes(1);
    private const string TimeoutEnvironmentVariable = "CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS";
    internal const int MaxGitHubIssueTitleLength = 255;
    internal const int MaxScrubInputLength = 16 * 1024;
    internal const int MaxGitHubApiErrorBodyBytes = 4 * 1024;
    private const int MaxGitHubApiErrorDetailLength = 500;
    private const string CodeExampleRemovedText = "[code example removed]";
    private const string ScrubInputTruncatedText = "\n[truncated]";
    private const string ApiErrorBodyTruncatedText = " [response body truncated]";
    private static readonly Regex SensitiveJsonFieldPattern = new(
        "(\"(?:token|access_token|authorization|password|secret|client_secret|private_key|api_key)\"\\s*:\\s*)(\"(?:\\\\.|[^\"])*\"|[^,}\\]\\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    // Static HttpClient singleton — .NET best practice for reuse.
    // 静的 HttpClient シングルトン — .NET の再利用ベストプラクティス。
    private static readonly HttpClient s_defaultHttpClient = CreateDefaultHttpClient();

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders =
            {
                { "User-Agent", "cdidx" },
                { "Accept", "application/vnd.github+json" },
                { "X-GitHub-Api-Version", "2022-11-28" },
            }
        };
        return client;
    }

    // Test seam: when set, replaces the default HttpClient so tests can
    // mock GitHub responses without hitting the network. Production code
    // never sets this.
    // テスト用シーム: テスト時にネットワーク非依存で GitHub レスポンスをモックするため、
    // デフォルトの HttpClient を差し替える。プロダクションコードからは設定しない。
    internal static HttpClient? s_httpClientOverride;

    private static HttpClient HttpClient => s_httpClientOverride ?? s_defaultHttpClient;

    // Target repository for issue creation / Issue 作成先リポジトリ
    private const string RepoOwner = "widthdom";
    private const string RepoName = "CodeIndex";
    private const string ApiBase = "https://api.github.com";
    private static readonly string[] ExistingSuggestionLookupLabels = ["enhancement", "bug"];

    /// <summary>
    /// Try to create a GitHub Issue for the given suggestion.
    /// Returns the issue URL on success, null if no token is set or on failure.
    /// This method is best-effort, but preserves cooperative cancellation and fatal runtime failures.
    /// 指定された提案の GitHub Issue 作成を試みる。
    /// 成功時は Issue URL を返し、トークン未設定時や失敗時は null を返す。
    /// ベストエフォート。ただし協調キャンセルと致命的なランタイム障害は保持する。
    /// </summary>
    public static async Task<string?> TryCreateIssueAsync(SuggestionRecord record, string version, CancellationToken cancellationToken = default)
    {
        var result = await TryCreateIssueDetailedAsync(record, version, cancellationToken);
        return result.IssueUrl;
    }

    /// <summary>
    /// Try to create a GitHub Issue and return diagnostic state for persistence.
    /// GitHub Issue 作成を試み、永続化用の診断状態を返す。
    /// Preserves cooperative cancellation and fatal runtime failures.
    /// 協調キャンセルと致命的なランタイム障害は保持する。
    /// </summary>
    public static async Task<SuggestionStore.SubmitAttemptResult> TryCreateIssueDetailedAsync(
        SuggestionRecord record,
        string version,
        CancellationToken cancellationToken = default)
    {
        var token = ResolveToken();
        if (token == null)
            return SuggestionStore.SubmitAttemptResult.Skipped();

        using var timeoutCts = CreateTimeoutCancellationSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            // Idempotency check: if a previous submission attempt actually
            // created an issue on GitHub but the response was lost in transit,
            // the local record still shows SubmittedToGitHub=false. Look for
            // an existing issue carrying this suggestion's hash before
            // posting a new one, so retries do not create duplicates.
            // 冪等性チェック: 過去の送信試行で GitHub 側に Issue が作成されたが
            // レスポンスが消失した場合、ローカルレコードでは SubmittedToGitHub=false の
            // ままになる。再試行で重複 Issue を作らないよう、新規 POST 前に
            // 当該提案ハッシュを含む既存 Issue を探す。
            var existingUrl = await FindExistingIssueByHashAsync(record.Hash, token, BuildIssueLabels(record), linkedCts.Token);
            if (existingUrl != null)
                return SuggestionStore.SubmitAttemptResult.Success(existingUrl);

            return await CreateIssueAsync(record, version, token, linkedCts.Token);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var detail = $"{ex.GetType().Name}: GitHub submission timed out after {ResolveSubmitTimeout().TotalSeconds:0} seconds";
            Console.Error.WriteLine(BuildSubmissionFailureMessage(detail));
            return SuggestionStore.SubmitAttemptResult.Failure(detail);
        }
        catch (Exception ex) when (ShouldTreatAsSubmissionFailure(ex))
        {
            // Best-effort: log to stderr but do not propagate.
            // ベストエフォート: stderr にログ出力するが伝播しない。
            Console.Error.WriteLine(BuildSubmissionFailureMessage(ex.Message));
            return SuggestionStore.SubmitAttemptResult.Failure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool ShouldTreatAsSubmissionFailure(Exception ex)
    {
        if (ex is OutOfMemoryException)
            return false;

        if (ex is TaskCanceledException taskCanceled)
            return !taskCanceled.CancellationToken.IsCancellationRequested || IsTimeoutCancellation(taskCanceled);

        return ex is not OperationCanceledException;
    }

    private static CancellationTokenSource CreateTimeoutCancellationSource()
    {
        var timeout = ResolveSubmitTimeout();
        return timeout <= TimeSpan.Zero
            ? new CancellationTokenSource()
            : new CancellationTokenSource(timeout);
    }

    internal static TimeSpan ResolveSubmitTimeout()
    {
        var raw = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultTimeout;

        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultTimeout;
    }

    private static bool IsTimeoutCancellation(Exception ex)
    {
        for (var current = ex.InnerException; current != null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Search the target GitHub repository for an existing Issue whose body
    /// contains the suggestion hash. The primary check uses GitHub Search;
    /// the backstop lists issues by the labels cdidx would apply so same-second retries
    /// are not exposed to Search indexing latency. Returns the html_url of the
    /// first match, or null if no match is found or the hash looks unsafe to
    /// search with. On API failure this returns null — the caller falls through
    /// to the normal create path so a GitHub-side lookup outage never blocks a
    /// legitimate first submission.
    /// 当該提案ハッシュを含む既存 Issue を対象リポジトリから検索する。
    /// 主経路は GitHub Search を使い、backstop として cdidx が付ける label の Issue を
    /// 直接一覧取得することで、同秒の再試行が Search の index 遅延に影響されない
    /// ようにする。一致した最初の Issue の html_url を返す。一致なし、またはハッシュが
    /// 検索に使えない形の場合は null。API 失敗時も null を返し、GitHub 側 lookup の
    /// 障害によって新規送信がブロックされないようにする。
    /// </summary>
    internal static async Task<string?> FindExistingIssueByHashAsync(string hash, string token, CancellationToken cancellationToken = default)
        => await FindExistingIssueByHashAsync(hash, token, ExistingSuggestionLookupLabels, cancellationToken);

    private static async Task<string?> FindExistingIssueByHashAsync(
        string hash,
        string token,
        IReadOnlyList<string> lookupLabels,
        CancellationToken cancellationToken)
    {
        // Defensive: only search with hex-shaped hashes to avoid accidentally
        // injecting search operators if the field ever held arbitrary text.
        // 防御的: 検索演算子の混入を避けるため、16進形式のハッシュのみで検索する。
        if (string.IsNullOrEmpty(hash) || !IsHexHash(hash))
            return null;

        var searchUrl = await SearchExistingIssueByHashAsync(hash, token, cancellationToken);
        if (searchUrl != null)
            return searchUrl;

        return await ListExistingSuggestionIssueByHashAsync(hash, token, lookupLabels, cancellationToken);
    }

    private static async Task<string?> SearchExistingIssueByHashAsync(string hash, string token, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"repo:{RepoOwner}/{RepoName} \"{hash}\" in:body");
        var url = $"{ApiBase}/search/issues?q={query}&per_page=1";

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await HttpClient.SendAsync(requestMessage, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var node = JsonNode.Parse(responseJson);
        var items = node?["items"] as JsonArray;
        if (items == null || items.Count == 0)
            return null;

        return items[0]?["html_url"]?.GetValue<string>();
    }

    private static async Task<string?> ListExistingSuggestionIssueByHashAsync(
        string hash,
        string token,
        IReadOnlyList<string> lookupLabels,
        CancellationToken cancellationToken)
    {
        foreach (var label in lookupLabels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var page = 1; ; page++)
            {
                var labels = Uri.EscapeDataString(label);
                var url = $"{ApiBase}/repos/{RepoOwner}/{RepoName}/issues?labels={labels}&state=all&per_page=100&page={page}";

                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var response = await HttpClient.SendAsync(requestMessage, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var items = JsonNode.Parse(responseJson) as JsonArray;
                if (items == null || items.Count == 0)
                    break;

                foreach (var item in items)
                {
                    var body = item?["body"]?.GetValue<string>();
                    if (body != null && body.Contains(hash, StringComparison.Ordinal))
                        return item?["html_url"]?.GetValue<string>();
                }

                if (items.Count < 100)
                    break;
            }
        }

        return null;
    }

    private static bool IsHexHash(string value)
    {
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Resolve the GitHub token from the CDIDX_GITHUB_TOKEN environment variable.
    /// Only CDIDX_GITHUB_TOKEN is accepted — generic GITHUB_TOKEN is NOT used.
    /// This prevents ambient tokens (e.g. from CI) from silently publishing
    /// suggestions to an external repository without explicit user intent.
    /// Returns null if the variable is not set.
    /// 環境変数 CDIDX_GITHUB_TOKEN からGitHubトークンを解決する。
    /// CDIDX_GITHUB_TOKEN のみを受け付ける — 汎用の GITHUB_TOKEN は使用しない。
    /// CI等で設定された環境トークンが、ユーザーの明示的な意図なしに
    /// 外部リポジトリに提案を公開してしまうことを防ぐ。
    /// 未設定の場合は null を返す。
    /// </summary>
    internal static string? ResolveToken()
    {
        var cdidxToken = Environment.GetEnvironmentVariable("CDIDX_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(cdidxToken))
            return cdidxToken;

        return null;
    }

    /// <summary>
    /// Create the GitHub Issue via the REST API.
    /// REST API 経由で GitHub Issue を作成する。
    /// </summary>
    private static async Task<SuggestionStore.SubmitAttemptResult> CreateIssueAsync(
        SuggestionRecord record,
        string version,
        string token,
        CancellationToken cancellationToken)
    {
        var url = $"{ApiBase}/repos/{RepoOwner}/{RepoName}/issues";

        // Build the issue title — scrub and truncate for readability.
        // Issue タイトルを構築 — 除去・切り詰めて可読性を確保。
        var title = BuildIssueTitle(record.Category, record.Description);

        // Scrub inline code from description and context before external submission.
        // SourceCodeDetector intentionally allows short inline code examples for local
        // storage, but we strip them before publishing to GitHub to prevent any
        // code-like content from reaching an external repository.
        // 外部送信前に description と context からインラインコードを除去する。
        // SourceCodeDetector はローカル保存用に短いインラインコード例を意図的に許容するが、
        // GitHub に公開する前に除去し、コード的な内容が外部リポジトリに到達することを防ぐ。
        var scrubbedDescription = ScrubInlineCode(record.Description);
        var scrubbedContext = record.Context != null ? ScrubInlineCode(record.Context) : null;
        var scrubbedToolInvocationContext = record.ToolInvocationContext != null
            ? ScrubInlineCode(record.ToolInvocationContext)
            : null;

        // Build the issue body — structured fields only, with code scrubbed.
        // Issue 本文を構築 — 構造化フィールドのみ、コードは除去済み。
        var body = new StringBuilder();
        body.AppendLine("## Category");
        body.AppendLine(record.Category);
        body.AppendLine();
        body.AppendLine("## Language");
        body.AppendLine(record.Language ?? "N/A");
        body.AppendLine();
        body.AppendLine("## Evidence paths");
        var evidencePaths = NormalizeEvidencePaths(record.EvidencePaths);
        if (evidencePaths.Count == 0)
        {
            body.AppendLine("N/A");
        }
        else
        {
            foreach (var path in evidencePaths)
                body.AppendLine($"- {path}");
        }
        body.AppendLine();
        body.AppendLine("## Description");
        body.AppendLine(scrubbedDescription);
        body.AppendLine();
        body.AppendLine("## Context");
        body.AppendLine(scrubbedContext ?? "N/A");
        body.AppendLine();
        body.AppendLine("## Attribution");
        body.AppendLine($"Created by: {record.CreatedByAgent}");
        body.AppendLine($"Session: {record.SessionId}");
        body.AppendLine($"MCP client: {record.McpClientName ?? "N/A"}");
        body.AppendLine($"MCP client version: {record.McpClientVersion ?? "N/A"}");
        body.AppendLine($"Tool invocation context: {scrubbedToolInvocationContext ?? "N/A"}");
        body.AppendLine();
        body.AppendLine("---");
        body.AppendLine($"_Submitted by cdidx v{version}. Hash: `{record.Hash}`_");

        // Build the request payload / リクエストペイロードを構築
        var payload = new JsonObject
        {
            ["title"] = title,
            ["body"] = body.ToString(),
            ["labels"] = new JsonArray(BuildIssueLabels(record).Select(label => JsonValue.Create(label)).ToArray<JsonNode?>()),
        };

        var content = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        // Set auth header per-request (not on the shared HttpClient instance)
        // リクエスト単位で認証ヘッダーを設定（共有 HttpClient インスタンスには設定しない）
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await HttpClient.SendAsync(requestMessage, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadBoundedApiErrorBodyAsync(response.Content, cancellationToken);
            var rateLimitRetryAt = GetRateLimitRetryAt(response, DateTime.UtcNow);
            if (rateLimitRetryAt != null)
            {
                Console.Error.WriteLine(BuildRateLimitFailureMessage((int)response.StatusCode, errorBody, rateLimitRetryAt.Value));
                return SuggestionStore.SubmitAttemptResult.RetryAfter(
                    BuildRateLimitErrorDetail((int)response.StatusCode, errorBody, rateLimitRetryAt.Value),
                    rateLimitRetryAt.Value);
            }

            Console.Error.WriteLine(BuildApiFailureMessage((int)response.StatusCode, errorBody));
            return SuggestionStore.SubmitAttemptResult.Failure(BuildApiErrorDetail((int)response.StatusCode, errorBody));
        }

        // Parse response to extract the issue URL / レスポンスを解析して Issue URL を抽出
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseNode = JsonNode.Parse(responseJson);
        var issueUrl = responseNode?["html_url"]?.GetValue<string>();
        return issueUrl != null
            ? SuggestionStore.SubmitAttemptResult.Success(issueUrl)
            : SuggestionStore.SubmitAttemptResult.Failure("InvalidResponse: missing html_url");
    }

    /// <summary>
    /// Remove fenced code blocks and inline code spans from a string before
    /// external submission. Replaces code-shaped markdown with [code example removed].
    /// This is a stricter outbound policy than SourceCodeDetector's local policy:
    /// locally, inline code is useful for gap descriptions; externally, we strip
    /// it to prevent any code-like content from reaching GitHub.
    /// 外部送信前に fenced code block とインラインコードスパンを文字列から除去する。
    /// コード形の Markdown を [code example removed] に置換する。
    /// これは SourceCodeDetector のローカルポリシーより厳格な送信ポリシーである:
    /// ローカルではインラインコードはギャップ記述に有用だが、外部には GitHub に
    /// コード的内容が到達しないよう除去する。
    /// </summary>
    internal static string ScrubInlineCode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var (boundedText, wasTruncated) = BoundScrubInput(text);
        var scrubbed = ScrubFencedCodeBlocks(boundedText);
        scrubbed = ScrubSingleBacktickSpans(scrubbed);

        return wasTruncated
            ? scrubbed + ScrubInputTruncatedText
            : scrubbed;
    }

    private static (string Text, bool WasTruncated) BoundScrubInput(string text) =>
        text.Length <= MaxScrubInputLength
            ? (text, false)
            : (text[..MaxScrubInputLength], true);

    private static string ScrubFencedCodeBlocks(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var open = FindTripleBacktickFence(text, index);
            if (open < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, open - index);
            builder.Append(CodeExampleRemovedText);

            var close = FindTripleBacktickFence(text, open + 3);
            if (close < 0)
                break;

            index = close + 3;
        }

        return builder.ToString();
    }

    private static int FindTripleBacktickFence(string text, int start)
    {
        for (var i = start; i + 2 < text.Length; i++)
        {
            if (text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`')
                return i;
        }

        return -1;
    }

    internal static string BuildIssueTitle(string category, string description)
    {
        var sanitizedCategory = SanitizeIssueTitleText(category);
        if (sanitizedCategory.Length > 40)
            sanitizedCategory = sanitizedCategory[..40].TrimEnd();

        var prefix = $"[AI Suggestion] {sanitizedCategory}: ";
        if (prefix.Length >= MaxGitHubIssueTitleLength)
            return prefix[..MaxGitHubIssueTitleLength];

        var scrubbedForTitle = SanitizeIssueTitleText(ScrubInlineCode(description));
        var maxDescriptionLength = MaxGitHubIssueTitleLength - prefix.Length;
        var shortDesc = TruncateWithEllipsis(scrubbedForTitle, Math.Min(63, maxDescriptionLength));
        var title = prefix + shortDesc;
        return title.Length <= MaxGitHubIssueTitleLength
            ? title
            : title[..MaxGitHubIssueTitleLength];
    }

    internal static string[] BuildIssueLabels(SuggestionRecord record)
    {
        return record.Category is "crash_report" or "unexpected_error"
            ? ["bug"]
            : ["enhancement"];
    }

    private static List<string> NormalizeEvidencePaths(string[]? paths)
        => SuggestionEvidencePaths.Normalize(paths);

    internal static string SanitizeIssueTitleText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Replace("\r", " ").Replace("\n", " "))
        {
            if (c is '[' or ']' or '(' or ')' or '`')
                continue;
            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private static string TruncateWithEllipsis(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;
        if (maxLength <= 3)
            return value[..maxLength];
        return value[..(maxLength - 3)] + "...";
    }

    private static string ScrubSingleBacktickSpans(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] != '`' || IsEscaped(text, index) || IsTripleBacktickAt(text, index))
            {
                builder.Append(text[index]);
                index++;
                continue;
            }

            var close = FindInlineCodeClose(text, index + 1);
            if (close < 0)
            {
                builder.Append(CodeExampleRemovedText);
                break;
            }

            builder.Append(CodeExampleRemovedText);
            index = close + 1;
        }

        return builder.ToString();
    }

    private static int FindInlineCodeClose(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\r' || text[i] == '\n')
                return -1;
            if (text[i] != '`' || IsEscaped(text, i) || IsTripleBacktickAt(text, i))
                continue;

            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            var previous = i > start ? text[i - 1] : '\0';
            if ((char.IsWhiteSpace(previous) || previous == '=') &&
                char.IsLetterOrDigit(next) &&
                HasLaterInlineBacktick(text, i + 1))
                continue;

            while (i + 1 < text.Length && text[i + 1] == '`')
                i++;
            return i;
        }

        return -1;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
            slashCount++;
        return slashCount % 2 == 1;
    }

    private static bool HasLaterInlineBacktick(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\r' || text[i] == '\n')
                return false;
            if (text[i] == '`' && !IsEscaped(text, i) && !IsTripleBacktickAt(text, i))
                return true;
        }

        return false;
    }

    private static bool IsTripleBacktickAt(string text, int index)
        => index + 2 < text.Length && text[index + 1] == '`' && text[index + 2] == '`';

    internal static string BuildSubmissionFailureMessage(string detail) =>
        $"[cdidx] GitHub issue creation failed: {detail}. The suggestion stays recorded locally; check `CDIDX_GITHUB_TOKEN`, network access, and proxy environment variables (`HTTPS_PROXY`, `HTTP_PROXY`, `ALL_PROXY`, `NO_PROXY`), then retry `suggest_improvement` when ready.";

    internal static string BuildApiFailureMessage(int statusCode, string errorBody) =>
        $"[cdidx] GitHub API responded {BuildApiErrorDetail(statusCode, errorBody)}. GitHub submission was skipped; the suggestion stays local. Check `CDIDX_GITHUB_TOKEN`, repository permissions, or network access, then retry `suggest_improvement`.";

    internal static string BuildRateLimitFailureMessage(int statusCode, string errorBody, DateTime nextRetryAt) =>
        $"[cdidx] GitHub API rate limit response {BuildApiErrorDetail(statusCode, errorBody)}. GitHub submission was paused until {nextRetryAt:O}; the suggestion stays local and will not be retried before then.";

    internal static string BuildApiErrorDetail(int statusCode, string errorBody)
    {
        var normalized = SanitizeApiErrorBody(errorBody);
        if (normalized.Length > MaxGitHubApiErrorDetailLength)
            normalized = TruncateWithEllipsis(normalized, MaxGitHubApiErrorDetailLength);
        return $"{statusCode}: {normalized}";
    }

    private static async Task<string> ReadBoundedApiErrorBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        return await ReadBoundedApiErrorBodyAsync(stream, cancellationToken);
    }

    internal static async Task<string> ReadBoundedApiErrorBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxGitHubApiErrorBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
                break;
            total += read;
        }

        var bodyLength = Math.Min(total, MaxGitHubApiErrorBodyBytes);
        var body = Encoding.UTF8.GetString(buffer, 0, bodyLength);
        return total > MaxGitHubApiErrorBodyBytes
            ? body + ApiErrorBodyTruncatedText
            : body;
    }

    private static string SanitizeApiErrorBody(string errorBody)
    {
        var bounded = BoundApiErrorBodyForFormatting(errorBody);
        var sanitized = TryRedactSensitiveJsonFields(bounded, out var redactedJson)
            ? redactedJson
            : RedactSensitiveJsonLikeFields(bounded);
        var normalized = sanitized.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length == 0 ? "<empty response body>" : normalized;
    }

    private static string BoundApiErrorBodyForFormatting(string errorBody)
    {
        if (string.IsNullOrEmpty(errorBody))
            return string.Empty;

        return errorBody.Length <= MaxGitHubApiErrorBodyBytes
            ? errorBody
            : errorBody[..MaxGitHubApiErrorBodyBytes] + ApiErrorBodyTruncatedText;
    }

    private static bool TryRedactSensitiveJsonFields(string errorBody, out string redactedJson)
    {
        redactedJson = errorBody;
        try
        {
            var node = JsonNode.Parse(errorBody);
            if (node == null || !RedactSensitiveJsonFields(node))
                return false;

            redactedJson = node.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool RedactSensitiveJsonFields(JsonNode node)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (IsSensitiveApiErrorField(property.Key))
                {
                    obj[property.Key] = "[redacted]";
                    changed = true;
                    continue;
                }

                if (property.Value != null)
                    changed |= RedactSensitiveJsonFields(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item != null)
                    changed |= RedactSensitiveJsonFields(item);
            }
        }

        return changed;
    }

    private static bool IsSensitiveApiErrorField(string fieldName) =>
        fieldName.Contains("token", StringComparison.OrdinalIgnoreCase)
        || fieldName.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || fieldName.Contains("password", StringComparison.OrdinalIgnoreCase)
        || fieldName.Equals("authorization", StringComparison.OrdinalIgnoreCase)
        || fieldName.Equals("api_key", StringComparison.OrdinalIgnoreCase)
        || fieldName.Equals("private_key", StringComparison.OrdinalIgnoreCase);

    private static string RedactSensitiveJsonLikeFields(string errorBody)
    {
        try
        {
            return SensitiveJsonFieldPattern.Replace(
                errorBody,
                match => match.Groups[1].Value + "\"[redacted]\"");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[response body omitted after redaction timeout]";
        }
    }

    internal static string BuildRateLimitErrorDetail(int statusCode, string errorBody, DateTime nextRetryAt) =>
        $"{BuildApiErrorDetail(statusCode, errorBody)}; next_retry_at={nextRetryAt:O}";

    internal static DateTime? GetRateLimitRetryAt(HttpResponseMessage response, DateTime nowUtc)
    {
        var isRateLimited = response.StatusCode == (HttpStatusCode)429
            || (response.StatusCode == HttpStatusCode.Forbidden
                && response.Headers.TryGetValues("x-ratelimit-remaining", out var remainingValues)
                && remainingValues.Any(value => string.Equals(value, "0", StringComparison.Ordinal)));
        if (!isRateLimited)
            return null;

        if (response.Headers.RetryAfter?.Delta is { } delta)
            return nowUtc.Add(delta).ToUniversalTime();

        if (response.Headers.RetryAfter?.Date is { } retryDate)
            return retryDate.UtcDateTime;

        if (response.Headers.TryGetValues("x-ratelimit-reset", out var resetValues))
        {
            foreach (var value in resetValues)
            {
                if (long.TryParse(value, out var epochSeconds))
                    return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
            }
        }

        return nowUtc.Add(DefaultRateLimitRetryDelay);
    }
}
