using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeIndex.Diagnostics;

internal readonly record struct JsonFrameParseDiagnostic(string Reason, string Detail, int MaxDepth);

internal static class JsonFrameParser
{
    internal const int MaxParseDiagnosticChars = 240;

    internal static JsonDocumentOptions CreateDocumentOptions(int maxDepth)
    {
        if (maxDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "JSON max depth must be non-negative.");

        return new JsonDocumentOptions { MaxDepth = maxDepth };
    }

    internal static JsonNode? ParseNode(string frame, int maxDepth)
        => JsonNode.Parse(frame, documentOptions: CreateDocumentOptions(maxDepth));

    internal static bool TryParseNode(
        string frame,
        int maxDepth,
        out JsonNode? node,
        out JsonFrameParseDiagnostic diagnostic)
    {
        try
        {
            node = ParseNode(frame, maxDepth);
            diagnostic = default;
            return true;
        }
        catch (JsonException ex)
        {
            node = null;
            diagnostic = CreateDiagnostic(ex, maxDepth);
            return false;
        }
    }

    internal static JsonFrameParseDiagnostic CreateDiagnostic(JsonException ex, int maxDepth)
        => new("json_parse_error", FormatExceptionDetail(ex), maxDepth);

    internal static string FormatExceptionDetail(JsonException ex)
        => DiagnosticRedactor.BoundDiagnosticText(DiagnosticSanitizer.ForMessage(ex.Message), MaxParseDiagnosticChars);
}
