using System.Diagnostics;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;

namespace CodeIndex.Mcp;

internal sealed class McpIndexRunLock : IDisposable
{
    internal const string LockFileName = "index.lock";
    private const int MaxInfoBytes = 4 * 1024;
    internal const int MaxInfoJsonDepth = 16;
    private static readonly TimeSpan StaleInfoGracePeriod = TimeSpan.FromSeconds(2);
    private readonly FileStream _stream;
    private readonly string _infoPath;
    private bool _disposed;

    internal static Action<string> DeleteFileForTesting { get; set; } = File.Delete;
    internal static Action<LockCleanupDiagnostic>? CleanupDiagnosticSinkForTesting { get; set; }

    private McpIndexRunLock(FileStream stream, string infoPath)
    {
        _stream = stream;
        _infoPath = infoPath;
    }

    internal static bool TryAcquire(string dbPath, out McpIndexRunLock? runLock, out string? error)
    {
        runLock = null;
        error = null;

        var lockPath = ResolveLockPath(dbPath);
        var lockDirectory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(lockDirectory))
            Directory.CreateDirectory(lockDirectory);

        var infoPath = lockPath + ".info";
        try
        {
            var stream = ExclusiveFileLock.Open(lockPath);
            var acquired = new McpIndexRunLock(stream, infoPath);
            acquired.WriteHolderInfo();
            runLock = acquired;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = BuildBusyMessage(infoPath);
            return false;
        }
    }

    internal static string ResolveLockPath(string dbPath)
    {
        if (Uri.TryCreate(dbPath, UriKind.Absolute, out var uri) && uri.IsFile)
            dbPath = uri.LocalPath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.GetFullPath(".");

        var fileName = Path.GetFileName(dbPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "codeindex.db";

        return Path.Combine(directory, $"{fileName}.{LockFileName}");
    }

    private void WriteHolderInfo()
    {
        var since = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            ExclusiveFileLock.WriteHolderInfo(_infoPath, $$"""{"pid":{{Environment.ProcessId}},"since":"{{since}}"}""");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string BuildBusyMessage(string infoPath)
    {
        var holder = TryReadHolderInfo(infoPath);
        if (holder is { ProcessStillRunning: false } && DateTimeOffset.UtcNow - holder.Since >= StaleInfoGracePeriod)
            return $"index already running on this DB (stale lock metadata from pid {holder.Pid} since {holder.Since:o})";

        if (holder != null)
            return $"index already running on this DB (held by pid {holder.Pid} since {holder.Since:o})";

        return "index already running on this DB (holder metadata unavailable)";
    }

    private static HolderInfo? TryReadHolderInfo(string infoPath)
    {
        try
        {
            if (!ExclusiveFileLock.TryReadHolderInfoText(infoPath, MaxInfoBytes, out var text))
                return null;

            using var document = BoundedJson.ParseDocument(text!, MaxInfoBytes, MaxInfoJsonDepth);
            var root = document.RootElement;
            if (!root.TryGetProperty("pid", out var pidElement) || !pidElement.TryGetInt32(out var pid))
                return null;
            if (!root.TryGetProperty("since", out var sinceElement)
                || !DateTimeOffset.TryParse(
                    sinceElement.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var since))
            {
                return null;
            }

            return new HolderInfo(pid, since.ToUniversalTime(), IsProcessStillRunning(pid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsProcessStillRunning(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ExclusiveFileLock.TryDeleteCleanupTarget(_infoPath, "mcp_index_lock", "metadata", DeleteFileForTesting, CleanupDiagnosticSinkForTesting);
        _stream.Dispose();
    }

    private sealed record HolderInfo(int Pid, DateTimeOffset Since, bool ProcessStillRunning);
}
