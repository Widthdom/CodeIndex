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
        });

        var element = document.RootElement;
        var strategy = ReadString(element, "index_strategy") ?? "per_member";
        var dbName = ReadString(element, "default_db_name") ?? "codeindex.db";
        var rawMembers = element.TryGetProperty("members", out var membersElement) && membersElement.ValueKind == JsonValueKind.Array
            ? membersElement.EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.String)
                .Select(m => m.GetString())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m!)
                .ToArray()
            : Array.Empty<string>();

        var members = rawMembers.Select(member =>
        {
            var fullMember = Path.GetFullPath(Path.Combine(root, member));
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
}
