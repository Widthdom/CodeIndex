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
    private bool _disposed;

    public StdioMcpTransport(int bufferSize)
        : this(Console.OpenStandardInput(), Console.OpenStandardOutput(), bufferSize)
    {
    }

    internal StdioMcpTransport(Stream stdin, Stream stdout, int bufferSize)
    {
        _stdin = stdin;
        _stdout = stdout;
        _reader = new StreamReader(
            _stdin,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: bufferSize);
        _writer = new StreamWriter(_stdout, new UTF8Encoding(false), bufferSize: bufferSize) { AutoFlush = true };
    }

    public string Name => "stdio";

    public string Endpoint => "stdin/stdout";

    public async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // ReadLineAsync's CancellationToken overload was added in .NET 7; the legacy overload
        // remains the call shape used by the existing MCP loop, so we keep it here too and
        // honour cancellation only when the writer fails. Stdin closure is the canonical exit.
        // ReadLineAsync の CancellationToken 版は .NET 7 で追加されたが、既存ループと同じ
        // 呼び出し形を使い、stdin クローズを正規の終了経路として保つ。
        var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line;
    }

    public async Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame is null)
            return; // notifications produce no wire output on stdio.
        await _writer.WriteLineAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
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
