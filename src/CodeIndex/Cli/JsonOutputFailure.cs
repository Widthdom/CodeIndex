using System.Text.Json;

namespace CodeIndex.Cli;

internal static class JsonOutputFailure
{
    internal static bool TryHandle(Exception ex, out int exitCode)
    {
        if (!IsTrimmedJsonUnavailable(ex))
        {
            exitCode = CommandExitCodes.Success;
            return false;
        }

        CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.FeatureUnavailable}]: --json is not available on this trimmed build.");
        CommandErrorWriter.WriteStderr("Hint: use `cdidx mcp` for structured output, omit `--json` for human-readable output, or use the NuGet/global-tool build if you need CLI JSON.");
        exitCode = CommandExitCodes.FeatureUnavailable;
        return true;
    }

    internal static bool IsTrimmedJsonUnavailable(Exception ex) =>
        IsTrimmedJsonUnavailable(ex, JsonSerializer.IsReflectionEnabledByDefault);

    internal static bool IsTrimmedJsonUnavailable(Exception ex, bool reflectionEnabledByDefault)
    {
        if (reflectionEnabledByDefault)
            return false;

        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is InvalidOperationException &&
                IsSystemTextJsonException(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSystemTextJsonException(Exception ex) =>
        string.Equals(ex.Source, "System.Text.Json", StringComparison.Ordinal) ||
        string.Equals(ex.TargetSite?.DeclaringType?.Assembly.GetName().Name, "System.Text.Json", StringComparison.Ordinal);
}
