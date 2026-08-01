using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

internal static class CommandErrorWriter
{
    internal const string DefaultHint = "Run '<cmd> --help' for usage information.";
    internal const string ResponseBudgetCategory = "response_budget";
    internal const string MinimumResponseBytesUnavailableBeforeMaterialization = "normal_payload_not_materialized";
    internal const string MinimumResponseBytesUncertainRuntimeEnvelope = "runtime_metadata_or_embedded_budget_varies_between_invocations";
    internal const string MinimumResponseBytesUncertainCapturedValidation = "captured_validation_output_may_vary_between_invocations";
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
        JsonObject? additionalJsonProperties = null,
        bool omitNullUsage = false)
    {
        if (json)
        {
            var payload = BuildJsonPayload(
                jsonOptions,
                message,
                exitCode,
                hint,
                usage,
                errorCode,
                category,
                command,
                path,
                additionalJsonProperties,
                omitNullUsage);

            WriteStdout(payload.ToJsonString(jsonOptions));
            return exitCode;
        }

        Write(message, exitCode, hint, usage, errorCode);
        return exitCode;
    }

    internal static JsonObject BuildJsonPayload(
        JsonSerializerOptions jsonOptions,
        string message,
        int exitCode,
        string? hint = null,
        string? usage = null,
        string? errorCode = null,
        string? category = null,
        string? command = null,
        string? path = null,
        JsonObject? additionalJsonProperties = null,
        bool omitNullUsage = false)
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
        if (omitNullUsage && usage == null)
            payload.Remove("usage");
        if (additionalJsonProperties != null)
        {
            foreach (var property in additionalJsonProperties)
            {
                if (!payload.ContainsKey(property.Key))
                    payload[property.Key] = property.Value?.DeepClone();
            }
        }

        return payload;
    }

    internal static int WriteResponseBudgetError(
        bool json,
        JsonSerializerOptions jsonOptions,
        string command,
        string message,
        string hint,
        long? requestedBytes,
        long? effectiveBytes,
        long? minimumRequiredBytes,
        string? minimumRequiredBytesUnavailableReason = null,
        string? minimumRequiredBytesUncertaintyReason = null,
        long? recommendedBytes = null,
        string? usage = null,
        int exitCode = CommandExitCodes.UsageError,
        bool retryByIncreasingBudget = true,
        long? maximumEffectiveBytes = null,
        JsonObject? additionalJsonProperties = null)
    {
        var minimumKnown = minimumRequiredBytes.HasValue;
        if (!minimumKnown && string.IsNullOrWhiteSpace(minimumRequiredBytesUnavailableReason))
            throw new ArgumentException(
                "An unavailable minimum response size requires a stable reason.",
                nameof(minimumRequiredBytesUnavailableReason));

        long? retryBytes = retryByIncreasingBudget
            ? recommendedBytes ?? minimumRequiredBytes ?? 1
            : null;
        var minimumUncertain = !string.IsNullOrWhiteSpace(minimumRequiredBytesUncertaintyReason);
        var responseBudgetProperties = new JsonObject
        {
            ["requested_bytes"] = requestedBytes,
            ["effective_bytes"] = effectiveBytes,
            ["minimum_required_bytes"] = minimumRequiredBytes,
            ["minimum_required_bytes_known"] = minimumKnown,
            ["minimum_required_bytes_unavailable_reason"] = minimumKnown
                ? null
                : minimumRequiredBytesUnavailableReason,
            ["minimum_required_bytes_uncertain"] = minimumUncertain,
            ["minimum_required_bytes_uncertainty_reason"] = minimumUncertain
                ? minimumRequiredBytesUncertaintyReason
                : null,
            ["retry"] = new JsonObject
            {
                ["action"] = retryByIncreasingBudget
                    ? "increase_max_json_bytes"
                    : "reduce_response_size",
                ["option"] = retryByIncreasingBudget ? "--max-json-bytes" : null,
                ["recommended_bytes"] = retryBytes,
                ["maximum_effective_bytes"] = retryByIncreasingBudget
                    ? null
                    : maximumEffectiveBytes,
                ["command"] = command,
            },
        };
        if (additionalJsonProperties != null)
        {
            foreach (var property in additionalJsonProperties)
            {
                if (!responseBudgetProperties.ContainsKey(property.Key))
                    responseBudgetProperties[property.Key] = property.Value?.DeepClone();
            }
        }

        return WriteJsonOrHuman(
            json,
            jsonOptions,
            message,
            exitCode,
            hint,
            usage,
            errorCode: CommandErrorCodes.ResponseBudgetTooSmall,
            category: ResponseBudgetCategory,
            command: command,
            additionalJsonProperties: responseBudgetProperties);
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
