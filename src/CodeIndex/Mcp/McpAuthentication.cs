using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

/// <summary>
/// Caller identity attached to an authenticated MCP request. <see cref="Source"/> names the
/// transport / authentication scheme (e.g. <c>stdio</c>, <c>stdio-token</c>) and
/// <see cref="Subject"/> names the specific principal (e.g. <c>local</c>, <c>token</c>).
/// Networked transports and audit logs (#1562) can reuse the same shape without rewiring
/// the dispatch path.
/// 認証済みリクエストに紐づく呼び出し元アイデンティティ。<see cref="Source"/> は transport /
/// 認証方式（例: <c>stdio</c>, <c>stdio-token</c>）、<see cref="Subject"/> は具体的な
/// プリンシパル（例: <c>local</c>, <c>token</c>）を表す。後続のネットワーク transport や
/// 監査ログ（#1562）は dispatch を書き換えずに同じ形を再利用できる。
/// </summary>
public sealed record McpCallerIdentity(string Source, string Subject)
{
    /// <summary>
    /// Shared identity for the default stdio transport. Cached so allocations stay flat in the
    /// hot dispatch path.
    /// 既定 stdio transport の共有アイデンティティ。dispatch ホットパスのアロケーションを
    /// 抑えるためキャッシュする。
    /// </summary>
    public static readonly McpCallerIdentity LocalStdio = new("stdio", "local");
}

/// <summary>
/// Outcome of an <see cref="IMcpAuthenticator"/> check. Either an authenticated identity
/// (<see cref="Identity"/>) or a redacted machine-readable failure reason
/// (<see cref="FailureReason"/>) — never both. The failure reason is for stderr diagnostics,
/// not the JSON-RPC wire (the wire response only carries <c>Unauthorized</c> per #1530).
/// <see cref="IMcpAuthenticator"/> チェックの結果。認証済みアイデンティティ
/// (<see cref="Identity"/>) と機械可読の失敗理由 (<see cref="FailureReason"/>) はどちらか
/// 一方のみが set される。失敗理由は stderr 診断用で、JSON-RPC のワイヤ応答には載らない
/// (#1530 に従い "Unauthorized" のみ返す)。
/// </summary>
public sealed record McpAuthenticationResult(McpCallerIdentity? Identity, string? FailureReason)
{
    public bool IsAuthenticated => Identity is not null;

    public static McpAuthenticationResult Allow(McpCallerIdentity identity) => new(identity, null);
    public static McpAuthenticationResult Deny(string reason) => new(null, reason);
}

/// <summary>
/// Per-request authentication strategy for the MCP server. Implementations look at the
/// incoming JSON-RPC request envelope and produce an identity or a failure reason. The
/// existing stdio default is permissive (<see cref="LocalStdioAuthenticator"/>) so unconfigured
/// deployments keep working; setting <c>CDIDX_MCP_AUTH_TOKEN</c> swaps in
/// <see cref="TokenMcpAuthenticator"/> which enforces a constant-time token check.
/// MCP サーバー向けのリクエスト単位認証戦略。実装は受信した JSON-RPC リクエストエンベロープ
/// を見てアイデンティティもしくは失敗理由を返す。既定の stdio は permissive
/// (<see cref="LocalStdioAuthenticator"/>) なので未設定のデプロイは従来通り動作する。
/// <c>CDIDX_MCP_AUTH_TOKEN</c> を設定すると <see cref="TokenMcpAuthenticator"/> が
/// 有効になり、定数時間のトークン比較を強制する。
/// </summary>
public interface IMcpAuthenticator
{
    /// <summary>
    /// Authenticate a JSON-RPC request. Implementations MUST be side-effect free and MUST
    /// NOT throw on malformed input — wrap parsing in try/catch and return
    /// <see cref="McpAuthenticationResult.Deny"/> instead, so the server can return a
    /// uniform <c>Unauthorized</c> response.
    /// JSON-RPC リクエストを認証する。実装は副作用フリーで、不正入力でも例外を投げない
    /// こと (パース失敗は <see cref="McpAuthenticationResult.Deny"/> で返し、サーバーは
    /// 統一された <c>Unauthorized</c> 応答にできる)。
    /// </summary>
    McpAuthenticationResult Authenticate(JsonNode request);
}

/// <summary>
/// Default authenticator for the stdio transport. The OS-enforced process boundary already
/// gates access, so every request is allowed and every caller maps to the shared
/// <see cref="McpCallerIdentity.LocalStdio"/> identity. Networked transports must replace
/// this with a real authenticator before exposing the server.
/// stdio transport 用の既定 authenticator。OS のプロセス境界がアクセスを既に絞っているため、
/// 全リクエストを許可し、呼び出し元は共有の <see cref="McpCallerIdentity.LocalStdio"/>
/// にマップする。ネットワーク transport を露出する場合はこれを実 authenticator に
/// 差し替えること。
/// </summary>
public sealed class LocalStdioAuthenticator : IMcpAuthenticator
{
    public static readonly LocalStdioAuthenticator Instance = new();

    private LocalStdioAuthenticator() { }

    public McpAuthenticationResult Authenticate(JsonNode request)
        => McpAuthenticationResult.Allow(McpCallerIdentity.LocalStdio);
}

/// <summary>
/// Token authenticator. Each JSON-RPC request must include a matching token at
/// <c>params.auth.token</c>. The expected token is stored as a fixed-length SHA-256 digest
/// and the presented token is hashed to the same length before comparison with
/// <see cref="CryptographicOperations.FixedTimeEquals(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte})"/>;
/// equal-length inputs keep the compare on its constant-time path and the hash step erases
/// the length-leak that the raw-byte form would otherwise have (FixedTimeEquals short-circuits
/// on length mismatch). The hash-and-compare runs unconditionally — even on a missing token —
/// so callers cannot distinguish "missing", "wrong length", and "wrong value" by timing.
/// Identity is <c>stdio-token</c> / <c>token</c>. Notifications skip the check upstream because
/// they produce no response and cannot be acted on.
/// トークン認証 authenticator。各 JSON-RPC リクエストは <c>params.auth.token</c> に
/// 一致するトークンを含む必要がある。期待トークンは固定長 (SHA-256 32 バイト) のダイジェスト
/// として保持し、提示トークンも同じ長さにハッシュしてから
/// <see cref="CryptographicOperations.FixedTimeEquals(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte})"/>
/// で比較する。これにより比較は常に等長で定数時間パスに留まり、生バイト比較なら漏れる長さ情報も
/// ハッシュ段階で隠れる (FixedTimeEquals は長さ不一致で即 return する)。ハッシュ＋比較は
/// トークン未提示でも必ず走らせるため、呼び出し元は「未提示」「長さ違い」「値違い」を時間差で
/// 区別できない。アイデンティティは <c>stdio-token</c> / <c>token</c>。通知は応答が
/// 無く副作用も持たないので呼び出し側でチェック前にスキップされる。
/// </summary>
public sealed class TokenMcpAuthenticator : IMcpAuthenticator
{
    public const string AuthSource = "stdio-token";
    public const string AuthSubject = "token";

    private static readonly McpCallerIdentity TokenIdentity = new(AuthSource, AuthSubject);

    private readonly byte[] _expectedTokenHash;

    public TokenMcpAuthenticator(string expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
            throw new ArgumentException("Token must not be empty", nameof(expectedToken));
        _expectedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
    }

    public McpAuthenticationResult Authenticate(JsonNode request)
    {
        if (request is not JsonObject obj)
            return McpAuthenticationResult.Deny("request is not a JSON object");

        string? presented;
        try
        {
            presented = obj["params"]?["auth"]?["token"]?.GetValue<string>();
        }
        catch
        {
            // Shape mismatch on `auth` / `token` (e.g. caller sent an object or array instead
            // of a string). Treat as missing rather than crashing, so the wire response stays
            // uniform.
            // `auth` / `token` の型不整合（例: 文字列ではなくオブジェクトや配列）を欠落扱いに
            // し、ワイヤ応答を統一する。
            presented = null;
        }

        // Always hash and constant-time compare — even when no token was presented — so the
        // missing / wrong-length / wrong-value branches all take the same path. Choosing the
        // stderr `Deny` reason after the compare runs keeps the timing uniform; the wire
        // response is "Unauthorized" either way.
        // トークン未提示でも必ずハッシュ＋定数時間比較を走らせる。Deny 理由の選択は比較後に
        // 行い、stderr 用文字列だけ分岐させる（ワイヤ応答は常に "Unauthorized"）。
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented ?? string.Empty));
        var matches = CryptographicOperations.FixedTimeEquals(presentedHash, _expectedTokenHash);

        if (string.IsNullOrEmpty(presented))
            return McpAuthenticationResult.Deny("missing auth token");
        if (!matches)
            return McpAuthenticationResult.Deny("auth token mismatch");

        return McpAuthenticationResult.Allow(TokenIdentity);
    }
}

/// <summary>
/// Pick the authenticator based on environment configuration. With
/// <c>CDIDX_MCP_AUTH_TOKEN</c> unset (the default), keep the historical stdio behaviour
/// (<see cref="LocalStdioAuthenticator"/>). When set to a non-whitespace value, enforce
/// token authentication on every request (<see cref="TokenMcpAuthenticator"/>). This is
/// the only public composition surface the CLI uses; tests inject authenticators directly.
/// 環境変数に応じて authenticator を選ぶ。<c>CDIDX_MCP_AUTH_TOKEN</c> 未設定（既定）では
/// 従来の stdio 動作を維持する (<see cref="LocalStdioAuthenticator"/>)。空白以外の値が
/// セットされていれば全リクエストにトークン認証を強制する (<see cref="TokenMcpAuthenticator"/>)。
/// CLI 側はこの公開合成 API のみを使い、テストは authenticator を直接 inject する。
/// </summary>
public static class McpAuthenticatorFactory
{
    public const string AuthTokenEnvVar = "CDIDX_MCP_AUTH_TOKEN";

    public static IMcpAuthenticator FromEnvironment()
    {
        var token = Environment.GetEnvironmentVariable(AuthTokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
            return LocalStdioAuthenticator.Instance;
        return new TokenMcpAuthenticator(token);
    }
}
