namespace CodeIndex.Database;

/// <summary>
/// Shared contract for top-level machine-readable results that carry the current JSON API version.
/// 機械可読なトップレベル結果に現在の JSON API version を付与するための共通契約。
/// </summary>
public interface IVersionedJsonResult
{
    string ApiVersion { get; }
}

/// <summary>
/// Versioned identifier stamped onto CodeIndex CLI/MCP JSON output payloads via the
/// <c>api_version</c> field on top-level DTOs and on the <c>--json-envelope</c> metadata.
/// Bump on breaking changes (rename, remove, or type-change a field). Additive changes
/// (new optional fields) keep the version stable. Exposed publicly so external consumers
/// can pin against a known contract value. Issue #1555.
/// CodeIndex の CLI/MCP JSON 出力に付与するスキーマ版数。フィールドの rename/remove/型変更
/// など破壊的変更で bump し、追加のみの変更では据え置く。Issue #1555。
/// </summary>
public static class JsonOutputContract
{
    /// <summary>
    /// Current API version string. Stamped on each top-level JSON DTO via
    /// <c>api_version</c> and on the <c>--json-envelope</c> metadata block.
    /// </summary>
    public const string ApiVersion = "1";
}
