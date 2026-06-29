namespace CodeIndex.Cli;

internal sealed record JsonCompatibilityAliasLifecycle(
    string AliasName,
    string ReplacementName,
    string Contract,
    string JsonContractType,
    string PropertyName,
    string RemovalCriteria);

internal static class JsonCompatibilityAliasLifecycles
{
    internal static IReadOnlyList<JsonCompatibilityAliasLifecycle> All { get; } =
    [
        new(
            "file_count",
            "files",
            "find --count --json",
            nameof(QueryFindCountJsonResult),
            "FileCount",
            "Keep serialized until at least one minor release has announced the deprecation and targeted serialization tests, documentation, changelog fragments, and production-code deprecation scans are intentionally updated.")
    ];
}
