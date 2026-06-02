using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeIndex.Changelog;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            var root = FindRepositoryRoot();
            var tool = new ChangelogTool(root);

            var command = args[0];

            switch (command)
            {
                case "check":
                    {
                        var summary = tool.CheckFragments();
                        Console.Out.WriteLine(summary);
                        return 0;
                    }
                case "prepare":
                    {
                        var options = ParseOptions(args[1..], requireDate: true);
                        var result = tool.Prepare(options.Version, options.ReleaseDate, writeChanges: true);
                        Console.Out.WriteLine(result.Summary);
                        return 0;
                    }
                case "render":
                    {
                        var options = ParseOptions(args[1..], requireDate: true);
                        var result = tool.Prepare(options.Version, options.ReleaseDate, writeChanges: false);
                        Console.Out.Write(result.RenderedChangelog ?? string.Empty);
                        return 0;
                    }
                case "release-notes":
                    {
                        var options = ParseOptions(args[1..], requireDate: false);
                        Console.Out.Write(tool.RenderReleaseNotes(options.Version));
                        return 0;
                    }
                default:
                    throw new ChangelogException($"Unknown command '{command}'.");
            }
        }
        catch (ChangelogException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool IsHelp(string arg) => arg is "-h" or "--help" or "help";

    private static void PrintUsage()
    {
        Console.Out.WriteLine("Usage:");
        Console.Out.WriteLine("  dotnet run --project tools/CodeIndex.Changelog -- check");
        Console.Out.WriteLine("  dotnet run --project tools/CodeIndex.Changelog -- prepare --version X.Y.Z --date YYYY-MM-DD");
        Console.Out.WriteLine("  dotnet run --project tools/CodeIndex.Changelog -- render --version X.Y.Z --date YYYY-MM-DD");
        Console.Out.WriteLine("  dotnet run --project tools/CodeIndex.Changelog -- release-notes --version X.Y.Z");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Limits:");
        Console.Out.WriteLine($"  unreleased fragments: {ChangelogTool.MaxFragmentCount}");
        Console.Out.WriteLine($"  fragment file size: {ChangelogTool.MaxFragmentBytes} bytes");
        Console.Out.WriteLine($"  CHANGELOG.md size: {ChangelogTool.MaxChangelogBytes} bytes");
        Console.Out.WriteLine($"  version.json size: {ChangelogTool.MaxVersionJsonBytes} bytes");
    }

    private static ParsedOptions ParseOptions(string[] args, bool requireDate)
    {
        Version? version = null;
        DateOnly? releaseDate = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--version")
            {
                if (i + 1 >= args.Length)
                    throw new ChangelogException("Missing value for --version.");

                version = Version.Parse(args[++i]);
                continue;
            }

            if (arg == "--date")
            {
                if (i + 1 >= args.Length)
                    throw new ChangelogException("Missing value for --date.");

                releaseDate = DateOnly.Parse(args[++i]);
                continue;
            }

            throw new ChangelogException($"Unknown option '{arg}'.");
        }

        if (version is null)
            throw new ChangelogException("Missing required option --version.");

        if (requireDate && releaseDate is null)
            throw new ChangelogException("Missing required option --date.");

        return new ParsedOptions(version, releaseDate ?? default);
    }

    private static string FindRepositoryRoot()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var assemblyDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location);

        foreach (var startDirectory in new[] { currentDirectory, assemblyDirectory }.Where(directory => !string.IsNullOrWhiteSpace(directory)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var root = TryFindRepositoryRoot(startDirectory!);
            if (root is not null)
                return root;
        }

        throw new ChangelogException("Could not locate the repository root.");
    }

    private static string? TryFindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CHANGELOG.md")) &&
                File.Exists(Path.Combine(current.FullName, "CodeIndex.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private sealed record ParsedOptions(Version Version, DateOnly ReleaseDate);
}

public sealed class ChangelogTool
{
    public const int MaxFragmentCount = 512;
    public const long MaxFragmentBytes = 128 * 1024;
    public const long MaxChangelogBytes = 8 * 1024 * 1024;
    public const long MaxVersionJsonBytes = 16 * 1024;
    internal static Action<PrepareWritePhase>? PrepareWritePhaseForTesting { get; set; }

    private static readonly string[] AllowedCategories =
    [
        "added",
        "changed",
        "fixed",
        "deprecated",
        "removed",
        "security",
        "docs",
        "internal",
    ];

    private static readonly Dictionary<string, string> CategoryHeadings = new(StringComparer.Ordinal)
    {
        ["added"] = "Added",
        ["changed"] = "Changed",
        ["fixed"] = "Fixed",
        ["deprecated"] = "Deprecated",
        ["removed"] = "Removed",
        ["security"] = "Security",
        ["docs"] = "Documentation",
        ["internal"] = "Internal",
    };

    private static readonly Dictionary<string, string> JapaneseCategoryHeadings = new(StringComparer.Ordinal)
    {
        ["added"] = "追加",
        ["changed"] = "変更",
        ["fixed"] = "修正",
        ["deprecated"] = "非推奨",
        ["removed"] = "削除",
        ["security"] = "セキュリティ",
        ["docs"] = "ドキュメント",
        ["internal"] = "内部変更",
    };

    private static readonly Regex NumericFragmentNameRegex = new(@"^(?<issues>\d+(?:-\d+)?)\.(?<category>added|changed|fixed|deprecated|removed|security|docs|internal)\.md$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SlugFragmentNameRegex = new(@"^\+(?<slug>[A-Za-z0-9][A-Za-z0-9-]*)\.(?<category>added|changed|fixed|deprecated|removed|security|docs|internal)\.md$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReleaseHeadingRegex = new(@"^### \[[^\]]+\](?: - .+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FooterLinkRegex = new(@"^\[(?<label>[^\]]+)\]: https://github\.com/Widthdom/CodeIndex/compare/v(?<base>\d+\.\d+\.\d+)(?:\.\.\.v(?<target>\d+\.\d+\.\d+)|\.\.\.HEAD)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FooterTagLinkRegex = new(@"^\[(?<label>[^\]]+)\]: https://github\.com/Widthdom/CodeIndex/releases/tag/v(?<version>\d+\.\d+\.\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BulletRegex = new(@"^\s*-\s+\S", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string EnglishUnreleasedNotice = "- **Pending changelog fragments live under `changelog.d/unreleased/`** — this section stays empty during ordinary work; see `changelog.d/unreleased/` for the release notes that are waiting to be aggregated.";
    private const string JapaneseUnreleasedNotice = "- **未リリースの変更内容は `changelog.d/unreleased/` にまとまっています** — 通常の作業ではこのセクションは空のままにし、リリース待ちの変更は `changelog.d/unreleased/` を参照してください。";

    private readonly string _repositoryRoot;

    public ChangelogTool(string repositoryRoot)
    {
        _repositoryRoot = repositoryRoot;
    }

    public string CheckFragments()
    {
        var fragments = LoadFragments(validate: true);
        return $"Validated {fragments.Count} changelog fragment(s).";
    }

    public PrepareResult Prepare(Version targetVersion, DateOnly releaseDate, bool writeChanges)
    {
        var fragments = LoadFragments(validate: true);
        var versionPath = Path.Combine(_repositoryRoot, "version.json");
        var originalVersionJson = ReadAllTextBounded(versionPath, _repositoryRoot, MaxVersionJsonBytes);
        var currentVersion = ParseCurrentVersion(originalVersionJson);

        var changelogPath = Path.Combine(_repositoryRoot, "CHANGELOG.md");
        var originalChangelogText = ReadAllTextBounded(changelogPath, _repositoryRoot, MaxChangelogBytes);
        var changelogText = originalChangelogText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var changelog = ParsedChangelog.Parse(changelogText);

        var targetHeading = $"### [{targetVersion}] - {releaseDate:yyyy-MM-dd}";
        var existingTargetBase = changelog.FooterEntries
            .FirstOrDefault(entry => entry.Label == targetVersion.ToString())
            ?.BaseVersion;

        var unreleasedBase = changelog.FooterEntries
            .FirstOrDefault(entry => entry.Label == "Unreleased")
            ?.BaseVersion;

        var expectedUnreleasedBase = changelog.HasReleaseSection(targetVersion)
            ? targetVersion.ToString()
            : currentVersion.ToString();

        if (unreleasedBase is not null && !StringComparer.Ordinal.Equals(unreleasedBase, expectedUnreleasedBase))
            throw new ChangelogException($"CHANGELOG.md footer mismatch: [Unreleased] points to v{unreleasedBase}, expected v{expectedUnreleasedBase}.");

        var releaseBase = existingTargetBase ?? currentVersion.ToString();

        var english = PrepareLanguageSection(changelog.EnglishBlocks, targetVersion, targetHeading, fragments, language: Language.English);
        var japanese = PrepareLanguageSection(changelog.JapaneseBlocks, targetVersion, targetHeading, fragments, language: Language.Japanese);

        var footerEntries = PrepareFooterEntries(changelog.FooterEntries, targetVersion.ToString(), releaseBase);
        var updatedChangelog = changelog.Render(english, japanese, footerEntries);

        var consumedFragmentFiles = fragments.Select(fragment => fragment.RelativePath).ToList();
        var updatedVersionJson = JsonSerializer.Serialize(new { version = targetVersion.ToString() }, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        if (writeChanges)
        {
            WritePreparedFiles(
                changelogPath,
                originalChangelogText,
                updatedChangelog,
                versionPath,
                originalVersionJson,
                updatedVersionJson,
                fragments);
        }

        var changedFiles = new List<string>
        {
            "CHANGELOG.md",
            "version.json",
        };
        changedFiles.AddRange(consumedFragmentFiles);

        var summary = new StringBuilder();
        summary.AppendLine($"Prepared changelog for v{targetVersion}.");
        summary.AppendLine($"Previous version: v{releaseBase}.");
        summary.AppendLine($"Fragments consumed: {fragments.Count}.");
        summary.AppendLine($"Files changed: {string.Join(", ", changedFiles)}.");
        summary.AppendLine($"Footer updated: [Unreleased] -> v{targetVersion}...HEAD; [{targetVersion}] -> v{releaseBase}...v{targetVersion}.");

        return new PrepareResult(summary.ToString().TrimEnd(), writeChanges ? null : updatedChangelog);
    }

    public string RenderReleaseNotes(Version targetVersion)
    {
        var changelogPath = Path.Combine(_repositoryRoot, "CHANGELOG.md");
        var changelogText = ReadAllTextBounded(changelogPath, _repositoryRoot, MaxChangelogBytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        var changelog = ParsedChangelog.Parse(changelogText);
        var versionPrefix = $"### [{targetVersion}]";

        var englishBlock = changelog.EnglishBlocks.FirstOrDefault(block => block.HeadingLine.StartsWith(versionPrefix, StringComparison.Ordinal));
        var japaneseBlock = changelog.JapaneseBlocks.FirstOrDefault(block => block.HeadingLine.StartsWith(versionPrefix, StringComparison.Ordinal));
        if (englishBlock is null || japaneseBlock is null)
            throw new ChangelogException($"CHANGELOG.md is missing release notes for v{targetVersion}.");

        if (englishBlock.BodyLines.Count == 0 && japaneseBlock.BodyLines.Count == 0)
            throw new ChangelogException($"CHANGELOG.md release notes for v{targetVersion} are empty.");

        var output = new List<string>
        {
            $"## CodeIndex v{targetVersion}",
            string.Empty,
            "### English",
            string.Empty,
        };
        output.AddRange(englishBlock.BodyLines);
        output.Add(string.Empty);
        output.Add("### 日本語");
        output.Add(string.Empty);
        output.AddRange(japaneseBlock.BodyLines);

        return string.Join('\n', output).TrimEnd() + Environment.NewLine;
    }

    private List<Fragment> LoadFragments(bool validate)
    {
        var fragmentDirectory = Path.Combine(_repositoryRoot, "changelog.d", "unreleased");
        if (!Directory.Exists(fragmentDirectory))
            return [];

        var fragments = new List<Fragment>();
        var errors = new List<string>();
        var fragmentPaths = new List<string>();

        foreach (var path in Directory.EnumerateFiles(fragmentDirectory, "*.md", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (fileName == ".gitkeep")
                continue;

            fragmentPaths.Add(path);
            if (fragmentPaths.Count > MaxFragmentCount)
                throw new ChangelogException($"changelog.d/unreleased: too many changelog fragments ({fragmentPaths.Count}); maximum supported count is {MaxFragmentCount}.");
        }

        fragmentPaths.Sort(StringComparer.Ordinal);

        foreach (var path in fragmentPaths)
        {
            try
            {
                fragments.Add(ParseFragment(path, _repositoryRoot));
            }
            catch (ChangelogException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (errors.Count > 0)
            throw new ChangelogException(string.Join(Environment.NewLine, errors));

        if (validate && fragments.Count == 0)
            return [];

        return fragments;
    }

    private static Fragment ParseFragment(string absolutePath, string repositoryRoot)
    {
        var relativePath = Path.GetRelativePath(repositoryRoot, absolutePath).Replace('\\', '/');
        var fileName = Path.GetFileName(absolutePath);
        var match = NumericFragmentNameRegex.Match(fileName);
        var slugMatch = SlugFragmentNameRegex.Match(fileName);

        if (!match.Success && !slugMatch.Success)
            throw new ChangelogException($"{relativePath}: invalid fragment file name.");

        var category = match.Success ? match.Groups["category"].Value : slugMatch.Groups["category"].Value;
        var frontMatterIssues = new List<int>();
        var frontMatterAffected = new List<string>();

        var text = ReadAllTextBounded(absolutePath, repositoryRoot, MaxFragmentBytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        if (lines.Length == 0)
            throw new ChangelogException($"{relativePath}: fragment is empty.");

        var index = 0;
        if (lines[index].TrimStart('\uFEFF') != "---")
            throw new ChangelogException($"{relativePath}: missing front matter.");

        index++;
        var frontMatterClosed = false;
        var currentKey = string.Empty;
        var frontMatterCategory = string.Empty;

        while (index < lines.Length)
        {
            var line = lines[index];
            if (line == "---")
            {
                frontMatterClosed = true;
                index++;
                break;
            }

            if (line.Length == 0)
            {
                index++;
                continue;
            }

            if (line.StartsWith("  - ", StringComparison.Ordinal))
            {
                var item = line[4..].Trim();
                if (currentKey == "issues")
                {
                    if (!int.TryParse(item, out var issueNumber))
                        throw new ChangelogException($"{relativePath}: invalid issue number '{item}'.");

                    frontMatterIssues.Add(issueNumber);
                }
                else if (currentKey == "affected")
                {
                    frontMatterAffected.Add(item);
                }

                index++;
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
                throw new ChangelogException($"{relativePath}: invalid front matter line '{line}'.");

            currentKey = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            switch (currentKey)
            {
                case "category":
                    frontMatterCategory = value;
                    break;
                case "issues":
                case "affected":
                    if (value.Length > 0)
                    {
                        if (currentKey == "issues")
                        {
                            if (!int.TryParse(value, out var issueNumber))
                                throw new ChangelogException($"{relativePath}: invalid issue number '{value}'.");

                            frontMatterIssues.Add(issueNumber);
                        }
                        else
                        {
                            frontMatterAffected.Add(value);
                        }
                    }
                    break;
            }

            index++;
        }

        if (!frontMatterClosed)
            throw new ChangelogException($"{relativePath}: unterminated front matter.");

        if (string.IsNullOrWhiteSpace(frontMatterCategory))
            throw new ChangelogException($"{relativePath}: front matter category is required.");

        if (!string.Equals(frontMatterCategory, category, StringComparison.Ordinal))
            throw new ChangelogException($"{relativePath}: file name category '{category}' does not match front matter category '{frontMatterCategory}'.");

        if (match.Success && frontMatterIssues.Count == 0)
            throw new ChangelogException($"{relativePath}: numeric fragment file names require issues in front matter.");

        var englishHeading = FindHeading(lines, index, "## English", relativePath);
        var japaneseHeading = FindHeading(lines, englishHeading + 1, "## 日本語", relativePath);

        var englishLines = ExtractSectionLines(lines, englishHeading + 1, japaneseHeading);
        var japaneseLines = ExtractSectionLines(lines, japaneseHeading + 1, lines.Length);

        ValidateLanguageSection(relativePath, englishLines, "English");
        ValidateLanguageSection(relativePath, japaneseLines, "Japanese");

        if (lines.Any(line => ReleaseHeadingRegex.IsMatch(line)))
            throw new ChangelogException($"{relativePath}: fragments must not contain release headings.");

        if (lines.Any(line => line.StartsWith("[Unreleased]:", StringComparison.Ordinal)))
            throw new ChangelogException($"{relativePath}: fragments must not contain compare-link footer definitions.");

        return new Fragment(
            absolutePath,
            relativePath,
            category,
            frontMatterIssues,
            frontMatterAffected,
            ExtractBodyLines(englishLines),
            ExtractBodyLines(japaneseLines));
    }

    private static void ValidateLanguageSection(string relativePath, string[] lines, string languageName)
    {
        if (!lines.Any(line => BulletRegex.IsMatch(line)))
            throw new ChangelogException($"{relativePath}: {languageName} section must contain at least one bullet.");
    }

    private static int FindHeading(string[] lines, int startIndex, string heading, string relativePath)
    {
        for (var i = startIndex; i < lines.Length; i++)
        {
            if (string.Equals(lines[i].Trim(), heading, StringComparison.Ordinal))
                return i;
        }

        throw new ChangelogException($"{relativePath}: missing '{heading}' heading.");
    }

    private static string[] ExtractSectionLines(string[] lines, int startIndex, int endIndex)
    {
        var slice = lines[startIndex..endIndex];
        return TrimTrailingAndLeadingBlankLines(slice);
    }

    private static List<string> ExtractBodyLines(string[] lines)
    {
        var body = lines.ToList();
        return TrimLeadingBlankLines(body);
    }

    private static List<string> TrimLeadingBlankLines(List<string> lines)
    {
        var index = 0;
        while (index < lines.Count && string.IsNullOrWhiteSpace(lines[index]))
            index++;

        return lines[index..].ToList();
    }

    private static string[] TrimTrailingAndLeadingBlankLines(string[] lines)
    {
        var start = 0;
        var end = lines.Length - 1;

        while (start <= end && string.IsNullOrWhiteSpace(lines[start]))
            start++;

        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
            end--;

        return start <= end ? lines[start..(end + 1)] : [];
    }

    private static Version ParseCurrentVersion(string text)
    {
        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetProperty("version", out var versionElement))
            throw new ChangelogException("version.json is missing the version property.");

        return Version.Parse(versionElement.GetString() ?? throw new ChangelogException("version.json contains an empty version."));
    }

    private static string ReadAllTextBounded(string absolutePath, string repositoryRoot, long maxBytes)
    {
        var fileInfo = new FileInfo(absolutePath);
        var length = fileInfo.Length;
        if (fileInfo.LinkTarget is null && length > maxBytes)
        {
            var relativePath = string.IsNullOrWhiteSpace(repositoryRoot)
                ? Path.GetFileName(absolutePath)
                : Path.GetRelativePath(repositoryRoot, absolutePath).Replace('\\', '/');
            throw new ChangelogException($"{relativePath}: file is {length} bytes; maximum supported size is {maxBytes} bytes.");
        }

        using var stream = File.Open(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        var totalBytesRead = 0L;

        while (true)
        {
            var remainingBytes = maxBytes + 1 - totalBytesRead;
            var readLength = (int)Math.Min(buffer.Length, remainingBytes);
            var bytesRead = stream.Read(buffer.AsSpan(0, readLength));
            if (bytesRead == 0)
                break;

            totalBytesRead += bytesRead;
            if (totalBytesRead > maxBytes)
            {
                var relativePath = string.IsNullOrWhiteSpace(repositoryRoot)
                    ? Path.GetFileName(absolutePath)
                    : Path.GetRelativePath(repositoryRoot, absolutePath).Replace('\\', '/');
                throw new ChangelogException($"{relativePath}: file is larger than {maxBytes} bytes; maximum supported size is {maxBytes} bytes.");
            }

            memory.Write(buffer.AsSpan(0, bytesRead));
        }

        memory.Position = 0;
        using var reader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WritePreparedFiles(
        string changelogPath,
        string originalChangelog,
        string updatedChangelog,
        string versionPath,
        string originalVersionJson,
        string updatedVersionJson,
        IReadOnlyList<Fragment> fragments)
    {
        var changelogTempPath = string.Empty;
        var versionTempPath = string.Empty;
        var changelogReplaced = false;
        var versionReplaced = false;
        var fragmentDeletionStarted = false;
        var changelogWritePath = ResolveWriteTargetPath(changelogPath);
        var versionWritePath = ResolveWriteTargetPath(versionPath);

        try
        {
            changelogTempPath = WriteStagedText(changelogWritePath, updatedChangelog);
            versionTempPath = WriteStagedText(versionWritePath, updatedVersionJson);

            NotifyPrepareWritePhase(PrepareWritePhase.StagedFilesWritten);

            ReplaceWithStagedFile(changelogTempPath, changelogWritePath);
            changelogTempPath = string.Empty;
            changelogReplaced = true;
            NotifyPrepareWritePhase(PrepareWritePhase.ChangelogReplaced);

            ReplaceWithStagedFile(versionTempPath, versionWritePath);
            versionTempPath = string.Empty;
            versionReplaced = true;
            NotifyPrepareWritePhase(PrepareWritePhase.VersionReplaced);

            NotifyPrepareWritePhase(PrepareWritePhase.BeforeFragmentsDeleted);
            fragmentDeletionStarted = true;
            foreach (var fragment in fragments)
                DeleteConsumedFragment(fragment);
        }
        catch (Exception) when (!fragmentDeletionStarted)
        {
            RollBackPreparedFiles(
                changelogWritePath,
                originalChangelog,
                changelogReplaced,
                versionWritePath,
                originalVersionJson,
                versionReplaced);
            throw;
        }
        finally
        {
            TryDelete(changelogTempPath);
            TryDelete(versionTempPath);
        }
    }

    private static string WriteStagedText(string targetPath, string contents)
    {
        var tempPath = BuildTempPath(targetPath);
        var tempCreated = false;
        var writeCompleted = false;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                tempCreated = true;
                NotifyPrepareWritePhase(PrepareWritePhase.StagedTempCreated);

                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true);
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            writeCompleted = true;
        }
        finally
        {
            if (tempCreated && !writeCompleted)
                TryDelete(tempPath);
        }

        return tempPath;
    }

    private static string ResolveWriteTargetPath(string targetPath)
    {
        var fileInfo = new FileInfo(targetPath);
        if (fileInfo.LinkTarget is null)
            return targetPath;

        var finalTarget = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
        if (finalTarget is not null)
            return finalTarget.FullName;

        var linkTarget = fileInfo.LinkTarget;
        if (string.IsNullOrEmpty(linkTarget))
            return targetPath;

        if (Path.IsPathFullyQualified(linkTarget))
            return linkTarget;

        var directory = fileInfo.DirectoryName;
        return Path.GetFullPath(Path.Combine(
            string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory,
            linkTarget));
    }

    private static void ReplaceWithStagedFile(string stagedPath, string targetPath)
    {
        File.Move(stagedPath, targetPath, overwrite: true);
    }

    private static void DeleteConsumedFragment(Fragment fragment)
    {
        try
        {
            File.Delete(fragment.AbsolutePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ChangelogException($"{fragment.RelativePath}: failed to delete consumed changelog fragment after CHANGELOG.md and version.json were updated; delete this fragment manually before retrying prepare. {ex.Message}");
        }
    }

    private static void RollBackPreparedFiles(
        string changelogPath,
        string originalChangelog,
        bool changelogReplaced,
        string versionPath,
        string originalVersionJson,
        bool versionReplaced)
    {
        try
        {
            if (versionReplaced)
                RestoreText(versionPath, originalVersionJson);

            if (changelogReplaced)
                RestoreText(changelogPath, originalChangelog);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ChangelogException($"prepare failed before fragment deletion and rollback also failed: {ex.Message}");
        }
    }

    private static void RestoreText(string targetPath, string contents)
    {
        var tempPath = string.Empty;
        try
        {
            tempPath = WriteStagedText(targetPath, contents);
            ReplaceWithStagedFile(tempPath, targetPath);
            tempPath = string.Empty;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static string BuildTempPath(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath);
        var fileName = Path.GetFileName(targetPath);
        var tempFileName = $".{fileName}.{Guid.NewGuid():N}.tmp";
        return string.IsNullOrEmpty(directory)
            ? tempFileName
            : Path.Combine(directory, tempFileName);
    }

    private static void NotifyPrepareWritePhase(PrepareWritePhase phase)
    {
        PrepareWritePhaseForTesting?.Invoke(phase);
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<VersionBlock> PrepareLanguageSection(
        IReadOnlyList<VersionBlock> existingBlocks,
        Version targetVersion,
        string targetHeading,
        IReadOnlyList<Fragment> fragments,
        Language language)
    {
        var orderedBlocks = existingBlocks.Select(block => block with { BodyLines = block.BodyLines.ToList() }).ToList();
        var unreleasedIndex = orderedBlocks.FindIndex(block => block.HeadingLine == "### [Unreleased]");
        if (unreleasedIndex < 0)
            throw new ChangelogException($"Missing ### [Unreleased] block in the {language} section.");

        var targetIndex = orderedBlocks.FindIndex(block => block.HeadingLine.StartsWith($"### [{targetVersion}]", StringComparison.Ordinal));

        var existingTargetBody = targetIndex >= 0 ? orderedBlocks[targetIndex].BodyLines : Array.Empty<string>();
        var unreleasedBody = orderedBlocks[unreleasedIndex].BodyLines;
        var unreleasedNotice = GetUnreleasedNotice(language);
        var pinnedUnreleasedBody = unreleasedBody.Contains(unreleasedNotice, StringComparer.Ordinal)
            ? new[] { unreleasedNotice }
            : Array.Empty<string>();
        var releasableUnreleasedBody = unreleasedBody.Where(line => !string.Equals(line, unreleasedNotice, StringComparison.Ordinal)).ToList();
        var cleanedExistingTargetBody = existingTargetBody.Where(line => !string.Equals(line, unreleasedNotice, StringComparison.Ordinal)).ToList();

        var renderedFragments = RenderFragmentsForLanguage(fragments, language);
        var combinedTargetBody = CombineBodies(cleanedExistingTargetBody, releasableUnreleasedBody, renderedFragments);

        orderedBlocks[unreleasedIndex] = orderedBlocks[unreleasedIndex] with { BodyLines = pinnedUnreleasedBody };

        var targetBlock = new VersionBlock(targetHeading, combinedTargetBody);
        if (targetIndex >= 0)
        {
            orderedBlocks[targetIndex] = targetBlock;
        }
        else
        {
            orderedBlocks.Insert(unreleasedIndex + 1, targetBlock);
        }

        return orderedBlocks;
    }

    private static string GetUnreleasedNotice(Language language) =>
        language == Language.English ? EnglishUnreleasedNotice : JapaneseUnreleasedNotice;

    private static List<string> CombineBodies(params IReadOnlyList<string>[] pieces)
    {
        var result = new List<string>();
        foreach (var piece in pieces)
        {
            if (piece.Count == 0)
                continue;

            if (result.Count > 0 && result[^1].Length != 0)
                result.Add(string.Empty);

            result.AddRange(piece);
        }

        return result;
    }

    private static List<string> RenderFragmentsForLanguage(IReadOnlyList<Fragment> fragments, Language language)
    {
        var result = new List<string>();
        var grouped = fragments
            .GroupBy(fragment => fragment.Category, StringComparer.Ordinal)
            .OrderBy(group => Array.IndexOf(AllowedCategories, group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in grouped)
        {
            var heading = language == Language.English
                ? CategoryHeadings[group.Key]
                : JapaneseCategoryHeadings[group.Key];

            if (result.Count > 0)
                result.Add(string.Empty);

            result.Add($"#### {heading}");
            result.Add(string.Empty);

            foreach (var fragment in group.OrderBy(fragment => fragment.RelativePath, StringComparer.Ordinal))
            {
                var body = language == Language.English ? fragment.EnglishBodyLines : fragment.JapaneseBodyLines;
                result.AddRange(body);
            }
        }

        return result;
    }

    private static List<FooterEntry> PrepareFooterEntries(
        IReadOnlyList<FooterEntry> existingEntries,
        string targetVersion,
        string releaseBase)
    {
        var entries = existingEntries.ToList();

        var unreleasedIndex = entries.FindIndex(entry => entry.Label == "Unreleased");
        if (unreleasedIndex < 0)
        {
            entries.Insert(0, new FooterEntry("Unreleased", targetVersion, null));
        }
        else
        {
            entries[unreleasedIndex] = new FooterEntry("Unreleased", targetVersion, null);
        }

        var targetIndex = entries.FindIndex(entry => entry.Label == targetVersion);
        var targetEntry = new FooterEntry(targetVersion, releaseBase, targetVersion);

        if (targetIndex >= 0)
            entries[targetIndex] = targetEntry;
        else
            entries.Insert(Math.Min(unreleasedIndex >= 0 ? unreleasedIndex + 1 : 1, entries.Count), targetEntry);

        return entries;
    }

    private sealed record Fragment(
        string AbsolutePath,
        string RelativePath,
        string Category,
        IReadOnlyList<int> Issues,
        IReadOnlyList<string> Affected,
        IReadOnlyList<string> EnglishBodyLines,
        IReadOnlyList<string> JapaneseBodyLines);

    private sealed record VersionBlock(string HeadingLine, IReadOnlyList<string> BodyLines);

    private sealed record FooterEntry(string Label, string BaseVersion, string? TargetVersion, bool IsTagLink = false);

    private sealed record ParsedChangelog(
        IReadOnlyList<string> PrefixLines,
        IReadOnlyList<VersionBlock> EnglishBlocks,
        IReadOnlyList<VersionBlock> JapaneseBlocks,
        IReadOnlyList<FooterEntry> FooterEntries)
    {
        public static ParsedChangelog Parse(string text)
        {
            var lines = text.Split('\n');
            var englishIndex = Array.FindIndex(lines, line => line == "## English");
            var japaneseIndex = Array.FindIndex(lines, line => line == "## 日本語");
            if (englishIndex < 0 || japaneseIndex < 0 || japaneseIndex <= englishIndex)
                throw new ChangelogException("CHANGELOG.md is missing the English/日本語 section markers.");

            var footerIndex = Array.FindIndex(lines, japaneseIndex + 1, line => FooterLinkRegex.IsMatch(line));
            if (footerIndex < 0)
                throw new ChangelogException("CHANGELOG.md is missing compare-link footer definitions.");

            var prefixLines = lines[..englishIndex].ToList();
            var englishSectionLines = lines[(englishIndex + 1)..japaneseIndex];
            var japaneseSectionLines = lines[(japaneseIndex + 1)..footerIndex];
            var footerLines = lines[footerIndex..];

            return new ParsedChangelog(
                prefixLines,
                ParseBlocks(englishSectionLines, "English"),
                ParseBlocks(japaneseSectionLines, "Japanese"),
                ParseFooter(footerLines));
        }

        public bool HasReleaseSection(Version version)
        {
            var heading = $"### [{version}]";
            return EnglishBlocks.Any(block => block.HeadingLine.StartsWith(heading, StringComparison.Ordinal)) ||
                   JapaneseBlocks.Any(block => block.HeadingLine.StartsWith(heading, StringComparison.Ordinal));
        }

        public string Render(
            IReadOnlyList<VersionBlock> english,
            IReadOnlyList<VersionBlock> japanese,
            IReadOnlyList<FooterEntry> footerEntries)
        {
            var output = new List<string>();
            output.AddRange(PrefixLines);
            if (output.Count > 0 && output[^1].Length != 0)
                output.Add(string.Empty);

            output.Add("## English");
            output.Add(string.Empty);
            AppendBlocks(output, english);

            if (english.Count > 0 && english[^1].BodyLines.Count > 0)
                output.Add(string.Empty);

            output.Add("## 日本語");
            output.Add(string.Empty);
            AppendBlocks(output, japanese);

            if (japanese.Count > 0 && japanese[^1].BodyLines.Count > 0)
                output.Add(string.Empty);

            output.AddRange(RenderFooter(footerEntries));

            var builder = new StringBuilder();
            for (var i = 0; i < output.Count; i++)
            {
                builder.Append(output[i]);
                if (i < output.Count - 1)
                    builder.Append('\n');
            }

            return builder.ToString().TrimEnd() + Environment.NewLine;
        }

        private static void AppendBlocks(List<string> output, IReadOnlyList<VersionBlock> blocks)
        {
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                output.Add(block.HeadingLine);
                output.Add(string.Empty);
                output.AddRange(block.BodyLines);

                var isLast = i == blocks.Count - 1;
                if (!isLast && block.BodyLines.Count > 0)
                    output.Add(string.Empty);
            }
        }

        private static List<VersionBlock> ParseBlocks(string[] lines, string sectionName)
        {
            var blocks = new List<VersionBlock>();
            var index = 0;

            while (index < lines.Length)
            {
                while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
                    index++;

                if (index >= lines.Length)
                    break;

                if (!lines[index].StartsWith("### ", StringComparison.Ordinal))
                    throw new ChangelogException($"CHANGELOG.md {sectionName} section contains unexpected content: '{lines[index]}'.");

                var heading = lines[index];
                index++;

                while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
                    index++;

                var body = new List<string>();
                while (index < lines.Length && !lines[index].StartsWith("### ", StringComparison.Ordinal))
                {
                    body.Add(lines[index]);
                    index++;
                }

                body = TrimLeadingAndTrailingBlankLines(body);
                blocks.Add(new VersionBlock(heading, body));
            }

            return blocks;
        }

        private static List<string> TrimLeadingAndTrailingBlankLines(List<string> lines)
        {
            var start = 0;
            var end = lines.Count - 1;

            while (start <= end && string.IsNullOrWhiteSpace(lines[start]))
                start++;

            while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
                end--;

            if (start > end)
                return [];

            return lines.GetRange(start, end - start + 1);
        }

        private static List<FooterEntry> ParseFooter(string[] lines)
        {
            var entries = new List<FooterEntry>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var match = FooterLinkRegex.Match(line);
                if (match.Success)
                {
                    var label = match.Groups["label"].Value;
                    var baseVersion = match.Groups["base"].Value;
                    var targetVersion = match.Groups["target"].Success ? match.Groups["target"].Value : null;
                    entries.Add(new FooterEntry(label, baseVersion, targetVersion, IsTagLink: false));
                    continue;
                }

                var tagMatch = FooterTagLinkRegex.Match(line);
                if (tagMatch.Success)
                {
                    var label = tagMatch.Groups["label"].Value;
                    var version = tagMatch.Groups["version"].Value;
                    entries.Add(new FooterEntry(label, version, version, IsTagLink: true));
                    continue;
                }

                throw new ChangelogException($"CHANGELOG.md footer contains invalid line: '{line}'.");
            }

            return entries;
        }

        private static IReadOnlyList<string> RenderFooter(IReadOnlyList<FooterEntry> entries)
        {
            return entries.Select(entry =>
                entry.IsTagLink
                    ? $"[{entry.Label}]: https://github.com/Widthdom/CodeIndex/releases/tag/v{entry.TargetVersion}"
                    : entry.TargetVersion is null
                        ? $"[{entry.Label}]: https://github.com/Widthdom/CodeIndex/compare/v{entry.BaseVersion}...HEAD"
                        : $"[{entry.Label}]: https://github.com/Widthdom/CodeIndex/compare/v{entry.BaseVersion}...v{entry.TargetVersion}")
                .ToList();
        }
    }

    private enum Language
    {
        English,
        Japanese,
    }
}

internal enum PrepareWritePhase
{
    StagedTempCreated,
    StagedFilesWritten,
    ChangelogReplaced,
    VersionReplaced,
    BeforeFragmentsDeleted,
}

public sealed record PrepareResult(string Summary, string? RenderedChangelog);

public sealed class ChangelogException : Exception
{
    public ChangelogException(string message)
        : base(message)
    {
    }
}
