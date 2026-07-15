using System.Text;

namespace CodeIndex.Mcp;

/// <summary>
/// Default MCP transport: LF-delimited JSON-RPC over stdin/stdout. This is not LSP
/// `Content-Length` framing. Mirrors the byte-for-byte behavior of the pre-#1558 inline loop
/// (strict UTF-8, BOM-less UTF-8 on output, 64 KiB buffer, AutoFlush) so existing clients keep
/// working unchanged.
/// 既定の MCP トランスポート: stdin/stdout 上の行区切り JSON-RPC。#1558 以前のインラインループと
/// 同じ I/O 挙動（strict UTF-8、出力 BOM なし UTF-8、64 KiB バッファ、AutoFlush）を維持し、
/// 既存クライアントを動かしたまま透過的に置き換える。
/// </summary>
internal sealed class StdioMcpTransport : IMcpTransport
{
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly int _maxLineCharacters;
    private readonly int _maxLineUtf8Bytes;
    private readonly object _disposeGate = new();
    private Task _disposeBarrier = Task.CompletedTask;
    private volatile bool _inputDisposed;
    private volatile bool _outputDisposed;
    private bool _disposeRequested;

    public StdioMcpTransport(int bufferSize)
        : this(Console.OpenStandardInput(), Console.OpenStandardOutput(), bufferSize)
    {
    }

    internal StdioMcpTransport(
        Stream stdin,
        Stream stdout,
        int bufferSize,
        int maxLineCharacters = McpServer.MaxLineCharacterCount,
        int maxLineUtf8Bytes = McpServer.MaxLineByteLength)
    {
        _stdin = stdin;
        _stdout = stdout;
        _maxLineCharacters = maxLineCharacters;
        _maxLineUtf8Bytes = maxLineUtf8Bytes;
        _reader = new StreamReader(
            _stdin,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: bufferSize);
        _writer = new StreamWriter(_stdout, new UTF8Encoding(false), bufferSize: bufferSize)
        {
            AutoFlush = true,
            // JSON-RPC stdio is line-delimited with LF on every host.
            NewLine = "\n",
        };
    }

    public string Name => "stdio";

    public string Endpoint => "stdin/stdout";

    public async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_inputDisposed, this);
        var line = await BoundedLineReader.ReadLineAsync(
            _reader,
            _maxLineCharacters,
            _maxLineUtf8Bytes,
            cancellationToken).ConfigureAwait(false);
        return line;
    }

    public async Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_outputDisposed, this);
        if (frame is null)
            return; // notifications produce no wire output on stdio.
        await _writer.WriteLineAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Delay output disposal until every request task that can still reach its response writer has
    /// completed. Bounded server teardown may intentionally return before that task; callers can
    /// still dispose the transport immediately without racing a late stdio write (#4543).
    /// response writer へ到達し得る request task がすべて完了するまで output dispose を遅延する。
    /// bounded teardown が task より先に return しても、caller の即時 Dispose と late stdio write
    /// が競合しないようにする (#4543)。
    /// </summary>
    internal void DeferDisposalUntil(Task completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_disposeGate)
        {
            if (_disposeRequested)
                return;
            _disposeBarrier = completion;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task outputBarrier;
        var disposeOutputNow = false;
        lock (_disposeGate)
        {
            if (_disposeRequested)
                return ValueTask.CompletedTask;
            _disposeRequested = true;
            _inputDisposed = true;
            outputBarrier = _disposeBarrier;
            if (outputBarrier.IsCompleted)
            {
                _outputDisposed = true;
                disposeOutputNow = true;
            }
        }

        // Schedule deferred output cleanup before touching input. A throwing custom input stream
        // must not strand `_disposeRequested` with no remaining path that can close stdout.
        // input を触る前に deferred output cleanup を登録し、custom input の Dispose が例外でも
        // stdout の cleanup 経路を失わないようにする。
        if (!disposeOutputNow)
            _ = DisposeOutputAfterBarrierAsync(outputBarrier);

        try
        {
            try
            {
                _reader.Dispose();
            }
            finally
            {
                _stdin.Dispose();
            }
        }
        finally
        {
            if (disposeOutputNow)
                DisposeOutputStreams();
        }
        return ValueTask.CompletedTask;
    }

    private async Task DisposeOutputAfterBarrierAsync(Task outputBarrier)
    {
        try
        {
            await outputBarrier.ConfigureAwait(false);
        }
        catch
        {
            // The server observes request faults separately; disposal must still run afterwards.
        }

        lock (_disposeGate)
        {
            if (_outputDisposed)
                return;
            _outputDisposed = true;
        }

        try
        {
            DisposeOutputStreams();
        }
        catch
        {
            // DisposeAsync already returned to keep teardown bounded. Late cleanup is best-effort.
        }
    }

    private void DisposeOutputStreams()
    {
        try
        {
            _writer.Dispose();
        }
        finally
        {
            _stdout.Dispose();
        }
    }
}
