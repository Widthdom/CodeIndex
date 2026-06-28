namespace CodeIndex.Cli;

internal static class CdidxEnvironment
{
    internal static string? GetEnvironmentVariable(string name)
        => global::CodeIndex.EnvironmentAccess.GetEnvironmentVariable(name);

    internal static string? GetProcessEnvironmentVariable(string name)
        => global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(name);

    internal static IEnumerable<(string Key, string Value)> EnumerateProcessEnvironmentVariables()
        => global::CodeIndex.EnvironmentAccess.EnumerateProcessEnvironmentVariables();

    internal static string? GetConfigSource(string name)
        => global::CodeIndex.EnvironmentAccess.GetConfigSource(
            name,
            CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix);

    internal static IDisposable Push(
        IReadOnlyDictionary<string, string>? values,
        IReadOnlyDictionary<string, string>? sources = null)
        => global::CodeIndex.EnvironmentAccess.Push(values, sources);
}
