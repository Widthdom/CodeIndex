using System.Security.Cryptography;
using System.Text;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;

namespace CodeIndex.Indexer.Hooks;

internal static class PostExtractionHookIdentity
{
    internal static string Create(string assemblyPath, string assemblyFingerprint, string typeName)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var normalizedPath = PathCasing.ComparisonFor(fullPath) == StringComparison.OrdinalIgnoreCase
            ? fullPath.ToUpperInvariant()
            : fullPath;
        var value = $"{assemblyFingerprint}\n{normalizedPath}\n{typeName}";
        return "hook:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    internal static string? ForDiagnostic(string? hookId)
    {
        if (hookId == null)
            return null;
        if (hookId.Length == 69
            && hookId.StartsWith("hook:", StringComparison.Ordinal)
            && hookId.AsSpan(5).IndexOfAnyExcept("0123456789abcdef") < 0)
        {
            return hookId;
        }

        return DiagnosticSanitizer.ForOptionalLabel(hookId);
    }
}
