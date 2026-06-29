using System.Text.Json;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal sealed record ActiveWorkspaceState(string Name, string Root, string DbPath);

internal static class ActiveWorkspace
{
    internal const string EnvironmentVariable = "CDIDX_ACTIVE_WORKSPACE";
    internal const int MaxEnvironmentPathChars = 4096;
    internal const int MaxWorkspaceNameChars = 128;
    private const int MaxStateBytes = 64 * 1024;
    internal const int MaxStateJsonDepth = 16;
    internal static string StatePath
    {
        get
        {
            if (TryGetStatePath(out var path, out var reason))
                return path;
            throw new InvalidOperationException($"Active workspace state path is invalid: {reason}.");
        }
    }

    internal static ActiveWorkspaceState? Load()
    {
        var envPath = CdidxEnvironment.GetProcessEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath))
            return LoadFromEnvironment(envPath);

        if (!TryGetStatePath(out var path, out var statePathReason))
        {
            WriteLoadWarning("config home", statePathReason);
            return null;
        }

        if (!File.Exists(LongPath.EnsureWindowsPrefix(path)))
            return null;

        try
        {
            var text = DataDirectorySecurity.ReadTextWithinLimit(path, MaxStateBytes, FileShare.ReadWrite);
            if (text is null)
            {
                WriteLoadWarning("state file", $"file exceeds {MaxStateBytes} bytes");
                return null;
            }

            using var document = BoundedJson.ParseDocument(text, MaxStateBytes, MaxStateJsonDepth);
            var root = document.RootElement;
            var name = ReadString(root, "name") ?? "default";
            var workspaceRoot = ReadString(root, "root");
            var dbPath = ReadString(root, "db_path");
            if (!TryNormalizeState(name, workspaceRoot, dbPath, out var state, out var stateReason))
            {
                WriteLoadWarning("state file", stateReason);
                return null;
            }

            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            WriteLoadWarning("state file", DescribeLoadFailure(ex));
            return null;
        }
    }

    internal static void Save(ActiveWorkspaceState state)
    {
        if (!TryGetStatePath(out var statePath, out var statePathReason))
            throw new InvalidOperationException($"Active workspace state path is invalid: {statePathReason}.");
        if (!TryNormalizeState(state.Name, state.Root, state.DbPath, out var payload, out var stateReason))
            throw new InvalidOperationException($"Active workspace state is invalid: {stateReason}.");

        DataDirectorySecurity.CreateSensitiveDirectory(Path.GetDirectoryName(statePath)!);
        DataDirectorySecurity.WritePrivateText(statePath, JsonSerializer.Serialize(payload, ProgramRunner.CreateDefaultJsonOptions()));
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ActiveWorkspaceState? LoadFromEnvironment(string envPath)
    {
        if (envPath.Length > MaxEnvironmentPathChars)
        {
            WriteLoadWarning($"environment variable {EnvironmentVariable}", $"value exceeds {MaxEnvironmentPathChars} characters");
            return null;
        }

        try
        {
            if (!IsFullyQualifiedPath(envPath))
            {
                WriteLoadWarning($"environment variable {EnvironmentVariable}", "value must be an absolute database path");
                return null;
            }

            var fullPath = Path.GetFullPath(envPath);
            var root = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                WriteLoadWarning($"environment variable {EnvironmentVariable}", "database path parent is unavailable");
                return null;
            }

            if (!TryNormalizeState("env", root, fullPath, out var state, out var stateReason))
            {
                WriteLoadWarning($"environment variable {EnvironmentVariable}", stateReason);
                return null;
            }

            return state;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            WriteLoadWarning($"environment variable {EnvironmentVariable}", DescribeLoadFailure(ex));
            return null;
        }
    }

    private static bool TryGetStatePath(out string path, out string reason)
    {
        path = string.Empty;
        reason = string.Empty;
        var configHome = CdidxEnvironment.GetProcessEnvironmentVariable("XDG_CONFIG_HOME");
        string root;
        if (string.IsNullOrWhiteSpace(configHome))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile))
            {
                reason = "user profile directory is unavailable";
                return false;
            }

            root = Path.Combine(profile, ".config");
        }
        else
        {
            if (configHome.Length > MaxEnvironmentPathChars)
            {
                reason = $"XDG_CONFIG_HOME exceeds {MaxEnvironmentPathChars} characters";
                return false;
            }

            if (!IsFullyQualifiedPath(configHome))
            {
                reason = "XDG_CONFIG_HOME must be an absolute path";
                return false;
            }

            root = configHome;
        }

        try
        {
            path = Path.Combine(PathCasing.NormalizeBoundaryPath(root), "cdidx", "active.json");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            reason = "XDG_CONFIG_HOME is invalid";
            return false;
        }
    }

    private static bool TryNormalizeState(
        string? name,
        string? root,
        string? dbPath,
        out ActiveWorkspaceState? state,
        out string reason)
    {
        state = null;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(root))
        {
            reason = "`root` is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            reason = "`db_path` is required";
            return false;
        }

        if (root.Length > MaxEnvironmentPathChars)
        {
            reason = $"`root` exceeds {MaxEnvironmentPathChars} characters";
            return false;
        }

        if (dbPath.Length > MaxEnvironmentPathChars)
        {
            reason = $"`db_path` exceeds {MaxEnvironmentPathChars} characters";
            return false;
        }

        if (!TryNormalizeName(name, out var normalizedName, out reason))
            return false;

        if (!IsFullyQualifiedPath(root))
        {
            reason = "`root` must be an absolute path";
            return false;
        }

        if (!IsFullyQualifiedPath(dbPath))
        {
            reason = "`db_path` must be an absolute path";
            return false;
        }

        try
        {
            var normalizedRoot = PathCasing.NormalizeBoundaryPath(root);
            var normalizedDbPath = PathCasing.NormalizeBoundaryPath(dbPath);
            if (PathCasing.PathsEqual(normalizedRoot, normalizedDbPath)
                || !PathCasing.IsPathEqualOrParent(normalizedRoot, normalizedDbPath))
            {
                reason = "`db_path` must be inside `root`";
                return false;
            }

            state = new ActiveWorkspaceState(normalizedName, normalizedRoot, normalizedDbPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            reason = "state paths are invalid";
            return false;
        }
    }

    private static bool TryNormalizeName(string? name, out string normalizedName, out string reason)
    {
        reason = string.Empty;
        normalizedName = "default";
        if (string.IsNullOrWhiteSpace(name))
            return true;

        normalizedName = name.Trim();
        if (normalizedName.Length > MaxWorkspaceNameChars)
        {
            reason = $"`name` exceeds {MaxWorkspaceNameChars} characters";
            return false;
        }

        if (normalizedName.Any(char.IsControl))
        {
            reason = "`name` must not contain control characters";
            return false;
        }

        return true;
    }

    private static bool IsFullyQualifiedPath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string DescribeLoadFailure(Exception ex) => ex switch
    {
        JsonException => "invalid JSON",
        UnauthorizedAccessException => "permission denied",
        ArgumentException or NotSupportedException or PathTooLongException => "invalid path",
        IOException => "read failed",
        _ => "load failed",
    };

    private static void WriteLoadWarning(string source, string reason)
        => CommandErrorWriter.WriteStderr($"[cdidx] Ignoring active workspace {source}: {ConsoleUi.FormatBoundedValue(reason)}. Hint: inspect or reset the active workspace configuration.");
}
