using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal sealed record WorkspaceMember(string Path, string DbPath, bool Exists);

internal sealed record WorkspaceManifest(
    string Path,
    string Root,
    string IndexStrategy,
    string DefaultDbName,
    IReadOnlyList<WorkspaceMember> Members);

internal sealed record WorkspaceListJsonResult(
    WorkspaceManifest? Manifest,
    IReadOnlyList<WorkspaceMember> Members,
    WorkspaceManifestStatusJsonResult ManifestStatus)
{
    [JsonPropertyName("manifest_found")]
    public bool ManifestFound => Manifest is not null;
}

internal sealed record ActiveWorkspaceJsonResult
{
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("workspace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public ActiveWorkspaceState? Workspace { get; init; }

    [JsonPropertyName("active_workspace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActiveWorkspaceState? ActiveWorkspace => Workspace;

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "inactive";

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "not_set";

    [JsonPropertyName("code")]
    public string Code { get; init; } = "active_workspace_not_set";

    internal static ActiveWorkspaceJsonResult From(ActiveWorkspaceState? state, string? path)
        => state is null
            ? new ActiveWorkspaceJsonResult()
            : new ActiveWorkspaceJsonResult
            {
                Active = true,
                Workspace = state,
                Path = path,
                Status = "active",
                Reason = "active",
                Code = "active_workspace_set",
            };
}

internal sealed record ConfigShowJsonResult(
    string? ConfigPath,
    ActiveWorkspaceState? ActiveWorkspace,
    IReadOnlyList<string> Precedence,
    IReadOnlyList<string> SupportedFiles,
    ConfigFileStatusJsonResult ConfigFile,
    ActiveWorkspaceStatusJsonResult ActiveWorkspaceStatus,
    WorkspaceManifestStatusJsonResult WorkspaceManifest,
    IReadOnlyList<string> SearchedPaths,
    IReadOnlyDictionary<string, ConfigEffectiveValueJsonResult> EffectiveConfig,
    EnvironmentVariableInventorySummaryJsonResult EnvironmentInventorySummary,
    DoctorRedactionJsonResult Redaction);

internal sealed record ConfigFileStatusJsonResult(
    string Status,
    string Reason,
    string? Path,
    string? Error,
    IReadOnlyList<string> SearchedPaths,
    IReadOnlyList<string> SupportedFiles);

internal sealed record ActiveWorkspaceStatusJsonResult(
    string Status,
    string Reason,
    string? Path,
    string? Name,
    string? Root,
    string? DbPath);

internal sealed record WorkspaceManifestStatusJsonResult(
    string Status,
    string Reason,
    string? Path,
    IReadOnlyList<string> SearchedPaths,
    IReadOnlyList<string> SupportedFiles)
{
    [JsonPropertyName("manifest_found")]
    public bool ManifestFound => Path is not null;

    [JsonPropertyName("code")]
    public string Code
        => Path is not null
            ? "workspace_manifest_found"
            : Reason switch
            {
                "invalid" => "workspace_manifest_invalid",
                "not_found" => "workspace_manifest_not_found",
                _ => "workspace_manifest_unavailable",
            };
}

internal sealed record ConfigEffectiveValueJsonResult(
    string EnvironmentVariable,
    string Value,
    string Source,
    string DefaultValue,
    bool Sensitive,
    string ConfigFileSupported,
    string Policy);

internal sealed record WorkspaceManifestDiscoveryResult(
    string? Path,
    IReadOnlyList<string> SearchedPaths,
    IReadOnlyList<string> SupportedFiles,
    string Status,
    string Reason);

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
    private static readonly IReadOnlyList<string> DiscoveryFileNames = [DotFileName, FileName];
    internal static readonly IReadOnlyList<string> SupportedFiles = [FileName, DotFileName];

    internal static WorkspaceManifest? Find(string startingDirectory)
    {
        var discovery = Discover(startingDirectory);
        return discovery.Path is null ? null : Load(discovery.Path);
    }

    internal static WorkspaceManifestDiscoveryResult Discover(string startingDirectory)
    {
        var searchedPaths = new List<string>();
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
            throw new InvalidDataException($"Workspace manifest discovery start directory is invalid: {ConsoleUi.FormatBoundedValue(startingDirectory)}", ex);
        }

        var searchedAncestors = 0;
        while (current is not null)
        {
            if (searchedAncestors >= MaxManifestDiscoveryAncestors)
                throw new InvalidDataException($"Workspace manifest discovery exceeded the {MaxManifestDiscoveryAncestors} ancestor limit from {ConsoleUi.FormatBoundedValue(startingDirectory)}.");
            searchedAncestors++;

            foreach (var name in DiscoveryFileNames)
            {
                var candidate = Path.Combine(current.FullName, name);
                searchedPaths.Add(candidate);
                if (File.Exists(LongPath.EnsureWindowsPrefix(candidate)))
                    return new WorkspaceManifestDiscoveryResult(candidate, searchedPaths, SupportedFiles, "found", "found");
            }

            current = current.Parent;
        }

        return new WorkspaceManifestDiscoveryResult(null, searchedPaths, SupportedFiles, "not_found", "not_found");
    }

    internal static WorkspaceManifest Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        var text = DataDirectorySecurity.ReadTextWithinLimit(fullPath, MaxManifestBytes)
                   ?? throw new InvalidDataException($"{fullPath} exceeds the {MaxManifestBytes} byte limit.");
        using var document = BoundedJson.ParseDocument(
            text,
            MaxManifestBytes,
            MaxManifestDepth,
            JsonCommentHandling.Skip,
            allowTrailingCommas: true);

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

        throw new InvalidDataException($"Workspace manifest index_strategy must be 'per_member' or 'single': {ConsoleUi.FormatBoundedValue(strategy)}");
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
            throw new InvalidDataException($"Workspace manifest default_db_name must be a plain file name: {ConsoleUi.FormatBoundedValue(dbName)}");
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
            throw new InvalidDataException("Workspace manifest member path must be relative.");

        var normalizedRoot = PathCasing.NormalizeBoundaryPath(root);
        var fullMember = PathCasing.NormalizeBoundaryPath(Path.Combine(normalizedRoot, member));
        if (!PathCasing.IsPathEqualOrParent(normalizedRoot, fullMember))
            throw new InvalidDataException("Workspace manifest member path escapes the manifest root.");

        return fullMember;
    }
}
