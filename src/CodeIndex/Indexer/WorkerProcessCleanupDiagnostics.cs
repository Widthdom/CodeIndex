using System.Diagnostics;

namespace CodeIndex.Indexer;

internal readonly record struct WorkerProcessExitWaitResult(bool Exited, string? Diagnostic);

internal static class WorkerProcessCleanupDiagnostics
{
    internal static WorkerProcessExitWaitResult WaitForExit(Process process, int milliseconds)
    {
        try
        {
            return new WorkerProcessExitWaitResult(process.WaitForExit(milliseconds), null);
        }
        catch (Exception ex)
        {
            return new WorkerProcessExitWaitResult(
                Exited: false,
                Diagnostic: SafeDiagnosticFormatter.FormatExceptionCategory("worker_wait_failed", ex));
        }
    }

    internal static string? TryKill(Process process, int waitMilliseconds)
    {
        string? killDiagnostic = null;
        string? waitDiagnostic = null;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            killDiagnostic = SafeDiagnosticFormatter.FormatExceptionCategory("worker_kill_failed", ex);
        }

        try
        {
            process.WaitForExit(waitMilliseconds);
        }
        catch (Exception ex)
        {
            waitDiagnostic = SafeDiagnosticFormatter.FormatExceptionCategory("worker_kill_wait_failed", ex);
        }

        return Combine(killDiagnostic, waitDiagnostic);
    }

    internal static string? Combine(params string?[] diagnostics)
    {
        var present = Array.FindAll(diagnostics, static diagnostic => !string.IsNullOrWhiteSpace(diagnostic));
        return present.Length == 0 ? null : string.Join("; ", present);
    }

    internal static string AppendToMessage(string message, string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
            return message;

        return $"{message} Worker diagnostic: {diagnostic}";
    }
}
