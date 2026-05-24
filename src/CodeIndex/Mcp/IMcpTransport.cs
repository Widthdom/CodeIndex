namespace CodeIndex.Mcp;

/// <summary>
/// Abstraction over the stream of JSON-RPC frames consumed and produced by the MCP server
/// (issue #1558). Each <see cref="ReadFrameAsync"/> call returns one client-to-server JSON-RPC
/// message (or null when the transport has closed); the matching <see cref="WriteFrameAsync"/>
/// call carries the server's response, or null when the request was a notification that yields
/// no response. The contract is strictly one read followed by one write — the MCP loop is
/// single-threaded today, and pluggable transports rely on that pairing to map a request body
/// to its response on connection-oriented transports such as HTTP.
/// MCP サーバーが扱う JSON-RPC フレームの読み書きを抽象化する (issue #1558)。<see cref="ReadFrameAsync"/>
/// で 1 件のクライアント→サーバーメッセージを受け取り（クローズで null）、対応する
/// <see cref="WriteFrameAsync"/> でサーバー応答を返す（通知の場合は null）。MCP ループは現状
/// 単一スレッドであり、HTTP のようにリクエストとレスポンスを紐付ける必要があるため、
/// 「読み 1 回 → 書き 1 回」のペアリングを厳密に守る。
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
