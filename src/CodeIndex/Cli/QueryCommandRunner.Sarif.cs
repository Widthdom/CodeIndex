using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        => (result.Path, GetSymbolDisplayLine(result), GetSymbolDisplayColumn(result), FormatSymbolLocationLabel(result));

    private static (string Path, int Line, int Column, string Message, string RuleId) ToSymbolSarifItem(SymbolResult result)
        => (result.Path, GetSymbolDisplayLine(result), GetSymbolDisplayColumn(result), FormatSymbolLocationLabel(result), string.IsNullOrWhiteSpace(result.Kind) ? "symbol" : $"symbol.{result.Kind}");

    private static int GetSymbolDisplayColumn(SymbolResult result)
        => result.StartColumn.HasValue ? result.StartColumn.Value + 1 : 1;

    private static int GetSymbolDisplayLine(SymbolResult result)
        => Math.Max(1, result.Line > 0 ? result.Line : result.StartLine);

    private static string FormatSymbolLocationLabel(SymbolResult result)
    {
        var kind = string.IsNullOrWhiteSpace(result.Kind) ? "symbol" : result.Kind;
        return string.IsNullOrWhiteSpace(result.Name) ? kind : $"{kind} {result.Name}";
    }

    private static void WriteSarif(
        IEnumerable<(string Path, int Line, int Column, string Message, string RuleId)> items,
        JsonSerializerOptions jsonOptions,
        string level = "warning")
        => WriteSarif(
            items.Select(item => new SarifLocation(item.Path, item.Line, item.Column, null, item.Message, item.RuleId)),
            jsonOptions,
            level);

    private static void WriteSarif(
        IEnumerable<SarifLocation> items,
        JsonSerializerOptions jsonOptions,
        string level = "warning",
        JsonObject? runProperties = null)
    {
        var writer = Console.Out;
        var itemOptions = GetCompactJsonOptions(jsonOptions);
        var itemList = items.ToList();
        writer.Write("{\"version\":\"2.1.0\",\"runs\":[{\"tool\":{\"driver\":{\"name\":\"cdidx\",\"informationUri\":\"https://github.com/Widthdom/CodeIndex\",\"rules\":");
        WriteJsonArrayInline(
            itemList
                .Where(item => !string.IsNullOrWhiteSpace(item.RuleId))
                .GroupBy(item => item.RuleId, StringComparer.Ordinal)
                .Select(group => (RuleId: group.Key, Level: GetHighestSarifLevel(group.Select(item => item.Level ?? level))))
                .OrderBy(rule => rule.RuleId, StringComparer.Ordinal),
            (ruleWriter, rule) => WriteSarifRule(ruleWriter, rule.RuleId, rule.Level, itemOptions),
            separator: ",");
        writer.Write("}},\"results\":");
        WriteJsonArrayInline(
            itemList,
            (resultWriter, item) => WriteSarifResult(resultWriter, item, item.Level ?? level, itemOptions),
            separator: ",");
        if (runProperties is { Count: > 0 })
        {
            writer.Write(",\"properties\":");
            writer.Write(runProperties.ToJsonString(itemOptions));
        }
        writer.Write("}]");
        WriteActiveSqliteDiagnosticsProperties(writer, itemOptions);
        writer.WriteLine('}');
    }

    private static string GetHighestSarifLevel(IEnumerable<string> levels)
    {
        var highest = "none";
        var highestRank = 0;
        foreach (var level in levels)
        {
            var rank = level switch
            {
                "error" => 3,
                "warning" => 2,
                "note" => 1,
                _ => 0,
            };
            if (rank <= highestRank)
                continue;
            highest = level;
            highestRank = rank;
        }
        return highest;
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

    private static void WriteSarifRule(TextWriter writer, string ruleId, string level, JsonSerializerOptions jsonOptions)
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
        writer.Write("},\"defaultConfiguration\":{\"level\":");
        writer.Write(JsonSerializer.Serialize(level, jsonOptions));
        writer.Write("},\"properties\":{\"tags\":[\"cdidx\",\"code-search\"]}}");
    }

    private static void WriteSarifResult(TextWriter writer, SarifLocation item, string level, JsonSerializerOptions jsonOptions)
    {
        writer.Write("{\"ruleId\":");
        writer.Write(JsonSerializer.Serialize(item.RuleId, jsonOptions));
        writer.Write(",\"level\":");
        writer.Write(JsonSerializer.Serialize(level, jsonOptions));
        writer.Write(",\"message\":{\"text\":");
        writer.Write(JsonSerializer.Serialize(item.Message, jsonOptions));
        writer.Write("},\"locations\":[{\"physicalLocation\":{\"artifactLocation\":{\"uri\":");
        writer.Write(JsonSerializer.Serialize(NormalizeSarifArtifactUri(item.Path), jsonOptions));
        writer.Write("},\"region\":{\"startLine\":");
        writer.Write(Math.Max(1, item.Line).ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"startColumn\":");
        writer.Write(Math.Max(1, item.Column).ToString(CultureInfo.InvariantCulture));
        if (item.EndColumn.HasValue)
        {
            writer.Write(",\"endColumn\":");
            writer.Write(Math.Max(Math.Max(1, item.Column) + 1, item.EndColumn.Value).ToString(CultureInfo.InvariantCulture));
        }
        writer.Write("}}}]");
        if (item.Properties is { Count: > 0 })
        {
            writer.Write(",\"properties\":");
            writer.Write(item.Properties.ToJsonString(jsonOptions));
        }
        writer.Write('}');
    }

    private static string NormalizeSarifArtifactUri(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }

    private sealed record SarifLocation(
        string Path,
        int Line,
        int Column,
        int? EndColumn,
        string Message,
        string RuleId,
        string? Level = null,
        JsonObject? Properties = null);
}
