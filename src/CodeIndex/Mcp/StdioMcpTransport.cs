using System.Text;

namespace CodeIndex.Mcp;

/// <summary>
/// Default MCP transport: line-delimited JSON-RPC over stdin/stdout. Mirrors the byte-for-byte
/// behavior of the pre-#1558 inline loop (strict UTF-8, BOM-less UTF-8 on
/// output, 64 KiB buffer, AutoFlush) so existing clients keep working unchanged.
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
    private bool _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        var line = await BoundedLineReader.ReadLineAsync(
            _reader,
            _maxLineCharacters,
            _maxLineUtf8Bytes,
            cancellationToken).ConfigureAwait(false);
        return line;
    }

    public async Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame is null)
            return; // notifications produce no wire output on stdio.
        await _writer.WriteLineAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _reader.Dispose();
        _writer.Dispose();
        _stdin.Dispose();
        _stdout.Dispose();
        return ValueTask.CompletedTask;
    }
}
