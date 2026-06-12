using System.Text;

namespace CodeIndex;

internal static class SafeDiagnosticFormatter
{
    private const int MaxCategoryCharacters = 64;
    private const int MaxExceptionTypeCharacters = 128;
    private const string TruncationMarker = "...";

    internal static string FormatExceptionCategory(string category, Exception ex)
        => FormatCategoryType(category, ex.GetType().Name);

    internal static string FormatCategoryType(string category, string typeName)
        => $"{BoundToken(category, MaxCategoryCharacters)}: {BoundToken(typeName, MaxExceptionTypeCharacters)}";

    internal static string FormatWorkerExit(string category, int? exitCode, string fallback)
    {
        var safeCategory = BoundToken(category, MaxCategoryCharacters);
        var safeFallback = BoundToken(fallback, MaxExceptionTypeCharacters);
        return exitCode.HasValue
            ? $"{safeCategory}: worker exited with code {exitCode.Value}. {safeFallback}."
            : $"{safeCategory}: worker exited before the exit code was available. {safeFallback}.";
    }

    private static string BoundToken(string? value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var builder = new StringBuilder(Math.Min(value.Length, maxCharacters));
        foreach (var ch in value)
        {
            if (builder.Length >= maxCharacters)
                break;

            builder.Append(char.IsControl(ch) || char.IsWhiteSpace(ch) ? '_' : ch);
        }

        var bounded = builder.ToString().Trim('_');
        if (bounded.Length == 0)
            bounded = "unknown";
        if (value.Length > maxCharacters && bounded.Length + TruncationMarker.Length <= maxCharacters)
            bounded += TruncationMarker;
        return bounded;
    }
}
