using System.Globalization;
using System.Text.Json;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static void WriteQuickfix(IEnumerable<(string Path, int Line, int Column, string Message)> items)
    {
        foreach (var item in items)
            Console.WriteLine($"{item.Path}:{item.Line}:{item.Column}:{item.Message}");
    }

    private static (string Path, int Line, int Column, string Message) ToSymbolQuickfixItem(SymbolResult result)
        => (result.Path, GetSymbolDisplayLine(result), 1, FormatSymbolLocationLabel(result));

    private static (string Path, int Line, int Column, string Message, string RuleId) ToSymbolSarifItem(SymbolResult result)
        => (result.Path, GetSymbolDisplayLine(result), 1, FormatSymbolLocationLabel(result), string.IsNullOrWhiteSpace(result.Kind) ? "symbol" : $"symbol.{result.Kind}");

    private static int GetSymbolDisplayLine(SymbolResult result)
        => Math.Max(1, result.Line > 0 ? result.Line : result.StartLine);

    private static string FormatSymbolLocationLabel(SymbolResult result)
    {
        var kind = string.IsNullOrWhiteSpace(result.Kind) ? "symbol" : result.Kind;
        return string.IsNullOrWhiteSpace(result.Name) ? kind : $"{kind} {result.Name}";
    }

    private static void WriteSarif(IEnumerable<(string Path, int Line, int Column, string Message, string RuleId)> items, JsonSerializerOptions jsonOptions)
    {
        var writer = Console.Out;
        var itemOptions = GetCompactJsonOptions(jsonOptions);
        var itemList = items.ToList();
        writer.Write("{\"version\":\"2.1.0\",\"runs\":[{\"tool\":{\"driver\":{\"name\":\"cdidx\",\"informationUri\":\"https://github.com/Widthdom/CodeIndex\",\"rules\":");
        WriteJsonArrayInline(
            itemList
                .Select(item => item.RuleId)
                .Where(ruleId => !string.IsNullOrWhiteSpace(ruleId))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(ruleId => ruleId, StringComparer.Ordinal),
            (ruleWriter, ruleId) => WriteSarifRule(ruleWriter, ruleId, itemOptions),
            separator: ",");
        writer.Write("}},\"results\":");
        WriteJsonArrayInline(
            itemList,
            (resultWriter, item) => WriteSarifResult(resultWriter, item, itemOptions),
            separator: ",");
        writer.WriteLine("}]}");
    }

    private static void WriteJsonArrayInline<T>(IEnumerable<T> items, Action<TextWriter, T> writeItem, string separator)
    {
        var writer = Console.Out;
        writer.Write('[');
        var first = true;
        foreach (var item in items)
        {
            if (!first)
                writer.Write(separator);
            writeItem(writer, item);
            first = false;
        }
        writer.Write(']');
    }

    private static void WriteSarifRule(TextWriter writer, string ruleId, JsonSerializerOptions jsonOptions)
    {
        writer.Write("{\"id\":");
        writer.Write(JsonSerializer.Serialize(ruleId, jsonOptions));
        writer.Write(",\"name\":");
        writer.Write(JsonSerializer.Serialize($"cdidx {ruleId}", jsonOptions));
        writer.Write(",\"shortDescription\":{\"text\":");
        writer.Write(JsonSerializer.Serialize($"cdidx {ruleId} result", jsonOptions));
        writer.Write("},\"fullDescription\":{\"text\":");
        writer.Write(JsonSerializer.Serialize("A machine-readable cdidx finding emitted from an indexed code query.", jsonOptions));
        writer.Write("},\"helpUri\":\"https://github.com/Widthdom/CodeIndex\",\"help\":{\"text\":");
        writer.Write(JsonSerializer.Serialize("Review the referenced location and surrounding code before filing or acting on this result.", jsonOptions));
        writer.Write("},\"defaultConfiguration\":{\"level\":\"warning\"},\"properties\":{\"tags\":[\"cdidx\",\"code-search\"]}}");
    }

    private static void WriteSarifResult(TextWriter writer, (string Path, int Line, int Column, string Message, string RuleId) item, JsonSerializerOptions jsonOptions)
    {
        writer.Write("{\"ruleId\":");
        writer.Write(JsonSerializer.Serialize(item.RuleId, jsonOptions));
        writer.Write(",\"level\":\"warning\",\"message\":{\"text\":");
        writer.Write(JsonSerializer.Serialize(item.Message, jsonOptions));
        writer.Write("},\"locations\":[{\"physicalLocation\":{\"artifactLocation\":{\"uri\":");
        writer.Write(JsonSerializer.Serialize(NormalizeSarifArtifactUri(item.Path), jsonOptions));
        writer.Write("},\"region\":{\"startLine\":");
        writer.Write(Math.Max(1, item.Line).ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"startColumn\":");
        writer.Write(Math.Max(1, item.Column).ToString(CultureInfo.InvariantCulture));
        writer.Write("}}}]}");
    }

    private static string NormalizeSarifArtifactUri(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }
}
