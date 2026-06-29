using System.Diagnostics;
using System.Globalization;
using System.Text;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

internal static class GitProcessRunner
{
    internal const int MaxCapturedGitOutputChars = 1024 * 1024;
    internal const int MaxGitFailureDiagnosticChars = 512;
    private const int MaxGitDiagnosticRedactionInputChars = 4096;
    private const int GitCaptureReadBufferChars = 8192;
    private const int GitProcessFailureExitCode = -1;
    private static readonly TimeSpan GitKillWaitTimeout = TimeSpan.FromSeconds(5);

    internal readonly record struct CaptureResult(
        int? ExitCode,
        string Output,
        string Error,
        GitCommandFailureKind FailureKind,
        string? Diagnostic);

    // Drain stdout and stderr concurrently at stream level so a newline-free chunk is capped
    // before framework line buffering can accumulate unbounded data. Returns null if the
    // process fails to start; otherwise the caller decides how to interpret the exit code.
    // stdout/stderr を stream level で同時に汲み出し、改行なしの巨大 chunk でも framework の
    // 行バッファに溜まる前に上限を強制する。
    internal static (int ExitCode, string Output, string Error)? RunCapturingOutput(
        ProcessStartInfo psi,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = RunCapturingResult(psi, timeout, cancellationToken);
        if (result == null || result.Value.FailureKind == GitCommandFailureKind.StartFailed)
            return null;

        return (result.Value.ExitCode ?? GitProcessFailureExitCode, result.Value.Output, result.Value.Error);
    }

    internal static CaptureResult? RunCapturingResult(
        ProcessStartInfo psi,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => RunCapturingResultAsync(psi, timeout, cancellationToken).GetAwaiter().GetResult();

    internal static string FormatDiagnostic(string diagnostic)
    {
        var boundedBeforeRedaction = DiagnosticRedactor.BoundDiagnosticText(
            diagnostic,
            MaxGitDiagnosticRedactionInputChars);
        return DiagnosticRedactor.BoundDiagnosticText(
            DiagnosticRedactor.RedactSensitiveText(boundedBeforeRedaction, "[redacted]", redactPaths: true),
            MaxGitFailureDiagnosticChars);
    }

    private static async Task<CaptureResult?> RunCapturingResultAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        GitCommandFailureKind failureKind = GitCommandFailureKind.None;
        string? failureDiagnostic = null;
        var failureLock = new object();

        void MarkFailure(GitCommandFailureKind kind, string diagnostic)
        {
            lock (failureLock)
            {
                if (failureKind != GitCommandFailureKind.None)
                    return;
                failureKind = kind;
                failureDiagnostic = FormatDiagnostic(diagnostic);
            }
            TryKillProcessTree(process);
        }

        try
        {
            if (!process.Start())
            {
                var diagnostic = FormatDiagnostic("git process did not start.");
                return new CaptureResult(
                    null,
                    string.Empty,
                    diagnostic,
                    GitCommandFailureKind.StartFailed,
                    diagnostic);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            var diagnostic = FormatDiagnostic($"git process start failed: {DiagnosticRedactor.ClassifyException(ex)}: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            return new CaptureResult(
                null,
                string.Empty,
                diagnostic,
                GitCommandFailureKind.StartFailed,
                diagnostic);
        }

        var stdoutTask = ReadCapturedStreamAsync(process.StandardOutput, stdout, "stdout", MarkFailure);
        var stderrTask = ReadCapturedStreamAsync(process.StandardError, stderr, "stderr", MarkFailure);
        var waitResult = await WaitForGitExitAsync(process, timeout, cancellationToken).ConfigureAwait(false);
        var cancelled = waitResult.Cancelled;
        var exited = waitResult.Exited;
        if (!exited)
        {
            MarkFailure(
                cancelled ? GitCommandFailureKind.Cancelled : GitCommandFailureKind.TimedOut,
                cancelled
                ? "git command cancelled."
                : $"git command timed out after {FormatDuration(timeout)}.");
            if (!await WaitForGitExitAfterKillAsync(process, GitKillWaitTimeout).ConfigureAwait(false))
            {
                if (cancelled)
                    cancellationToken.ThrowIfCancellationRequested();
                return new CaptureResult(
                    GitProcessFailureExitCode,
                    ReadCaptured(stdout),
                    CombineCapturedError(ReadCaptured(stderr), failureDiagnostic!),
                    failureKind,
                    failureDiagnostic);
            }
        }

        if (!await WaitForCaptureReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false))
            MarkFailure(GitCommandFailureKind.OutputCaptureIncomplete, "git command output capture did not finish.");

        var output = ReadCaptured(stdout);
        var error = ReadCaptured(stderr);
        if (cancelled)
            cancellationToken.ThrowIfCancellationRequested();
        if (failureKind != GitCommandFailureKind.None)
            return new CaptureResult(
                GitProcessFailureExitCode,
                output,
                CombineCapturedError(error, failureDiagnostic!),
                failureKind,
                failureDiagnostic);

        var exitCode = process.ExitCode;
        if (exitCode != 0)
        {
            var diagnostic = FormatDiagnostic(string.IsNullOrWhiteSpace(error)
                ? $"git command exited with {exitCode.ToString(CultureInfo.InvariantCulture)} and no stderr."
                : error);
            return new CaptureResult(
                exitCode,
                output,
                diagnostic,
                GitCommandFailureKind.ExitCode,
                diagnostic);
        }

        return new CaptureResult(exitCode, output, error, GitCommandFailureKind.None, null);
    }

    private readonly record struct GitExitWaitResult(bool Exited, bool Cancelled);

    private static async Task<GitExitWaitResult> WaitForGitExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var exitTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(NormalizePositiveTimeout(timeout), CancellationToken.None);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(exitTask, timeoutTask, cancellationTask).ConfigureAwait(false);
        if (completed == exitTask)
        {
            try
            {
                await exitTask.ConfigureAwait(false);
                return new GitExitWaitResult(true, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new GitExitWaitResult(false, true);
            }
        }

        return new GitExitWaitResult(false, completed == cancellationTask || cancellationToken.IsCancellationRequested);
    }

    private static string ReadCaptured(StringBuilder builder)
    {
        lock (builder)
            return builder.ToString();
    }

    private static async Task<bool> WaitForGitExitAfterKillAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(NormalizePositiveTimeout(timeout))
                .ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForCaptureReadersAsync(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(NormalizePositiveTimeout(GitKillWaitTimeout))
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
        {
            return false;
        }
    }

    private static async Task ReadCapturedStreamAsync(
        TextReader reader,
        StringBuilder builder,
        string streamName,
        Action<GitCommandFailureKind, string> markFailure)
    {
        var buffer = new char[GitCaptureReadBufferChars];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (read == 0)
                    return;
                if (!AppendBoundedCapturedChars(builder, buffer.AsSpan(0, read), streamName, markFailure))
                    return;
            }
        }
        catch (IOException ex)
        {
            markFailure(
                GitCommandFailureKind.CaptureFailed,
                $"git command {streamName} read failed: {DiagnosticRedactor.ClassifyException(ex)}");
        }
        catch (ObjectDisposedException)
        {
            // Process cleanup closed the stream after another failure path.
        }
    }

    private static bool AppendBoundedCapturedChars(
        StringBuilder builder,
        ReadOnlySpan<char> data,
        string streamName,
        Action<GitCommandFailureKind, string> markFailure)
    {
        lock (builder)
        {
            var remaining = MaxCapturedGitOutputChars - builder.Length;
            if (remaining <= 0)
            {
                markFailure(GitCommandFailureKind.CaptureLimitExceeded, BuildCaptureLimitMessage(streamName));
                return false;
            }

            if (data.Length <= remaining)
            {
                builder.Append(data);
                return true;
            }

            builder.Append(data[..Math.Min(data.Length, remaining)]);
        }

        markFailure(GitCommandFailureKind.CaptureLimitExceeded, BuildCaptureLimitMessage(streamName));
        return false;
    }

    private static string BuildCaptureLimitMessage(string streamName)
        => $"git command captured {streamName} exceeded {MaxCapturedGitOutputChars.ToString(CultureInfo.InvariantCulture)} characters.";

    private static string CombineCapturedError(string stderr, string diagnostic)
        => FormatDiagnostic(string.IsNullOrWhiteSpace(stderr)
            ? diagnostic
            : diagnostic + "\n" + stderr.TrimEnd('\r', '\n'));

    private static TimeSpan NormalizePositiveTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return TimeSpan.FromMilliseconds(1);
        return timeout;
    }

    private static string FormatDuration(TimeSpan timeout)
        => timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup only; callers receive the timeout/capture diagnostic.
        }
    }
}
