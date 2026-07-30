using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static class HookCommandRunner
{
    private const string HookName = "pre-commit";
    private const string ChainedHookName = "pre-commit.cdidx-chain";
    private const string BeginMarker = "# BEGIN CDIDX MANAGED PRE-COMMIT";
    private const string EndMarker = "# END CDIDX MANAGED PRE-COMMIT";
    private const string ExecutableManifestPrefix = "# CDIDX EXECUTABLE MANIFEST ";
    private const int MaxExecutableManifestChars = 32 * 1024;
    private const int MaxExecutableArgumentChars = 8 * 1024;
    private const int UnixExecuteAccess = 1;
    private const int UnixReadAccess = 4;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFileType = 0x8000;
    private static readonly byte[] BeginMarkerBytes = Encoding.ASCII.GetBytes(BeginMarker);
    private static readonly byte[] EndMarkerBytes = Encoding.ASCII.GetBytes(EndMarker);
    private static readonly byte[] HookPreambleBytes = Encoding.ASCII.GetBytes("#!/bin/sh");
    private static readonly byte[] Utf8BomBytes = [0xEF, 0xBB, 0xBF];
    internal const int MaxHookMarkerBytes = 64 * 1024;
    internal static Action<string>? DeleteFileForTesting { get; set; }
    internal static Action<string, string, string?>? ReplaceFileForTesting { get; set; }
    internal static Func<string, HookExecutableSelection>? ExecutableSelectionForTesting { get; set; }

    public static int Run(string[] args, JsonSerializerOptions jsonOptions, string? appVersion = null)
    {
        appVersion ??= ConsoleUi.LoadVersion();
        var options = ParseArgs(args);
        var wantsJson = args.Any(static arg => arg == "--json" || arg.StartsWith("--json=", StringComparison.Ordinal));
        if (wantsJson && !options.Json)
            options = options with { Json = true };
        if (options.ShowHelp)
        {
            PrintUsage();
            return CommandExitCodes.Success;
        }

        string projectPath;
        try
        {
            projectPath = Path.GetFullPath(options.ProjectPath ?? Environment.CurrentDirectory);
        }
        catch (Exception ex) when (IsHookFileOperationException(ex))
        {
            return WriteResult(
                options.Json,
                jsonOptions,
                "error",
                $"invalid hooks project path ({CommandErrorWriter.FormatSanitizedException(ex)})",
                DiagnosticSanitizer.ForPath(options.ProjectPath ?? Environment.CurrentDirectory),
                null,
                null,
                CommandExitCodes.InvalidArgument);
        }

        if (options.Command == null && options.ParseError == null)
        {
            return WriteResult(
                options.Json,
                jsonOptions,
                "error",
                "hooks requires an install, uninstall, or status command",
                projectPath,
                null,
                null,
                CommandExitCodes.UsageError);
        }

        if (options.ParseError != null)
        {
            if (!options.Json)
                PrintUsage();
            return WriteResult(options.Json, jsonOptions, "error", options.ParseError, projectPath, null, null, CommandExitCodes.UsageError);
        }

        try
        {
            var gitDir = GitHelper.ResolveGitCommonDir(projectPath);
            if (gitDir == null)
                return WriteResult(options.Json, jsonOptions, "error", "not a git repository", projectPath, null, null, CommandExitCodes.NotFound);

            if (!GitHelper.TryResolveGitMetadataChildPath(
                    gitDir,
                    "hooks",
                    expectDirectory: true,
                    allowMissing: true,
                    out var hooksDir))
            {
                return WriteResult(options.Json, jsonOptions, "error", "unsafe Git hooks metadata path", projectPath, null, null, CommandExitCodes.InstallError);
            }

            var hookPath = Path.Combine(hooksDir, HookName);
            var chainedHookPath = Path.Combine(hooksDir, ChainedHookName);
            if (Directory.Exists(LongPath.EnsureWindowsPrefix(hooksDir))
                && (!GitHelper.TryResolveGitMetadataChildPath(
                        hooksDir,
                        HookName,
                        expectDirectory: false,
                        allowMissing: true,
                        out hookPath)
                    || !GitHelper.TryResolveGitMetadataChildPath(
                        hooksDir,
                        ChainedHookName,
                        expectDirectory: false,
                        allowMissing: true,
                        out chainedHookPath)))
            {
                return WriteResult(options.Json, jsonOptions, "error", "unsafe Git hook file path", projectPath, null, null, CommandExitCodes.InstallError);
            }

            return options.Command switch
            {
                "install" => Install(options, jsonOptions, appVersion, projectPath, gitDir, hooksDir, hookPath, chainedHookPath),
                "uninstall" => Uninstall(options, jsonOptions, projectPath, gitDir, hooksDir, hookPath, chainedHookPath),
                "status" => Status(options, jsonOptions, appVersion, projectPath, hookPath, chainedHookPath),
                _ => UnknownCommand(options, jsonOptions, projectPath)
            };
        }
        catch (Exception ex) when (IsHookFileOperationException(ex))
        {
            return WriteResult(
                options.Json,
                jsonOptions,
                "error",
                $"hook operation failed ({CommandErrorWriter.FormatSanitizedException(ex)})",
                projectPath,
                null,
                null,
                CommandExitCodes.InstallError);
        }
    }

    internal static HookCommandOptions ParseArgs(string[] args)
    {
        string? command = null;
        string? projectPath = null;
        var json = false;
        var force = false;
        var dryRun = false;
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
                case "--dry-run":
                    dryRun = true;
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

        if (dryRun
            && !string.Equals(command, "install", StringComparison.Ordinal)
            && !string.Equals(command, "uninstall", StringComparison.Ordinal))
        {
            parseError ??= "--dry-run is supported only for hooks install or uninstall";
        }

        return new HookCommandOptions(command, projectPath, json, force, dryRun, showHelp, parseError);
    }

    private static int Install(
        HookCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        string projectPath,
        string gitDir,
        string hooksDir,
        string hookPath,
        string chainedHookPath)
    {
        if (!TryResolveCurrentExecutable(appVersion, out var executableSelection, out var resolutionFailure))
        {
            var message = $"could not pin the current cdidx executable ({resolutionFailure})";
            var unresolvedExecutable = HookExecutableJsonResult.Unresolved(resolutionFailure);
            if (options.DryRun)
            {
                var preflightPlan = BuildInstallPreflightFailurePlan(
                    hookPath,
                    chainedHookPath,
                    message);
                return WriteDryRunResult(
                    options,
                    jsonOptions,
                    projectPath,
                    hookPath,
                    chainedHookPath,
                    preflightPlan,
                    unresolvedExecutable);
            }

            return WriteResult(
                options.Json,
                jsonOptions,
                "error",
                message,
                projectPath,
                hookPath,
                null,
                CommandExitCodes.InstallError,
                executable: unresolvedExecutable);
        }

        var executable = InspectExecutable(executableSelection, executableSelection);
        var warnings = new List<HookCommandWarningJsonResult>();
        if (!TryBuildHookScript(
                chainedHookPath,
                projectPath,
                executableSelection,
                out var hookScript))
        {
            return WriteGeneratedHookTooLargeResult(
                options,
                jsonOptions,
                projectPath,
                hookPath,
                chainedHookPath,
                executable);
        }
        var plan = BuildInstallPlan(options, hookPath, chainedHookPath, hookScript);
        if (options.DryRun)
            return WriteDryRunResult(options, jsonOptions, projectPath, hookPath, chainedHookPath, plan, executable, hookScript);

        if (plan.Blocked)
            return WriteBlockedPlanResult(options, jsonOptions, projectPath, hookPath, chainedHookPath, plan, executable);

        Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(hooksDir));
        if (!TryResolveHookWritePaths(gitDir, out hooksDir, out hookPath, out chainedHookPath))
            return WriteResult(options.Json, jsonOptions, "error", "unsafe Git hook file path", projectPath, null, null, CommandExitCodes.InstallError);

        if (!TryBuildHookScript(
                chainedHookPath,
                projectPath,
                executableSelection,
                out hookScript))
        {
            return WriteGeneratedHookTooLargeResult(
                options,
                jsonOptions,
                projectPath,
                hookPath,
                chainedHookPath,
                executable);
        }
        plan = BuildInstallPlan(options, hookPath, chainedHookPath, hookScript);
        if (plan.Blocked)
            return WriteBlockedPlanResult(options, jsonOptions, projectPath, hookPath, chainedHookPath, plan, executable);

        if (plan.PlannedAction == "none")
        {
            return WriteResult(
                options.Json,
                jsonOptions,
                "already_installed",
                "cdidx pre-commit hook is already installed",
                projectPath,
                hookPath,
                plan.ChainedHookState == "present" ? chainedHookPath : null,
                CommandExitCodes.Success,
                executable: executable);
        }

        if (plan.PlannedAction == "chain_existing")
        {
            try
            {
                if (!TryResolveHookWritePaths(gitDir, out hooksDir, out hookPath, out chainedHookPath))
                    return WriteResult(options.Json, jsonOptions, "error", "Git hook file path became unsafe before write", projectPath, null, null, CommandExitCodes.InstallError, executable: executable);
                if (!TryBuildHookScript(
                        chainedHookPath,
                        projectPath,
                        executableSelection,
                        out hookScript))
                {
                    return WriteGeneratedHookTooLargeResult(
                        options,
                        jsonOptions,
                        projectPath,
                        hookPath,
                        chainedHookPath,
                        executable);
                }
                ReplaceCustomHookWithManagedHook(
                    hooksDir,
                    hookPath,
                    chainedHookPath,
                    hookScript,
                    warnings);
            }
            catch (Exception ex) when (IsHookFileOperationException(ex))
            {
                RecordHookWarning(warnings, "chained_hook_backup", chainedHookPath, "failed to back up existing hook", ex);
                var message = $"failed to install cdidx pre-commit hook ({CommandErrorWriter.FormatSanitizedException(ex)})";
                return WriteResult(options.Json, jsonOptions, "error", message, projectPath, hookPath, chainedHookPath, CommandExitCodes.InstallError, warnings, executable: executable);
            }

            return WriteResult(options.Json, jsonOptions, "updated", "cdidx pre-commit hook updated", projectPath, hookPath, chainedHookPath, CommandExitCodes.Success, warnings, executable: executable);
        }

        if (!TryResolveHookWritePaths(gitDir, out hooksDir, out hookPath, out chainedHookPath))
            return WriteResult(options.Json, jsonOptions, "error", "Git hook file path became unsafe before write", projectPath, null, null, CommandExitCodes.InstallError, executable: executable);

        if (!TryBuildHookScript(
                chainedHookPath,
                projectPath,
                executableSelection,
                out hookScript))
        {
            return WriteGeneratedHookTooLargeResult(
                options,
                jsonOptions,
                projectPath,
                hookPath,
                chainedHookPath,
                executable);
        }
        var status = plan.PlannedAction == "create" ? "installed" : "updated";
        var resultMessage = status == "updated"
            ? "cdidx pre-commit hook updated"
            : "cdidx pre-commit hook installed";
        AtomicFileWriter.WriteText(hookPath, hookScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), MakeExecutable);

        return WriteResult(
            options.Json,
            jsonOptions,
            status,
            resultMessage,
            projectPath,
            hookPath,
            File.Exists(LongPath.EnsureWindowsPrefix(chainedHookPath)) ? chainedHookPath : null,
            CommandExitCodes.Success,
            warnings,
            executable: executable);
    }

    private static HookOperationPlan BuildInstallPlan(
        HookCommandOptions options,
        string hookPath,
        string chainedHookPath,
        string hookScript)
    {
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        var hookExists = File.Exists(ioHookPath);
        var chainedHookExists = File.Exists(ioChainedHookPath);
        var chainedHookState = chainedHookExists ? "present" : "absent";
        var generatedBytes = Encoding.UTF8.GetBytes(hookScript);
        var generatedHash = ComputeBytesSha256(generatedBytes);
        if (!hookExists)
        {
            return new HookOperationPlan(
                "create",
                "cdidx pre-commit hook would be installed",
                "absent",
                chainedHookState,
                [
                    new HookCommandFileChangeJsonResult(
                        "create",
                        hookPath,
                        "generated_managed_hook",
                        null,
                        null,
                        generatedHash,
                        null,
                        true),
                ]);
        }

        var existingHook = ReadHookBytesWithinLimit(ioHookPath);
        var analysis = AnalyzeManagedHook(existingHook);
        var existingHash = existingHook is null
            ? null
            : ComputeBytesSha256(existingHook);
        var executable = IsExecutableHook(ioHookPath);
        if (analysis.State == "managed")
        {
            if (existingHook is not null
                && existingHook.AsSpan().SequenceEqual(generatedBytes)
                && executable)
            {
                return new HookOperationPlan(
                    "none",
                    "cdidx pre-commit hook is already installed; no filesystem change is planned",
                    analysis.State,
                    chainedHookState,
                    []);
            }

            return new HookOperationPlan(
                "replace_managed",
                "cdidx pre-commit hook would be updated",
                analysis.State,
                chainedHookState,
                [
                    new HookCommandFileChangeJsonResult(
                        "replace",
                        hookPath,
                        "generated_managed_hook",
                        null,
                        existingHash,
                        generatedHash,
                        executable,
                        true),
                ]);
        }

        if (chainedHookExists && !options.Force)
        {
            return new HookOperationPlan(
                "blocked",
                $"chained hook already exists: {chainedHookPath}",
                analysis.State,
                chainedHookState,
                [],
                Blocked: true,
                BlockExitCode: CommandExitCodes.UsageError);
        }

        return new HookOperationPlan(
            "chain_existing",
            "cdidx pre-commit hook would be updated and the existing hook would be chained",
            analysis.State,
            chainedHookState,
            [
                new HookCommandFileChangeJsonResult(
                    chainedHookExists ? "replace" : "create",
                    chainedHookPath,
                    "existing_pre_commit_hook",
                    hookPath,
                    chainedHookExists ? ComputeFileSha256WithinLimit(ioChainedHookPath) : null,
                    existingHash,
                    chainedHookExists ? IsExecutableHook(ioChainedHookPath) : null,
                    executable),
                new HookCommandFileChangeJsonResult(
                    "replace",
                    hookPath,
                    "generated_managed_hook",
                    null,
                    existingHash,
                    generatedHash,
                    executable,
                    true),
            ]);
    }

    private static bool TryResolveHookWritePaths(
        string gitDir,
        out string hooksDir,
        out string hookPath,
        out string chainedHookPath)
    {
        hooksDir = string.Empty;
        hookPath = string.Empty;
        chainedHookPath = string.Empty;
        return GitHelper.TryResolveGitMetadataChildPath(
                   gitDir,
                   "hooks",
                   expectDirectory: true,
                   allowMissing: false,
                   out hooksDir)
               && GitHelper.TryResolveGitMetadataChildPath(
                   hooksDir,
                   HookName,
                   expectDirectory: false,
                   allowMissing: true,
                   out hookPath)
               && GitHelper.TryResolveGitMetadataChildPath(
                   hooksDir,
                   ChainedHookName,
                   expectDirectory: false,
                   allowMissing: true,
                   out chainedHookPath);
    }

    private static int Uninstall(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath, string gitDir, string hooksDir, string hookPath, string chainedHookPath)
    {
        var warnings = new List<HookCommandWarningJsonResult>();
        if (!Directory.Exists(LongPath.EnsureWindowsPrefix(hooksDir)))
        {
            var absentPlan = HookOperationPlan.Absent;
            return options.DryRun
                ? WriteDryRunResult(options, jsonOptions, projectPath, hookPath, chainedHookPath, absentPlan)
                : WriteResult(options.Json, jsonOptions, "absent", "cdidx pre-commit hook is not installed", projectPath, hookPath, null, CommandExitCodes.Success);
        }

        if (!TryResolveHookWritePaths(gitDir, out _, out hookPath, out chainedHookPath))
            return WriteResult(options.Json, jsonOptions, "error", "unsafe Git hook file path", projectPath, null, null, CommandExitCodes.InstallError);

        var plan = BuildUninstallPlan(options, hookPath, chainedHookPath);
        if (options.DryRun)
            return WriteDryRunResult(options, jsonOptions, projectPath, hookPath, chainedHookPath, plan);
        if (plan.Blocked)
            return WriteBlockedPlanResult(options, jsonOptions, projectPath, hookPath, chainedHookPath, plan);
        if (plan.PlannedAction == "none")
        {
            return WriteResult(
                options.Json,
                jsonOptions,
                "absent",
                "cdidx pre-commit hook is not installed",
                projectPath,
                hookPath,
                plan.ChainedHookState == "present" ? chainedHookPath : null,
                CommandExitCodes.Success);
        }

        if (!TryResolveHookWritePaths(gitDir, out _, out hookPath, out chainedHookPath))
            return WriteResult(options.Json, jsonOptions, "error", "Git hook file path became unsafe before write", projectPath, null, null, CommandExitCodes.InstallError);

        if (plan.PlannedAction is "restore_chained" or "force_restore_chained")
        {
            try
            {
                var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
                var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
                ReplaceFile(ioChainedHookPath, ioHookPath, destinationBackupFileName: null);
                MakeExecutable(ioHookPath);
            }
            catch (Exception ex) when (IsHookFileOperationException(ex))
            {
                RecordHookWarning(warnings, "chained_hook_backup", chainedHookPath, "failed to restore chained hook backup", ex);
                return WriteResult(options.Json, jsonOptions, "error", "failed to restore chained pre-commit hook", projectPath, hookPath, chainedHookPath, CommandExitCodes.InstallError, warnings);
            }
        }
        else if (plan.PlannedAction == "remove_managed_block")
        {
            Action<string>? applyFileMode = null;
            if (plan.ResultingHookMode is { } resultingHookMode)
                applyFileMode = path => ApplyUnixFileMode(path, resultingHookMode);
            AtomicFileWriter.Write(
                hookPath,
                stream => stream.Write(plan.ResultingHookBytes!),
                applyFileMode);
        }
        else
        {
            var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
            if (!TryDeleteFile(ioHookPath, hookPath, "managed_hook", warnings))
                return WriteResult(options.Json, jsonOptions, "error", "failed to delete managed pre-commit hook", projectPath, hookPath, null, CommandExitCodes.InstallError, warnings);
        }

        return WriteResult(options.Json, jsonOptions, "uninstalled", "cdidx pre-commit hook uninstalled", projectPath, hookPath, null, CommandExitCodes.Success, warnings);
    }

    private static HookOperationPlan BuildUninstallPlan(
        HookCommandOptions options,
        string hookPath,
        string chainedHookPath)
    {
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        var chainedHookExists = File.Exists(ioChainedHookPath);
        var chainedHookState = chainedHookExists ? "present" : "absent";
        if (!File.Exists(ioHookPath))
            return HookOperationPlan.Absent with { ChainedHookState = chainedHookState };

        var hookContent = ReadHookBytesWithinLimit(ioHookPath);
        var analysis = AnalyzeManagedHook(hookContent);
        var hookHash = hookContent is null
            ? null
            : ComputeBytesSha256(hookContent);
        var hookExecutable = IsExecutableHook(ioHookPath);
        UnixFileMode? hookMode = null;
        if (!OperatingSystem.IsWindows())
            hookMode = File.GetUnixFileMode(ioHookPath);
        if (analysis.State != "managed" && !options.Force)
        {
            var message = analysis.State == "conflicted"
                ? "pre-commit hook has conflicted cdidx managed markers; pass --force to remove it"
                : "pre-commit hook is not managed by cdidx; pass --force to remove it";
            return new HookOperationPlan(
                "blocked",
                message,
                analysis.State,
                chainedHookState,
                [],
                Blocked: true,
                BlockExitCode: CommandExitCodes.UsageError);
        }

        if (chainedHookExists)
        {
            var chainedHash = ComputeFileSha256WithinLimit(ioChainedHookPath);
            return new HookOperationPlan(
                options.Force && analysis.State != "managed" ? "force_restore_chained" : "restore_chained",
                "the chained pre-commit hook would be restored",
                analysis.State,
                chainedHookState,
                [
                    new HookCommandFileChangeJsonResult(
                        "restore",
                        hookPath,
                        "chained_hook_backup",
                        chainedHookPath,
                        hookHash,
                        chainedHash,
                        hookExecutable,
                        true),
                    new HookCommandFileChangeJsonResult(
                        "consume",
                        chainedHookPath,
                        "chained_hook_backup",
                        chainedHookPath,
                        chainedHash,
                        null,
                        IsExecutableHook(ioChainedHookPath),
                        null),
                ]);
        }

        if (analysis.State == "managed"
            && analysis.BytesWithoutManagedBlock is { } remainingBytes
            && !analysis.BytesWithoutManagedBlockArePreamble)
        {
            return new HookOperationPlan(
                "remove_managed_block",
                "the cdidx managed block would be removed while preserving the surrounding hook content",
                analysis.State,
                chainedHookState,
                [
                    new HookCommandFileChangeJsonResult(
                        "replace",
                        hookPath,
                        "existing_hook_without_cdidx_managed_block",
                        hookPath,
                        hookHash,
                        ComputeBytesSha256(remainingBytes),
                        hookExecutable,
                        hookExecutable),
                ],
                ResultingHookBytes: remainingBytes,
                ResultingHookMode: hookMode);
        }

        var plannedAction = options.Force && analysis.State != "managed"
            ? "force_delete_unmanaged"
            : "delete_managed";
        return new HookOperationPlan(
            plannedAction,
            "the pre-commit hook would be deleted",
            analysis.State,
            chainedHookState,
            [
                new HookCommandFileChangeJsonResult(
                    "delete",
                    hookPath,
                    analysis.State == "managed" ? "cdidx_managed_hook" : "force_selected_hook",
                    null,
                    hookHash,
                    null,
                    hookExecutable,
                    null),
            ]);
    }

    private static int Status(
        HookCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        string projectPath,
        string hookPath,
        string chainedHookPath)
    {
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        var hookExists = File.Exists(ioHookPath);
        var hookContent = hookExists ? ReadHookBytesWithinLimit(ioHookPath) : null;
        var installed = hookExists && AnalyzeManagedHook(hookContent).State == "managed";
        var status = installed ? "installed" : hookExists ? "custom" : "absent";
        HookExecutableJsonResult? executable = null;
        if (installed)
        {
            if (!TryReadExecutableManifest(hookContent, out var installedSelection))
            {
                executable = HookExecutableJsonResult.Unresolved("managed_hook_missing_executable_manifest");
            }
            else if (!ManagedInvocationMatches(
                         hookContent,
                         projectPath,
                         chainedHookPath,
                         installedSelection))
            {
                executable = HookExecutableJsonResult.Unresolved("managed_hook_executable_manifest_mismatch");
            }
            else
            {
                _ = TryResolveCurrentExecutable(appVersion, out var currentSelection, out _);
                executable = InspectExecutable(installedSelection, currentSelection);
            }
        }

        var message = $"cdidx pre-commit hook is {status}";
        if (executable is { Status: not "available" })
            message += $"; pinned executable is {executable.Status}";
        return WriteResult(
            options.Json,
            jsonOptions,
            status,
            message,
            projectPath,
            hookPath,
            File.Exists(ioChainedHookPath) ? chainedHookPath : null,
            CommandExitCodes.Success,
            executable: executable);
    }

    private static int UnknownCommand(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath)
    {
        if (!options.Json)
            PrintUsage();
        return WriteResult(options.Json, jsonOptions, "error", $"unknown hooks command: {ConsoleUi.FormatBoundedValue(options.Command)}", projectPath, null, null, CommandExitCodes.UsageError);
    }

    private static byte[]? ReadHookBytesWithinLimit(string ioHookPath)
        => DataDirectorySecurity.ReadBytesWithinLimit(ioHookPath, MaxHookMarkerBytes, FileShare.ReadWrite);

    private static ManagedHookAnalysis AnalyzeManagedHook(byte[]? content)
    {
        if (content is null)
            return new ManagedHookAnalysis("unmanaged", null, false);

        var rawAnalysis = AnalyzeRawManagedHook(content);
        if (rawAnalysis.State != "unmanaged"
            || !TryGetBomEncoding(content, out var encoding, out var bomLength))
        {
            return rawAnalysis;
        }

        return AnalyzeBomEncodedManagedHook(content, encoding, bomLength);
    }

    private static ManagedHookAnalysis AnalyzeRawManagedHook(byte[] content)
    {
        var contentSpan = content.AsSpan();
        var beginCount = CountOccurrences(contentSpan, BeginMarkerBytes);
        var endCount = CountOccurrences(contentSpan, EndMarkerBytes);
        if (beginCount == 0 && endCount == 0)
            return new ManagedHookAnalysis("unmanaged", null, false);
        if (beginCount != 1 || endCount != 1)
            return new ManagedHookAnalysis("conflicted", null, false);

        var beginIndex = contentSpan.IndexOf(BeginMarkerBytes);
        var endIndex = contentSpan.IndexOf(EndMarkerBytes);
        if (endIndex < beginIndex
            || !IsMarkerOnlyLine(contentSpan, beginIndex, BeginMarkerBytes)
            || !IsMarkerOnlyLine(contentSpan, endIndex, EndMarkerBytes))
        {
            return new ManagedHookAnalysis("conflicted", null, false);
        }

        var blockStart = FindLineStart(contentSpan, beginIndex);
        var blockEnd = FindLineEndIncludingTerminator(
            contentSpan,
            endIndex + EndMarkerBytes.Length);
        var remainingBytes = new byte[content.Length - (blockEnd - blockStart)];
        contentSpan[..blockStart].CopyTo(remainingBytes);
        contentSpan[blockEnd..].CopyTo(remainingBytes.AsSpan(blockStart));
        return new ManagedHookAnalysis(
            "managed",
            remainingBytes,
            IsOnlyManagedHookPreamble(remainingBytes));
    }

    private static ManagedHookAnalysis AnalyzeBomEncodedManagedHook(
        byte[] content,
        Encoding encoding,
        int bomLength)
    {
        string text;
        try
        {
            text = encoding.GetString(content, bomLength, content.Length - bomLength);
        }
        catch (DecoderFallbackException)
        {
            return new ManagedHookAnalysis("unmanaged", null, false);
        }

        var beginCount = CountOccurrences(text, BeginMarker);
        var endCount = CountOccurrences(text, EndMarker);
        if (beginCount == 0 && endCount == 0)
            return new ManagedHookAnalysis("unmanaged", null, false);
        if (beginCount != 1 || endCount != 1)
            return new ManagedHookAnalysis("conflicted", null, false);

        var beginIndex = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        var endIndex = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (endIndex < beginIndex
            || !IsMarkerOnlyLine(text, beginIndex, BeginMarker)
            || !IsMarkerOnlyLine(text, endIndex, EndMarker))
        {
            return new ManagedHookAnalysis("conflicted", null, false);
        }

        var blockStart = FindLineStart(text, beginIndex);
        var blockEnd = FindLineEndIncludingTerminator(
            text,
            endIndex + EndMarker.Length);
        var byteBlockStart = bomLength + encoding.GetByteCount(text.AsSpan(0, blockStart));
        var byteBlockEnd = bomLength + encoding.GetByteCount(text.AsSpan(0, blockEnd));
        var remainingBytes = new byte[content.Length - (byteBlockEnd - byteBlockStart)];
        content.AsSpan(0, byteBlockStart).CopyTo(remainingBytes);
        content.AsSpan(byteBlockEnd).CopyTo(remainingBytes.AsSpan(byteBlockStart));
        var remainingText = text.Remove(blockStart, blockEnd - blockStart);
        return new ManagedHookAnalysis(
            "managed",
            remainingBytes,
            IsOnlyManagedHookPreamble(remainingText));
    }

    private static bool TryGetBomEncoding(
        ReadOnlySpan<byte> content,
        out Encoding encoding,
        out int bomLength)
    {
        if (content.Length >= 4
            && content[0] == 0xFF
            && content[1] == 0xFE
            && content[2] == 0x00
            && content[3] == 0x00)
        {
            encoding = new UTF32Encoding(
                bigEndian: false,
                byteOrderMark: false,
                throwOnInvalidCharacters: true);
            bomLength = 4;
            return true;
        }

        if (content.Length >= 4
            && content[0] == 0x00
            && content[1] == 0x00
            && content[2] == 0xFE
            && content[3] == 0xFF)
        {
            encoding = new UTF32Encoding(
                bigEndian: true,
                byteOrderMark: false,
                throwOnInvalidCharacters: true);
            bomLength = 4;
            return true;
        }

        if (content.Length >= 2
            && content[0] == 0xFF
            && content[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: false,
                throwOnInvalidBytes: true);
            bomLength = 2;
            return true;
        }

        if (content.Length >= 2
            && content[0] == 0xFE
            && content[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(
                bigEndian: true,
                byteOrderMark: false,
                throwOnInvalidBytes: true);
            bomLength = 2;
            return true;
        }

        encoding = Encoding.UTF8;
        bomLength = 0;
        return false;
    }

    private static int CountOccurrences(string value, string marker)
    {
        var count = 0;
        var offset = 0;
        while (offset <= value.Length - marker.Length)
        {
            var index = value.IndexOf(marker, offset, StringComparison.Ordinal);
            if (index < 0)
                break;
            count++;
            offset = index + marker.Length;
        }

        return count;
    }

    private static bool IsMarkerOnlyLine(
        string content,
        int markerIndex,
        string marker)
    {
        var lineStart = FindLineStart(content, markerIndex);
        var lineEnd = FindLineEnd(content, markerIndex + marker.Length);
        return content[lineStart..lineEnd].Trim().Equals(
            marker,
            StringComparison.Ordinal);
    }

    private static int FindLineStart(string content, int offset)
    {
        while (offset > 0 && !IsLineTerminator(content[offset - 1]))
            offset--;
        return offset;
    }

    private static int FindLineEnd(string content, int offset)
    {
        while (offset < content.Length && !IsLineTerminator(content[offset]))
            offset++;
        return offset;
    }

    private static int FindLineEndIncludingTerminator(string content, int offset)
    {
        var lineEnd = FindLineEnd(content, offset);
        if (lineEnd == content.Length)
            return lineEnd;
        if (content[lineEnd] == '\r'
            && lineEnd + 1 < content.Length
            && content[lineEnd + 1] == '\n')
        {
            return lineEnd + 2;
        }

        return lineEnd + 1;
    }

    private static bool IsLineTerminator(char value)
        => value is '\r' or '\n';

    private static int CountOccurrences(ReadOnlySpan<byte> value, ReadOnlySpan<byte> marker)
    {
        var count = 0;
        var offset = 0;
        while (offset <= value.Length - marker.Length)
        {
            var relativeIndex = value[offset..].IndexOf(marker);
            if (relativeIndex < 0)
                break;
            count++;
            offset += relativeIndex + marker.Length;
        }

        return count;
    }

    private static bool IsMarkerOnlyLine(
        ReadOnlySpan<byte> content,
        int markerIndex,
        ReadOnlySpan<byte> marker)
    {
        var lineStart = FindLineStart(content, markerIndex);
        var lineEnd = FindLineEnd(content, markerIndex + marker.Length);
        var line = TrimAsciiWhitespace(content[lineStart..lineEnd]);
        return line.SequenceEqual(marker);
    }

    private static int FindLineStart(ReadOnlySpan<byte> content, int offset)
    {
        while (offset > 0 && !IsLineTerminator(content[offset - 1]))
            offset--;
        return offset;
    }

    private static int FindLineEnd(ReadOnlySpan<byte> content, int offset)
    {
        while (offset < content.Length && !IsLineTerminator(content[offset]))
            offset++;
        return offset;
    }

    private static int FindLineEndIncludingTerminator(ReadOnlySpan<byte> content, int offset)
    {
        var lineEnd = FindLineEnd(content, offset);
        if (lineEnd == content.Length)
            return lineEnd;
        if (content[lineEnd] == (byte)'\r'
            && lineEnd + 1 < content.Length
            && content[lineEnd + 1] == (byte)'\n')
        {
            return lineEnd + 2;
        }

        return lineEnd + 1;
    }

    private static bool IsLineTerminator(byte value)
        => value is (byte)'\r' or (byte)'\n';

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] <= 0x20)
            start++;
        var end = value.Length;
        while (end > start && value[end - 1] <= 0x20)
            end--;
        return value[start..end];
    }

    private static bool IsOnlyManagedHookPreamble(ReadOnlySpan<byte> content)
    {
        var trimmed = TrimAsciiWhitespace(content);
        if (trimmed.StartsWith(Utf8BomBytes))
            trimmed = TrimAsciiWhitespace(trimmed[Utf8BomBytes.Length..]);
        return trimmed.IsEmpty || trimmed.SequenceEqual(HookPreambleBytes);
    }

    private static bool IsOnlyManagedHookPreamble(string content)
    {
        var trimmed = content.Trim();
        return trimmed.Length == 0
            || trimmed.Equals("#!/bin/sh", StringComparison.Ordinal);
    }

    private static string ComputeBytesSha256(ReadOnlySpan<byte> content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string? ComputeFileSha256WithinLimit(string ioPath)
    {
        var bytes = DataDirectorySecurity.ReadBytesWithinLimit(
            ioPath,
            MaxHookMarkerBytes,
            FileShare.ReadWrite);
        return bytes is null ? null : ComputeBytesSha256(bytes);
    }

    private static void ReplaceCustomHookWithManagedHook(
        string hooksDir,
        string hookPath,
        string chainedHookPath,
        string hookScript,
        List<HookCommandWarningJsonResult> warnings)
    {
        var stagedHookPath = Path.Combine(hooksDir, $".{HookName}.{Guid.NewGuid():N}.tmp");
        var ioStagedHookPath = LongPath.EnsureWindowsPrefix(stagedHookPath);
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var ioChainedHookPath = LongPath.EnsureWindowsPrefix(chainedHookPath);
        var stagedHookMoved = false;

        try
        {
            WriteStagedHookScript(ioStagedHookPath, hookScript);
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

    private static void WriteStagedHookScript(
        string ioStagedHookPath,
        string hookScript)
    {
        using (var stream = CreateStagedHookFileStream(ioStagedHookPath))
        {
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true))
            {
                writer.Write(hookScript);
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
        warnings.Add(new HookCommandWarningJsonResult(category, path, DiagnosticSanitizer.ForPath(path), message));
    }

    private static bool TryResolveCurrentExecutable(
        string appVersion,
        out HookExecutableSelection selection,
        out string failureReason)
    {
        if (ExecutableSelectionForTesting != null)
        {
            selection = ExecutableSelectionForTesting(appVersion);
            return ValidateExecutableSelection(selection, out failureReason);
        }

        return TryCreateExecutableSelection(
            Environment.ProcessPath,
            typeof(HookCommandRunner).Assembly.Location,
            appVersion,
            out selection,
            out failureReason);
    }

    internal static bool TryCreateExecutableSelection(
        string? processPath,
        string? entryAssemblyPath,
        string appVersion,
        out HookExecutableSelection selection,
        out string failureReason)
    {
        selection = default!;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            failureReason = "current_process_path_unavailable";
            return false;
        }

        var launcherIsDotnetHost = IsDotnetHost(processPath);
        if (!TryResolvePinnedPath(processPath, out var resolvedProcessPath, out failureReason))
            return false;

        if (!launcherIsDotnetHost && !IsDotnetHost(resolvedProcessPath))
        {
            selection = new HookExecutableSelection(
                "process_path",
                appVersion,
                [resolvedProcessPath]);
            return ValidateExecutableSelection(selection, out failureReason);
        }

        if (string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            failureReason = "current_entry_assembly_path_unavailable";
            return false;
        }

        if (!TryResolvePinnedPath(entryAssemblyPath, out var resolvedAssemblyPath, out failureReason))
            return false;

        selection = new HookExecutableSelection(
            "dotnet_host_and_assembly",
            appVersion,
            [resolvedProcessPath, resolvedAssemblyPath]);
        return ValidateExecutableSelection(selection, out failureReason);
    }

    private static bool TryResolvePinnedPath(
        string path,
        out string resolvedPath,
        out string failureReason)
    {
        resolvedPath = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(LongPath.EnsureWindowsPrefix(fullPath)))
            {
                failureReason = "current_executable_path_missing";
                return false;
            }

            var linkTarget = new FileInfo(LongPath.EnsureWindowsPrefix(fullPath))
                .ResolveLinkTarget(returnFinalTarget: true);
            resolvedPath = Path.GetFullPath(linkTarget?.FullName ?? fullPath);
            if (!File.Exists(LongPath.EnsureWindowsPrefix(resolvedPath)))
            {
                failureReason = "resolved_executable_path_missing";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsHookFileOperationException(ex))
        {
            failureReason = $"current_executable_path_unusable:{DiagnosticRedactor.ClassifyException(ex)}";
            return false;
        }
    }

    private static bool ValidateExecutableSelection(
        HookExecutableSelection selection,
        out string failureReason)
    {
        if (!HasValidExecutableSelectionShape(selection))
        {
            failureReason = "current_executable_provenance_invalid";
            return false;
        }

        foreach (var argument in selection.Argv)
        {
            if (!File.Exists(LongPath.EnsureWindowsPrefix(argument)))
            {
                failureReason = "current_executable_path_unusable";
                return false;
            }
        }
        if (!IsRunnableExecutable(selection.Argv[0]))
        {
            failureReason = "current_executable_not_runnable";
            return false;
        }
        if (selection.Source == "dotnet_host_and_assembly"
            && !ValidateManagedDeploymentFiles(selection.Argv[1], out failureReason))
        {
            return false;
        }
        if (EncodeExecutableManifest(selection).Length > MaxExecutableManifestChars)
        {
            failureReason = "current_executable_provenance_too_large";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool HasValidExecutableSelectionShape(HookExecutableSelection selection)
        => selection.Source is "process_path" or "dotnet_host_and_assembly"
           && !string.IsNullOrWhiteSpace(selection.Version)
           && selection.Version.Length <= 128
           && !selection.Version.Any(char.IsControl)
           && ((selection.Source == "process_path" && selection.Argv.Count == 1)
               || (selection.Source == "dotnet_host_and_assembly" && selection.Argv.Count == 2))
           && selection.Argv.All(static argument =>
               !string.IsNullOrWhiteSpace(argument)
               && argument.Length <= MaxExecutableArgumentChars
               && IsCanonicalFullyQualifiedPath(argument)
               && argument.IndexOfAny(['\0', '\r', '\n']) < 0);

    internal static bool IsCanonicalFullyQualifiedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            return false;

        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(path, Path.GetFullPath(path), comparison);
        }
        catch (Exception ex) when (IsHookFileOperationException(ex))
        {
            return false;
        }
    }

    private static HookExecutableJsonResult InspectExecutable(
        HookExecutableSelection installedSelection,
        HookExecutableSelection? currentSelection)
    {
        var path = installedSelection.Argv.FirstOrDefault();
        var entryAssemblyPath = installedSelection.Argv.Count > 1
            ? installedSelection.Argv[1]
            : null;
        var diagnosticArgv = installedSelection.Argv
            .Select(DiagnosticSanitizer.ForSupportSafePath)
            .ToArray();
        HookExecutableJsonResult Result(
            string status,
            string failureReason,
            string? actualVersion = null)
            => new(
                installedSelection.Source,
                path,
                entryAssemblyPath,
                installedSelection.Argv,
                path == null ? null : DiagnosticSanitizer.ForSupportSafePath(path),
                entryAssemblyPath == null ? null : DiagnosticSanitizer.ForSupportSafePath(entryAssemblyPath),
                diagnosticArgv,
                installedSelection.Version,
                actualVersion,
                status,
                failureReason);

        var missingPath = installedSelection.Argv.FirstOrDefault(
            static argument => !File.Exists(LongPath.EnsureWindowsPrefix(argument)));
        if (missingPath != null)
            return Result("missing", "pinned_executable_missing");
        if (!IsRunnableExecutable(installedSelection.Argv[0]))
            return Result("not_executable", "pinned_executable_not_runnable");
        if (installedSelection.Source == "dotnet_host_and_assembly")
        {
            if (!IsReadableRegularFile(installedSelection.Argv[1]))
                return Result("not_executable", "pinned_entry_assembly_unreadable");

            foreach (var runtimePath in GetManagedRuntimePaths(installedSelection.Argv[1]))
            {
                if (!File.Exists(LongPath.EnsureWindowsPrefix(runtimePath)))
                    return Result("missing", "pinned_runtime_file_missing");
                if (!IsReadableRegularFile(runtimePath))
                    return Result("not_executable", "pinned_runtime_file_unreadable");
            }
        }

        string? actualVersion;
        if (currentSelection != null && ExecutableSelectionsMatch(installedSelection, currentSelection))
            actualVersion = currentSelection.Version;
        else
            actualVersion = TryReadPinnedVersion(installedSelection);

        var status = actualVersion == null
            ? "available_unverified"
            : string.Equals(actualVersion, installedSelection.Version, StringComparison.Ordinal)
                ? "available"
                : "version_mismatch";
        var failureReason = status switch
        {
            "available_unverified" => "pinned_executable_version_unavailable",
            "version_mismatch" => "pinned_executable_version_mismatch",
            _ => null,
        };
        return new HookExecutableJsonResult(
            installedSelection.Source,
            path,
            entryAssemblyPath,
            installedSelection.Argv,
            path == null ? null : DiagnosticSanitizer.ForSupportSafePath(path),
            entryAssemblyPath == null ? null : DiagnosticSanitizer.ForSupportSafePath(entryAssemblyPath),
            diagnosticArgv,
            installedSelection.Version,
            actualVersion,
            status,
            failureReason);
    }

    private static bool ExecutableSelectionsMatch(
        HookExecutableSelection left,
        HookExecutableSelection right)
    {
        if (!string.Equals(left.Source, right.Source, StringComparison.Ordinal)
            || left.Argv.Count != right.Argv.Count)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (var index = 0; index < left.Argv.Count; index++)
        {
            if (!string.Equals(left.Argv[index], right.Argv[index], comparison))
                return false;
        }

        return true;
    }

    private static string? TryReadPinnedVersion(HookExecutableSelection selection)
    {
        var versionTarget = selection.Argv.Count > 1
            ? selection.Argv[1]
            : selection.Argv[0];
        try
        {
            var directory = Path.GetDirectoryName(versionTarget);
            if (directory == null)
                return null;
            var versionPath = Path.Combine(directory, "version.json");
            if (!IsReadableRegularFile(versionPath))
                return null;
            var versionBytes = DataDirectorySecurity.ReadBytesWithinLimit(
                versionPath,
                16 * 1024,
                FileShare.ReadWrite);
            if (versionBytes is null)
                return null;

            using var document = JsonDocument.Parse(versionBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!document.RootElement.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = version.GetString();
            return !string.IsNullOrWhiteSpace(value)
                   && value.Length <= 128
                   && !value.Any(char.IsControl)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException
                                       or JsonException)
        {
            return null;
        }
    }

    private static string EncodeExecutableManifest(HookExecutableSelection selection)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1);
            WriteManifestString(writer, selection.Source);
            WriteManifestString(writer, selection.Version);
            writer.Write(selection.Argv.Count);
            foreach (var argument in selection.Argv)
                WriteManifestString(writer, argument);
        }

        return Convert.ToBase64String(buffer.ToArray());
    }

    private static bool TryReadExecutableManifest(
        byte[]? hookContent,
        out HookExecutableSelection selection)
    {
        selection = default!;
        if (hookContent == null)
            return false;

        string text;
        try
        {
            text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(hookContent);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var manifestLines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith(ExecutableManifestPrefix, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (manifestLines.Length != 1)
            return false;
        var manifestLine = manifestLines[0];
        var encoded = manifestLine[ExecutableManifestPrefix.Length..].Trim();
        if (encoded.Length is 0 or > MaxExecutableManifestChars)
            return false;

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            using var buffer = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadInt32() != 1)
                return false;
            if (!TryReadManifestString(reader, 64, out var source)
                || !TryReadManifestString(reader, 128, out var version))
            {
                return false;
            }
            var argumentCount = reader.ReadInt32();
            if (argumentCount is < 1 or > 2)
                return false;
            var arguments = new string[argumentCount];
            for (var index = 0; index < arguments.Length; index++)
            {
                if (!TryReadManifestString(reader, MaxExecutableArgumentChars, out arguments[index]))
                    return false;
            }

            if (buffer.Position != buffer.Length)
                return false;
            selection = new HookExecutableSelection(source, version, arguments);
            return HasValidExecutableSelectionShape(selection);
        }
        catch (Exception ex) when (ex is FormatException
                                       or EndOfStreamException
                                       or IOException
                                       or ArgumentException
                                       or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool ManagedInvocationMatches(
        byte[]? hookContent,
        string projectPath,
        string chainedHookPath,
        HookExecutableSelection selection)
    {
        if (hookContent is null)
            return false;

        string actualText;
        try
        {
            actualText = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(hookContent);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (!TryBuildHookScript(
                chainedHookPath,
                projectPath,
                selection,
                out var expectedText))
        {
            return false;
        }
        return TryExtractManagedBlock(actualText, out var actualBlock)
               && TryExtractManagedBlock(expectedText, out var expectedBlock)
               && ManagedBlocksMatchRepositoryPaths(
                   actualBlock,
                   expectedBlock,
                   projectPath,
                   chainedHookPath);
    }

    private static bool ManagedBlocksMatchRepositoryPaths(
        string actualBlock,
        string expectedBlock,
        string projectPath,
        string chainedHookPath)
    {
        try
        {
            actualBlock = NormalizeManagedBlockPathToken(
                actualBlock,
                projectPath,
                "\0CDIDX_PROJECT_PATH\0");
            expectedBlock = NormalizeManagedBlockPathToken(
                expectedBlock,
                projectPath,
                "\0CDIDX_PROJECT_PATH\0");
            actualBlock = NormalizeManagedBlockPathToken(
                actualBlock,
                chainedHookPath,
                "\0CDIDX_CHAINED_HOOK_PATH\0");
            expectedBlock = NormalizeManagedBlockPathToken(
                expectedBlock,
                chainedHookPath,
                "\0CDIDX_CHAINED_HOOK_PATH\0");
            return string.Equals(actualBlock, expectedBlock, StringComparison.Ordinal);
        }
        catch (Exception ex) when (IsHookFileOperationException(ex) || ex is CodeIndexException)
        {
            return false;
        }
    }

    private static string NormalizeManagedBlockPathToken(
        string block,
        string path,
        string replacement)
    {
        var token = QuoteShell(path);
        var comparison = PathCasing.ComparisonFor(path);
        var firstIndex = block.IndexOf(token, comparison);
        if (firstIndex < 0)
            return block;

        var builder = new StringBuilder(block.Length);
        var offset = 0;
        while (firstIndex >= 0)
        {
            builder.Append(block, offset, firstIndex - offset);
            builder.Append(replacement);
            offset = firstIndex + token.Length;
            firstIndex = block.IndexOf(token, offset, comparison);
        }

        builder.Append(block, offset, block.Length - offset);
        return builder.ToString();
    }

    private static bool TryExtractManagedBlock(string text, out string block)
    {
        block = string.Empty;
        if (CountOccurrences(text, BeginMarker) != 1
            || CountOccurrences(text, EndMarker) != 1)
        {
            return false;
        }

        var beginIndex = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        var endIndex = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (endIndex < beginIndex
            || !IsMarkerOnlyLine(text, beginIndex, BeginMarker)
            || !IsMarkerOnlyLine(text, endIndex, EndMarker))
        {
            return false;
        }

        var blockStart = FindLineStart(text, beginIndex);
        var blockEnd = FindLineEndIncludingTerminator(text, endIndex + EndMarker.Length);
        block = text[blockStart..blockEnd];
        return true;
    }

    private static void WriteManifestString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static bool TryReadManifestString(
        BinaryReader reader,
        int maxCharacters,
        out string value)
    {
        value = string.Empty;
        var byteLength = reader.ReadInt32();
        var maxBytes = checked(maxCharacters * 4);
        if (byteLength is < 0 || byteLength > maxBytes)
            return false;
        var bytes = reader.ReadBytes(byteLength);
        if (bytes.Length != byteLength)
            return false;
        value = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(bytes);
        return value.Length <= maxCharacters;
    }

    private static string FormatManifestComment(string value)
        => new(value.Select(static character => char.IsControl(character) ? '?' : character).ToArray());

    private static string NormalizeShellPath(string value)
        => OperatingSystem.IsWindows()
            ? value.Replace('\\', '/')
            : value;

    private static bool IsRunnableExecutable(string path)
    {
        if (!IsRegularFile(path))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        try
        {
            return UnixAccess(LongPath.EnsureWindowsPrefix(path), UnixExecuteAccess) == 0;
        }
        catch (Exception ex) when (IsHookFileOperationException(ex)
                                   || ex is DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool ValidateManagedDeploymentFiles(
        string assemblyPath,
        out string failureReason)
    {
        if (!IsReadableRegularFile(assemblyPath))
        {
            failureReason = "current_entry_assembly_unreadable";
            return false;
        }

        foreach (var runtimePath in GetManagedRuntimePaths(assemblyPath))
        {
            if (!File.Exists(LongPath.EnsureWindowsPrefix(runtimePath)))
            {
                failureReason = "current_runtime_file_missing";
                return false;
            }
            if (!IsReadableRegularFile(runtimePath))
            {
                failureReason = "current_runtime_file_unreadable";
                return false;
            }
        }

        failureReason = string.Empty;
        return true;
    }

    private static string[] GetManagedRuntimePaths(string assemblyPath)
        =>
        [
            Path.ChangeExtension(assemblyPath, ".runtimeconfig.json"),
            Path.ChangeExtension(assemblyPath, ".deps.json"),
        ];

    private static bool IsReadableRegularFile(string path)
    {
        if (!IsRegularFile(path))
            return false;

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                return UnixAccess(LongPath.EnsureWindowsPrefix(path), UnixReadAccess) == 0;
            }
            catch (Exception ex) when (IsHookFileOperationException(ex)
                                       || ex is DllNotFoundException
                                           or EntryPointNotFoundException)
            {
                return false;
            }
        }

        try
        {
            using var stream = File.Open(
                LongPath.EnsureWindowsPrefix(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return stream.CanRead;
        }
        catch (Exception ex) when (IsHookFileOperationException(ex))
        {
            return false;
        }
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            var ioPath = LongPath.EnsureWindowsPrefix(path);
            var attributes = File.GetAttributes(ioPath);
            if ((attributes
                 & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
                return true;

            return UnixStat(ioPath, out var status) == 0
                   && (status.Mode & UnixFileTypeMask) == UnixRegularFileType;
        }
        catch (Exception ex) when (IsHookFileOperationException(ex)
                                   || ex is DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int UnixAccess(string path, int mode);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", CharSet = CharSet.Ansi)]
    private static extern int UnixStat(string path, out UnixFileStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixFileStatus
    {
        internal uint Flags;
        internal int Mode;
        internal uint Uid;
        internal uint Gid;
        internal long Size;
        internal long ATime;
        internal long ATimeNsec;
        internal long MTime;
        internal long MTimeNsec;
        internal long CTime;
        internal long CTimeNsec;
        internal long BirthTime;
        internal long BirthTimeNsec;
        internal long Dev;
        internal long RDev;
        internal long Ino;
        internal uint UserFlags;
    }

    private static bool IsDotnetHost(string processPath)
        => string.Equals(
            Path.GetFileNameWithoutExtension(processPath.Replace('\\', '/')),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

    internal static bool TryBuildHookScript(
        string chainedHookPath,
        string projectPath,
        HookExecutableSelection executableSelection,
        out string hookScript)
    {
        hookScript = BuildHookScript(chainedHookPath, projectPath, executableSelection);
        if (Encoding.UTF8.GetByteCount(hookScript) <= MaxHookMarkerBytes)
            return true;

        hookScript = string.Empty;
        return false;
    }

    private static string BuildHookScript(
        string chainedHookPath,
        string projectPath,
        HookExecutableSelection executableSelection)
    {
        var quotedChainedHook = QuoteShell(chainedHookPath);
        var quotedProjectPath = QuoteShell(projectPath);
        var invocation = string.Join(
            ' ',
            executableSelection.Argv.Select(static argument => QuoteShell(NormalizeShellPath(argument))));
        var manifest = EncodeExecutableManifest(executableSelection);
        return $"""
#!/bin/sh
{BeginMarker}
# CDIDX EXECUTABLE SOURCE: {FormatManifestComment(executableSelection.Source)}
# CDIDX EXECUTABLE VERSION: {FormatManifestComment(executableSelection.Version)}
{ExecutableManifestPrefix}{manifest}
{invocation} index {quotedProjectPath} --quiet
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

    private static void ApplyUnixFileMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }

    private static bool IsExecutableHook(string path)
        => OperatingSystem.IsWindows()
            || (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;

    private static int WriteDryRunResult(
        HookCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string projectPath,
        string hookPath,
        string chainedHookPath,
        HookOperationPlan plan,
        HookExecutableJsonResult? executable = null,
        string? managedHookPreview = null)
    {
        var reportedChainedHookPath = plan.ChainedHookState == "present"
            || plan.PlannedAction == "chain_existing"
            ? chainedHookPath
            : null;
        return WriteResult(
            options.Json,
            jsonOptions,
            plan.Blocked ? "error" : "dry_run",
            plan.Message,
            projectPath,
            hookPath,
            reportedChainedHookPath,
            plan.Blocked ? plan.BlockExitCode : CommandExitCodes.Success,
            dryRun: true,
            plannedAction: plan.PlannedAction,
            managedHookPreview: managedHookPreview,
            filesystemMutation: false,
            hookState: plan.HookState,
            chainedHookState: plan.ChainedHookState,
            plannedChanges: plan.PlannedChanges,
            executable: executable);
    }

    private static int WriteBlockedPlanResult(
        HookCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string projectPath,
        string hookPath,
        string chainedHookPath,
        HookOperationPlan plan,
        HookExecutableJsonResult? executable = null)
        => WriteResult(
            options.Json,
            jsonOptions,
            "error",
            plan.Message,
            projectPath,
            hookPath,
            plan.ChainedHookState == "present" ? chainedHookPath : null,
            plan.BlockExitCode,
            executable: executable);

    private static int WriteGeneratedHookTooLargeResult(
        HookCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string projectPath,
        string hookPath,
        string chainedHookPath,
        HookExecutableJsonResult executable)
    {
        var message =
            $"generated cdidx pre-commit hook exceeds the {MaxHookMarkerBytes}-byte management limit";
        if (options.DryRun)
        {
            var plan = BuildInstallPreflightFailurePlan(
                hookPath,
                chainedHookPath,
                message);
            return WriteDryRunResult(
                options,
                jsonOptions,
                projectPath,
                hookPath,
                chainedHookPath,
                plan,
                executable);
        }

        return WriteResult(
            options.Json,
            jsonOptions,
            "error",
            message,
            projectPath,
            hookPath,
            null,
            CommandExitCodes.InstallError,
            executable: executable);
    }

    private static HookOperationPlan BuildInstallPreflightFailurePlan(
        string hookPath,
        string chainedHookPath,
        string message)
    {
        var ioHookPath = LongPath.EnsureWindowsPrefix(hookPath);
        var hookState = File.Exists(ioHookPath)
            ? AnalyzeManagedHook(ReadHookBytesWithinLimit(ioHookPath)).State
            : "absent";
        var chainedHookState = File.Exists(LongPath.EnsureWindowsPrefix(chainedHookPath))
            ? "present"
            : "absent";
        return new HookOperationPlan(
            "blocked",
            message,
            hookState,
            chainedHookState,
            [],
            Blocked: true,
            BlockExitCode: CommandExitCodes.InstallError);
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
        IReadOnlyList<HookCommandWarningJsonResult>? warnings = null,
        bool? dryRun = null,
        string? plannedAction = null,
        string? managedHookPreview = null,
        bool? filesystemMutation = null,
        string? hookState = null,
        string? chainedHookState = null,
        IReadOnlyList<HookCommandFileChangeJsonResult>? plannedChanges = null,
        HookExecutableJsonResult? executable = null)
    {
        var hasWarnings = warnings is { Count: > 0 };
        if (exitCode != CommandExitCodes.Success)
        {
            if (!json && hasWarnings)
            {
                foreach (var warning in warnings!)
                    CommandErrorWriter.WriteWarning(warning.Message);
            }

            JsonObject? additionalJsonProperties = null;
            if (json)
            {
                var diagnosticProjectPath = DiagnosticSanitizer.ForPath(projectPath);
                var diagnosticHookPath = hookPath == null ? null : DiagnosticSanitizer.ForPath(hookPath);
                var diagnosticChainedHookPath = chainedHookPath == null ? null : DiagnosticSanitizer.ForPath(chainedHookPath);
                var safeWarnings = hasWarnings
                    ? warnings!
                        .Select(static warning => new HookCommandWarningJsonResult(
                            warning.Category,
                            warning.DiagnosticPath,
                            warning.DiagnosticPath,
                            DiagnosticSanitizer.ForMessage(warning.Message)))
                        .ToArray()
                    : null;
                var safePlannedChanges = plannedChanges?
                    .Select(static change => change with
                    {
                        Path = DiagnosticSanitizer.ForPath(change.Path),
                        SourcePath = change.SourcePath == null
                            ? null
                            : DiagnosticSanitizer.ForPath(change.SourcePath),
                    })
                    .ToArray();
                var safeExecutable = executable == null
                    ? null
                    : SanitizeExecutableResult(executable);
                additionalJsonProperties = JsonSerializer.SerializeToNode(
                    new HookCommandJsonResult(
                        status,
                        message,
                        diagnosticProjectPath,
                        diagnosticHookPath,
                        diagnosticChainedHookPath,
                        diagnosticProjectPath,
                        diagnosticHookPath,
                        diagnosticChainedHookPath,
                        safeWarnings,
                        dryRun,
                        plannedAction,
                        managedHookPreview == null
                            ? null
                            : SanitizeManagedHookPreviewForError(managedHookPreview),
                        filesystemMutation,
                        hookState,
                        chainedHookState,
                        safePlannedChanges,
                        safeExecutable),
                    CliJsonSerializerContextFactory.Create(jsonOptions).HookCommandJsonResult)!.AsObject();
            }

            var (errorCode, category, hint) = exitCode switch
            {
                CommandExitCodes.UsageError or CommandExitCodes.InvalidArgument
                    => (CommandErrorCodes.UsageError, "usage", "Use `cdidx hooks <install|uninstall|status> --help` and correct the command arguments."),
                CommandExitCodes.NotFound
                    => (CommandErrorCodes.NotGitRepository, "not_found", "Run from a Git worktree or pass `--project <path>` for one."),
                _ => (CommandErrorCodes.HookOperationFailed, "platform", "Inspect the Git metadata path and filesystem permissions, then retry the hook operation."),
            };
            var result = CommandErrorWriter.WriteJsonOrHuman(
                json,
                jsonOptions,
                json ? DiagnosticSanitizer.ForMessage(message) : message,
                exitCode,
                hint,
                GetUsage(),
                errorCode,
                category,
                "hooks",
                DiagnosticSanitizer.ForPath(projectPath),
                additionalJsonProperties);
            if (!json && dryRun == true)
            {
                WriteDryRunDetails(
                    plannedAction,
                    hookState,
                    chainedHookState,
                    plannedChanges,
                    managedHookPreview);
            }

            return result;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new HookCommandJsonResult(
                    status,
                    message,
                    projectPath,
                    hookPath,
                    chainedHookPath,
                    DiagnosticSanitizer.ForPath(projectPath),
                    hookPath == null ? null : DiagnosticSanitizer.ForPath(hookPath),
                    chainedHookPath == null ? null : DiagnosticSanitizer.ForPath(chainedHookPath),
                    hasWarnings ? warnings : null,
                    dryRun,
                    plannedAction,
                    managedHookPreview,
                    filesystemMutation,
                    hookState,
                    chainedHookState,
                    plannedChanges,
                    executable),
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
                WriteExecutableDetails(executable);
            }
            else
            {
                CommandErrorWriter.WriteStderr($"Error: {message}");
            }

            if (dryRun == true)
            {
                WriteDryRunDetails(
                    plannedAction,
                    hookState,
                    chainedHookState,
                    plannedChanges,
                    managedHookPreview);
            }
        }

        return exitCode;
    }

    private static void WriteDryRunDetails(
        string? plannedAction,
        string? hookState,
        string? chainedHookState,
        IReadOnlyList<HookCommandFileChangeJsonResult>? plannedChanges,
        string? managedHookPreview)
    {
        CommandErrorWriter.WriteStdout($"Planned action: {plannedAction}");
        CommandErrorWriter.WriteStdout("Filesystem mutation: false");
        CommandErrorWriter.WriteStdout($"Hook state: {hookState}");
        CommandErrorWriter.WriteStdout($"Chained hook state: {chainedHookState}");
        if (plannedChanges is { Count: > 0 })
        {
            CommandErrorWriter.WriteStdout("Planned changes:");
            foreach (var change in plannedChanges)
            {
                var source = change.SourcePath == null
                    ? string.Empty
                    : $" from {change.SourcePath}";
                CommandErrorWriter.WriteStdout(
                    $"- {change.Action} {change.Path}{source} ({change.Provenance})");
            }
        }

        if (managedHookPreview != null)
        {
            CommandErrorWriter.WriteStdout("Managed hook preview:");
            CommandErrorWriter.WriteStdout(managedHookPreview);
        }
    }

    private static HookExecutableJsonResult SanitizeExecutableResult(HookExecutableJsonResult executable)
        => executable with
        {
            Path = executable.DiagnosticPath,
            EntryAssemblyPath = executable.DiagnosticEntryAssemblyPath,
            Argv = executable.DiagnosticArgv,
            FailureReason = executable.FailureReason == null
                ? null
                : DiagnosticSanitizer.ForMessage(executable.FailureReason),
        };

    private static string SanitizeManagedHookPreviewForError(string managedHookPreview)
    {
        var manifestStart = managedHookPreview.IndexOf(
            ExecutableManifestPrefix,
            StringComparison.Ordinal);
        if (manifestStart >= 0)
        {
            var payloadStart = manifestStart + ExecutableManifestPrefix.Length;
            var payloadEnd = managedHookPreview.IndexOfAny(['\r', '\n'], payloadStart);
            if (payloadEnd < 0)
                payloadEnd = managedHookPreview.Length;
            managedHookPreview = string.Concat(
                managedHookPreview.AsSpan(0, payloadStart),
                "[redacted]",
                managedHookPreview.AsSpan(payloadEnd));
        }

        return DiagnosticRedactor.RedactSensitiveText(
            managedHookPreview,
            "[redacted]",
            redactPaths: true);
    }

    private static void WriteExecutableDetails(HookExecutableJsonResult? executable)
    {
        if (executable == null)
            return;

        CommandErrorWriter.WriteStdout($"Executable status: {executable.Status}");
        CommandErrorWriter.WriteStdout($"Executable source: {executable.Source}");
        if (executable.Path != null)
            CommandErrorWriter.WriteStdout($"Executable: {executable.Path}");
        if (executable.EntryAssemblyPath != null)
            CommandErrorWriter.WriteStdout($"Entry assembly: {executable.EntryAssemblyPath}");
        if (executable.ExpectedVersion != null)
            CommandErrorWriter.WriteStdout($"Expected version: {executable.ExpectedVersion}");
        if (executable.ActualVersion != null)
            CommandErrorWriter.WriteStdout($"Actual version: {executable.ActualVersion}");
        if (executable.FailureReason != null)
            CommandErrorWriter.WriteStdout($"Executable diagnostic: {executable.FailureReason}");
    }

    private static string GetUsage()
        => "cdidx hooks <install|uninstall|status> [--project <path>] [--force] [--dry-run] [--json]";

    private static void PrintUsage()
        => CommandErrorWriter.WriteStderr($"Usage: {GetUsage()}");

    private sealed record ManagedHookAnalysis(
        string State,
        byte[]? BytesWithoutManagedBlock,
        bool BytesWithoutManagedBlockArePreamble);

    private sealed record HookOperationPlan(
        string PlannedAction,
        string Message,
        string HookState,
        string ChainedHookState,
        IReadOnlyList<HookCommandFileChangeJsonResult> PlannedChanges,
        bool Blocked = false,
        int BlockExitCode = CommandExitCodes.Success,
        byte[]? ResultingHookBytes = null,
        UnixFileMode? ResultingHookMode = null)
    {
        public static HookOperationPlan Absent { get; } = new(
            "none",
            "cdidx pre-commit hook is not installed; no filesystem change is planned",
            "absent",
            "absent",
            []);
    }
}

public sealed record HookCommandOptions(string? Command, string? ProjectPath, bool Json, bool Force, bool DryRun, bool ShowHelp, string? ParseError);

public sealed record HookCommandWarningJsonResult(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("diagnostic_path")] string DiagnosticPath,
    [property: JsonPropertyName("message")] string Message);

public sealed record HookCommandFileChangeJsonResult(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("provenance")] string Provenance,
    [property: JsonPropertyName("source_path")] string? SourcePath,
    [property: JsonPropertyName("before_sha256")] string? BeforeSha256,
    [property: JsonPropertyName("after_sha256")] string? AfterSha256,
    [property: JsonPropertyName("executable_before")] bool? ExecutableBefore,
    [property: JsonPropertyName("executable_after")] bool? ExecutableAfter);

public sealed record HookCommandJsonResult(
    string Status,
    string Message,
    string ProjectPath,
    string? HookPath,
    string? ChainedHookPath,
    string DiagnosticProjectPath,
    string? DiagnosticHookPath,
    string? DiagnosticChainedHookPath,
    IReadOnlyList<HookCommandWarningJsonResult>? Warnings = null,
    bool? DryRun = null,
    string? PlannedAction = null,
    string? ManagedHookPreview = null,
    bool? FilesystemMutation = null,
    string? HookState = null,
    string? ChainedHookState = null,
    IReadOnlyList<HookCommandFileChangeJsonResult>? PlannedChanges = null,
    HookExecutableJsonResult? Executable = null,
    [property: JsonPropertyName("api_version")] string ApiVersion = JsonOutputContract.ApiVersion) : IVersionedJsonResult;

internal sealed record HookExecutableSelection(
    string Source,
    string Version,
    IReadOnlyList<string> Argv);

public sealed record HookExecutableJsonResult(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("entry_assembly_path")] string? EntryAssemblyPath,
    [property: JsonPropertyName("argv")] IReadOnlyList<string>? Argv,
    [property: JsonPropertyName("diagnostic_path")] string? DiagnosticPath,
    [property: JsonPropertyName("diagnostic_entry_assembly_path")] string? DiagnosticEntryAssemblyPath,
    [property: JsonPropertyName("diagnostic_argv")] IReadOnlyList<string>? DiagnosticArgv,
    [property: JsonPropertyName("expected_version")] string? ExpectedVersion,
    [property: JsonPropertyName("actual_version")] string? ActualVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("failure_reason")] string? FailureReason)
{
    public static HookExecutableJsonResult Unresolved(string failureReason)
        => new(
            "unresolved",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "unresolved",
            failureReason);
}
