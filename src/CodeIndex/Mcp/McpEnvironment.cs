using CodeIndex.Cli;
using CodeIndex.Diagnostics;

namespace CodeIndex.Mcp;

internal enum McpEnvironmentSwitchState
{
    Unset,
    Enabled,
    Disabled,
    Invalid,
}

internal readonly record struct McpEnvironmentSwitch(McpEnvironmentSwitchState State)
{
    internal bool IsEnabled => State == McpEnvironmentSwitchState.Enabled;
    internal bool IsDisabled => State == McpEnvironmentSwitchState.Disabled;
    internal bool IsInvalid => State == McpEnvironmentSwitchState.Invalid;
}

internal static class McpEnvironment
{
    internal static string? GetVariable(string name)
        => CdidxEnvironment.GetEnvironmentVariable(name);

    internal static string? GetOptionalToken(string name)
    {
        var token = GetVariable(name);
        if (string.IsNullOrEmpty(token))
            return null;

        if (!McpAuthenticationLimits.IsTokenShapeValid(token))
            throw new FormatException(McpAuthenticationLimits.FormatTokenShapeError(name));

        return token;
    }

    internal static bool IsUnsafeDebugEnabled(string name)
        => string.Equals(GetVariable(name), "unsafe", StringComparison.OrdinalIgnoreCase);

    internal static McpEnvironmentSwitch ReadOptInSwitch(string name)
    {
        var raw = GetVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return new McpEnvironmentSwitch(McpEnvironmentSwitchState.Unset);

        var value = raw.Trim();
        if (IsOptInValue(value))
            return new McpEnvironmentSwitch(McpEnvironmentSwitchState.Enabled);
        if (IsOptOutValue(value))
            return new McpEnvironmentSwitch(McpEnvironmentSwitchState.Disabled);
        return new McpEnvironmentSwitch(McpEnvironmentSwitchState.Invalid);
    }

    internal static void WriteWarning(string source, string message)
        => Console.Error.WriteLine($"Warning: {source}: {message}");

    internal static string FormatDiagnosticValue(string? raw)
        => DiagnosticRedactor.FormatEnvironmentValue(raw);

    private static bool IsOptInValue(string value)
        => value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);

    private static bool IsOptOutValue(string value)
        => value.Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase);
}
