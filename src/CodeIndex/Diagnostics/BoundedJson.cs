using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeIndex.Diagnostics;

internal static class BoundedJson
{
    internal static JsonDocumentOptions CreateDocumentOptions(
        int maxDepth,
        JsonCommentHandling commentHandling = JsonCommentHandling.Disallow,
        bool allowTrailingCommas = false)
    {
        if (maxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "JSON max depth must be positive.");

        return new JsonDocumentOptions
        {
            MaxDepth = maxDepth,
            CommentHandling = commentHandling,
            AllowTrailingCommas = allowTrailingCommas,
        };
    }

    internal static JsonDocument ParseDocument(
        string json,
        int maxUtf8Bytes,
        int maxDepth,
        JsonCommentHandling commentHandling = JsonCommentHandling.Disallow,
        bool allowTrailingCommas = false)
    {
        ValidateStringPayload(json, maxUtf8Bytes);
        return JsonDocument.Parse(json, CreateDocumentOptions(maxDepth, commentHandling, allowTrailingCommas));
    }

    internal static JsonDocument ParseDocument(
        ReadOnlyMemory<byte> utf8Json,
        int maxUtf8Bytes,
        int maxDepth,
        JsonCommentHandling commentHandling = JsonCommentHandling.Disallow,
        bool allowTrailingCommas = false)
    {
        ValidateBytePayload(utf8Json.Length, maxUtf8Bytes);
        return JsonDocument.Parse(utf8Json, CreateDocumentOptions(maxDepth, commentHandling, allowTrailingCommas));
    }

    internal static JsonNode? ParseNode(
        string json,
        int maxUtf8Bytes,
        int maxDepth,
        JsonCommentHandling commentHandling = JsonCommentHandling.Disallow,
        bool allowTrailingCommas = false)
    {
        ValidateStringPayload(json, maxUtf8Bytes);
        return JsonNode.Parse(json, documentOptions: CreateDocumentOptions(maxDepth, commentHandling, allowTrailingCommas));
    }

    internal static T? Deserialize<T>(
        string json,
        int maxUtf8Bytes,
        JsonSerializerOptions options)
    {
        ValidateStringPayload(json, maxUtf8Bytes);
        return JsonSerializer.Deserialize<T>(json, options);
    }

    internal static T? Deserialize<T>(
        ReadOnlySpan<byte> utf8Json,
        int maxUtf8Bytes,
        JsonSerializerOptions options)
    {
        ValidateBytePayload(utf8Json.Length, maxUtf8Bytes);
        return JsonSerializer.Deserialize<T>(utf8Json, options);
    }

    internal static string FormatExceptionDetail(JsonException ex)
        => JsonFrameParser.FormatExceptionDetail(ex);

    private static void ValidateStringPayload(string json, int maxUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(json);
        ValidateByteLimit(maxUtf8Bytes);
        if (json.Length > maxUtf8Bytes)
            ThrowPayloadTooLarge(maxUtf8Bytes);
        if (Encoding.UTF8.GetByteCount(json) > maxUtf8Bytes)
            ThrowPayloadTooLarge(maxUtf8Bytes);
    }

    private static void ValidateBytePayload(int byteCount, int maxUtf8Bytes)
    {
        ValidateByteLimit(maxUtf8Bytes);
        if (byteCount > maxUtf8Bytes)
            ThrowPayloadTooLarge(maxUtf8Bytes);
    }

    private static void ValidateByteLimit(int maxUtf8Bytes)
    {
        if (maxUtf8Bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes), maxUtf8Bytes, "JSON byte limit must be positive.");
    }

    private static void ThrowPayloadTooLarge(int maxUtf8Bytes)
        => throw new InvalidDataException($"JSON payload exceeds the {maxUtf8Bytes} byte limit.");
}
