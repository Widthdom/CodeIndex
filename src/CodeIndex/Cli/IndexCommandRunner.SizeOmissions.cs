using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    internal static void WriteSizeOmissionWarning(SizeOmissionSummary? omissions)
    {
        if (omissions == null)
            return;
        ConsoleUi.PrintWarning($"Size omissions: {omissions.AffectedFileCount} files ({(omissions.FilesTruncated ? "sample" : "complete")} path list). Partial outcome; --allow-partial accepts exit 0 without restoring completeness.");
        foreach (var file in omissions.Files)
            ConsoleUi.PrintWarning($"  {file.Path}{(file.PathTruncated ? "..." : "")}: actual_bytes={file.ActualBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; limit_bytes={file.LimitBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");
        ConsoleUi.PrintWarning(omissions.RecommendedAction);
        ConsoleUi.PrintWarning(omissions.AlternativeAction);
    }
}
