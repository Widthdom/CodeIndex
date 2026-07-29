using System.Text.Json;
using System.Text.Json.Nodes;
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
            WriteStderr(FormatUsage(usage));
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
        string? category = null,
        string? command = null,
        string? path = null,
        JsonObject? additionalJsonProperties = null)
    {
        if (json)
        {
            var (resolvedErrorCode, resolvedCategory) = ResolveMachineContract(exitCode, errorCode, category);
            var payload = JsonSerializer.SerializeToNode(
                new CommandErrorJsonResult(
                    "error",
                    message,
                    hint ?? DefaultHint,
                    resolvedErrorCode,
                    path,
                    resolvedCategory,
                    command,
                    exitCode,
                    usage),
                CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult)!.AsObject();
            if (usage == null)
                payload.Remove("usage");
            if (additionalJsonProperties != null)
            {
                foreach (var property in additionalJsonProperties)
                {
                    if (!payload.ContainsKey(property.Key))
                        payload[property.Key] = property.Value?.DeepClone();
                }
            }

            WriteStdout(payload.ToJsonString(jsonOptions));
            return exitCode;
        }

        Write(message, exitCode, hint, usage, errorCode);
        return exitCode;
    }

    internal static (string ErrorCode, string Category) ResolveMachineContract(
        int exitCode,
        string? errorCode = null,
        string? category = null)
    {
        var defaults = exitCode switch
        {
            CommandExitCodes.UsageError or CommandExitCodes.InvalidArgument or CommandExitCodes.ExUsage
                => (CommandErrorCodes.UsageError, "usage"),
            CommandExitCodes.NotFound
                => (CommandErrorCodes.CommandFailed, "not_found"),
            CommandExitCodes.DatabaseError or CommandExitCodes.TransientDatabaseError
                => (CommandErrorCodes.DbError, "database_error"),
            CommandExitCodes.FeatureUnavailable
                => (CommandErrorCodes.FeatureUnavailable, "feature_unavailable"),
            CommandExitCodes.StaleIndex
                => (CommandErrorCodes.CommandFailed, "stale_index"),
            CommandExitCodes.CancelledBySignal or CommandExitCodes.LegacyInterrupted
                => (CommandErrorCodes.Interrupted, "interrupted"),
            CommandExitCodes.InstallError
                => (CommandErrorCodes.CommandFailed, "install_error"),
            CommandExitCodes.PartialResult
                => (CommandErrorCodes.IndexPartial, "partial_result"),
            _ => (CommandErrorCodes.CommandFailed, "runtime_error"),
        };
        return (errorCode ?? defaults.Item1, category ?? defaults.Item2);
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

    private static string FormatUsage(string usage)
        => usage.StartsWith("Usage:", StringComparison.Ordinal) ? usage : $"Usage: {usage}";
}
