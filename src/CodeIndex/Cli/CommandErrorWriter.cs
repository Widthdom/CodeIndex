using System.Text.Json;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

internal static class CommandErrorWriter
{
    internal const string DefaultHint = "Run '<cmd> --help' for usage information.";
    private const int SanitizedExceptionTypeNameLimit = 120;

    internal static void WriteStdout(string message = "")
        => CommandOutputWriter.WriteLine(message);

    internal static void WriteStderr(string? message = "")
        => Console.Error.WriteLine(message);

    internal static void WriteWarning(string message)
        => WriteStderr($"Warning: {message}");

    internal static void Write(string message, string? hint = null, string? usage = null, string? errorCode = null)
    {
        var prefix = errorCode is null ? "Error" : $"Error [{errorCode}]";
        WriteStderr($"{prefix}: {message}");
        WriteStderr($"Hint: {hint ?? DefaultHint}");
        if (usage != null)
            WriteStderr($"Usage: {usage}");
    }

    internal static int Write(
        string message,
        int exitCode,
        string? hint = null,
        string? usage = null,
        string? errorCode = null)
    {
        Write(message, hint, usage, errorCode);
        return exitCode;
    }

    internal static int WriteJsonOrHuman(
        bool json,
        JsonSerializerOptions jsonOptions,
        string message,
        int exitCode,
        string? hint = null,
        string? usage = null,
        string? errorCode = null,
        string? category = null)
    {
        if (json)
        {
            WriteStdout(JsonSerializer.Serialize(
                new CommandErrorJsonResult("error", message, hint, errorCode, Category: category),
                CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult));
            return exitCode;
        }

        Write(message, exitCode, hint, usage, errorCode);
        return exitCode;
    }

    internal static string FormatSanitizedException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var typeName = ex.GetType().Name;
        if (string.IsNullOrWhiteSpace(typeName))
            return nameof(Exception);

        return typeName.Length <= SanitizedExceptionTypeNameLimit
            ? typeName
            : typeName[..SanitizedExceptionTypeNameLimit] + "...";
    }

    internal static string FormatSanitizedExceptionMessage(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return DiagnosticRedactor.FormatExceptionMessage(ex);
    }

    internal static string FormatSanitizedExceptionDetail(Exception ex, int maxMessageChars = 240)
        => DiagnosticRedactor.FormatExceptionDetail(ex, maxMessageChars);
}
