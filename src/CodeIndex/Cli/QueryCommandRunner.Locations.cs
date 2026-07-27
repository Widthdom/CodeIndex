using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static void AttachLspLocations(IEnumerable<DefinitionResult> results)
    {
        foreach (var result in results)
        {
            var location = ToLspLocation(result);
            result.Uri = location.Uri;
            result.Range = location.Range;
        }
    }

    public static void AttachLspLocations(IEnumerable<ReferenceResult> results)
    {
        foreach (var result in results)
        {
            var location = BuildLspLocation(result.Path, result.Line, result.Column, result.Line, result.Column + 1);
            result.Uri = location.Uri;
            result.Range = location.Range;
        }
    }

    public static LspLocation BuildLspLocation(string path, int startLine, int startColumn, int endLine, int endColumn, string? projectRoot = null)
    {
        var baseRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? s_activeQueryProjectRoot ?? Environment.CurrentDirectory
            : projectRoot;
        var absolutePath = Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(path, baseRoot);
        return new LspLocation
        {
            Uri = new Uri(absolutePath).AbsoluteUri,
            Range = new LspRange
            {
                Start = new LspPosition
                {
                    Line = Math.Max(0, startLine - 1),
                    Character = Math.Max(0, startColumn - 1),
                },
                End = new LspPosition
                {
                    Line = Math.Max(0, endLine - 1),
                    Character = Math.Max(0, endColumn - 1),
                },
            },
        };
    }

    private static LspLocation ToLspLocation(DefinitionResult result)
        => BuildSymbolLspLocation(result);

    private static LspLocation ToLspLocation(ReferenceResult result)
        => BuildLspLocation(result.Path, result.Line, result.Column, result.Line, result.Column + Math.Max(1, result.SymbolName.Length));

    private static LspLocation ToLspLocation(SearchResult result)
        => BuildLspLocation(result.Path, result.StartLine, 1, result.EndLine + 1, 1);

    private static LspLocation ToLspLocation(FileFindResult result)
        => BuildLspLocation(result.Path, result.Line, result.Column, result.Line, result.Column + Math.Max(1, result.Length));

    private static LspLocation ToLspLocation(FileIssue result)
    {
        var line = Math.Max(1, result.Line);
        var location = BuildLspLocation(result.Path, line, 1, line, 2);
        location.Kind = result.Kind;
        location.Message = result.Message;
        location.Severity = string.IsNullOrWhiteSpace(result.Severity) ? FileIssue.SeverityWarning : result.Severity;
        location.Source = "cdidx validate";
        return location;
    }

    private static LspLocation ToLspLocation(SymbolResult result)
        => BuildSymbolLspLocation(result);

    private static LspLocation BuildSymbolLspLocation(SymbolResult result)
    {
        var line = result.Line > 0 ? result.Line : Math.Max(1, result.StartLine);
        var column = result.StartColumn.HasValue ? result.StartColumn.Value + 1 : 1;
        return BuildLspLocation(result.Path, line, column, line, column + Math.Max(1, result.Name.Length));
    }

    private static LspLocation ToLspLocation(CallerResult result)
        => BuildLspLocation(result.Path, result.FirstLine, Math.Max(1, result.FirstColumn), result.FirstLine, Math.Max(1, result.FirstColumn) + Math.Max(1, result.CalleeName.Length));

    private static LspLocation ToLspLocation(CalleeResult result)
        => BuildLspLocation(result.Path, result.FirstLine, 1, result.FirstLine, 1);

    private static void WriteLspLocations(IEnumerable<LspLocation> locations, JsonSerializerOptions jsonOptions)
    {
        var itemOptions = GetCompactJsonOptions(jsonOptions);
        var context = CliJsonSerializerContextFactory.Create(itemOptions);
        var locationList = locations.ToList();
        if (locationList.Count == 0)
        {
            var payload = new JsonArray();
            AddActiveSqliteDiagnostics(payload);
            CommandOutputWriter.WriteJsonNode(payload, jsonOptions);
            return;
        }

        WriteJsonArray(
            locationList,
            (writer, location) => writer.Write(SerializeQueryJson(location, context.LspLocation, itemOptions)),
            jsonOptions);
    }

    private static bool TryWriteEmptyFormattedResult(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        JsonObject? sarifRunProperties = null)
    {
        if (options.OutputFormat == OutputFormatCount)
        {
            WriteFormattedCount(0, jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCompact)
        {
            WriteCompactLocations([], options, jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv)
        {
            WriteDelimitedLocations([], options.OutputFormat);
            return true;
        }
        if (options.OutputFormat == OutputFormatLsp)
        {
            WriteLspLocations([], jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatQf)
            return true;
        if (options.OutputFormat == OutputFormatSarif)
        {
            WriteSarif(Array.Empty<SarifLocation>(), jsonOptions, runProperties: sarifRunProperties);
            return true;
        }
        return false;
    }

    private sealed record FormattedLocation(string File, int Line, int? Column = null, string? Label = null);

    private static bool TryWriteFormattedLocations(QueryCommandOptions options, IEnumerable<FormattedLocation> locations, JsonSerializerOptions jsonOptions)
    {
        if (options.OutputFormat == OutputFormatCount)
        {
            WriteFormattedCount(locations.Count(), jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCompact)
        {
            WriteCompactLocations(locations, options, jsonOptions);
            return true;
        }
        if (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv)
        {
            WriteDelimitedLocations(locations, options.OutputFormat);
            return true;
        }
        return false;
    }

    private static void WriteFormattedCount(int count, JsonSerializerOptions jsonOptions)
    {
        var payload = new JsonObject
        {
            ["count"] = count,
            ["total_estimated"] = count,
        };
        AddActiveSqliteDiagnostics(payload);
        CommandOutputWriter.WriteJsonNode(payload, jsonOptions);
    }

    private static void WriteCompactLocations(IEnumerable<FormattedLocation> locations, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var payload = BuildCompactLocationsPayload(locations, options, jsonOptions);
        AddActiveSqliteDiagnostics(payload);
        CommandOutputWriter.WriteJsonNode(payload, jsonOptions);
    }

    private static JsonObject BuildCompactLocationsPayload(IEnumerable<FormattedLocation> locations, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var rows = new JsonArray();
        foreach (var location in locations)
        {
            var row = new JsonObject
            {
                ["file"] = location.File,
                ["line"] = location.Line,
            };
            if (location.Column.HasValue)
                row["column"] = location.Column.Value;
            rows.Add(row);
        }

        var limitReached = rows.Count >= options.Limit;
        return new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["format"] = OutputFormatCompact,
            ["count"] = rows.Count,
            ["truncated"] = limitReached,
            ["truncation"] = new JsonObject
            {
                ["limit"] = options.Limit,
                ["limit_reached"] = limitReached,
            },
            ["query_context"] = BuildQueryContextJson(options, jsonOptions),
            ["results"] = rows,
        };
    }

    private static void WriteDelimitedLocations(IEnumerable<FormattedLocation> locations, string outputFormat)
    {
        var delimiter = outputFormat == OutputFormatTsv ? "\t" : ",";
        Console.WriteLine(string.Join(delimiter, ["file", "line", "column", "label"]));
        foreach (var location in locations)
        {
            var values = new[]
            {
                location.File,
                location.Line.ToString(CultureInfo.InvariantCulture),
                location.Column?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                location.Label ?? string.Empty,
            };
            Console.WriteLine(string.Join(delimiter, values.Select(value => EscapeDelimitedValue(value, outputFormat))));
        }
    }
}
