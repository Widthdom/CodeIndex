using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static class HookCommandRunner
{
    private const string HookName = "pre-commit";
    private const string ChainedHookName = "pre-commit.cdidx-chain";
    private const string BeginMarker = "# BEGIN CDIDX MANAGED PRE-COMMIT";
    private const string EndMarker = "# END CDIDX MANAGED PRE-COMMIT";
    internal const int MaxHookMarkerBytes = 64 * 1024;
    internal static Action<string>? DeleteFileForTesting { get; set; }
    internal static Action<string, string, string?>? ReplaceFileForTesting { get; set; }

    public static int Run(string[] args, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(args);
        if (options.ShowHelp || (options.Command == null && options.ParseError == null))
        {
            PrintUsage();
            return options.ShowHelp ? CommandExitCodes.Success : CommandExitCodes.UsageError;
        }

        if (options.ParseError != null)
        {
            var errorProjectPath = Path.GetFullPath(options.ProjectPath ?? Environment.CurrentDirectory);
            if (!options.Json)
                PrintUsage();
            return WriteResult(options.Json, jsonOptions, "error", options.ParseError, errorProjectPath, null, null, CommandExitCodes.UsageError);
        }

        var projectPath = Path.GetFullPath(options.ProjectPath ?? Environment.CurrentDirectory);
        var gitDir = GitHelper.ResolveGitCommonDir(projectPath);
        if (gitDir == null)
            return WriteResult(options.Json, jsonOptions, "error", "not a git repository", projectPath, null, null, CommandExitCodes.NotFound);

        var hooksDir = Path.Combine(gitDir, "hooks");
        var hookPath = Path.Combine(hooksDir, HookName);
        var chainedHookPath = Path.Combine(hooksDir, ChainedHookName);

        return options.Command switch
        {
            "install" => Install(options, jsonOptions, projectPath, hooksDir, hookPath, chainedHookPath),
            "uninstall" => Uninstall(options, jsonOptions, projectPath, hookPath, chainedHookPath),
            "status" => Status(options, jsonOptions, projectPath, hookPath, chainedHookPath),
            _ => UnknownCommand(options, jsonOptions, projectPath)
        };
    }

    internal static HookCommandOptions ParseArgs(string[] args)
    {
        string? command = null;
        string? projectPath = null;
        var json = false;
        var force = false;
        var showHelp = false;
        string? parseError = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    showHelp = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--project" when i + 1 < args.Length:
                    projectPath = args[++i];
                    break;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        var displayValue = ConsoleUi.FormatBoundedValue(args[i]);
                        parseError ??= $"unknown option '{displayValue}'";
                    }
                    else if (command == null)
                        command = args[i];
                    else
                        projectPath = args[i];
                    break;
            }
        }

        return new HookCommandOptions(command, projectPath, json, force, showHelp, parseError);
    }

    private static int Install(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath, string hooksDir, string hookPath, string chainedHookPath)
    {
        var warnings = new List<HookCommandWarningJsonResult>();
        Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(hooksDir));

        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        if (File.Exists(ioHookPath))
        {
            if (!IsManagedHookFile(ioHookPath))
            {
                if (File.Exists(ioChainedHookPath) && !options.Force)
                    return WriteResult(options.Json, jsonOptions, "error", $"chained hook already exists: {chainedHookPath}", projectPath, hookPath, chainedHookPath, CommandExitCodes.UsageError);

                try
                {
                    ReplaceCustomHookWithManagedHook(hooksDir, hookPath, chainedHookPath, projectPath, warnings);
                }
                catch (Exception ex) when (IsHookFileOperationException(ex))
                {
                    RecordHookWarning(warnings, "chained_hook_backup", chainedHookPath, "failed to back up existing hook", ex);
                    var message = $"failed to install cdidx pre-commit hook ({CommandErrorWriter.FormatSanitizedException(ex)})";
                    return WriteResult(options.Json, jsonOptions, "error", message, projectPath, hookPath, chainedHookPath, CommandExitCodes.InstallError, warnings);
                }

                return WriteResult(options.Json, jsonOptions, "installed", "cdidx pre-commit hook installed", projectPath, hookPath, chainedHookPath, CommandExitCodes.Success, warnings);
            }
        }

        AtomicFileWriter.WriteText(hookPath, BuildHookScript(chainedHookPath, projectPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), MakeExecutable);

        return WriteResult(options.Json, jsonOptions, "installed", "cdidx pre-commit hook installed", projectPath, hookPath, File.Exists(ioChainedHookPath) ? chainedHookPath : null, CommandExitCodes.Success, warnings);
    }

    private static int Uninstall(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath, string hookPath, string chainedHookPath)
    {
        var warnings = new List<HookCommandWarningJsonResult>();
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        if (!File.Exists(ioHookPath))
            return WriteResult(options.Json, jsonOptions, "absent", "cdidx pre-commit hook is not installed", projectPath, hookPath, File.Exists(ioChainedHookPath) ? chainedHookPath : null, CommandExitCodes.Success);

        if (!IsManagedHookFile(ioHookPath) && !options.Force)
            return WriteResult(options.Json, jsonOptions, "error", "pre-commit hook is not managed by cdidx; pass --force to remove it", projectPath, hookPath, null, CommandExitCodes.UsageError);

        if (File.Exists(ioChainedHookPath))
        {
            try
            {
                ReplaceFile(ioChainedHookPath, ioHookPath, destinationBackupFileName: null);
                MakeExecutable(ioHookPath);
            }
            catch (Exception ex) when (IsHookFileOperationException(ex))
            {
                RecordHookWarning(warnings, "chained_hook_backup", chainedHookPath, "failed to restore chained hook backup", ex);
                return WriteResult(options.Json, jsonOptions, "error", "failed to restore chained pre-commit hook", projectPath, hookPath, chainedHookPath, CommandExitCodes.InstallError, warnings);
            }
        }
        else
        {
            if (!TryDeleteFile(ioHookPath, hookPath, "managed_hook", warnings))
                return WriteResult(options.Json, jsonOptions, "error", "failed to delete managed pre-commit hook", projectPath, hookPath, null, CommandExitCodes.InstallError, warnings);
        }

        return WriteResult(options.Json, jsonOptions, "uninstalled", "cdidx pre-commit hook uninstalled", projectPath, hookPath, null, CommandExitCodes.Success, warnings);
    }

    private static int Status(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath, string hookPath, string chainedHookPath)
    {
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        var hookExists = File.Exists(ioHookPath);
        var installed = hookExists && IsManagedHookFile(ioHookPath);
        var status = installed ? "installed" : hookExists ? "custom" : "absent";
        return WriteResult(options.Json, jsonOptions, status, $"cdidx pre-commit hook is {status}", projectPath, hookPath, File.Exists(ioChainedHookPath) ? chainedHookPath : null, CommandExitCodes.Success);
    }

    private static int UnknownCommand(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath)
    {
        if (!options.Json)
            PrintUsage();
        return WriteResult(options.Json, jsonOptions, "error", $"unknown hooks command: {ConsoleUi.FormatBoundedValue(options.Command)}", projectPath, null, null, CommandExitCodes.UsageError);
    }

    private static bool IsManagedHook(string content)
        => content.Contains(BeginMarker, StringComparison.Ordinal) && content.Contains(EndMarker, StringComparison.Ordinal);

    private static bool IsManagedHookFile(string ioHookPath)
    {
        var content = DataDirectorySecurity.ReadTextWithinLimit(ioHookPath, MaxHookMarkerBytes, FileShare.ReadWrite);
        return content is not null && IsManagedHook(content);
    }

    private static void ReplaceCustomHookWithManagedHook(
        string hooksDir,
        string hookPath,
        string chainedHookPath,
        string projectPath,
        List<HookCommandWarningJsonResult> warnings)
    {
        var stagedHookPath = Path.Combine(hooksDir, $".{HookName}.{Guid.NewGuid():N}.tmp");
        var ioStagedHookPath = LongPath.EnsureWindowsPrefix(stagedHookPath);
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        var stagedHookMoved = false;

        try
        {
            WriteStagedHookScript(ioStagedHookPath, chainedHookPath, projectPath);
            ReplaceFile(ioStagedHookPath, ioHookPath, ioChainedHookPath);
            stagedHookMoved = true;
            MakeExecutable(ioHookPath);
        }
        finally
        {
            if (!stagedHookMoved)
                TryDeleteFile(ioStagedHookPath, stagedHookPath, "staged_hook_temp", warnings);
        }
    }

    private static void WriteStagedHookScript(string ioStagedHookPath, string chainedHookPath, string projectPath)
    {
        using (var stream = CreateStagedHookFileStream(ioStagedHookPath))
        {
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true))
            {
                writer.Write(BuildHookScript(chainedHookPath, projectPath));
                writer.Flush();
            }

            stream.Flush(flushToDisk: true);
        }

        MakeExecutable(ioStagedHookPath);
    }

    internal static FileStream CreateStagedHookFileStream(string ioStagedHookPath)
        => DataDirectorySecurity.OpenPrivateFileStream(ioStagedHookPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    private static void ReplaceFile(string sourceFileName, string destinationFileName, string? destinationBackupFileName)
    {
        if (ReplaceFileForTesting != null)
            ReplaceFileForTesting(sourceFileName, destinationFileName, destinationBackupFileName);
        else
            File.Replace(sourceFileName, destinationFileName, destinationBackupFileName, ignoreMetadataErrors: true);
    }

    private static bool TryDeleteFile(
        string ioPath,
        string displayPath,
        string category,
        List<HookCommandWarningJsonResult> warnings)
    {
        try
        {
            if (File.Exists(ioPath))
            {
                if (DeleteFileForTesting != null)
                    DeleteFileForTesting(ioPath);
                else
                    File.Delete(ioPath);
            }

            return true;
        }
        catch (Exception ex) when (IsHookFileOperationException(ex))
        {
            RecordHookWarning(warnings, category, displayPath, $"failed to delete {category}", ex);
            return false;
        }
    }

    private static bool IsHookFileOperationException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static void RecordHookWarning(
        List<HookCommandWarningJsonResult> warnings,
        string category,
        string path,
        string action,
        Exception ex)
    {
        var message = $"{action} {ConsoleUi.FormatBoundedValue(path)} ({CommandErrorWriter.FormatSanitizedException(ex)}).";
        warnings.Add(new HookCommandWarningJsonResult(category, path, message));
    }

    private static string BuildHookScript(string chainedHookPath, string projectPath)
    {
        var quotedChainedHook = QuoteShell(chainedHookPath);
        var quotedProjectPath = QuoteShell(projectPath);
        return $"""
#!/bin/sh
{BeginMarker}
cdidx index {quotedProjectPath} --quiet
cdidx_status=$?
if [ "$cdidx_status" -ne 0 ]; then
  echo "cdidx pre-commit index failed; commit aborted. Use git commit --no-verify to bypass hooks." >&2
  exit "$cdidx_status"
fi
if [ -x {quotedChainedHook} ]; then
  {quotedChainedHook} "$@"
fi
{EndMarker}
""";
    }

    private static string QuoteShell(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static int WriteResult(
        bool json,
        JsonSerializerOptions jsonOptions,
        string status,
        string message,
        string projectPath,
        string? hookPath,
        string? chainedHookPath,
        int exitCode,
        IReadOnlyList<HookCommandWarningJsonResult>? warnings = null)
    {
        var hasWarnings = warnings is { Count: > 0 };
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new HookCommandJsonResult(status, message, projectPath, hookPath, chainedHookPath, hasWarnings ? warnings : null),
                CliJsonSerializerContextFactory.Create(jsonOptions).HookCommandJsonResult));
        }
        else
        {
            if (hasWarnings)
            {
                foreach (var warning in warnings!)
                    CommandErrorWriter.WriteWarning(warning.Message);
            }

            if (exitCode == CommandExitCodes.Success)
            {
                CommandErrorWriter.WriteStdout(message);
                if (hookPath != null)
                    CommandErrorWriter.WriteStdout($"Hook: {hookPath}");
                if (chainedHookPath != null)
                    CommandErrorWriter.WriteStdout($"Chained hook: {chainedHookPath}");
            }
            else
            {
                CommandErrorWriter.WriteStderr($"Error: {message}");
            }
        }

        return exitCode;
    }

    private static void PrintUsage()
        => CommandErrorWriter.WriteStderr("Usage: cdidx hooks <install|uninstall|status> [--project <path>] [--force] [--json]");
}

public sealed record HookCommandOptions(string? Command, string? ProjectPath, bool Json, bool Force, bool ShowHelp, string? ParseError);

public sealed record HookCommandWarningJsonResult(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("message")] string Message);

public sealed record HookCommandJsonResult(
    string Status,
    string Message,
    string ProjectPath,
    string? HookPath,
    string? ChainedHookPath,
    IReadOnlyList<HookCommandWarningJsonResult>? Warnings = null);
