namespace CodeIndex.Database;

internal static class IndexOutcomePolicy
{
    // Intentional symbol policies and ordinary scoped writes retain their success contract.
    internal static bool IsPartial(int errors, PersistedIndexGenerationReadiness readiness)
        => errors > 0 || readiness.IndexIncompleteReasons.Contains("file_too_large", StringComparer.Ordinal);
}
