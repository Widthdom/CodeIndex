using System.Text.Json;

namespace CodeIndex.Lsp;

internal sealed partial class LspServer : IDisposable
{
    private readonly record struct LspPosition(int Line, int Character);
    private readonly record struct LspRange(LspPosition Start, LspPosition End);

    private static void ValidateCoordinateParameters(string method, JsonElement root)
    {
        switch (method)
        {
            case "textDocument/definition":
            case "textDocument/declaration":
            case "textDocument/references":
            case "textDocument/hover":
            case "textDocument/completion":
            case "textDocument/documentHighlight":
                _ = ReadRequiredLspPosition(root, "params", "position");
                break;
            case "textDocument/inlayHint":
                _ = ReadRequiredLspRange(root, "params", "range");
                break;
            case "textDocument/didChange":
                ValidateContentChangeRanges(root);
                break;
        }
    }

    private static LspPosition ReadRequiredLspPosition(JsonElement root, params string[] path)
    {
        if (!TryGet(root, out var position, path))
            throw new ArgumentException("LSP position is required.");
        return ReadRequiredLspPosition(position);
    }

    private static LspPosition ReadRequiredLspPosition(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Object
            || !position.TryGetProperty("line", out var lineElement)
            || lineElement.ValueKind != JsonValueKind.Number
            || !lineElement.TryGetInt32(out var line)
            || line < 0
            || !position.TryGetProperty("character", out var characterElement)
            || characterElement.ValueKind != JsonValueKind.Number
            || !characterElement.TryGetInt32(out var character)
            || character < 0)
        {
            throw new ArgumentException("LSP position must contain non-negative integer line and character values.");
        }

        return new LspPosition(line, character);
    }

    private static LspRange ReadRequiredLspRange(JsonElement root, params string[] path)
    {
        if (!TryGet(root, out var range, path))
            throw new ArgumentException("LSP range is required.");
        return ReadRequiredLspRange(range);
    }

    private static LspRange ReadRequiredLspRange(JsonElement range)
    {
        if (range.ValueKind != JsonValueKind.Object
            || !range.TryGetProperty("start", out var startElement)
            || !range.TryGetProperty("end", out var endElement))
        {
            throw new ArgumentException("LSP range must contain start and end positions.");
        }

        var start = ReadRequiredLspPosition(startElement);
        var end = ReadRequiredLspPosition(endElement);
        if (ComparePosition(start.Line, start.Character, end.Line, end.Character) > 0)
            throw new ArgumentException("LSP range start must not follow its end.");
        return new LspRange(start, end);
    }

    private static void ValidateContentChangeRanges(JsonElement root)
    {
        if (!TryGet(root, out var changes, "params", "contentChanges")
            || changes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var change in changes.EnumerateArray())
        {
            if (change.ValueKind == JsonValueKind.Object
                && change.TryGetProperty("range", out var range))
            {
                _ = ReadRequiredLspRange(range);
            }
        }
    }

    private static int ComparePosition(int leftLine, int leftCharacter, int rightLine, int rightCharacter)
        => leftLine != rightLine ? leftLine.CompareTo(rightLine) : leftCharacter.CompareTo(rightCharacter);

    private static int ToOneBasedLspLine(int line)
        => line == int.MaxValue ? int.MaxValue : line + 1;
}
