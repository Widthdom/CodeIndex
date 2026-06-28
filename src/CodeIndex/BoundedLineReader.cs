using System.Text;
using System.Runtime.CompilerServices;
using CodeIndex.Diagnostics;

namespace CodeIndex;

internal sealed class BoundedLineLengthException : IOException
{
    internal BoundedLineLengthException(int charactersRead, int utf8BytesRead, int maxCharacters, int maxUtf8Bytes)
        : base($"Line exceeds the {maxCharacters} character or {maxUtf8Bytes} byte cap.")
    {
        CharactersRead = charactersRead;
        Utf8BytesRead = utf8BytesRead;
        MaxCharacters = maxCharacters;
        MaxUtf8Bytes = maxUtf8Bytes;
    }

    internal int CharactersRead { get; }

    internal int Utf8BytesRead { get; }

    internal int MaxCharacters { get; }

    internal int MaxUtf8Bytes { get; }
}

internal enum BoundedTextFileReadFailureKind
{
    None,
    BytesExceeded,
    LinesExceeded,
    LineLengthExceeded,
    ReadFailed,
}

internal readonly record struct BoundedTextFileReadFailure(
    BoundedTextFileReadFailureKind Kind,
    string Reason,
    int? LineNumber = null,
    int? CharactersRead = null,
    int? Limit = null,
    string? ExceptionType = null);

internal static class WorkerProtocolLineLimits
{
    // Worker requests carry source content as JSON. Keep this above the default 4 MiB source
    // file cap after JSON escaping while still bounding line-protocol memory growth.
    internal const int MaxLineCharacters = 32 * 1024 * 1024;
    internal const int MaxLineUtf8Bytes = 32 * 1024 * 1024;
    internal const int MaxExtendedLineCharacters = 384 * 1024 * 1024;
    internal const int MaxExtendedLineUtf8Bytes = 384 * 1024 * 1024;
    private const long JsonEscapedCharacterBytes = 6;
    private const long ProtocolEnvelopeBytes = 1024 * 1024;

    internal static int ResolveForSourceFileBytes(long? maxFileSizeBytes)
    {
        if (maxFileSizeBytes is not > 0)
            return MaxLineUtf8Bytes;

        var largestUncappedFileBytes = (MaxExtendedLineUtf8Bytes - ProtocolEnvelopeBytes) / JsonEscapedCharacterBytes;
        if (maxFileSizeBytes.Value >= largestUncappedFileBytes)
            return MaxExtendedLineUtf8Bytes;

        var required = checked(maxFileSizeBytes.Value * JsonEscapedCharacterBytes + ProtocolEnvelopeBytes);
        if (required <= MaxLineUtf8Bytes)
            return MaxLineUtf8Bytes;

        return (int)required;
    }
}

internal static class BoundedLineReader
{
    private const int AsyncReadBufferSize = 4096;
    private static readonly ConditionalWeakTable<TextReader, AsyncReadBuffer> AsyncBuffers = new();

    internal static bool TryReadUtf8File(
        string path,
        int maxBytes,
        int maxLines,
        int maxLineCharacters,
        out IReadOnlyList<string> lines,
        out BoundedTextFileReadFailure failure,
        Func<string, Stream>? openFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "Maximum file bytes must be positive.");
        if (maxLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLines), maxLines, "Maximum line count must be positive.");
        if (maxLineCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLineCharacters), maxLineCharacters, "Maximum line characters must be positive.");

        lines = [];
        failure = default;

        try
        {
            using var stream = openFile?.Invoke(path)
                ?? BoundedFile.OpenReadForLengthCheckedText(path);

            if (stream.CanSeek && stream.Length > maxBytes)
            {
                failure = new(
                    BoundedTextFileReadFailureKind.BytesExceeded,
                    $"it exceeds {maxBytes} bytes",
                    Limit: maxBytes);
                return false;
            }

            using var accumulator = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, maxBytes) : 0);
            var buffer = new byte[8192];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    failure = new(
                        BoundedTextFileReadFailureKind.BytesExceeded,
                        $"it exceeds {maxBytes} bytes",
                        Limit: maxBytes);
                    return false;
                }

                accumulator.Write(buffer, 0, read);
            }

            var text = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(accumulator.ToArray());
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text[1..];

            var result = new List<string>();
            using var reader = new StringReader(text);
            while (true)
            {
                string? line;
                try
                {
                    line = ReadLine(reader, maxLineCharacters, maxBytes);
                }
                catch (BoundedLineLengthException ex)
                {
                    var lineNumber = result.Count + 1;
                    failure = new(
                        BoundedTextFileReadFailureKind.LineLengthExceeded,
                        $"line {lineNumber} exceeds {maxLineCharacters} characters",
                        lineNumber,
                        ex.CharactersRead,
                        maxLineCharacters);
                    return false;
                }

                if (line == null)
                    break;

                if (result.Count >= maxLines)
                {
                    failure = new(
                        BoundedTextFileReadFailureKind.LinesExceeded,
                        $"it exceeds {maxLines} lines",
                        Limit: maxLines);
                    return false;
                }

                result.Add(line);
            }

            lines = result;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var reason = $"it could not be read ({ex.GetType().Name}: {DiagnosticSanitizer.ForMessage(ex.Message)})";
            failure = new(
                BoundedTextFileReadFailureKind.ReadFailed,
                reason,
                ExceptionType: ex.GetType().Name);
            return false;
        }
    }

    internal static string? ReadLine(TextReader reader, int maxCharacters, int maxUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var state = new LineState(maxCharacters, maxUtf8Bytes);

        while (true)
        {
            var value = reader.Read();
            if (value < 0)
                return state.HasAnyInput ? state.CompleteLine() : null;

            if (state.Process((char)value, out var line))
                return line;
        }
    }

    internal static async Task<string?> ReadLineAsync(
        TextReader reader,
        int maxCharacters,
        int maxUtf8Bytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var state = new LineState(maxCharacters, maxUtf8Bytes);
        var asyncBuffer = AsyncBuffers.GetValue(reader, _ => new AsyncReadBuffer(AsyncReadBufferSize));

        while (true)
        {
            while (asyncBuffer.TryReadBufferedChar(out var bufferedChar))
            {
                if (state.Process(bufferedChar, out var bufferedLine))
                    return bufferedLine;
            }

            var buffer = asyncBuffer.Buffer;
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return state.HasAnyInput ? state.CompleteLine() : null;

            for (var index = 0; index < read; index++)
            {
                if (!state.Process(buffer[index], out var line))
                    continue;

                asyncBuffer.StoreRemainder(index + 1, read);
                return line;
            }
        }
    }

    private sealed class AsyncReadBuffer(int size)
    {
        private int _start;
        private int _length;

        internal char[] Buffer { get; } = new char[size];

        internal bool TryReadBufferedChar(out char ch)
        {
            if (_length == 0)
            {
                ch = default;
                return false;
            }

            ch = Buffer[_start++];
            _length--;
            if (_length == 0)
                _start = 0;
            return true;
        }

        internal void StoreRemainder(int start, int read)
        {
            _start = start;
            _length = Math.Max(0, read - start);
        }
    }

    private sealed class LineState
    {
        private readonly int _maxCharacters;
        private readonly int _maxUtf8Bytes;
        private readonly StringBuilder _builder = new();
        private readonly Encoder _utf8Encoder = Encoding.UTF8.GetEncoder();
        private int _charactersRead;
        private int _utf8BytesRead;
        private bool _pendingCarriageReturn;

        internal LineState(int maxCharacters, int maxUtf8Bytes)
        {
            if (maxCharacters <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCharacters), maxCharacters, "Maximum line characters must be positive.");
            if (maxUtf8Bytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes), maxUtf8Bytes, "Maximum line UTF-8 bytes must be positive.");

            _maxCharacters = maxCharacters;
            _maxUtf8Bytes = maxUtf8Bytes;
        }

        internal bool HasAnyInput { get; private set; }

        internal bool Process(char ch, out string? line)
        {
            HasAnyInput = true;
            line = null;

            if (_pendingCarriageReturn)
            {
                if (ch == '\n')
                {
                    _pendingCarriageReturn = false;
                    line = CompleteLine();
                    return true;
                }

                Append('\r');
                _pendingCarriageReturn = false;
            }

            if (ch == '\r')
            {
                _pendingCarriageReturn = true;
                return false;
            }

            if (ch == '\n')
            {
                line = CompleteLine();
                return true;
            }

            Append(ch);
            return false;
        }

        internal string CompleteLine()
        {
            _pendingCarriageReturn = false;
            FlushEncoder();
            return _builder.ToString();
        }

        private void Append(char ch)
        {
            _builder.Append(ch);
            _charactersRead++;
            _utf8BytesRead += CountUtf8Bytes(ch, flush: false);
            ThrowIfExceeded();
        }

        private void FlushEncoder()
        {
            _utf8BytesRead += CountUtf8Bytes(default, flush: true);
            ThrowIfExceeded();
        }

        private int CountUtf8Bytes(char ch, bool flush)
        {
            Span<byte> bytes = stackalloc byte[8];
            if (flush)
            {
                _utf8Encoder.Convert(ReadOnlySpan<char>.Empty, bytes, flush: true, out _, out var bytesUsed, out _);
                return bytesUsed;
            }

            Span<char> chars = stackalloc char[1];
            chars[0] = ch;
            _utf8Encoder.Convert(chars, bytes, flush: false, out _, out var used, out _);
            return used;
        }

        private void ThrowIfExceeded()
        {
            if (_charactersRead > _maxCharacters || _utf8BytesRead > _maxUtf8Bytes)
                throw new BoundedLineLengthException(_charactersRead, _utf8BytesRead, _maxCharacters, _maxUtf8Bytes);
        }
    }
}
