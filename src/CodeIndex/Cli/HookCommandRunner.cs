using System.Text.Json;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static class HookCommandRunner
{
    private const string HookName = "pre-commit";
    private const string ChainedHookName = "pre-commit.cdidx-chain";
    private const string BeginMarker = "# BEGIN CDIDX MANAGED PRE-COMMIT";
    private const string EndMarker = "# END CDIDX MANAGED PRE-COMMIT";

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
                        Console.Error.WriteLine($"Warning: unknown option '{args[i]}' (ignored) / 不明なオプション '{args[i]}'（無視されます）");
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
            var existing = File.ReadAllText(ioHookPath);
            if (!IsManagedHook(existing))
            {
                if (File.Exists(ioChainedHookPath) && !options.Force)
                    return WriteResult(options.Json, jsonOptions, "error", $"chained hook already exists: {chainedHookPath}", projectPath, hookPath, chainedHookPath, CommandExitCodes.UsageError);
                if (File.Exists(ioChainedHookPath))
                    File.Delete(ioChainedHookPath);
                File.Move(ioHookPath, ioChainedHookPath);
            }
        }

        File.WriteAllText(ioHookPath, BuildHookScript(chainedHookPath));
        MakeExecutable(ioHookPath);

        return WriteResult(options.Json, jsonOptions, "installed", "cdidx pre-commit hook installed", projectPath, hookPath, File.Exists(ioChainedHookPath) ? chainedHookPath : null, CommandExitCodes.Success);
    }

    private static int Uninstall(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath, string hookPath, string chainedHookPath)
    {
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        if (!File.Exists(ioHookPath))
            return WriteResult(options.Json, jsonOptions, "absent", "cdidx pre-commit hook is not installed", projectPath, hookPath, File.Exists(ioChainedHookPath) ? chainedHookPath : null, CommandExitCodes.Success);

        var existing = File.ReadAllText(ioHookPath);
        if (!IsManagedHook(existing) && !options.Force)
            return WriteResult(options.Json, jsonOptions, "error", "pre-commit hook is not managed by cdidx; pass --force to remove it", projectPath, hookPath, null, CommandExitCodes.UsageError);

        File.Delete(ioHookPath);
        if (File.Exists(ioChainedHookPath))
            File.Move(ioChainedHookPath, ioHookPath);

        return WriteResult(options.Json, jsonOptions, "uninstalled", "cdidx pre-commit hook uninstalled", projectPath, hookPath, null, CommandExitCodes.Success);
    }

    private static int Status(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath, string hookPath, string chainedHookPath)
    {
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        var hookExists = File.Exists(ioHookPath);
        var installed = hookExists && IsManagedHook(File.ReadAllText(ioHookPath));
        var status = installed ? "installed" : hookExists ? "custom" : "absent";
        return WriteResult(options.Json, jsonOptions, status, $"cdidx pre-commit hook is {status}", projectPath, hookPath, File.Exists(ioChainedHookPath) ? chainedHookPath : null, CommandExitCodes.Success);
    }

    private static int UnknownCommand(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath)
    {
        if (!options.Json)
            PrintUsage();
        return WriteResult(options.Json, jsonOptions, "error", $"unknown hooks command: {options.Command}", projectPath, null, null, CommandExitCodes.UsageError);
    }

    private static bool IsManagedHook(string content)
        => content.Contains(BeginMarker, StringComparison.Ordinal) && content.Contains(EndMarker, StringComparison.Ordinal);

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
