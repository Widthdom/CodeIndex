using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Lsp;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    internal static int RunInstallerProcess(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        bool suppressOutput = false)
        => RunInstallerProcessDetailed(startInfo, timeout, cancellationToken, suppressOutput).ExitCode;

    internal static InstallerProcessResult RunInstallerProcessDetailed(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        bool suppressOutput = false)
    {
        if (suppressOutput)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (IsInstallerProcessStartException(ex))
        {
            if (!suppressOutput)
            {
                CommandErrorWriter.WriteStderr($"Error: failed to start install.sh for upgrade ({CommandErrorWriter.FormatSanitizedException(ex)}).");
                CommandErrorWriter.WriteStderr("Hint: rerun `install.sh` manually for the desired release.");
            }
            return InstallerProcessResult.Failure(CommandExitCodes.InstallError);
        }

        if (process == null)
        {
            if (!suppressOutput)
            {
                CommandErrorWriter.WriteStderr("Error: failed to start install.sh for upgrade.");
                CommandErrorWriter.WriteStderr("Hint: rerun `install.sh` manually for the desired release.");
            }
            return InstallerProcessResult.Failure(CommandExitCodes.InstallError);
        }

        using (process)
        {
            var outputDrainTask = suppressOutput
                ? DrainSuppressedInstallerOutputAsync(process)
                : Task.FromResult(SuppressedInstallerOutputResult.Empty);

            try
            {
                var waitTask = process.WaitForExitAsync(cancellationToken);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timeoutTask = Task.Delay(ToWaitMilliseconds(timeout), timeoutCts.Token);
                var completedTask = Task.WhenAny(waitTask, timeoutTask).GetAwaiter().GetResult();
                if (completedTask == waitTask)
                {
                    timeoutCts.Cancel();
                    waitTask.GetAwaiter().GetResult();
                    var output = outputDrainTask.GetAwaiter().GetResult();
                    return new InstallerProcessResult(
                        process.ExitCode,
                        output.StdoutTail,
                        output.StderrTail,
                        output.Truncated);
                }

                if (cancellationToken.IsCancellationRequested)
                    waitTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                if (!process.WaitForExit(ToWaitMilliseconds(InstallerKillWaitTimeout)))
                {
                    if (!suppressOutput)
                        CommandErrorWriter.WriteStderr("Error: install.sh was cancelled and did not exit after cancellation.");
                }
                else
                {
                    outputDrainTask.GetAwaiter().GetResult();
                }
                throw;
            }

            if (process.HasExited)
            {
                var output = outputDrainTask.GetAwaiter().GetResult();
                return new InstallerProcessResult(
                    process.ExitCode,
                    output.StdoutTail,
                    output.StderrTail,
                    output.Truncated);
            }

            TryKillProcessTree(process);
            if (!process.WaitForExit(ToWaitMilliseconds(InstallerKillWaitTimeout)))
            {
                if (!suppressOutput)
                    CommandErrorWriter.WriteStderr("Error: install.sh timed out and did not exit after cancellation.");
            }
            else
            {
                outputDrainTask.GetAwaiter().GetResult();
                if (!suppressOutput)
                    CommandErrorWriter.WriteStderr($"Error: install.sh timed out after {FormatDuration(timeout)}.");
            }
            if (!suppressOutput)
                CommandErrorWriter.WriteStderr("Hint: rerun `install.sh` manually for the desired release.");
            var timeoutOutput = outputDrainTask.IsCompletedSuccessfully
                ? outputDrainTask.GetAwaiter().GetResult()
                : SuppressedInstallerOutputResult.Empty;
            return new InstallerProcessResult(
                CommandExitCodes.InstallError,
                timeoutOutput.StdoutTail,
                timeoutOutput.StderrTail,
                timeoutOutput.Truncated);
        }
    }

    private static bool IsInstallerProcessStartException(Exception ex)
        => ex is Win32Exception
            or InvalidOperationException
            or FileNotFoundException
            or DirectoryNotFoundException
            or UnauthorizedAccessException;

    private static async Task<SuppressedInstallerOutputResult> DrainSuppressedInstallerOutputAsync(Process process)
    {
        var outputs = await Task.WhenAll(
            DrainSuppressedInstallerOutputAsync(process.StandardOutput),
            DrainSuppressedInstallerOutputAsync(process.StandardError)).ConfigureAwait(false);
        return new SuppressedInstallerOutputResult(
            outputs[0].Tail,
            outputs[1].Tail,
            outputs[0].Truncated || outputs[1].Truncated);
    }

    private static async Task<SuppressedInstallerOutput> DrainSuppressedInstallerOutputAsync(TextReader reader)
    {
        var buffer = new char[InstallerSuppressedOutputDrainBufferChars];
        var tail = new SuppressedOutputTail(InstallerSuppressedOutputTailChars);
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
                break;

            tail.Append(buffer.AsSpan(0, read));
        }

        return new SuppressedInstallerOutput(tail.Value, tail.Truncated);
    }

    internal sealed record InstallerProcessResult(
        int ExitCode,
        string? StdoutTail,
        string? StderrTail,
        bool OutputTruncated)
    {
        internal static InstallerProcessResult Failure(int exitCode) => new(exitCode, null, null, false);
    }

    private sealed record SuppressedInstallerOutputResult(
        string? StdoutTail,
        string? StderrTail,
        bool Truncated)
    {
        internal static SuppressedInstallerOutputResult Empty { get; } = new(null, null, false);
    }

    private sealed record SuppressedInstallerOutput(string? Tail, bool Truncated);

    private sealed class SuppressedOutputTail(int maxChars)
    {
        private readonly StringBuilder _builder = new(maxChars);
        private long _totalChars;

        internal bool Truncated { get; private set; }

        internal string? Value => _builder.Length == 0 ? null : _builder.ToString();

        internal void Append(ReadOnlySpan<char> value)
        {
            _totalChars += value.Length;
            if (_totalChars > maxChars)
                Truncated = true;

            if (value.Length >= maxChars)
            {
                _builder.Clear();
                _builder.Append(value[^maxChars..]);
                return;
            }

            _builder.Append(value);
            if (_builder.Length > maxChars)
                _builder.Remove(0, _builder.Length - maxChars);
        }
    }

    private static void TryDeleteUpgradeInstallerScript(string scriptPath)
    {
        try
        {
            if (!File.Exists(scriptPath))
                return;

            if (DeleteUpgradeInstallerScriptForTesting != null)
                DeleteUpgradeInstallerScriptForTesting(scriptPath);
            else
                File.Delete(scriptPath);
        }
        catch (Exception ex) when (IsExpectedCleanupException(ex))
        {
            CommandErrorWriter.WriteStderr($"Warning: failed to delete upgrade installer script {ConsoleUi.FormatBoundedValue(scriptPath)} ({FormatSanitizedExceptionSummary(ex)}).");
        }
    }

    private static void TryDeleteUpgradeInstallerDirectory(string scriptDirectory)
    {
        try
        {
            if (!TryValidateUpgradeInstallerDirectoryCleanupTarget(scriptDirectory, out var fullPath, out var validationFailure))
            {
                CommandErrorWriter.WriteStderr($"Warning: skipped deleting upgrade installer temporary directory {ConsoleUi.FormatBoundedValue(scriptDirectory)} ({validationFailure}).");
                return;
            }

            if (!Directory.Exists(LongPath.EnsureWindowsPrefix(fullPath)))
                return;

            if (!TryValidateUpgradeInstallerDirectoryCleanupTarget(fullPath, out fullPath, out validationFailure))
            {
                CommandErrorWriter.WriteStderr($"Warning: skipped deleting upgrade installer temporary directory {ConsoleUi.FormatBoundedValue(scriptDirectory)} ({validationFailure}).");
                return;
            }

            if (DeleteUpgradeInstallerDirectoryForTesting != null)
                DeleteUpgradeInstallerDirectoryForTesting(fullPath);
            else
                Directory.Delete(LongPath.EnsureWindowsPrefix(fullPath), recursive: true);
        }
        catch (Exception ex) when (IsExpectedCleanupException(ex))
        {
            CommandErrorWriter.WriteStderr($"Warning: failed to delete upgrade installer temporary directory {ConsoleUi.FormatBoundedValue(scriptDirectory)} ({FormatSanitizedExceptionSummary(ex)}).");
        }
    }

    private static bool IsExpectedCleanupException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException;

    internal static bool TryValidateUpgradeInstallerDirectoryCleanupTarget(
        string path,
        out string fullPath,
        out string failureReason)
    {
        var options = new DirectoryCleanupBoundaryOptions(
            UpgradeInstallerDirectoryPrefix,
            "target is outside the expected cleanup root",
            "target name does not match the expected upgrade temporary-directory prefix",
            "target is a symbolic link, reparse point, or device");
        return FileSystemBoundary.TryValidateDirectoryCleanupTarget(
            path,
            Path.GetTempPath(),
            options,
            out fullPath,
            out failureReason);
    }
}
