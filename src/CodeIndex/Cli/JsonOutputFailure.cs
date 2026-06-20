using System.Text.Json;

namespace CodeIndex.Cli;

internal static class JsonOutputFailure
{
    internal const string ReflectionDisabledMessage = "Reflection-based serialization has been disabled for this application";

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

    internal static bool IsTrimmedJsonUnavailable(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is InvalidOperationException &&
                current.Message.Contains(ReflectionDisabledMessage, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
