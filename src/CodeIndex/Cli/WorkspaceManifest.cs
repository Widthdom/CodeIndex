using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal sealed record WorkspaceMember(string Path, string DbPath, bool Exists);

internal sealed record WorkspaceManifest(
    string Path,
    string Root,
    string IndexStrategy,
    string DefaultDbName,
    IReadOnlyList<WorkspaceMember> Members);

internal sealed record WorkspaceListJsonResult(WorkspaceManifest? Manifest, IReadOnlyList<WorkspaceMember> Members);

internal sealed record ActiveWorkspaceJsonResult(ActiveWorkspaceState? ActiveWorkspace, string? Path);

internal sealed record ConfigShowJsonResult(
    string? ConfigPath,
    ActiveWorkspaceState? ActiveWorkspace,
    IReadOnlyList<string> Precedence,
    IReadOnlyList<string> SupportedFiles);

internal static class WorkspaceManifestLoader
{
    internal const string FileName = "cdidx.workspace.json";
    internal const string DotFileName = ".cdidx-workspace.json";
    internal const int MaxManifestBytes = 64 * 1024;
    internal const int MaxManifestDepth = 16;
    internal const int MaxManifestMembers = 1024;
    internal const int MaxManifestMemberPathChars = 4096;
    internal const int MaxManifestMemberDiagnostics = 8;
    internal const int MaxManifestDiscoveryAncestors = 256;
    internal const int MaxDefaultDbNameChars = 255;

    internal static WorkspaceManifest? Find(string startingDirectory)
    {
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(startingDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or IOException
                                      or NotSupportedException
                                      or PathTooLongException
                                      or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Workspace manifest discovery start directory is invalid: {startingDirectory}", ex);
        }

        var searchedAncestors = 0;
        while (current is not null)
        {
            if (searchedAncestors >= MaxManifestDiscoveryAncestors)
                throw new InvalidDataException($"Workspace manifest discovery exceeded the {MaxManifestDiscoveryAncestors} ancestor limit from {startingDirectory}.");
            searchedAncestors++;

            foreach (var name in new[] { DotFileName, FileName })
            {
                var candidate = Path.Combine(current.FullName, name);
                if (File.Exists(LongPath.EnsureWindowsPrefix(candidate)))
                    return Load(candidate);
            }

            current = current.Parent;
        }

        return null;
    }

    internal static WorkspaceManifest Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        var text = DataDirectorySecurity.ReadTextWithinLimit(fullPath, MaxManifestBytes)
                   ?? throw new InvalidDataException($"{fullPath} exceeds the {MaxManifestBytes} byte limit.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = MaxManifestDepth,
        });

        var element = document.RootElement;
        var strategy = ValidateIndexStrategy(ReadString(element, "index_strategy") ?? "per_member");
        var dbName = ValidateDefaultDbName(ReadString(element, "default_db_name") ?? "codeindex.db");
        var rawMembers = ReadMembers(element);
        var uniqueMembers = NormalizeAndDedupeMembers(root, rawMembers);

        var members = uniqueMembers.Select(fullMember =>
        {
            var dbPath = string.Equals(strategy, "single", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(root, ".cdidx", dbName)
                : Path.Combine(fullMember, ".cdidx", dbName);
            return new WorkspaceMember(fullMember, dbPath, Directory.Exists(LongPath.EnsureWindowsPrefix(fullMember)));
        }).ToArray();

        return new WorkspaceManifest(fullPath, root, strategy, dbName, members);
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ValidateIndexStrategy(string strategy)
    {
        if (string.Equals(strategy, "per_member", StringComparison.OrdinalIgnoreCase)
            || string.Equals(strategy, "single", StringComparison.OrdinalIgnoreCase))
        {
            return strategy;
        }

        throw new InvalidDataException($"Workspace manifest index_strategy must be 'per_member' or 'single': {strategy}");
    }

    private static string ValidateDefaultDbName(string dbName)
    {
        if (dbName.Length > MaxDefaultDbNameChars)
            throw new InvalidDataException($"Workspace manifest default_db_name exceeds the {MaxDefaultDbNameChars} character limit.");

        if (string.IsNullOrWhiteSpace(dbName)
            || dbName is "." or ".."
            || Path.IsPathRooted(dbName)
            || dbName.Contains('/')
            || dbName.Contains('\\')
            || dbName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetFileName(dbName), dbName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Workspace manifest default_db_name must be a plain file name: {dbName}");
        }

        return dbName;
    }

    private static IReadOnlyList<string> ReadMembers(JsonElement element)
    {
        if (!element.TryGetProperty("members", out var membersElement))
            return Array.Empty<string>();
        if (membersElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Workspace manifest members must be an array of relative path strings.");

        var members = new List<string>();
        var diagnostics = new List<string>();
        var invalidCount = 0;
        var memberIndex = 0;
        foreach (var member in membersElement.EnumerateArray())
        {
            if (member.ValueKind != JsonValueKind.String)
            {
                AddMemberDiagnostic(diagnostics, ref invalidCount, $"members[{memberIndex}] must be a string.");
                memberIndex++;
                continue;
            }

            var value = member.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                AddMemberDiagnostic(diagnostics, ref invalidCount, $"members[{memberIndex}] must be a non-empty relative path string.");
                memberIndex++;
                continue;
            }

            if (value.Length > MaxManifestMemberPathChars)
            {
                AddMemberDiagnostic(diagnostics, ref invalidCount, $"members[{memberIndex}] exceeds the {MaxManifestMemberPathChars} character limit.");
                memberIndex++;
                continue;
            }

            if (members.Count >= MaxManifestMembers)
                throw new InvalidDataException($"Workspace manifest members exceed the {MaxManifestMembers} member limit.");

            members.Add(value);
            memberIndex++;
        }

        if (diagnostics.Count > 0)
        {
            var suffix = invalidCount > diagnostics.Count
                ? $" and {invalidCount - diagnostics.Count} more invalid member entr{(invalidCount - diagnostics.Count == 1 ? "y" : "ies")}"
                : string.Empty;
            throw new InvalidDataException($"Workspace manifest members contain invalid entries: {string.Join("; ", diagnostics)}{suffix}.");
        }

        return members;
    }

    private static void AddMemberDiagnostic(List<string> diagnostics, ref int invalidCount, string diagnostic)
    {
        invalidCount++;
        if (diagnostics.Count < MaxManifestMemberDiagnostics)
            diagnostics.Add(diagnostic);
    }

    private static IReadOnlyList<string> NormalizeAndDedupeMembers(string root, IReadOnlyList<string> rawMembers)
    {
        if (rawMembers.Count == 0)
            return Array.Empty<string>();

        var members = new List<string>(rawMembers.Count);
        var seen = new HashSet<string>(StringComparer.FromComparison(PathCasing.ComparisonFor(root)));
        foreach (var member in rawMembers)
        {
            var fullMember = ResolveMemberPath(root, member);
            if (seen.Add(fullMember))
                members.Add(fullMember);
        }

        return members;
    }

    private static string ResolveMemberPath(string root, string member)
    {
        if (Path.IsPathRooted(member))
            throw new InvalidDataException($"Workspace manifest member path must be relative: {member}");

        var normalizedRoot = NormalizeBoundaryPath(Path.GetFullPath(root));
        var fullMember = NormalizeBoundaryPath(Path.GetFullPath(Path.Combine(normalizedRoot, member)));
        if (!PathCasing.IsPathEqualOrParent(normalizedRoot, fullMember))
            throw new InvalidDataException($"Workspace manifest member path escapes the manifest root: {member}");

        return fullMember;
    }

    private static string NormalizeBoundaryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.Ordinal))
            return fullPath;
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
