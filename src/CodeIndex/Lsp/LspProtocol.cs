using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Security;

namespace CodeIndex.Lsp;

internal static class LspProtocol
{
    internal const int MaxFrameBytes = 8 * 1024 * 1024;
    internal const int MaxResponseFrameBytes = MaxFrameBytes;
    internal const int MaxHeaderLineBytes = 8 * 1024;
    internal const int MaxHeaderCount = 64;
    internal const int MaxHeaderBytes = 64 * 1024;
    internal const int MaxPooledPayloadBufferBytes = 1024 * 1024;
    internal const int MaxRequestIdRawBytes = 4 * 1024;
    internal const int MaxJsonDepth = 32;
    internal const int MaxRequestIdStringChars = 256;
    internal const string ReadDiagnosticEndOfStream = "end_of_stream";
    internal const string ReadDiagnosticIncompleteHeader = "incomplete_header";
    internal const string ReadDiagnosticHeaderLineTooLarge = "header_line_too_large";
    internal const string ReadDiagnosticHeaderSectionTooLarge = "header_section_too_large";
    internal const string ReadDiagnosticDuplicateContentLength = "duplicate_content_length";
    internal const string ReadDiagnosticMalformedContentLength = "malformed_content_length";
    internal const string ReadDiagnosticNegativeContentLength = "negative_content_length";
    internal const string ReadDiagnosticContentLengthTooLarge = "content_length_too_large";
    internal const string ReadDiagnosticMissingContentLength = "missing_content_length";
    internal const string ReadDiagnosticIncompleteBody = "incomplete_body";
    private static readonly JsonReaderOptions JsonReaderOptions = new()
    {
        MaxDepth = MaxJsonDepth,
    };

    internal readonly record struct MessageReadResult(bool Success, string Payload);

    internal readonly record struct ReadDiagnostic(
        string Code,
        string Message,
        int? ContentLength = null,
        int? MaxContentLength = null);

    internal readonly record struct FrameReadResult(bool Success, string Payload, ReadDiagnostic? Diagnostic);

    private readonly record struct HeaderLineReadResult(string? Line, HeaderLineReadFailure Failure);

    private readonly struct SensitivePayloadBufferLease : IDisposable
    {
        private readonly bool _rented;
        private readonly int _usedBytes;

        internal SensitivePayloadBufferLease(byte[] buffer, int usedBytes, bool rented)
        {
            Buffer = buffer;
            _usedBytes = usedBytes;
            _rented = rented;
        }

        internal byte[] Buffer { get; }

        public void Dispose()
        {
            SensitiveBufferPolicy.ReturnSensitivePayloadBuffer(Buffer, _usedBytes, _rented);
        }
    }

    private enum HeaderLineReadFailure
    {
        None,
        EndOfStream,
        IncompleteHeader,
        LineTooLarge,
    }

    internal static bool TryParseRequestId(string payload, JsonElement idElement, out JsonNode? id, out string errorMessage)
    {
        id = null;
        errorMessage = "Invalid Request";
        if (!TryGetTopLevelRequestIdRawByteCount(payload, out var rawIdBytes) || rawIdBytes > MaxRequestIdRawBytes)
        {
            errorMessage = $"Request id must be {MaxRequestIdRawBytes} raw JSON bytes or fewer.";
            return false;
        }

        var rawId = idElement.GetRawText();
        if (Encoding.UTF8.GetByteCount(rawId) > MaxRequestIdRawBytes)
        {
            errorMessage = $"Request id must be {MaxRequestIdRawBytes} raw JSON bytes or fewer.";
            return false;
        }

        return TryCloneRequestId(idElement, out id);
    }

    internal static string FormatParseErrorMessage(string payload)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"Parse error (payload_bytes={Encoding.UTF8.GetByteCount(payload)}, max_json_depth={MaxJsonDepth})");

    internal static bool ShouldRentPayloadBuffer(int byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "Byte count must be non-negative.");
        return byteCount <= MaxPooledPayloadBufferBytes;
    }

    internal static void ClearSensitivePayloadBufferForTests(byte[] buffer, int usedBytes) =>
        SensitiveBufferPolicy.ClearUsedSensitiveBytes(buffer, usedBytes);

    internal static bool TryReadMessage(Stream input, out string payload) =>
        TryReadMessage(input, out payload, CancellationToken.None);

    internal static bool TryReadMessage(Stream input, out string payload, CancellationToken cancellationToken)
        => TryReadMessage(input, out payload, out _, cancellationToken);

    internal static bool TryReadMessage(
        Stream input,
        out string payload,
        out ReadDiagnostic? diagnostic,
        CancellationToken cancellationToken = default)
    {
        var result = TryReadMessageCoreAsync(input, cancellationToken).AsTask().GetAwaiter().GetResult();
        payload = result.Payload;
        diagnostic = result.Diagnostic;
        return result.Success;
    }

    internal static async ValueTask<MessageReadResult> TryReadMessageAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        var result = await TryReadMessageCoreAsync(input, cancellationToken).ConfigureAwait(false);
        return new MessageReadResult(result.Success, result.Payload);
    }

    internal static void WriteMessage(Stream output, string payload)
    {
        if (!TryWriteMessage(output, payload, out _))
            throw new InvalidOperationException($"LSP response body exceeded the {MaxResponseFrameBytes} byte limit.");
    }

    internal static bool TryWriteMessage(Stream output, string payload, out int bodyBytes)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        bodyBytes = body.Length;
        if (body.Length > MaxResponseFrameBytes)
            return false;

        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        output.Write(header);
        output.Write(body);
        output.Flush();
        return true;
    }

    internal static async Task<bool> TryWriteMessageAsync(Stream output, string payload, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        if (body.Length > MaxResponseFrameBytes)
            return false;

        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await output.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(body.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool TryCloneRequestId(JsonElement idElement, out JsonNode? id)
    {
        id = null;
        switch (idElement.ValueKind)
        {
            case JsonValueKind.String:
                var value = idElement.GetString();
                if (value == null || value.Length > MaxRequestIdStringChars)
                    return false;
                id = JsonValue.Create(value);
                return true;

            case JsonValueKind.Number:
                if (!idElement.TryGetInt64(out var number))
                    return false;
                id = JsonValue.Create(number);
                return true;

            case JsonValueKind.Null:
                return true;

            default:
                return false;
        }
    }

    private static bool TryGetTopLevelRequestIdRawByteCount(string payload, out int rawIdBytes)
    {
        rawIdBytes = 0;
        var payloadByteCount = Encoding.UTF8.GetByteCount(payload);
        using var lease = RentSensitivePayloadBuffer(payloadByteCount);
        var buffer = lease.Buffer;
        try
        {
            _ = Encoding.UTF8.GetBytes(payload.AsSpan(), buffer);
            var reader = new Utf8JsonReader(buffer.AsSpan(0, payloadByteCount), JsonReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return true;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                    continue;

                var isId = reader.ValueTextEquals("id"u8);
                if (!reader.Read())
                    return false;

                var valueStart = reader.TokenStartIndex;
                reader.Skip();
                if (isId)
                {
                    var rawLength = reader.BytesConsumed - valueStart;
                    if (rawLength > int.MaxValue)
                        return false;
                    rawIdBytes = (int)rawLength;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static SensitivePayloadBufferLease RentSensitivePayloadBuffer(int byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "Byte count must be non-negative.");
        var rented = ShouldRentPayloadBuffer(byteCount);
        var buffer = rented
            ? ArrayPool<byte>.Shared.Rent(byteCount)
            : new byte[byteCount];
        return new SensitivePayloadBufferLease(buffer, byteCount, rented);
    }

    private static async ValueTask<FrameReadResult> TryReadMessageCoreAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        var contentLength = -1;
        var hasContentLength = false;
        var headerCount = 0;
        var headerBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await ReadAsciiLineAsync(input, cancellationToken).ConfigureAwait(false);
            if (line.Line == null)
                return new FrameReadResult(false, string.Empty, CreateHeaderReadDiagnostic(line.Failure));
            if (line.Line.Length == 0)
                break;
            headerCount++;
            headerBytes += line.Line.Length;
            if (headerCount > MaxHeaderCount || headerBytes > MaxHeaderBytes)
            {
                var diagnostic = new ReadDiagnostic(
                    ReadDiagnosticHeaderSectionTooLarge,
                    $"LSP headers exceeded {MaxHeaderCount} lines or {MaxHeaderBytes} bytes.");
                return new FrameReadResult(false, string.Empty, diagnostic);
            }
            var colon = line.Line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line.Line[..colon].Trim();
            var value = line.Line[(colon + 1)..].Trim();
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (hasContentLength)
                {
                    var diagnostic = new ReadDiagnostic(
                        ReadDiagnosticDuplicateContentLength,
                        "LSP frame contained more than one Content-Length header.");
                    return new FrameReadResult(false, string.Empty, diagnostic);
                }

                if (value.StartsWith("-", StringComparison.Ordinal))
                {
                    var diagnostic = new ReadDiagnostic(
                        ReadDiagnosticNegativeContentLength,
                        "LSP Content-Length must not be negative.");
                    return new FrameReadResult(false, string.Empty, diagnostic);
                }

                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    var diagnostic = new ReadDiagnostic(
                        ReadDiagnosticMalformedContentLength,
                        "LSP Content-Length must be a base-10 byte count.");
                    return new FrameReadResult(false, string.Empty, diagnostic);
                }

                if (parsed > MaxFrameBytes)
                {
                    var diagnostic = new ReadDiagnostic(
                        ReadDiagnosticContentLengthTooLarge,
                        $"LSP Content-Length exceeded the {MaxFrameBytes} byte limit.",
                        parsed,
                        MaxFrameBytes);
                    return new FrameReadResult(false, string.Empty, diagnostic);
                }

                hasContentLength = true;
                contentLength = parsed;
            }
        }

        if (contentLength < 0)
        {
            var diagnostic = new ReadDiagnostic(
                ReadDiagnosticMissingContentLength,
                "LSP frame did not include a Content-Length header.");
            return new FrameReadResult(false, string.Empty, diagnostic);
        }

        using var lease = RentSensitivePayloadBuffer(contentLength);
        var buffer = lease.Buffer;
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await ReadAsync(input, buffer, offset, contentLength - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                var diagnostic = new ReadDiagnostic(
                    ReadDiagnosticIncompleteBody,
                    "LSP frame ended before the declared Content-Length body was complete.",
                    contentLength,
                    MaxFrameBytes);
                return new FrameReadResult(false, string.Empty, diagnostic);
            }
            offset += read;
        }

        return new FrameReadResult(true, Encoding.UTF8.GetString(buffer, 0, contentLength), null);
    }

    private static ReadDiagnostic CreateHeaderReadDiagnostic(HeaderLineReadFailure failure) => failure switch
    {
        HeaderLineReadFailure.LineTooLarge => new ReadDiagnostic(
            ReadDiagnosticHeaderLineTooLarge,
            $"LSP header line exceeded the {MaxHeaderLineBytes} byte limit."),
        HeaderLineReadFailure.IncompleteHeader => new ReadDiagnostic(
            ReadDiagnosticIncompleteHeader,
            "LSP frame ended before the header section was complete."),
        _ => new ReadDiagnostic(
            ReadDiagnosticEndOfStream,
            "LSP input ended before a frame was available."),
    };

    private static async ValueTask<HeaderLineReadResult> ReadAsciiLineAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxHeaderLineBytes + 1);
        var length = 0;
        try
        {
            while (true)
            {
                var read = await ReadAsync(input, buffer, length, 1, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    var failure = length == 0
                        ? HeaderLineReadFailure.EndOfStream
                        : HeaderLineReadFailure.IncompleteHeader;
                    return new HeaderLineReadResult(null, failure);
                }

                var value = buffer[length];
                if (value == '\n')
                    break;
                if (value != '\r')
                {
                    if (length >= MaxHeaderLineBytes)
                    {
                        return new HeaderLineReadResult(null, HeaderLineReadFailure.LineTooLarge);
                    }
                    length++;
                }
            }

            return new HeaderLineReadResult(Encoding.ASCII.GetString(buffer, 0, length), HeaderLineReadFailure.None);
        }
        finally
        {
            SensitiveBufferPolicy.ReturnNonSensitiveProtocolBuffer(buffer);
        }
    }

    private static ValueTask<int> ReadAsync(Stream input, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return input.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }
}
