namespace CodeIndex.Models;

/// <summary>
/// Stable public identities for scopes synthesized by the index rather than declared in source.
/// source declaration ではなく index が合成する scope の安定した公開 identity。
/// </summary>
public static class SyntheticSymbolIdentity
{
    public const string ScriptScopeSubKind = "script_scope";
    public const string CSharpTopLevelScopeSubKind = "top_level_scope";
    public const string CSharpTopLevelScopeName = "<top-level>";

    public static bool IsSyntheticSubKind(string? subKind)
        => subKind is ScriptScopeSubKind or CSharpTopLevelScopeSubKind;

    public static string BuildFileQualifiedName(string path, string name)
        => $"{path}::{name}";
}
