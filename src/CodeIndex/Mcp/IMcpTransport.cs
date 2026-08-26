namespace CodeIndex.Mcp;

/// <summary>
/// Abstraction over the stream of JSON-RPC frames consumed and produced by the MCP server
/// (issue #1558). Each <see cref="ReadFrameAsync"/> call returns one client-to-server JSON-RPC
/// message (or null when the transport has closed); the matching <see cref="WriteFrameAsync"/>
/// call carries the server's response, or null when the request was a notification that yields
/// no response. The base contract is strictly one read followed by one write. Transports that
/// can safely bind multiple simultaneous pairs implement <see cref="IConcurrentMcpTransport"/>
/// and return a request-scoped response writer for each frame. Implementations must make
/// <see cref="IAsyncDisposable.DisposeAsync"/> idempotent and use it to release/cancel pending
/// transport work without requiring additional server-loop calls.
/// MCP サーバーが扱う JSON-RPC フレームの読み書きを抽象化する (issue #1558)。<see cref="ReadFrameAsync"/>
/// で 1 件のクライアント→サーバーメッセージを受け取り（クローズで null）、対応する
/// <see cref="WriteFrameAsync"/> でサーバー応答を返す（通知の場合は null）。基本契約では
/// 「読み 1 回 → 書き 1 回」のペアリングを厳密に守る。複数 pair を安全に紐付けられる transport は
/// <see cref="IConcurrentMcpTransport"/> を実装し、frame ごとの response writer を返す。実装は
/// <see cref="IAsyncDisposable.DisposeAsync"/> を冪等にし、追加の server loop 呼び出しなしに
/// 未完了の transport 作業を解放またはキャンセルする。
/// </summary>
internal interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Short identifier used in diagnostics / logs (e.g. "stdio", "http").</summary>
    string Name { get; }

    /// <summary>Human-readable endpoint description (e.g. "stdin/stdout", "http://127.0.0.1:38080/").</summary>
    string Endpoint { get; }

    /// <summary>Read the next JSON-RPC frame. Returns null when the transport has closed.</summary>
    Task<string?> ReadFrameAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Write the response for the most recent <see cref="ReadFrameAsync"/>, or null when the
    /// request was a notification (no response wire frame is produced). Must be called exactly
    /// once per successful read.
    /// 直前の <see cref="ReadFrameAsync"/> に対応する応答を書く。通知（応答なし）の場合は null
    /// を渡す。読み 1 回に対して必ず 1 回呼ぶ。
    /// </summary>
    Task WriteFrameAsync(string? frame, CancellationToken cancellationToken);
}

internal interface IOutOfBandMcpTransport
{
    Task WriteOutOfBandFrameAsync(string frame, CancellationToken cancellationToken);
}

/// <summary>
/// Optional transport capability for request/response pairs that can be processed concurrently.
/// Each returned context owns the response writer for exactly one input frame, so completion order
/// cannot make an HTTP response attach to a different request (#4536).
/// 並行処理できる request/response pair 向けの任意 transport capability。各 context は入力
/// frame 1 件専用の response writer を所有するため、完了順が変わっても HTTP 応答が別 request
/// に結び付くことはない (#4536)。
/// </summary>
internal interface IConcurrentMcpTransport
{
    Task<McpTransportFrame?> ReadConcurrentFrameAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Request-scoped transport state captured at read time. The server may execute several contexts
/// concurrently, but must call <see cref="WriteResponseAsync"/> exactly once for each context.
/// read 時に取得する request scope の transport state。server は複数 context を並行実行できるが、
/// 各 context の <see cref="WriteResponseAsync"/> は必ず 1 回だけ呼び出す。
/// </summary>
internal sealed class McpTransportFrame(
    string frame,
    Func<string?, CancellationToken, Task> writeResponseAsync,
    CancellationToken requestCancellationToken = default,
    Action<Task>? completeResourceRetentionWhen = null,
    McpCallerIdentity? authenticatedCallerIdentity = null)
{
    private Action<Task>? _completeResourceRetentionWhen = completeResourceRetentionWhen;

    internal string Frame { get; } = frame;

    internal Func<string?, CancellationToken, Task> WriteResponseAsync { get; } = writeResponseAsync;

    /// <summary>
    /// Cancellation lifetime owned by this transport frame. The server links it with its loop
    /// token so an HTTP disconnect or deadline cancels only the matching request (#4546).
    /// この transport frame が所有する cancellation lifetime。server loop token と連結し、
    /// HTTP 切断や期限切れで対応 request だけを cancel する (#4546)。
    /// </summary>
    internal CancellationToken RequestCancellationToken { get; } = requestCancellationToken;

    /// <summary>
    /// Principal established by the transport for this exact frame. A null value means the
    /// server authenticator is the only identity source. Transport identity takes precedence
    /// over a successful placeholder authenticator identity, but never bypasses a failed
    /// server authentication check.
    /// この frame に対して transport が確立した principal。null の場合は server authenticator
    /// だけを identity source とする。transport identity は成功した placeholder authenticator
    /// identity より優先するが、server authentication の失敗を迂回しない。
    /// </summary>
    internal McpCallerIdentity? AuthenticatedCallerIdentity { get; } = authenticatedCallerIdentity;

    /// <summary>
    /// Complete the transport's pre-attached resource-retention barrier after any work that
    /// outlives the response has settled. The callback is transferred at most once so every
    /// control-flow exit can safely attempt completion without releasing a request lease twice.
    /// response より長く残る work の完了後に、transport が事前接続した resource-retention
    /// barrier を完了する。callback は一度だけ移譲し、各終了経路から安全に完了を試行できる。
    /// </summary>
    internal void CompleteResourceRetentionWhen(Task completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        Interlocked.Exchange(ref _completeResourceRetentionWhen, null)?.Invoke(completion);
    }
}

/// <summary>
/// Optional transport-level response-frame ceiling. The MCP server uses this when an
/// individual method can page its own payload before the transport has to reject it.
/// 任意のtransport-level response frame上限。method側でpagination可能な場合に、
/// transportが拒否する前にpayloadを縮小するためMCP serverが参照する。
/// </summary>
internal interface IMcpResponseSizeLimitProvider
{
    int MaxResponseFrameBytes { get; }
}
