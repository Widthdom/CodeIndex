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
    internal const int MaxDefaultDbNameChars = 255;

    internal static WorkspaceManifest? Find(string startingDirectory)
    {
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(startingDirectory));
        }
        catch
        {
            return null;
        }

        while (current is not null)
        {
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

        var members = rawMembers.Select(member =>
        {
            var fullMember = ResolveMemberPath(root, member);
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
        if (!element.TryGetProperty("members", out var membersElement) || membersElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var members = new List<string>();
        foreach (var member in membersElement.EnumerateArray())
        {
            if (member.ValueKind != JsonValueKind.String)
                continue;

            var value = member.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (value.Length > MaxManifestMemberPathChars)
                throw new InvalidDataException($"Workspace manifest member path exceeds the {MaxManifestMemberPathChars} character limit.");

            if (members.Count >= MaxManifestMembers)
                throw new InvalidDataException($"Workspace manifest members exceed the {MaxManifestMembers} member limit.");

            members.Add(value);
        }

        return members;
    }

    private static string ResolveMemberPath(string root, string member)
    {
        if (Path.IsPathRooted(member))
            throw new InvalidDataException($"Workspace manifest member path must be relative: {member}");

        var fullMember = Path.GetFullPath(Path.Combine(root, member));
        if (!IsSameOrDescendant(root, fullMember))
            throw new InvalidDataException($"Workspace manifest member path escapes the manifest root: {member}");

        return fullMember;
    }

    private static bool IsSameOrDescendant(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedPath = Path.GetFullPath(path);
        if (string.Equals(normalizedRoot, normalizedPath, comparison))
            return true;

        var rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSeparator, comparison);
    }
}
