namespace CodeIndex.Cli;

/// <summary>
/// Resolves scoped configuration provenance without coupling environment access to the
/// config-file loader.
/// environment access を config-file loader に結合せず、scoped config の出所を解決する。
/// </summary>
internal static class CdidxConfigSourceResolver
{
    internal const string EnvironmentVariablePrefix = "CDIDX_CONFIG_SOURCE__";

    internal static string? GetSource(string name) =>
        global::CodeIndex.EnvironmentAccess.GetConfigSource(name, EnvironmentVariablePrefix);
}
