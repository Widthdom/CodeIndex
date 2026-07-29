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
    private static readonly byte[] BeginMarkerBytes = Encoding.ASCII.GetBytes(BeginMarker);
    private static readonly byte[] EndMarkerBytes = Encoding.ASCII.GetBytes(EndMarker);
    private static readonly byte[] HookPreambleBytes = Encoding.ASCII.GetBytes("#!/bin/sh");
    private static readonly byte[] Utf8BomBytes = [0xEF, 0xBB, 0xBF];
    internal const int MaxHookMarkerBytes = 64 * 1024;
    internal static Action<string>? DeleteFileForTesting { get; set; }
    internal static Action<string, string, string?>? ReplaceFileForTesting { get; set; }

    public static int Run(string[] args, JsonSerializerOptions jsonOptions)
    {
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
                "install" => Install(options, jsonOptions, projectPath, gitDir, hooksDir, hookPath, chainedHookPath),
                "uninstall" => Uninstall(options, jsonOptions, projectPath, gitDir, hooksDir, hookPath, chainedHookPath),
                "status" => Status(options, jsonOptions, projectPath, hookPath, chainedHookPath),
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

    private static int Install(HookCommandOptions options, JsonSerializerOptions jsonOptions, string projectPath, string gitDir, string hooksDir, string hookPath, string chainedHookPath)
    {
        var warnings = new List<HookCommandWarningJsonResult>();
        var hookScript = BuildHookScript(chainedHookPath, projectPath);
        var plan = BuildInstallPlan(options, hookPath, chainedHookPath, hookScript);
        if (options.DryRun)
            return WriteDryRunResult(options, jsonOptions, projectPath, hookPath, chainedHookPath, plan, hookScript);

        if (plan.Blocked)
            return WriteBlockedPlanResult(options, jsonOptions, projectPath, hookPath, chainedHookPath, plan);

        Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(hooksDir));
        if (!TryResolveHookWritePaths(gitDir, out hooksDir, out hookPath, out chainedHookPath))
            return WriteResult(options.Json, jsonOptions, "error", "unsafe Git hook file path", projectPath, null, null, CommandExitCodes.InstallError);

        hookScript = BuildHookScript(chainedHookPath, projectPath);
        plan = BuildInstallPlan(options, hookPath, chainedHookPath, hookScript);
        if (plan.Blocked)
            return WriteBlockedPlanResult(options, jsonOptions, projectPath, hookPath, chainedHookPath, plan);

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
                CommandExitCodes.Success);
        }

        if (plan.PlannedAction == "chain_existing")
        {
            try
            {
                if (!TryResolveHookWritePaths(gitDir, out hooksDir, out hookPath, out chainedHookPath))
                    return WriteResult(options.Json, jsonOptions, "error", "Git hook file path became unsafe before write", projectPath, null, null, CommandExitCodes.InstallError);
                ReplaceCustomHookWithManagedHook(hooksDir, hookPath, chainedHookPath, projectPath, warnings);
            }
            catch (Exception ex) when (IsHookFileOperationException(ex))
            {
                RecordHookWarning(warnings, "chained_hook_backup", chainedHookPath, "failed to back up existing hook", ex);
                var message = $"failed to install cdidx pre-commit hook ({CommandErrorWriter.FormatSanitizedException(ex)})";
                return WriteResult(options.Json, jsonOptions, "error", message, projectPath, hookPath, chainedHookPath, CommandExitCodes.InstallError, warnings);
            }

            return WriteResult(options.Json, jsonOptions, "updated", "cdidx pre-commit hook updated", projectPath, hookPath, chainedHookPath, CommandExitCodes.Success, warnings);
        }

        if (!TryResolveHookWritePaths(gitDir, out hooksDir, out hookPath, out chainedHookPath))
            return WriteResult(options.Json, jsonOptions, "error", "Git hook file path became unsafe before write", projectPath, null, null, CommandExitCodes.InstallError);

        hookScript = BuildHookScript(chainedHookPath, projectPath);
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
            warnings);
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

    private static bool IsManagedHookFile(string ioHookPath)
    {
        var content = ReadHookBytesWithinLimit(ioHookPath);
        return AnalyzeManagedHook(content).State == "managed";
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
        warnings.Add(new HookCommandWarningJsonResult(category, path, DiagnosticSanitizer.ForPath(path), message));
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
            plannedChanges: plan.PlannedChanges);
    }

    private static int WriteBlockedPlanResult(
        HookCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string projectPath,
        string hookPath,
        string chainedHookPath,
        HookOperationPlan plan)
        => WriteResult(
            options.Json,
            jsonOptions,
            "error",
            plan.Message,
            projectPath,
            hookPath,
            plan.ChainedHookState == "present" ? chainedHookPath : null,
            plan.BlockExitCode);

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
        IReadOnlyList<HookCommandFileChangeJsonResult>? plannedChanges = null)
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
                            : DiagnosticRedactor.RedactSensitiveText(managedHookPreview, "[redacted]", redactPaths: true),
                        filesystemMutation,
                        hookState,
                        chainedHookState,
                        safePlannedChanges),
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
                    plannedChanges),
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
    [property: JsonPropertyName("api_version")] string ApiVersion = JsonOutputContract.ApiVersion) : IVersionedJsonResult;
