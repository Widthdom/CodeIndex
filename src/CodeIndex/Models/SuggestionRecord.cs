using System.Text.Json.Serialization;

namespace CodeIndex.Models;

/// <summary>
/// Lifecycle state for a locally recorded improvement suggestion.
/// ローカルに記録された改善提案のライフサイクル状態。
/// </summary>
public enum SuggestionStatus
{
    Draft,
    SubmittedPendingTriage,
    OpenInUpstream,
    ResolvedInUpstream,
    WontFix,
    Duplicate,
    Superseded,
}

/// <summary>
/// Represents a structured improvement suggestion from an AI agent.
/// AIエージェントからの構造化された改善提案を表す。
/// </summary>
public class SuggestionRecord
{
    /// <summary>
    /// Suggestion category. One of: symbol_extraction, reference_extraction,
    /// search_ranking, language_support, output_format, crash_report,
    /// unexpected_error, other.
    /// 提案カテゴリ。symbol_extraction, reference_extraction, search_ranking,
    /// language_support, output_format, crash_report, unexpected_error, other のいずれか。
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Target programming language (optional) / 対象プログラミング言語（任意）</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Description of the gap, improvement, or error situation.
    /// Must NOT contain source code — only natural language descriptions.
    /// ギャップ、改善、またはエラー状況の説明。
    /// ソースコードを含んではならない — 自然言語の記述のみ。
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// What the AI was trying to do when it noticed the gap (optional).
    /// Must NOT contain source code.
    /// AIがギャップに気づいたとき何をしようとしていたか（任意）。
    /// ソースコードを含んではならない。
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// Agent or tool name that recorded the suggestion (optional).
    /// 提案を記録したエージェントまたはツール名（任意）。
    /// </summary>
    public string? Agent { get; set; }

    /// <summary>
    /// SHA256 dedup hash of (category + language + normalized description).
    /// Prevents the same suggestion from being recorded twice.
    /// (category + language + 正規化済み description) のSHA256重複排除ハッシュ。
    /// 同一提案の二重記録を防ぐ。
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the suggestion was recorded / 提案が記録されたUTCタイムスタンプ</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Current lifecycle status / 現在のライフサイクル状態</summary>
    public SuggestionStatus Status { get; set; } = SuggestionStatus.Draft;

    /// <summary>Agent or client identity that created the suggestion / 提案を作成したエージェントまたはクライアントID</summary>
    public string CreatedByAgent { get; set; } = "unknown";

    /// <summary>Opaque cdidx MCP session identifier / cdidx MCP セッションの不透明ID</summary>
    public string SessionId { get; set; } = "unknown";

    /// <summary>cdidx version that recorded the suggestion / 提案を記録した cdidx バージョン</summary>
    public string ClientVersion { get; set; } = "unknown";

    /// <summary>MCP client name from initialize.clientInfo, when available / initialize.clientInfo 由来の MCP クライアント名（取得可能な場合）</summary>
    public string? McpClientName { get; set; }

    /// <summary>MCP client version from initialize.clientInfo, when available / initialize.clientInfo 由来の MCP クライアントバージョン（取得可能な場合）</summary>
    public string? McpClientVersion { get; set; }

    /// <summary>Optional natural-language invocation context supplied by the caller / 呼び出し元が渡す任意の自然言語コンテキスト</summary>
    public string? ToolInvocationContext { get; set; }

    /// <summary>One-line title extracted by MCP sampling, when available / MCP sampling で抽出された1行タイトル（取得可能な場合）</summary>
    public string? SampledTitle { get; set; }

    /// <summary>Structured tags extracted by MCP sampling, when available / MCP sampling で抽出された構造化タグ（取得可能な場合）</summary>
    public string[]? SampledTags { get; set; }

    /// <summary>Repository-relative paths that support the suggestion / 提案の根拠となるリポジトリ相対パス</summary>
    public string[]? EvidencePaths { get; set; }

    /// <summary>Upstream GitHub Issue number when known / 判明している場合の upstream GitHub Issue 番号</summary>
    public int? UpstreamIssueNumber { get; set; }

    /// <summary>Upstream GitHub Issue URL when known / 判明している場合の upstream GitHub Issue URL</summary>
    public string? UpstreamUrl { get; set; }

    /// <summary>UTC timestamp of the last upstream sync / 最後に upstream と同期したUTCタイムスタンプ</summary>
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>Last GitHub submission error, if any / 最後の GitHub 送信エラー（存在する場合）</summary>
    public string? LastSubmitError { get; set; }

    /// <summary>UTC timestamp of the last GitHub submission attempt / 最後に GitHub 送信を試行したUTCタイムスタンプ</summary>
    public DateTime? LastSubmitAttempt { get; set; }

    /// <summary>UTC timestamp when GitHub submission should be retried / GitHub 送信を再試行すべきUTCタイムスタンプ</summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>Number of GitHub submission attempts / GitHub 送信試行回数</summary>
    public int SubmitAttemptCount { get; set; }

    /// <summary>UTC timestamp when the suggestion was resolved / 提案が解決されたUTCタイムスタンプ</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Hash of an older suggestion superseded by this one / この提案が置き換える古い提案のハッシュ</summary>
    public string? Supersedes { get; set; }

    /// <summary>Hash of a newer suggestion that superseded this one / この提案を置き換えた新しい提案のハッシュ</summary>
    public string? SupersededBy { get; set; }

    /// <summary>Legacy submitted flag, read for migration only / 移行用に読み取る旧送信済みフラグ</summary>
    [JsonPropertyName("submitted_to_github")]
    public bool? SubmittedToGitHub { get; set; }

    /// <summary>Legacy GitHub Issue URL, read for migration only / 移行用に読み取る旧GitHub Issue URL</summary>
    [JsonPropertyName("github_issue_url")]
    public string? GitHubIssueUrl { get; set; }

    /// <summary>
    /// All valid category values.
    /// 有効なカテゴリ値の一覧。
    /// </summary>
    public static readonly string[] ValidCategories =
    {
        "symbol_extraction",
        "reference_extraction",
        "search_ranking",
        "language_support",
        "output_format",
        "crash_report",
        "unexpected_error",
        "other"
    };
}
