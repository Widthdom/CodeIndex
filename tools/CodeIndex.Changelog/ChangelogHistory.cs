using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Changelog;

public sealed partial class ChangelogTool
{
    public const int MaxArchiveCount = 256;
    private static readonly Regex HistoryHeadingRegex = new(
        @"^### \[(?<version>Unreleased|\d+\.\d+\.\d+)\](?: - \d{4}-\d{2}-\d{2})?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ArchiveLinkRegex = new(
        @"\]\((?<path>docs/changelog/v[^)#]+\.md)(?:#[^)]*)?\)", RegexOptions.CultureInvariant);

    private sealed record HistoryDocument(string Path, ParsedChangelog Changelog);

    private List<HistoryDocument> LoadHistory(string? rootText = null)
    {
        rootText ??= ReadAllTextBounded(Path.Combine(_repositoryRoot, "CHANGELOG.md"), _repositoryRoot, MaxChangelogBytes);
        ValidateChangelogSize(rootText, "CHANGELOG.md");
        var documents = new List<HistoryDocument> { new("CHANGELOG.md", ParsedChangelog.Parse(rootText.Replace("\r\n", "\n", StringComparison.Ordinal))) };
        var directory = Path.Combine(_repositoryRoot, "docs", "changelog");
        if (Directory.Exists(directory))
        {
            foreach (var name in new[] { "README.md", "USER_GUIDE.md" })
            {
                var navigationPath = Path.Combine(directory, name);
                if (File.Exists(navigationPath))
                    ReadAllTextBounded(navigationPath, _repositoryRoot, MaxChangelogBytes);
            }
            // Bound discovery before sorting or reading archive contents.
            var paths = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path) is not ("README.md" or "USER_GUIDE.md"))
                .Take(MaxArchiveCount + 1).ToList();
            if (paths.Count > MaxArchiveCount)
                throw new ChangelogException($"docs/changelog: maximum supported archive count is {MaxArchiveCount}.");
            foreach (var path in paths.Order(StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(_repositoryRoot, path).Replace('\\', '/');
                if (!Regex.IsMatch(Path.GetFileName(path), @"^v\d+\.\d+\.\d+-v\d+\.\d+\.\d+\.md$", RegexOptions.CultureInvariant))
                    throw new ChangelogException($"{relativePath}: use v<oldest>-v<newest>.md archive names.");
                var text = ReadAllTextBounded(path, _repositoryRoot, MaxChangelogBytes);
                documents.Add(new HistoryDocument(relativePath, ParsedChangelog.Parse(text.Replace("\r\n", "\n", StringComparison.Ordinal))));
            }
        }
        ValidateHistory(documents);
        return documents;
    }

    private static void ValidateChangelogSize(string text, string path)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxChangelogBytes)
            throw new ChangelogException($"{path}: maximum supported size is {MaxChangelogBytes} bytes. Archive complete bilingual releases first; see docs/changelog/README.md.");
    }

    private static void ValidateHistory(IReadOnlyList<HistoryDocument> documents)
    {
        var releases = new HashSet<string>(StringComparer.Ordinal);
        var root = documents[0].Changelog;
        foreach (var introduction in new[] { root.PrefixLines.Concat(root.EnglishIntroLines), root.JapaneseIntroLines.AsEnumerable() })
        {
            var linkedArchives = ArchiveLinkRegex.Matches(string.Join('\n', introduction))
                .Select(match => match.Groups["path"].Value).ToHashSet(StringComparer.Ordinal);
            if (!linkedArchives.SetEquals(documents.Skip(1).Select(document => document.Path)))
                throw new ChangelogException("CHANGELOG.md English/日本語 archive indexes have missing, unlisted, or invalid archive paths.");
        }

        Version? oldestRoot = null;
        var ranges = new List<(Version Oldest, Version Newest, string Path)>();
        foreach (var document in documents)
        {
            var changelog = document.Changelog;
            var english = changelog.EnglishBlocks;
            var japanese = changelog.JapaneseBlocks;
            if (!english.Select(block => block.HeadingLine).SequenceEqual(japanese.Select(block => block.HeadingLine), StringComparer.Ordinal))
                throw new ChangelogException($"{document.Path}: missing, duplicate, or mismatched English/日本語 release headings.");
            var footers = new Dictionary<string, FooterEntry>(StringComparer.Ordinal);
            foreach (var entry in changelog.FooterEntries)
            {
                if (!footers.TryAdd(entry.Label, entry))
                    throw new ChangelogException($"{document.Path}: duplicate compare link [{entry.Label}].");
                if (entry.Label == "Unreleased" ? entry.TargetVersion is not null : entry.Label != entry.TargetVersion)
                    throw new ChangelogException($"{document.Path}: compare link [{entry.Label}] has a mismatched target.");
            }

            Version? previous = null;
            Version? newest = null;
            for (var index = 0; index < english.Count; index++)
            {
                var match = HistoryHeadingRegex.Match(english[index].HeadingLine);
                if (!match.Success)
                    throw new ChangelogException($"{document.Path}: invalid release heading '{english[index].HeadingLine}'.");
                var label = match.Groups["version"].Value;
                if (!footers.ContainsKey(label))
                    throw new ChangelogException($"{document.Path}: missing compare link [{label}].");
                if (!releases.Add(label))
                    throw new ChangelogException($"{document.Path}: duplicate release [{label}].");
                if (label == "Unreleased")
                {
                    if (document.Path != "CHANGELOG.md" || index != 0)
                        throw new ChangelogException($"{document.Path}: Unreleased must appear first in the root changelog only.");
                    continue;
                }
                if (!Version.TryParse(label, out var version) || (previous is not null && version >= previous))
                    throw new ChangelogException($"{document.Path}: release versions must be in descending order.");
                var footer = footers[label];
                if (!footer.IsTagLink && (!Version.TryParse(footer.BaseVersion, out var baseVersion) || baseVersion >= version))
                    throw new ChangelogException($"{document.Path}: compare link [{label}] must start at an older version.");
                newest ??= version;
                previous = version;
            }

            if (document.Path == "CHANGELOG.md")
            {
                if (english.Count == 0 || english[0].HeadingLine != "### [Unreleased]")
                    throw new ChangelogException("CHANGELOG.md: both Unreleased sections are required.");
                oldestRoot = previous;
                if (newest is not null && footers["Unreleased"].BaseVersion != newest.ToString())
                    throw new ChangelogException("CHANGELOG.md: Unreleased compare link must start at the newest root release.");
            }
            else
            {
                if (previous is null || newest is null)
                    throw new ChangelogException($"{document.Path}: archive has no releases.");
                if (document.Path != $"docs/changelog/v{previous}-v{newest}.md")
                    throw new ChangelogException($"{document.Path}: archive filename does not match its release range.");
                if (!string.Join('\n', changelog.PrefixLines).Contains("](../../CHANGELOG.md)", StringComparison.Ordinal))
                    throw new ChangelogException($"{document.Path}: missing link back to CHANGELOG.md.");
                ranges.Add((previous, newest, document.Path));
            }
        }
        var boundary = oldestRoot;
        foreach (var range in ranges.OrderByDescending(range => range.Newest))
        {
            if (boundary is null || range.Newest >= boundary)
                throw new ChangelogException($"{range.Path}: archive range overlaps newer history or root has no current releases.");
            boundary = range.Oldest;
        }
    }
}
