using System.Globalization;
using System.Text.Json;

namespace CodeIndex.Cli;

internal static class DiffResultWriter
{
    internal static void WriteResult(DiffJsonResult result, DiffCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (options.SummaryOnly)
            WriteSummaryJson(result, jsonOptions);
        else if (options.Json)
            WriteJson(result, jsonOptions);
        else
            WriteText(result, options);
    }

    internal static int WriteCommandError(bool json, JsonSerializerOptions jsonOptions, string message, int exitCode, string? hint = null, string? errorCode = null)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(
                new CommandErrorJsonResult("error", message, hint, errorCode),
                CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult));
        else
        {
            CommandErrorWriter.WriteStderr($"Error [{errorCode ?? CommandErrorCodes.UsageError}]: {message}");
            if (!string.IsNullOrWhiteSpace(hint))
                CommandErrorWriter.WriteStderr($"Hint: {hint}");
        }
        return exitCode;
    }

    internal static string FormatDelta(long delta)
        => delta >= 0 ? $"+{delta}" : delta.ToString(CultureInfo.InvariantCulture);

    private static void WriteJson(DiffJsonResult result, JsonSerializerOptions jsonOptions)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            result,
            CliJsonSerializerContextFactory.Create(jsonOptions).DiffJsonResult));
    }

    private static void WriteSummaryJson(DiffJsonResult result, JsonSerializerOptions jsonOptions)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new DiffSummaryOnlyJsonResult(result.Status, result.Identical, result.LeftDb, result.RightDb, result.Summary),
            CliJsonSerializerContextFactory.Create(jsonOptions).DiffSummaryOnlyJsonResult));
    }

    private static void WriteText(DiffJsonResult result, DiffCommandOptions options)
    {
        Console.WriteLine("Index database diff");
        Console.WriteLine($"  left   : {result.LeftDb}");
        Console.WriteLine($"  right  : {result.RightDb}");
        Console.WriteLine($"  status : {result.Status}");
        Console.WriteLine($"  schema : {result.Summary.LeftSchemaVersion} -> {result.Summary.RightSchemaVersion}");
        Console.WriteLine($"  files  : {result.Summary.LeftFileCount} -> {result.Summary.RightFileCount} ({FormatDelta(result.Summary.FileCountDelta)})");
        Console.WriteLine($"  symbols: {result.Summary.LeftSymbolCount} -> {result.Summary.RightSymbolCount} ({FormatDelta(result.Summary.SymbolCountDelta)})");
        Console.WriteLine($"  refs   : {result.Summary.LeftReferenceCount} -> {result.Summary.RightReferenceCount} ({FormatDelta(result.Summary.ReferenceCountDelta)})");

        WriteList("files only in left", result.FilesOnlyInLeft);
        WriteList("files only in right", result.FilesOnlyInRight);
        if (options.Detailed)
        {
            WriteList("symbols only in left", result.SymbolsOnlyInLeft ?? []);
            WriteList("symbols only in right", result.SymbolsOnlyInRight ?? []);
        }
    }

    private static void WriteList(string label, List<string> values)
    {
        if (values.Count == 0)
            return;
        Console.WriteLine($"  {label}:");
        foreach (var value in values)
            Console.WriteLine($"    - {value}");
    }
}
