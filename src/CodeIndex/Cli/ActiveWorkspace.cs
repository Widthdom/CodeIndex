using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal sealed record ActiveWorkspaceState(string Name, string Root, string DbPath);

internal static class ActiveWorkspace
{
    internal const string EnvironmentVariable = "CDIDX_ACTIVE_WORKSPACE";
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
            return new ActiveWorkspaceState("env", Path.GetDirectoryName(Path.GetFullPath(envPath)) ?? Environment.CurrentDirectory, Path.GetFullPath(envPath));

        var path = StatePath;
        if (!File.Exists(LongPath.EnsureWindowsPrefix(path)))
            return null;

        try
        {
            var text = DataDirectorySecurity.ReadTextWithinLimit(path, MaxStateBytes, FileShare.ReadWrite);
            if (text is null)
            {
                WriteLoadWarning(path, $"file exceeds {MaxStateBytes} bytes");
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
            WriteLoadWarning(path, ex.Message);
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

    private static void WriteLoadWarning(string path, string reason)
        => Console.Error.WriteLine($"[cdidx] Ignoring active workspace state at {path}: {reason}");
}
