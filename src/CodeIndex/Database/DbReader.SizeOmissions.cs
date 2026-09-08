using System.Globalization;
using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbReader
{
    internal SizeOmissionSummary? GetSizeOmissions()
    {
        if (!_hasIssuesPhysicalTable)
            return null;
        using var command = _conn.CreateCommand();
        command.CommandText = "SELECT COUNT(DISTINCT file_id) FROM file_issues WHERE kind = 'file_too_large'";
        var count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (count == 0)
            return null;

        command.CommandText = """
            SELECT substr(f.path, 1, 513), substr(i.message, 1, 1024), f.size
            FROM files f JOIN file_issues i ON i.file_id = f.id
            WHERE i.kind = 'file_too_large'
            GROUP BY f.id ORDER BY f.path LIMIT 20
            """;
        var files = new List<SizeOmissionFile>();
        using var rows = command.ExecuteTrackedReader();
        while (rows.TrackedRead())
        {
            var path = rows.GetString(0);
            var message = rows.IsDBNull(1) ? string.Empty : rows.GetString(1);
            var actual = ReadSizeEvidence(message, "actual_bytes=")
                ?? (rows.IsDBNull(2) || rows.GetInt64(2) < 0 ? (long?)null : rows.GetInt64(2));
            var limit = ReadSizeEvidence(message, "limit_bytes=");
            files.Add(new SizeOmissionFile(
                string.Concat(path.Take(512).Select(c => char.IsControl(c) ? '?' : c)),
                path.Length > 512, actual, limit is > 0 and <= int.MaxValue ? limit : null));
        }
        return new SizeOmissionSummary { AffectedFileCount = count, Files = files };
    }

    private static long? ReadSizeEvidence(string message, string key)
    {
        var start = message.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += key.Length;
        var end = start;
        while (end < message.Length && char.IsAsciiDigit(message[end]))
            end++;
        return long.TryParse(message.AsSpan(start, end - start), NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
