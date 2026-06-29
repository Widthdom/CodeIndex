namespace CodeIndex.Archives;

internal static class ZipArchiveSafetyPolicy
{
    internal static bool TryNormalizeRelativeEntryName(
        string entryName,
        out string normalizedName,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(entryName);

        normalizedName = string.Empty;
        failureReason = string.Empty;

        if (entryName.Length == 0)
        {
            failureReason = "must not be empty";
            return false;
        }

        if (entryName.Contains('\\'))
        {
            failureReason = "must use '/' separators, not backslashes";
            return false;
        }

        if (entryName.Contains('\0'))
        {
            failureReason = "must not contain NUL characters";
            return false;
        }

        if (entryName[0] == '/' || StartsWithWindowsDrivePrefix(entryName))
        {
            failureReason = "must be a relative path";
            return false;
        }

        var segments = entryName.Split('/');
        var normalizedSegments = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                failureReason = "must not contain empty path segments";
                return false;
            }

            if (segment == "..")
            {
                failureReason = "must not contain parent-directory segments";
                return false;
            }

            if (segment == ".")
                continue;

            normalizedSegments.Add(segment);
        }

        if (normalizedSegments.Count == 0)
        {
            failureReason = "must not normalize to an empty path";
            return false;
        }

        normalizedName = string.Join('/', normalizedSegments);
        if (normalizedName[0] == '/' || StartsWithWindowsDrivePrefix(normalizedName))
        {
            failureReason = "must be a relative path";
            normalizedName = string.Empty;
            return false;
        }

        return true;
    }

    internal static bool TryAddUniqueEntryName<TEntry>(
        IDictionary<string, TEntry> entries,
        string entryName,
        TEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(entryName);

        if (entries.ContainsKey(entryName))
            return false;

        entries.Add(entryName, entry);
        return true;
    }

    private static bool StartsWithWindowsDrivePrefix(string entryName)
    {
        return entryName.Length >= 2
            && entryName[1] == ':'
            && ((entryName[0] >= 'A' && entryName[0] <= 'Z') || (entryName[0] >= 'a' && entryName[0] <= 'z'));
    }
}
