using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal sealed record ActiveWorkspaceState(string Name, string Root, string DbPath);

internal static class ActiveWorkspace
{
    internal const string EnvironmentVariable = "CDIDX_ACTIVE_WORKSPACE";
    internal const int MaxEnvironmentPathChars = 4096;
    private const int MaxStateBytes = 64 * 1024;
    internal const int MaxStateJsonDepth = 16;
    private static readonly JsonDocumentOptions StateJsonDocumentOptions = new()
    {
        MaxDepth = MaxStateJsonDepth,
    };

    internal static string StatePath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrWhiteSpace(configHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : configHome;
            return Path.Combine(root, "cdidx", "active.json");
        }
    }

    internal static ActiveWorkspaceState? Load()
    {
        var envPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath))
            return LoadFromEnvironment(envPath);

        var path = StatePath;
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

            using var document = JsonDocument.Parse(text, StateJsonDocumentOptions);
            var root = document.RootElement;
            var name = ReadString(root, "name") ?? "default";
            var workspaceRoot = ReadString(root, "root") ?? Environment.CurrentDirectory;
            var dbPath = ReadString(root, "db_path");
            if (string.IsNullOrWhiteSpace(dbPath))
                return null;
            return new ActiveWorkspaceState(name, Path.GetFullPath(workspaceRoot), Path.GetFullPath(dbPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            WriteLoadWarning("state file", DescribeLoadFailure(ex));
            return null;
        }
    }

    internal static void Save(ActiveWorkspaceState state)
    {
        DataDirectorySecurity.CreateSensitiveDirectory(Path.GetDirectoryName(StatePath)!);
        var payload = new ActiveWorkspaceState(state.Name, Path.GetFullPath(state.Root), Path.GetFullPath(state.DbPath));
        DataDirectorySecurity.WritePrivateText(StatePath, JsonSerializer.Serialize(payload, ProgramRunner.CreateDefaultJsonOptions()));
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
            var fullPath = Path.GetFullPath(envPath);
            return new ActiveWorkspaceState("env", Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory, fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            WriteLoadWarning($"environment variable {EnvironmentVariable}", DescribeLoadFailure(ex));
            return null;
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
        => Console.Error.WriteLine($"[cdidx] Ignoring active workspace {source}: {ConsoleUi.FormatBoundedValue(reason)}. Hint: inspect or reset the active workspace configuration.");
}
