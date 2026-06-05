using System.Text;
using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static class HookCommandRunner
{
    private const string HookName = "pre-commit";
    private const string ChainedHookName = "pre-commit.cdidx-chain";
    private const string BeginMarker = "# BEGIN CDIDX MANAGED PRE-COMMIT";
    private const string EndMarker = "# END CDIDX MANAGED PRE-COMMIT";
    internal const int MaxHookMarkerBytes = 64 * 1024;

    public static int Run(string[] args, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(args);
        if (options.ShowHelp || options.Command == null)
        {
            PrintUsage();
            return options.ShowHelp ? CommandExitCodes.Success : CommandExitCodes.UsageError;
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
                        Console.Error.WriteLine($"Warning: unknown option '{displayValue}' (ignored) / 不明なオプション '{displayValue}'（無視されます）");
                    }
                    else if (command == null)
                        command = args[i];
                    else
                        projectPath = args[i];
                    break;
            }
        }

        return new HookCommandOptions(command, projectPath, json, force, showHelp);
    }

    private static int Install(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath, string hooksDir, string hookPath, string chainedHookPath)
    {
        Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(hooksDir));

        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        if (File.Exists(ioHookPath))
        {
            if (!IsManagedHookFile(ioHookPath))
            {
                if (File.Exists(ioChainedHookPath) && !options.Force)
                    return WriteResult(options.Json, jsonOptions, "error", $"chained hook already exists: {chainedHookPath}", projectPath, hookPath, chainedHookPath, CommandExitCodes.UsageError);

                ReplaceCustomHookWithManagedHook(hooksDir, hookPath, chainedHookPath);
                return WriteResult(options.Json, jsonOptions, "installed", "cdidx pre-commit hook installed", projectPath, hookPath, chainedHookPath, CommandExitCodes.Success);
            }
        }

        AtomicFileWriter.WriteText(hookPath, BuildHookScript(chainedHookPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), MakeExecutable);

        return WriteResult(options.Json, jsonOptions, "installed", "cdidx pre-commit hook installed", projectPath, hookPath, File.Exists(ioChainedHookPath) ? chainedHookPath : null, CommandExitCodes.Success);
    }

    private static int Uninstall(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath, string hookPath, string chainedHookPath)
    {
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        if (!File.Exists(ioHookPath))
            return WriteResult(options.Json, jsonOptions, "absent", "cdidx pre-commit hook is not installed", projectPath, hookPath, File.Exists(ioChainedHookPath) ? chainedHookPath : null, CommandExitCodes.Success);

        if (!IsManagedHookFile(ioHookPath) && !options.Force)
            return WriteResult(options.Json, jsonOptions, "error", "pre-commit hook is not managed by cdidx; pass --force to remove it", projectPath, hookPath, null, CommandExitCodes.UsageError);

        if (File.Exists(ioChainedHookPath))
        {
            File.Replace(ioChainedHookPath, ioHookPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            MakeExecutable(ioHookPath);
        }
        else
        {
            File.Delete(ioHookPath);
        }

        return WriteResult(options.Json, jsonOptions, "uninstalled", "cdidx pre-commit hook uninstalled", projectPath, hookPath, null, CommandExitCodes.Success);
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

    private static void ReplaceCustomHookWithManagedHook(string hooksDir, string hookPath, string chainedHookPath)
    {
        var stagedHookPath = Path.Combine(hooksDir, $".{HookName}.{Guid.NewGuid():N}.tmp");
        var ioStagedHookPath = LongPath.EnsureWindowsPrefix(stagedHookPath);
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        var stagedHookMoved = false;

        try
        {
            WriteStagedHookScript(ioStagedHookPath, chainedHookPath);
            File.Replace(ioStagedHookPath, ioHookPath, ioChainedHookPath, ignoreMetadataErrors: true);
            stagedHookMoved = true;
            MakeExecutable(ioHookPath);
        }
        finally
        {
            if (!stagedHookMoved)
                TryDeleteFile(ioStagedHookPath);
        }
    }

    private static void WriteStagedHookScript(string ioStagedHookPath, string chainedHookPath)
    {
        using (var stream = new FileStream(ioStagedHookPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true))
            {
                writer.Write(BuildHookScript(chainedHookPath));
                writer.Flush();
            }

            stream.Flush(flushToDisk: true);
        }

        MakeExecutable(ioStagedHookPath);
    }

    private static void TryDeleteFile(string ioPath)
    {
        try
        {
            if (File.Exists(ioPath))
                File.Delete(ioPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string BuildHookScript(string chainedHookPath)
    {
        var quotedChainedHook = QuoteShell(chainedHookPath);
        return $"""
#!/bin/sh
{BeginMarker}
cdidx index . --quiet
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

    private static int WriteResult(bool json, JsonSerializerOptions jsonOptions, string status, string message, string projectPath, string? hookPath, string? chainedHookPath, int exitCode)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new HookCommandJsonResult(status, message, projectPath, hookPath, chainedHookPath),
                CliJsonSerializerContextFactory.Create(jsonOptions).HookCommandJsonResult));
        }
        else if (exitCode == CommandExitCodes.Success)
        {
            Console.WriteLine(message);
            if (hookPath != null)
                Console.WriteLine($"Hook: {hookPath}");
            if (chainedHookPath != null)
                Console.WriteLine($"Chained hook: {chainedHookPath}");
        }
        else
        {
            Console.Error.WriteLine($"Error: {message}");
        }

        return exitCode;
    }

    private static void PrintUsage()
        => Console.Error.WriteLine("Usage: cdidx hooks <install|uninstall|status> [--project <path>] [--force] [--json]");
}

public sealed record HookCommandOptions(string? Command, string? ProjectPath, bool Json, bool Force, bool ShowHelp);

public sealed record HookCommandJsonResult(
    string Status,
    string Message,
    string ProjectPath,
    string? HookPath,
    string? ChainedHookPath);
