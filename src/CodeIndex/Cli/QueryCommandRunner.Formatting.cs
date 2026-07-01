namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var totalDays = (int)duration.TotalDays;
        var hours = duration.Hours;
        var minutes = duration.Minutes;
        var seconds = duration.Seconds;

        if (totalDays > 0)
            return hours > 0 ? $"{totalDays}d{hours}h" : $"{totalDays}d";
        if (duration.TotalHours >= 1)
            return minutes > 0 ? $"{(int)duration.TotalHours}h{minutes}m" : $"{(int)duration.TotalHours}h";
        if (duration.TotalMinutes >= 1)
            return seconds > 0 ? $"{(int)duration.TotalMinutes}m{seconds}s" : $"{(int)duration.TotalMinutes}m";
        return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero))}s";
    }

    private static string FormatSamples(IReadOnlyList<string> samples)
        => samples.Count == 0 ? string.Empty : $" ({string.Join(", ", samples)})";

    private static string ShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return "<unknown>";
        return sha.Length <= 12 ? sha : sha[..12];
    }
}
